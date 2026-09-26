using Digitizer.Engine.Rules;
using System.Text.Json.Nodes;

namespace Api.Modules.Text.Pipeline;

/// <summary>
/// LLM 응답을 강제할 JSON 스키마 (Ollama format / OpenAI response_format).
/// 모든 필드를 required 로 두고 값이 없으면 null 을 받는다 ➔ 필드 누락과 "없음"을 구분.
/// </summary>
public static class DocumentSchemas
{
    public static readonly string[] ReceiptAmountFields = ["subtotal", "tax", "gross_total", "total"];
    public static readonly string[] InvoiceAmountFields = ["total_amount"];
    public static readonly string[] ItemAmountFields = ["unit_price", "amount"];

    public static JsonObject Classification() => Object(new()
    {
        ["document_type"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray([.. DocumentTypes.All.Select(t => JsonValue.Create(t))]),
        },
    });

    /// <param name="amountsAsString">
    /// true 면 금액을 "12850" 같은 문자열로 받음. gemma4 가 정수 스키마에서 합계를 5270000000000000 으로
    /// 뽑은 사례(0단계)가 있어 두 방식을 비교하기 위한 옵션
    /// </param>
    public static JsonObject For(string documentType, bool amountsAsString)
    {
        var amount = amountsAsString ? NullableString() : Nullable("integer");
        var decimalAmount = amountsAsString ? NullableString() : Nullable("number");
        return documentType switch
        {
            DocumentTypes.Receipt => Object(new()
            {
                ["store_name"] = NullableString(),
                ["business_no"] = NullableString(),
                ["date"] = NullableString(),
                ["time"] = NullableString(),
                ["receipt_no"] = NullableString(),
                ["items"] = Array(Object(new()
                {
                    ["name"] = NullableString(),
                    ["qty"] = Nullable("number"),
                    ["unit_price"] = amount.DeepClone(),
                    ["amount"] = amount.DeepClone(),
                })),
                ["subtotal"] = amount.DeepClone(),
                ["tax"] = amount.DeepClone(),
                ["gross_total"] = amount.DeepClone(),
                ["total"] = amount.DeepClone(),
                ["payment_method"] = NullableString(),
            }),
            DocumentTypes.CommercialInvoice => Object(new()
            {
                ["invoice_no"] = NullableString(),
                ["invoice_date"] = NullableString(),
                ["seller"] = NullableString(),
                ["buyer"] = NullableString(),
                ["currency"] = NullableString(),
                ["items"] = Array(Object(new()
                {
                    ["description"] = NullableString(),
                    ["qty"] = Nullable("number"),
                    ["unit_price"] = decimalAmount.DeepClone(),
                    ["amount"] = decimalAmount.DeepClone(),
                })),
                ["total_amount"] = decimalAmount.DeepClone(),
            }),
            // AI Hub 보험 청구서(DB손해보험 양식)는 금액 칸이 없고 날짜가 합성값("0825년")이라 원문 문자열로 받음
            DocumentTypes.InsuranceClaim => Object(new()
            {
                ["insured_name"] = NullableString(),
                ["resident_no"] = NullableString(),
                ["company_name"] = NullableString(),
                ["department"] = NullableString(),
                ["job_duty"] = NullableString(),
                ["address"] = NullableString(),
                ["contact_name"] = NullableString(),
                ["contact_relationship"] = NullableString(),
                ["contact_phone"] = NullableString(),
                ["email"] = NullableString(),
                ["other_insurers"] = Array(NullableString()),
                ["accident_datetime"] = NullableString(),
                ["accident_place"] = NullableString(),
                ["diagnosis"] = NullableString(),
                ["hospital"] = NullableString(),
                ["injury_part"] = NullableString(),
                ["bank_name"] = NullableString(),
                ["account_no"] = NullableString(),
                ["account_holder"] = NullableString(),
                ["written_date"] = NullableString(),
                ["claimant_name"] = NullableString(),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "필드 추출 대상이 아닌 문서 종류"),
        };
    }

    private static JsonObject Object(Dictionary<string, JsonNode> properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        ["required"] = new JsonArray([.. properties.Keys.Select(k => JsonValue.Create(k))]),
        ["additionalProperties"] = false,
    };

    private static JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };

    private static JsonObject Nullable(string type) => new() { ["type"] = new JsonArray(type, "null") };

    private static JsonObject NullableString() => Nullable("string");
}
