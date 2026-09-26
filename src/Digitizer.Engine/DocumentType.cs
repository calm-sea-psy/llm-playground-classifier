using System.Text.Json;
using System.Text.Json.Serialization;

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
/// 문서 종류 팩 (packs/{id}/type.json + prompt.md). 엔진은 이 정의만 보고 동작하고 종류별 코드는 두지 않는다.
/// 새 종류 = 팩 추가 ➔ 평가 도구에서 측정 ➔ 기준을 넘으면 exe 에 포함
/// </summary>
public sealed record DocumentType(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("fields")] List<FieldDef> Fields,
    [property: JsonPropertyName("forbidden")] List<ForbiddenDef> Forbidden,
    [property: JsonPropertyName("rules")] List<string> Rules)
{
    [JsonIgnore] public string Prompt { get; init; } = "";

    public static DocumentType Load(string typeDir)
    {
        var type = JsonSerializer.Deserialize<DocumentType>(File.ReadAllText(Path.Combine(typeDir, "type.json")))
            ?? throw new InvalidDataException($"문서 종류 정의를 읽지 못했습니다: {typeDir}");
        return type with { Prompt = File.ReadAllText(Path.Combine(typeDir, "prompt.md")) };
    }
}
