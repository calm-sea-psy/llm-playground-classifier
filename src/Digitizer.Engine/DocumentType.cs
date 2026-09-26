using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Digitizer.Engine.Rules;

namespace Digitizer.Engine;

/// <summary>필드 하나. list 면 Items 가 항목의 필드</summary>
public sealed record FieldDef(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("key")] string? Key = null,
    [property: JsonPropertyName("ranged")] bool Ranged = false,
    [property: JsonPropertyName("items")] List<FieldDef>? Items = null,
    [property: JsonPropertyName("description")] string? Description = null);

/// <summary>수집하면 안 되는 정보. Pattern 이 있으면 추출 결과 · 원문에서 코드로 찾음</summary>
public sealed record ForbiddenDef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("pattern")] string? Pattern = null);

/// <summary>
/// 문서 종류 팩 (packs/{id}/). 엔진은 이 정의만 보고 동작하고 종류별 코드는 두지 않는다.
/// 새 종류 = 팩 추가 ➔ 평가 도구에서 측정 ➔ 기준을 넘으면 exe 에 포함
/// 파일: type.json (필드 · 규칙 · 변수) · prompt.md (지시문, {{$변수}}) · prompt.{모델 계열}.md (선택, 예: prompt.qwen3-vl.md)
///       user.md (선택, 원문 전달 틀: {{$input_label}} · {{$ocr_text}}) · vlm.user.md (선택, 이미지 폴백용: + {{$issues}})
/// </summary>
public sealed partial record DocumentType(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("fields")] List<FieldDef> Fields,
    [property: JsonPropertyName("forbidden")] List<ForbiddenDef> Forbidden,
    [property: JsonPropertyName("rules")] List<string> Rules)
{
    /// <summary>false 면 필드 타입에서 나오는 공통 검사(원문 근거 · 형식)를 끄고 rules 에 적은 규칙만 씀 (기존 영수증 동작과 같게)</summary>
    [JsonPropertyName("generic_checks")] public bool GenericChecks { get; init; } = true;

    /// <summary>프롬프트 틀의 {{$이름}} 에 넣을 값 (예: 영수증 amount_rule)</summary>
    [JsonPropertyName("variables")] public Dictionary<string, string> Variables { get; init; } = [];

    [JsonIgnore] public string Prompt { get; init; } = "";
    [JsonIgnore] public Dictionary<string, string> ModelPrompts { get; init; } = [];
    [JsonIgnore] public string? UserTemplate { get; init; }
    [JsonIgnore] public string? VlmUserTemplate { get; init; }

    public static DocumentType Load(string typeDir)
    {
        var type = JsonSerializer.Deserialize<DocumentType>(File.ReadAllText(Path.Combine(typeDir, "type.json")))
            ?? throw new InvalidDataException($"문서 종류 정의를 읽지 못했습니다: {typeDir}");
        var unknown = type.Rules.Where(r => !RuleRegistry.IsKnown(r)).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"{type.Id}: 알 수 없는 규칙 {string.Join(", ", unknown)} (등록된 규칙: {string.Join(", ", RuleRegistry.Names)})");

        string? Optional(string file) => File.Exists(Path.Combine(typeDir, file)) ? File.ReadAllText(Path.Combine(typeDir, file)) : null;
        var variants = Directory.GetFiles(typeDir, "prompt.*.md")
            .ToDictionary(f => Path.GetFileName(f)["prompt.".Length..^".md".Length], File.ReadAllText);
        return type with
        {
            Prompt = File.ReadAllText(Path.Combine(typeDir, "prompt.md")),
            ModelPrompts = variants,
            UserTemplate = Optional("user.md"),
            VlmUserTemplate = Optional("vlm.user.md"),
        };
    }

    /// <summary>모델 계열 = 모델 이름의 ':' 앞 (qwen3-vl:8b ➔ qwen3-vl). 평가 도구 PromptLibrary.ModelFamily 와 같은 규칙</summary>
    public static string ModelFamily(string model) => model.Split(':')[0];

    public string SystemPromptFor(string model) =>
        Render(ModelPrompts.GetValueOrDefault(ModelFamily(model)) ?? Prompt, Variables);

    /// <summary>원문 전달 메시지. user.md 가 없으면 이력서 측정 때와 같은 기본 문장</summary>
    public string UserMessage(string inputLabel, string text) => UserTemplate is null
        ? $"다음은 {DisplayName} 원문입니다.\n\n{text}"
        : Render(UserTemplate, new Dictionary<string, string> { ["input_label"] = inputLabel, ["ocr_text"] = text });

    /// <summary>SK 템플릿과 같은 {{$이름}} 치환 (평가 도구 프롬프트 파일을 그대로 쓰기 위해). 모르는 변수는 빈 문자열</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        VariableRegex().Replace(template, m => values.GetValueOrDefault(m.Groups[1].Value, ""));

    [GeneratedRegex(@"\{\{\s*\$(\w+)\s*\}\}")]
    private static partial Regex VariableRegex();
}
