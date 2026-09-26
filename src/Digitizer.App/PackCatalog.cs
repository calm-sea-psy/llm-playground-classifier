using Digitizer.Engine;

namespace Digitizer.App;

/// <summary>
/// exe 에 들어 있는 문서 종류 팩 (실행 폴더의 packs\). 빌드가 합격 표시된 팩만 넣지만, 폴더를 손으로 바꿀 수도 있으므로
/// 시작할 때 다시 확인: 엔진이 읽을 수 있고 PackCheck 를 통과하고 release 가 있는 팩만 씀
/// </summary>
public sealed class PackCatalog
{
    public sealed record Skipped(string Folder, string Reason);

    public IReadOnlyList<DocumentType> Packs { get; }
    public IReadOnlyList<Skipped> SkippedPacks { get; }

    public PackCatalog(string root)
    {
        var packs = new List<DocumentType>();
        var skipped = new List<Skipped>();
        foreach (var dir in (Directory.Exists(root) ? Directory.GetDirectories(root) : []).Order())
        {
            var folder = Path.GetFileName(dir);
            try
            {
                var pack = DocumentType.Load(dir);
                var errors = PackCheck.Check(pack);
                if (pack.Release is null) skipped.Add(new(folder, "합격 표시(release)가 없음"));
                else if (pack.Id != folder) skipped.Add(new(folder, $"폴더 이름과 id({pack.Id})가 다름"));
                else if (errors.Count > 0) skipped.Add(new(folder, string.Join("; ", errors)));
                else packs.Add(pack);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                skipped.Add(new(folder, ex.Message));
            }
        }
        Packs = packs;
        SkippedPacks = skipped;
    }

    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "packs");

    public DocumentType? Get(string id) => Packs.FirstOrDefault(p => p.Id == id);
}
