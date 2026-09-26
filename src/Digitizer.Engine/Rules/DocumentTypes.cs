namespace Digitizer.Engine.Rules;

/// <summary>평가 도구 Text 모듈의 문서 종류 (영수증 · 상업송장 · 보험 청구서). 4차 통합 A 단계에서 Api 에서 옮김</summary>
public static class DocumentTypes
{
    public const string Receipt = "receipt";
    public const string CommercialInvoice = "commercial_invoice";
    public const string InsuranceClaim = "insurance_claim";
    public const string Other = "other";

    /// <summary>필드를 추출하는 문서 종류 (other 는 분류만)</summary>
    public static readonly string[] Extractable = [Receipt, CommercialInvoice, InsuranceClaim];

    public static readonly string[] All = [.. Extractable, Other];

    public static string DisplayName(string type) => type switch
    {
        Receipt => "영수증",
        CommercialInvoice => "상업송장",
        InsuranceClaim => "보험 청구서",
        _ => "기타",
    };
}
