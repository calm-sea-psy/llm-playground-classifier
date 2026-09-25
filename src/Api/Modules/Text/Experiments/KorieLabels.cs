using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Api.Modules.Text.Experiments;

public sealed class EvalOptions
{
    /// <summary>KORIE 헤더 필드 정답 CSV (eval/score_eval.py 와 같은 파일). 없으면 정확도 없이 채점</summary>
    public string KorieLabels { get; set; } = "../../data/verify/korie_fields_labels.csv";
}

/// <summary>
/// KORIE 정답 라벨 (파일명 stem ➔ 필드 ➔ 값). 채점 규칙은 eval/score_eval.py 와 같게 유지:
/// 금액은 숫자 일치, 시각은 인쇄된 정밀도까지, 텍스트는 공백 무시, #INVALID 는 제외
/// </summary>
public sealed partial class KorieLabels
{
    public static readonly string[] Fields = ["store_name", "date", "time", "receipt_no", "subtotal", "tax", "total"];
    public static readonly HashSet<string> AmountFields = ["subtotal", "tax", "total"];

    private readonly Dictionary<string, Dictionary<string, string>> _labels = [];

    public KorieLabels(Microsoft.Extensions.Options.IOptions<EvalOptions> options, IHostEnvironment env, ILogger<KorieLabels> log)
    {
        var path = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.KorieLabels));
        if (!File.Exists(path))
        {
            log.LogInformation("KORIE 정답 라벨 없음 ({Path}) ➔ 실험 정확도는 계산하지 않음", path);
            return;
        }
        foreach (var row in ReadCsv(path).Skip(1))
        {
            if (row.Length < 3 || row[2] is "" or "#INVALID")
            {
                continue;
            }
            if (!_labels.TryGetValue(row[0], out var fields))
            {
                _labels[row[0]] = fields = [];
            }
            fields[row[1]] = row[2];
        }
        log.LogInformation("KORIE 정답 라벨 {Count}장 로드", _labels.Count);
    }

    public IReadOnlyDictionary<string, string>? For(string fileName) =>
        _labels.GetValueOrDefault(Path.GetFileNameWithoutExtension(fileName));

    public static bool IsCorrect(string field, string label, string? predicted)
    {
        if (string.IsNullOrEmpty(predicted))
        {
            return false;
        }
        if (AmountFields.Contains(field))
        {
            return ToAmount(label) is { } l && ToAmount(predicted) == l;
        }
        if (field == "time")
        {
            var p = NoSpace(predicted);
            return p.Length >= label.Length && p[..label.Length] == label;
        }
        return NoSpace(label) == NoSpace(predicted);
    }

    private static long? ToAmount(string value) =>
        decimal.TryParse(value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? (long)Math.Round(d)
            : null;

    private static string NoSpace(string s) => WhitespaceRegex().Replace(s, "");

    /// <summary>따옴표·쉼표·줄바꿈을 처리하는 최소 CSV 파서 (라벨에 "주소, 동" 같은 쉼표가 있음)</summary>
    private static IEnumerable<string[]> ReadCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r')); cell.Clear();
                yield return row.ToArray(); row.Clear();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            yield return row.ToArray();
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
