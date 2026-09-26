using Digitizer.Engine.Rules;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Api.Shared.Llm;
using Digitizer.Engine;
using Microsoft.Extensions.Options;
using EngineLlmOptions = Digitizer.Engine.LlmOptions;
using LlmOptions = Api.Shared.Llm.LlmOptions;
using Microsoft.SemanticKernel;

namespace Api.Modules.Text.Pipeline;

public sealed class PipelineOptions
{
    /// <summary>OCR 평균 신뢰도가 이 값보다 낮으면 VLM 폴백</summary>
    public double FallbackConfidence { get; set; } = 0.6;
    public int VlmMaxImageSide { get; set; } = 1600;
    /// <summary>분류에는 앞부분만 사용 (문서 종류는 머리말로 충분히 구분됨)</summary>
    public int ClassifyMaxChars { get; set; } = 1500;
    /// <summary>문서 종류 팩 폴더 (ContentRoot 기준)</summary>
    public string PacksRoot { get; set; } = "../../packs";
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
    string Raw,
    /// <summary>추출에 쓴 문서 종류 팩 "팩@버전" (예: receipt@1.0.0). 4차 통합 전 기록은 null (기존 추출)</summary>
    string? Engine = null)
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
public sealed class TextPipeline(
    LlmClient llm,
    TextPrompts prompts,
    IOptions<PipelineOptions> options,
    PackStore packs,
    IHttpClientFactory httpFactory,
    IOptions<LlmOptions> llmOptions)
{
    /// <summary>Engine 이 Ollama 를 부를 HttpClient 이름 (TextModule 에서 등록)</summary>
    public const string EngineHttpClient = "digitizer-engine-ollama";

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

    /// <summary>
    /// 필드 추출: 문서 종류 팩(packs/{종류}) + Digitizer.Engine (평가 도구 · exe 가 같은 엔진).
    /// 지시문 · 사용자 메시지는 프롬프트 관리 화면의 적용 버전으로 렌더링 (파일 기본값의 원본은 팩).
    /// 4차 통합: 기존(legacy) 추출은 KORIE 150장 · AI Hub 상업송장 · 보험 청구서 30장씩에서 결과가 같음을 확인하고 삭제
    /// </summary>
    /// <param name="image">null 이면 OCR 텍스트만으로 추출, 있으면 VLM 폴백 (이미지 + OCR 텍스트)</param>
    /// <param name="previousIssues">폴백 때 텍스트 추출에서 발견된 문제를 힌트로 전달</param>
    public async Task<ExtractionAttempt> ExtractFieldsAsync(
        string model,
        string documentType,
        DocumentText document,
        LlmImage? image,
        IReadOnlyList<ValidationIssue>? previousIssues,
        CancellationToken ct)
    {
        var pack = packs.Get(documentType)
            ?? throw new InvalidOperationException($"문서 종류 팩이 없습니다: {Path.Combine(packs.Root, documentType)}");
        var system = await prompts.RenderForModelAsync($"extract.{documentType}", model,
            new(pack.Variables.ToDictionary(v => v.Key, v => (object?)v.Value)), ct);
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

        var o = llmOptions.Value;
        var extractor = new FieldExtractor(httpFactory.CreateClient(EngineHttpClient),
            new EngineLlmOptions(model, o.ContextLength, o.KeepAlive, MaxOutputTokens: o.MaxOutputTokens));
        var result = await extractor.ExtractMessagesAsync(pack, system, user,
            image is null ? null : [new ImageInput(image.Data, image.MimeType)], ct);

        var fields = result.Fields;
        var parseError = result.Error;
        if (fields is not null)
        {
            var missing = pack.Fields.Select(f => f.Name).Where(k => !fields.ContainsKey(k)).ToList();
            if (missing.Count > 0) parseError = $"필드 누락: {string.Join(", ", missing)}";
        }
        // 팩 rules: 보정(영수증 수량) ➔ 종류별 규칙 ➔ 근거 확인(텍스트 추출만, VLM 은 이미지를 직접 봄)
        var issues = fields is null ? [] : Validator.Validate(pack, fields, document.Text, fromImage: image is not null);
        return new ExtractionAttempt(image is null ? ExtractionAttempt.Text : ExtractionAttempt.Vlm, model, (int)result.ElapsedMs,
            (int?)result.InputTokens, (int?)result.OutputTokens, SchemaValid: parseError is null, parseError, fields, issues, result.Raw,
            Engine: $"{pack.Id}@{pack.Version}");
    }
}
