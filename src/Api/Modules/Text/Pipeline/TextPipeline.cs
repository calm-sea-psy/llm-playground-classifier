using Digitizer.Engine.Rules;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Api.Shared.Llm;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Api.Modules.Text.Pipeline;

public sealed class PipelineOptions
{
    /// <summary>OCR 평균 신뢰도가 이 값보다 낮으면 VLM 폴백</summary>
    public double FallbackConfidence { get; set; } = 0.6;
    /// <summary>금액을 문자열로 받는 스키마 사용 여부 (DocumentSchemas.For 참고)</summary>
    public bool AmountsAsString { get; set; }
    public int VlmMaxImageSide { get; set; } = 1600;
    /// <summary>분류에는 앞부분만 사용 (문서 종류는 머리말로 충분히 구분됨)</summary>
    public int ClassifyMaxChars { get; set; } = 1500;
}

/// <summary>LLM 에 넣을 문서 텍스트. Structured = 문서 파싱 엔진의 Markdown(표는 HTML), 아니면 OCR 줄 읽기 순서 텍스트</summary>
public sealed record DocumentText(string Text, bool Structured)
{
    public string Label => Structured
        ? "문서 파싱 결과 (Markdown, 표는 HTML 로 칸 구조 유지)"
        : "OCR 텍스트 (위➔아래, 같은 줄은 왼쪽➔오른쪽 순서)";
}

public sealed record Classification(string DocumentType, int ElapsedMs, string Raw, string? ParseError);

/// <summary>추출 1회 기록. 텍스트 추출과 VLM 폴백 추출을 모두 남겨 5단계 모델 비교 지표로 쓴다</summary>
public sealed record ExtractionAttempt(
    string Source,
    string Model,
    int ElapsedMs,
    int? PromptTokens,
    int? CompletionTokens,
    bool SchemaValid,
    string? ParseError,
    JsonObject? Fields,
    List<ValidationIssue> Issues,
    string Raw)
{
    public const string Text = "text";
    public const string Vlm = "vlm";

    /// <summary>스키마 위반도 오류 1건으로 셈</summary>
    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error) + (SchemaValid ? 0 : 1);
}

