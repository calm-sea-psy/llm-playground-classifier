using System.Text.Json.Nodes;
using Digitizer.Engine;

namespace Digitizer.App.Processing;

/// <summary>
/// 다른 종류 폴더에 넣은 문서 알아채기 (예: 이력서 폴더에 영수증). 필수 필드가 모두 비어 있고 맨 위 필드도 3분의 1 미만만 차 있으면 경고
/// (영수증은 합계가 없어도 상호 · 날짜 · 품목 등 절반 가까이 차는 경우가 많아 절반 기준은 너무 빡빡함).
/// 필수 필드만 비고 나머지가 차 있으면 그 종류 문서에서 값 하나를 못 찾은 것 ➔ 검증 문제로 다룸 (경고하지 않음).
/// 측정 흐름(Engine)에는 넣지 않은 exe 쪽 안내라 추출 · 검증 결과는 바꾸지 않음
/// </summary>
public static class TypeMismatch
{
    public const string Message = "필수 항목이 대부분 비어 있습니다. 이 폴더(문서 종류)가 맞는지 확인하세요";

    public static bool Suspect(DocumentType pack, JsonObject? fields)
    {
        if (fields is null) return false;  // 추출 실패는 따로 다룸
        var required = pack.Fields.Where(f => f.Required).ToList();
        var requiredEmpty = required.Count(f => IsEmpty(fields[f.Name]));
        var filled = pack.Fields.Count(f => !IsEmpty(fields[f.Name]));
        return (required.Count == 0 || requiredEmpty == required.Count) && filled * 3 < pack.Fields.Count;
    }

    public static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.All(p => IsEmpty(p.Value)),
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        _ => false,
    };
}
