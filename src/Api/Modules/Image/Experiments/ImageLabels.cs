using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Api.Modules.Image.Experiments;

public sealed class ImageEvalOptions
{
    /// <summary>IU X-Ray 소견서 CSV (ContentRoot 기준). 없으면 IU 정답 없이 채점</summary>
    public string IuReports { get; set; } = "../../data/samples/iu-xray/indiana_reports.csv";
}

/// <summary>정답: pneumonia = 폐렴(IU 는 폐렴성 음영), normal = 이상 소견 없음. 모르면 null</summary>
public sealed record ImageTruth(string Dataset, bool? Pneumonia, bool? Normal);

/// <summary>
/// 파일 이름으로 정답을 찾는다 (eval/cnn_data.py 와 같은 정의).
/// - Kaggle 소아 폐렴: person…_bacteria/virus = 폐렴, IM-… / NORMAL2-IM-… = 정상 (파일 이름이 곧 라벨)
/// - IU X-Ray: "{uid}_IM-….dcm.png" ➔ 소견서 Problems. 폐렴성 음영 = 폐렴·공기공간 질환·침윤, 정상 = Problems 가 "normal"
/// </summary>
public sealed partial class ImageLabels
{
    private static readonly HashSet<string> PneumoniaLike = ["Pneumonia", "Airspace Disease", "Infiltrate"];
    private readonly Dictionary<string, HashSet<string>> iu = [];

    public ImageLabels(IOptions<ImageEvalOptions> options, IHostEnvironment env)
    {
        var path = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.IuReports));
        if (!File.Exists(path))
        {
            return;
        }
        var lines = File.ReadAllLines(path);
        var header = SplitCsv(lines[0]);
        int uid = header.IndexOf("uid"), problems = header.IndexOf("Problems");
        foreach (var line in lines.Skip(1))
        {
            var cols = SplitCsv(line);
            if (cols.Count > Math.Max(uid, problems))
            {
                iu[cols[uid]] = cols[problems].Split(';').Select(p => p.Trim()).ToHashSet();
            }
        }
    }

    public int IuCount => iu.Count;

    public ImageTruth? For(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (IuRegex().Match(name) is { Success: true } m)
        {
            return iu.TryGetValue(m.Groups[1].Value, out var p)
                ? new ImageTruth("iu", p.Overlaps(PneumoniaLike), p.SetEquals(["normal"]))
                : null;
        }
        if (name.StartsWith("person", StringComparison.OrdinalIgnoreCase))
        {
            return new ImageTruth("kaggle", true, false);
        }
        if (KaggleNormalRegex().IsMatch(name))
        {
            return new ImageTruth("kaggle", false, true);
        }
        return null;
    }

    /// <summary>따옴표 안의 쉼표를 처리하는 CSV 한 줄 분리 (소견서 문장에 쉼표가 많음)</summary>
    private static List<string> SplitCsv(string line)
    {
        var cols = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                cols.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        cols.Add(cur.ToString());
        return cols;
    }

    [GeneratedRegex(@"^(\d+)_IM-.*\.dcm\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex IuRegex();

    [GeneratedRegex(@"^(NORMAL\d*-)?IM-\d+-\d+", RegexOptions.IgnoreCase)]
    private static partial Regex KaggleNormalRegex();
}