public static class PipelineJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// Step 1 LLM 단계: ClassifyDocument ➔ ExtractFields (+ ValidateFields 는 C# 규칙).
/// SK 는 LLM 호출(IChatCompletionService, ChatHistory·이미지 입력)과 프롬프트 템플릿 렌더링에 사용하고,
/// 단계 순서는 고정이므로 함수 자동 호출(planner)은 쓰지 않는다.
/// </summary>
public sealed class TextPipeline(LlmClient llm, TextPrompts prompts, IOptions<PipelineOptions> options)
{
    public async Task<Classification> ClassifyDocumentAsync(string model, DocumentText document, LlmImage? image, CancellationToken ct)
    {
        var text = document.Text.Length > options.Value.ClassifyMaxChars
            ? document.Text[..options.Value.ClassifyMaxChars]
            : document.Text;
        var response = await llm.CompleteJsonAsync(new LlmRequest(
            model,
            await prompts.RenderAsync("classify.system", ct: ct),
            await prompts.RenderAsync("classify.user",
                new() { ["ocr_text"] = text, ["input_label"] = document.Label }, ct),
            DocumentSchemas.Classification(),
            image is null ? null : [image]), ct);

        try
        {
            var type = JsonNode.Parse(response.Content)?["document_type"]?.GetValue<string>();
            return DocumentTypes.All.Contains(type)
                ? new Classification(type!, response.ElapsedMs, response.Content, null)
                : new Classification(DocumentTypes.Other, response.ElapsedMs, response.Content, $"알 수 없는 문서 종류: {type}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new Classification(DocumentTypes.Other, response.ElapsedMs, response.Content, ex.Message);
        }
    }

    /// <param name="image">null 이면 OCR 텍스트만으로 추출, 있으면 VLM 폴백 (이미지 + OCR 텍스트)</param>
    /// <param name="previousIssues">폴백 때 텍스트 추출에서 발견된 문제를 힌트로 전달</param>
    public async Task<ExtractionAttempt> ExtractFieldsAsync(
        string model,
        string documentType,
        DocumentText document,
        LlmImage? image,
        IReadOnlyList<ValidationIssue>? previousIssues,
        bool amountsAsString,
        CancellationToken ct)
    {
        var system = await prompts.RenderForModelAsync($"extract.{documentType}", model,
            new() { ["amount_rule"] = AmountRule(documentType, amountsAsString) }, ct);
        var user = await prompts.RenderAsync(image is null ? "extract.user" : "extract.vlm.user", new()
        {
            ["ocr_text"] = document.Text,
            ["input_label"] = document.Label,
            // 규칙을 "맞추라"고 하면 모델이 값을 계산해 바꿔 버림 (7×1,980=13,860 을 만들어 냄) ➔ 이미지 확인만 요청
            ["issues"] = previousIssues is { Count: > 0 }
                ? "OCR 텍스트로 먼저 추출한 결과를 규칙으로 검사했더니 아래 항목이 맞지 않았습니다. " +
                  "OCR 오인식일 수 있으니 해당 값들을 이미지에서 직접 확인해 인쇄된 그대로 옮기세요. " +
                  "규칙을 맞추려고 값을 계산하거나 바꾸지 마세요. 이미지와 같다면 그대로 둡니다.\n" +
                  string.Join("\n", previousIssues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"- {i.Message}"))
                : "",
        }, ct);

        var response = await llm.CompleteJsonAsync(
            new LlmRequest(model, system, user, DocumentSchemas.For(documentType, amountsAsString), image is null ? null : [image]),
            ct);

        var source = image is null ? ExtractionAttempt.Text : ExtractionAttempt.Vlm;
        JsonObject? fields;
        string? parseError = null;
        try
        {
            fields = JsonNode.Parse(response.Content) as JsonObject;
            if (fields is null)
            {
                parseError = "JSON 객체가 아닙니다";
            }
            else
            {
                var required = DocumentSchemas.For(documentType, amountsAsString)["required"]!.AsArray().Select(n => n!.GetValue<string>());
                var missing = required.Where(k => !fields.ContainsKey(k)).ToList();
                if (missing.Count > 0)
                {
                    parseError = $"필드 누락: {string.Join(", ", missing)}";
                }
            }
        }
        catch (JsonException ex)
        {
            fields = null;
            parseError = response.Truncated
                ? $"응답이 최대 길이({response.CompletionTokens} 토큰)에서 잘림 (같은 내용 반복 생성 의심)"
                : $"JSON 파싱 실패: {ex.Message}";
        }

        // 수량 보정(영수증: 금액 = 단가인데 수량이 7 같은 값 ➔ 1)은 검증 전에 적용하고 경고로 남김
        List<ValidationIssue> issues = fields is null ? []
            : [
                .. FieldValidator.CorrectQuantities(documentType, fields),
                .. FieldValidator.Validate(documentType, fields),
                // 근거 확인은 텍스트 추출만 (VLM 은 이미지를 직접 봄)
                .. image is null ? FieldValidator.CheckGrounded(documentType, fields, document.Text) : [],
            ];
        if (fields is not null)
        {
            NormalizeAmounts(documentType, fields);
        }
        return new ExtractionAttempt(source, model, response.ElapsedMs, response.PromptTokens, response.CompletionTokens,
            SchemaValid: parseError is null, parseError, fields, issues, response.Content);
    }

    private static string AmountRule(string documentType, bool asString) => (documentType, asString) switch
    {
        (DocumentTypes.CommercialInvoice, false) =>
            "Amounts are plain numbers without thousands separators or currency symbols (e.g. \"1,234.50\" → 1234.50).",
        (DocumentTypes.CommercialInvoice, true) =>
            "Amounts are strings containing only the number, without thousands separators or currency symbols (e.g. \"1,234.50\" → \"1234.50\").",
        (_, false) => "금액은 원 단위 정수로, 쉼표·'원'·통화 기호를 뺍니다 (예: \"12,850원\" → 12850).",
        (_, true) => "금액은 숫자만 담은 문자열로, 쉼표·'원'·통화 기호를 뺍니다 (예: \"12,850원\" → \"12850\").",
    };

    /// <summary>문자열 금액("12850")을 숫자로 바꿔 저장 형식을 통일 (검증은 두 형식 모두 처리)</summary>
    private static void NormalizeAmounts(string documentType, JsonObject fields)
    {
        var topLevel = documentType == DocumentTypes.Receipt ? DocumentSchemas.ReceiptAmountFields
            : documentType == DocumentTypes.CommercialInvoice ? DocumentSchemas.InvoiceAmountFields
            : [];
        foreach (var key in topLevel)
        {
            ToNumber(fields, key);
        }
        if (fields["items"] is JsonArray items && topLevel.Length > 0)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                foreach (var key in DocumentSchemas.ItemAmountFields)
                {
                    ToNumber(item, key);
                }
            }
        }
    }

    private static void ToNumber(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out _) && FieldValidator.Amount(v) is { } number)
        {
            obj[key] = number;
        }
    }
}
