using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Digitizer.Engine;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text.Pipeline;

/// <summary>
/// 문서 종류 팩 (packs/{문서 종류}/) 을 읽어 둠. 팩이 없는 종류는 null (필드 추출 불가).
/// 팩 관리 화면의 저장도 여기서: 팩 파일이 원본이고 (평가 도구 · exe 가 같은 파일을 씀) DB 버전은 두지 않는다.
/// 고칠 때는 버전을 올려야 하고, 이전 파일은 packs/{종류}/.history/{이전 버전}/ 에 남긴다
/// </summary>
public sealed partial class PackStore(IWebHostEnvironment env, IOptions<PipelineOptions> options, TimeProvider clock)
{
    public const string HistoryFolder = ".history";

    /// <summary>팩 ➔ 읽은 시점의 파일 수정 시각. 편집기로 파일을 고치거나 폴더를 지워도 재시작 없이 반영</summary>
    private readonly ConcurrentDictionary<string, (DocumentType Pack, DateTime Stamp)> _packs = new();
    private readonly Lock _write = new();

    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.PacksRoot));

    /// <summary>팩 폴더 전체 (문서 종류 화면 · 문서 처리의 종류 선택)</summary>
    public IReadOnlyList<DocumentType> All() => Directory.Exists(Root)
        ? [.. Directory.GetDirectories(Root).Select(d => Get(Path.GetFileName(d))).OfType<DocumentType>().OrderBy(p => p.Id)]
        : [];

    public DocumentType? Get(string documentType)
    {
        var dir = Path.Combine(Root, documentType);
        if (!PackCheck.IdRegex().IsMatch(documentType) || !Directory.Exists(dir))
        {
            _packs.TryRemove(documentType, out _);
            return null;
        }
        var stamp = Directory.GetFiles(dir).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        if (_packs.TryGetValue(documentType, out var cached) && cached.Stamp == stamp) return cached.Pack;
        var pack = DocumentType.Load(dir);
        _packs[documentType] = (pack, stamp);
        return pack;
    }

    /// <summary>팩 폴더의 type.json 원본 (화면에 없는 키 excel · notes · changelog 도 그대로 돌려주고 저장 때 보존)</summary>
    public JsonObject? TypeJson(string id)
    {
        var path = Path.Combine(Root, id, "type.json");
        return PackCheck.IdRegex().IsMatch(id) && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
    }

    /// <summary>프롬프트 파일들 (prompt.md · prompt.{모델 계열}.md · user.md · vlm.user.md)</summary>
    public Dictionary<string, string> Files(string id)
    {
        var dir = Path.Combine(Root, id);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.md").Where(f => IsPackFile(Path.GetFileName(f)))
                .OrderBy(f => Path.GetFileName(f) == "prompt.md" ? 0 : 1).ThenBy(f => f)
                .ToDictionary(f => Path.GetFileName(f), File.ReadAllText)
            : [];
    }

    public sealed record HistoryEntry(string Version, DateTimeOffset SavedAt);

    public IReadOnlyList<HistoryEntry> History(string id)
    {
        var dir = Path.Combine(Root, id, HistoryFolder);
        return Directory.Exists(dir)
            ? [.. Directory.GetDirectories(dir).Select(d => new HistoryEntry(Path.GetFileName(d), Directory.GetLastWriteTime(d)))
                .OrderByDescending(h => h.SavedAt)]
            : [];
    }

    /// <summary>
    /// 새 팩 추가(create) 또는 고치기. 임시 폴더에 써서 엔진이 읽을 수 있는지 · PackCheck 를 통과하는지 확인한 뒤에만 반영.
    /// files: 파일 이름 ➔ 내용 (null 이면 그 파일 삭제, 목록에 없는 파일은 그대로)
    /// </summary>
    /// <returns>문제 목록 (비면 저장됨)</returns>
    public List<string> Save(string id, JsonObject type, IReadOnlyDictionary<string, string?> files, string? note, bool create)
    {
        var errors = new List<string>();
        if (!PackCheck.IdRegex().IsMatch(id)) return [$"id '{id}' 는 영문 소문자로 시작하고 소문자 · 숫자 · _ 만 쓸 수 있습니다"];
        if (type["id"]?.GetValue<string>() != id) errors.Add("type.json 의 id 와 팩 id 가 다릅니다");
        foreach (var name in files.Keys.Where(n => !IsPackFile(n)))
            errors.Add($"팩에 둘 수 없는 파일 이름입니다: {name} (prompt.md · prompt.{{모델 계열}}.md · user.md · vlm.user.md)");
        if (errors.Count > 0) return errors;

        lock (_write)
        {
            var dir = Path.Combine(Root, id);
            var current = create ? null : TypeJson(id);
            if (create && Directory.Exists(dir)) return [$"이미 있는 팩입니다: {id}"];
            if (!create && current is null) return [$"팩이 없습니다: {id}"];

            var oldVersion = current?["version"]?.GetValue<string>();
            var newVersion = type["version"]?.GetValue<string>();
            if (oldVersion is not null && newVersion == oldVersion)
                return [$"버전을 올려야 저장됩니다 (지금 {oldVersion}). 측정 결과가 어느 정의로 나왔는지 구분하기 위해서입니다"];

            // 변경 메모 ➔ changelog 맨 앞에 (resume 팩과 같은 형식)
            type = (JsonObject)type.DeepClone();
            if (!string.IsNullOrWhiteSpace(note))
            {
                var log = type["changelog"] as JsonArray ?? [];
                type.Remove("changelog");
                log.Insert(0, new JsonObject
                {
                    ["version"] = newVersion,
                    ["date"] = clock.GetLocalNow().ToString("yyyy-MM-dd"),
                    ["changes"] = note.Trim(),
                });
                type["changelog"] = log;
            }

            // 새 파일 구성 = 지금 파일 + 바꾼 파일 - 지운 파일
            var next = create ? [] : Files(id);
            foreach (var (name, content) in files)
                if (content is null) next.Remove(name);
                else next[name] = content.Replace("\r\n", "\n");

            var staging = Path.Combine(Path.GetTempPath(), $"digitizer-pack-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(staging);
                var typeText = type.ToJsonString(JsonWrite) + "\n";
                File.WriteAllText(Path.Combine(staging, "type.json"), typeText);
                foreach (var (name, content) in next) File.WriteAllText(Path.Combine(staging, name), content);

                DocumentType loaded;
                try { loaded = DocumentType.Load(staging); }
                catch (Exception ex)
                {
                    return [$"엔진이 팩을 읽지 못했습니다: {ex.Message}"];
                }
                errors.AddRange(PackCheck.Check(loaded));
                if (errors.Count > 0) return errors;

                if (!create)
                {
                    // 이전 버전 보관 (같은 버전 폴더가 이미 있으면 뒤에 번호)
                    var backup = Path.Combine(dir, HistoryFolder, oldVersion!);
                    for (var i = 2; Directory.Exists(backup); i++) backup = Path.Combine(dir, HistoryFolder, $"{oldVersion}_{i}");
                    Directory.CreateDirectory(backup);
                    foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(backup, Path.GetFileName(f)));
                    foreach (var f in Directory.GetFiles(dir, "*.md").Where(f => IsPackFile(Path.GetFileName(f)) && !next.ContainsKey(Path.GetFileName(f))))
                        File.Delete(f);
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "type.json"), typeText);
                foreach (var (name, content) in next) File.WriteAllText(Path.Combine(dir, name), content);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            _packs.TryRemove(id, out _);
            return [];
        }
    }

    public static bool IsPackFile(string name) => name is "prompt.md" or "user.md" or "vlm.user.md" || ModelPromptRegex().IsMatch(name);

    [GeneratedRegex(@"^prompt\.[a-z0-9][a-z0-9.\-]*\.md$")]
    private static partial Regex ModelPromptRegex();
}
