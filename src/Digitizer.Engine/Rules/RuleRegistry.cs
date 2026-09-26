using System.Text.Json.Nodes;

namespace Digitizer.Engine.Rules;

/// <summary>
/// 종류별 코드 규칙 등록표. 팩 type.json 의 rules 에 이름만 적으면 켜진다 (새 규칙 = 여기 한 줄 + 규칙 코드).
/// - 보정(fixer): 검증 전에 값을 고치고 경고로 남김 (예: 영수증 수량 1 ➔ 7 오인식 보정)
/// - 검사(check): 문제를 돌려줌. fromImage 면 원문 근거 확인은 건너뜀 (이미지를 직접 본 VLM 결과)
/// - date_order · list_count 는 공통 검증기(Validator) 가 처리하는 이름
/// </summary>
public static class RuleRegistry
{
    public delegate List<ValidationIssue> Check(JsonObject fields, string source, bool fromImage);

    public static readonly IReadOnlyDictionary<string, Func<JsonObject, List<ValidationIssue>>> Fixers =
        new Dictionary<string, Func<JsonObject, List<ValidationIssue>>>
        {
            ["receipt_qty_correction"] = f => FieldValidator.CorrectQuantities(DocumentTypes.Receipt, f),
        };

    public static readonly IReadOnlyDictionary<string, Check> Checks = new Dictionary<string, Check>
    {
        ["receipt"] = (f, _, _) => FieldValidator.Validate(DocumentTypes.Receipt, f),
        ["receipt_grounded"] = (f, s, img) => img ? [] : FieldValidator.CheckGrounded(DocumentTypes.Receipt, f, s),
        ["commercial_invoice"] = (f, _, _) => FieldValidator.Validate(DocumentTypes.CommercialInvoice, f),
        ["insurance_claim"] = (f, _, _) => FieldValidator.Validate(DocumentTypes.InsuranceClaim, f),
    };

    public static readonly string[] Generic = ["date_order", "list_count"];

    /// <summary>팩 관리 화면의 규칙 설명</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["receipt_qty_correction"] = "영수증 수량 보정: 단가 × 수량 ≠ 금액이면 OCR 이 잘못 읽은 수량을 고치고 경고",
        ["receipt"] = "영수증 규칙: 합계 필수 · 품목 합 · 소계 + 세금 = 합계 · 시간 형식 · 사업자등록번호 체크섬",
        ["receipt_grounded"] = "영수증 원문 근거: 추출 값이 OCR 원문에 있는지 (이미지 폴백 결과는 건너뜀)",
        ["commercial_invoice"] = "상업송장 규칙: 합계 필수 · 품목 금액 합 · 통화 코드(ISO 4217)",
        ["insurance_claim"] = "보험 청구서 규칙: 피보험자 성명 · 날짜 필수 · 항목 금액",
        ["date_order"] = "목록 항목의 start ≤ end, 시작과 끝이 같으면 복사 의심",
        ["list_count"] = "원문의 기간 표기 수 > 추출된 기간 항목 수면 빠뜨림 의심 (ranged 목록)",
    };

    public static string Kind(string name) => Fixers.ContainsKey(name) ? "fixer" : Checks.ContainsKey(name) ? "check" : "generic";

    public static IEnumerable<string> Names => Fixers.Keys.Concat(Checks.Keys).Concat(Generic);

    public static bool IsKnown(string name) => Fixers.ContainsKey(name) || Checks.ContainsKey(name) || Generic.Contains(name);
}
