using System.Text.Json.Nodes;
using Digitizer.Engine;

namespace Digitizer.App.Review;

/// <summary>
/// 필드 JSON ➔ 칸 경로별 값 (name, career[1].company, skills). 검수에서 고친 칸을 찾고(수정률) 화면에서 검증 문제 칸을 짚는 데 씀.
/// string_list · text_list 는 목록 전체를 한 칸으로 봄 (순서만 바꿔도 고친 것)
/// </summary>
public static class FieldPaths
{
    public static Dictionary<string, string?> Flatten(DocumentType pack, JsonObject? fields)
    {
        var result = new Dictionary<string, string?>();
        foreach (var f in pack.Fields)
        {
            var node = fields?[f.Name];
            if (f.Type == "list")
            {
                var items = node as JsonArray ?? [];
                for (var i = 0; i < items.Count; i++)
                foreach (var sub in f.Items ?? [])
                    result[$"{f.Name}[{i}].{sub.Name}"] = Value(items[i]?[sub.Name]);
            }
            else
            {
                result[f.Name] = Value(node);
            }
        }
        return result;
    }

    /// <summary>비교용 값: 빈 문자열 · 빈 목록 = null, 숫자는 JSON 표기, 문자열은 앞뒤 공백 제거</summary>
    public static string? Value(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        JsonArray a when a.Count == 0 => null,
        _ => node.ToJsonString(),
    };

    public sealed record Change(string Path, string? Extracted, string? Corrected);

    /// <summary>추출값과 검수값이 다른 칸. slots = 비교한 칸 수 (둘 중 한쪽에라도 있는 경로)</summary>
    public static (List<Change> Changes, int Slots) Diff(DocumentType pack, JsonObject? extracted, JsonObject? reviewed)
    {
        var a = Flatten(pack, extracted);
        var b = Flatten(pack, reviewed);
        var paths = a.Keys.Union(b.Keys).ToList();
        var changes = paths
            .Select(p => new Change(p, a.GetValueOrDefault(p), b.GetValueOrDefault(p)))
            .Where(c => c.Extracted != c.Corrected)
            .ToList();
        return (changes, paths.Count);
    }

    /// <summary>
    /// 화면에서 보낸 필드를 팩 정의 모양으로: 모르는 키는 버리고 빠진 키는 null, 금액은 정수 · 숫자는 수로 (문자열 "3,000" 도 받음).
    /// 형식을 못 맞추는 값은 그대로 둬서 검증이 문제로 알려 줌
    /// </summary>
    public static JsonObject Normalize(DocumentType pack, JsonObject input)
    {
        static JsonObject Shape(List<FieldDef> defs, JsonObject? source)
        {
            var o = new JsonObject();
            foreach (var f in defs)
            {
                var node = source?[f.Name];
                o[f.Name] = f.Type switch
                {
                    "list" => new JsonArray([.. (node as JsonArray ?? []).OfType<JsonObject>().Select(i => (JsonNode)Shape(f.Items ?? [], i))]),
                    "string_list" or "text_list" => new JsonArray([.. (node as JsonArray ?? [])
                        .Select(n => Value(n) is { } s ? JsonValue.Create(s) : null)
                        .Where(n => n is not null || f.Type == "text_list")
                        .Select(n => (JsonNode?)n)]),
                    "amount" => Number(node, integer: true),
                    "number" => Number(node, integer: false),
                    _ => Value(node) is { } s ? JsonValue.Create(s) : null,
                };
            }
            return o;
        }
        return Shape(pack.Fields, input);
    }

    private static JsonNode? Number(JsonNode? node, bool integer)
    {
        if (node is JsonValue v && (v.TryGetValue<long>(out _) || v.TryGetValue<double>(out _))) return node.DeepClone();
        if (Value(node) is not { } s) return null;
        var clean = s.Replace(",", "").Replace("원", "").Trim();
        if (integer && long.TryParse(clean, out var l)) return JsonValue.Create(l);
        if (!integer && double.TryParse(clean, System.Globalization.CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(s);
    }
}
