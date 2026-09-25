using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;

namespace Api.Shared.Prompts;

/// <summary>
/// 프롬프트 1개의 목록 항목. ModelFamily = 모델 전용 변형이면 그 모델 계열 (예: "qwen3-vl"),
/// AllowsModelVariants = 모델 전용 변형을 만들 수 있는 공용 프롬프트 (파이프라인이 RenderForModelAsync 로 부름)
/// </summary>
public sealed record PromptInfo(
    string Module,
    string Name,
    string Kind,
    string Description,
    string? ModelFamily,
    bool AllowsModelVariants,
    bool HasFile,
    int? ActiveVersion,
    int VersionCount,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Variables);

/// <summary>
/// 프롬프트 원본(파일) + UI 에서 고친 버전(DB prompt_versions)을 관리한다 (싱글턴).
/// - 파일: 출력 폴더 Modules/{모듈}/Pipeline/Prompts/*.md(SK 템플릿) · *.json(데이터, 예: 용어집). 기본값이며 수정하지 않는다
/// - 적용 중인 버전이 있으면 파일 대신 그 내용을 쓴다. 적용 버전은 메모리에 두고 저장·적용·되돌리기 때마다 다시 읽는다 ➔ 재시작 없이 다음 작업부터 반영
/// </summary>
public sealed partial class PromptStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    public const string Markdown = "md";
    public const string Json = "json";

    /// <summary>알려진 프롬프트 설명과 모델 전용 변형 가능 여부 (파이프라인이 RenderForModelAsync 로 부르는 것)</summary>
    private static readonly Dictionary<string, (string Description, bool ModelVariants)> Known = new()
    {
        ["text/classify.system"] = ("문서 종류 분류 — 지시문 (영수증·상업송장·보험 청구서·기타)", false),
        ["text/classify.user"] = ("문서 종류 분류 — OCR 텍스트 전달", false),
        ["text/extract.receipt"] = ("영수증 필드 추출 — 지시문", true),
        ["text/extract.commercial_invoice"] = ("상업송장 필드 추출 — 지시문", true),
        ["text/extract.insurance_claim"] = ("보험 청구서 필드 추출 — 지시문", true),
        ["text/extract.user"] = ("필드 추출 — OCR 텍스트 전달", false),
        ["text/extract.vlm.user"] = ("VLM 폴백 — 이미지 + OCR 텍스트 + 앞선 검증 문제 전달", false),
        ["image/report.system"] = ("흉부 X-ray VLM 판독 — 지시문", true),
        ["image/report.user"] = ("흉부 X-ray VLM 판독 — 대상·CNN 결과(보여 줄 때) 전달", false),
        ["multimodal/summarize.system"] = ("소견서 요약 — 지시문 (소견별 언급 규칙)", true),
        ["multimodal/summarize.user"] = ("소견서 요약 — 소견서 텍스트 전달", false),
        ["multimodal/summarize.glossary"] = ("소견서 요약 용어집 (키워드 트리거) — 소견서에 나온 용어의 정의만 요약 지시문에 붙임", false),
        ["multimodal/synthesize.system"] = ("환자 종합 보고서 — 지시문", true),
        ["multimodal/synthesize.user"] = ("환자 종합 보고서 — 요약·CNN·VLM·일치 비교 전달", false),
    };

    private static readonly KernelPromptTemplateFactory Factory = new();

    private readonly ConcurrentDictionary<string, string?> files = new();
    private volatile Dictionary<string, PromptVersion> active = new();

    public static string PromptsDirectory(string module) =>
        Path.Combine(AppContext.BaseDirectory, "Modules", Folder(module), "Pipeline", "Prompts");

    /// <summary>모듈 키 ➔ 폴더 이름 ("multimodal" ➔ "Multimodal")</summary>
    private static string Folder(string module) => char.ToUpperInvariant(module[0]) + module[1..];

    private static string Key(string module, string name) => $"{module}/{name}";

    [GeneratedRegex(@"\{\{\s*\$([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex VariablePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.-]{0,100}$")]
    private static partial Regex NamePattern();

    /// <summary>시작 시·변경 후: 적용 중인 버전을 DB 에서 다시 읽음</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.Set<PromptVersion>().AsNoTracking().Where(p => p.Active).ToListAsync(ct);
        active = rows.ToDictionary(p => Key(p.Module, p.Name));
    }

    /// <summary>파이프라인이 쓰는 내용: 적용 버전 ➔ 없으면 파일. 둘 다 없으면 null</summary>
    public string? Content(string module, string name, string kind = Markdown) => Resolve(module, name, kind).Content;

    /// <summary>내용 + 적용 버전 번호 (파일 기본값이면 null) ➔ 작업 기록(PromptUsage)용</summary>
    public (string? Content, int? Version) Resolve(string module, string name, string kind = Markdown) =>
        active.TryGetValue(Key(module, name), out var v) ? (v.Content, v.Version) : (FileContent(module, name, kind), null);

    public bool Exists(string module, string name) =>
        active.ContainsKey(Key(module, name)) || FileContent(module, name, Markdown) is not null;

    /// <summary>파일 원본 (없으면 null). 파일은 실행 중 바뀌지 않는다고 보고 캐시</summary>
    public string? FileContent(string module, string name, string kind) =>
        files.GetOrAdd(Key(module, name) + "." + kind, _ =>
        {
            var path = Path.Combine(PromptsDirectory(module), $"{name}.{kind}");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });

    /// <summary>파일 + DB 버전을 합친 목록 (켜진 모듈만)</summary>
    public async Task<List<PromptInfo>> ListAsync(IEnumerable<string> modules, AppDbContext db, CancellationToken ct)
    {
        var moduleList = modules.ToList();
        var stats = await db.Set<PromptVersion>().AsNoTracking()
            .Where(p => moduleList.Contains(p.Module))
            .GroupBy(p => new { p.Module, p.Name })
            .Select(g => new
            {
                g.Key.Module,
                g.Key.Name,
                Count = g.Count(),
                Active = g.Where(p => p.Active).Select(p => (int?)p.Version).FirstOrDefault(),
                UpdatedAt = g.Max(p => p.CreatedAt),
            })
            .ToListAsync(ct);
        var result = new List<PromptInfo>();
        foreach (var module in moduleList)
        {
            var dir = PromptsDirectory(module);
            var fileNames = Directory.Exists(dir)
                ? Directory.GetFiles(dir).Where(f => f.EndsWith(".md") || f.EndsWith(".json"))
                    .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Kind: Path.GetExtension(f)[1..]))
                : [];
            var names = fileNames
                .Concat(stats.Where(s => s.Module == module).Select(s => (s.Name, Kind: KindOf(module, s.Name))))
                .DistinctBy(n => n.Name)
                .OrderBy(n => n.Name, StringComparer.Ordinal);
            foreach (var (name, kind) in names)
            {
                var s = stats.FirstOrDefault(x => x.Module == module && x.Name == name);
                result.Add(Describe(module, name, kind, s?.Active, s?.Count ?? 0, s?.UpdatedAt));
            }
        }
        return result;
    }

    public PromptInfo Describe(string module, string name, string kind, int? activeVersion, int count, DateTimeOffset? updatedAt)
    {
        var (baseName, family) = SplitVariant(module, name);
        var known = Known.GetValueOrDefault(Key(module, baseName));
        var description = known.Description ?? "(파이프라인에서 쓰지 않는 파일)";
        if (family is not null)
        {
            description = $"{description} · {family} 전용";
        }
        var content = Content(module, name, kind) ?? "";
        return new PromptInfo(module, name, kind, description, family, family is null && known.ModelVariants,
            FileContent(module, name, kind) is not null, activeVersion, count, updatedAt,
            kind == Markdown ? Variables(content) : []);
    }

    /// <summary>"summarize.system.qwen3-vl" ➔ ("summarize.system", "qwen3-vl"). 모델 전용 변형이 아니면 (name, null)</summary>
    public static (string BaseName, string? Family) SplitVariant(string module, string name)
    {
        foreach (var (key, info) in Known)
        {
            if (!info.ModelVariants || !key.StartsWith(module + "/"))
            {
                continue;
            }
            var baseName = key[(module.Length + 1)..];
            if (name.StartsWith(baseName + ".") && name.Length > baseName.Length + 1)
            {
                return (baseName, name[(baseName.Length + 1)..]);
            }
        }
        return (name, null);
    }

    public string KindOf(string module, string name) =>
        FileContent(module, name, Json) is not null ? Json : Markdown;

    public static List<string> Variables(string content) =>
        VariablePattern().Matches(content).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// 저장 전 검사. 반환 = 오류 메시지 (없으면 null).
    /// md: SK 템플릿으로 해석되는지 + 변수가 기본값(파일, 모델 전용이면 공용 파일)과 같은지. json(용어집): entries[].pattern 이 정규식인지
    /// </summary>
    public string? Validate(string module, string name, string kind, string content, IReadOnlyCollection<string> modelFamilies)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "내용이 비어 있습니다";
        }
        if (content.Length > 100_000)
        {
            return "프롬프트는 100,000자 이하여야 합니다";
        }
        var (baseName, family) = SplitVariant(module, name);
        var reference = FileContent(module, name, kind) ?? FileContent(module, baseName, kind);
        if (reference is null)
        {
            return family is null
                ? $"'{name}' 은(는) 파이프라인에서 쓰는 프롬프트가 아닙니다. 새 프롬프트는 모델 전용 변형({{이름}}.{{모델 계열}})만 추가할 수 있습니다"
                : $"공용 프롬프트 '{baseName}' 이(가) 없습니다";
        }
        if (family is not null && !modelFamilies.Contains(family))
        {
            return $"모델 계열 '{family}' 은(는) 설정된 모델(Llm:Models)에 없습니다";
        }
        if (kind == Json)
        {
            return ValidateGlossary(content);
        }
        try
        {
            Factory.Create(new PromptTemplateConfig(content) { AllowDangerouslySetContent = true });
        }
        catch (Exception ex)
        {
            return $"템플릿 해석 실패: {ex.Message}";
        }
        var expected = Variables(reference);
        var actual = Variables(content);
        var missing = expected.Except(actual).ToList();
        var unknown = actual.Except(expected).ToList();
        if (missing.Count > 0)
        {
            return $"필요한 변수가 빠졌습니다: {string.Join(", ", missing.Select(v => "{{$" + v + "}}"))}";
        }
        if (unknown.Count > 0)
        {
            return $"파이프라인이 넘기지 않는 변수입니다: {string.Join(", ", unknown.Select(v => "{{$" + v + "}}"))}";
        }
        return null;
    }

    private static string? ValidateGlossary(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return "용어집 JSON 에 entries 배열이 없습니다";
            }
            var i = 0;
            foreach (var e in entries.EnumerateArray())
            {
                i++;
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String
                    || !e.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(t.GetString()))
                {
                    return $"entries[{i}]: pattern·text 문자열이 필요합니다";
                }
                try
                {
                    _ = new Regex(p.GetString()!, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    return $"entries[{i}] 정규식 오류: {ex.Message}";
                }
            }
            return null;
        }
        catch (JsonException ex)
        {
            return $"JSON 형식 오류: {ex.Message}";
        }
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>새 버전 저장 (activate 면 바로 적용)</summary>
    public async Task<PromptVersion> SaveAsync(AppDbContext db, string module, string name, string content, string? note, bool activate, CancellationToken ct)
    {
        var last = await db.Set<PromptVersion>().Where(p => p.Module == module && p.Name == name)
            .MaxAsync(p => (int?)p.Version, ct) ?? 0;
        if (activate)
        {
            await DeactivateAsync(db, module, name, ct);
        }
        var version = new PromptVersion
        {
            Module = module,
            Name = name,
            Version = last + 1,
            Content = content,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            Active = activate,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Add(version);
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
        return version;
    }

    /// <summary>특정 버전 적용 (null = 파일 기본값으로 되돌리기)</summary>
    public async Task ActivateAsync(AppDbContext db, string module, string name, long? versionId, CancellationToken ct)
    {
        await DeactivateAsync(db, module, name, ct);
        if (versionId is { } id)
        {
            var v = await db.Set<PromptVersion>().FirstAsync(p => p.Id == id, ct);
            v.Active = true;
        }
        await db.SaveChangesAsync(ct);
        await ReloadAsync(ct);
    }

    private static async Task DeactivateAsync(AppDbContext db, string module, string name, CancellationToken ct)
    {
        var current = await db.Set<PromptVersion>().Where(p => p.Module == module && p.Name == name && p.Active).ToListAsync(ct);
        foreach (var c in current)
        {
            c.Active = false;
        }
        // 부분 유니크 인덱스(active) 때문에 끄는 것을 먼저 저장
        await db.SaveChangesAsync(ct);
    }
}
