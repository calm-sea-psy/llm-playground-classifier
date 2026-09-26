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

public sealed record Classification(string DocumentType, int ElapsedMs, string Raw, string? ParseError);

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
    /// 필드 추출 · 검증 · VLM 폴백 · 최종 선택 = Engine DocumentProcessor (exe 와 같은 흐름).
    /// 문장은 ManagedPromptSource: 프롬프트 관리에 있는 종류는 적용 버전, 없는 종류는 팩 파일
    /// </summary>
    public DocumentProcessor CreateProcessor(Func<ProcessStage, string, CancellationToken, Task>? onStatus = null)
    {
        var o = llmOptions.Value;
        return new DocumentProcessor(
            model => new FieldExtractor(httpFactory.CreateClient(EngineHttpClient),
                new EngineLlmOptions(model, o.ContextLength, o.KeepAlive, MaxOutputTokens: o.MaxOutputTokens)),
            new ManagedPromptSource(prompts))
        {
            OnStatus = onStatus,
        };
    }
}

/// <summary>
/// 평가 도구의 추출 문장: 프롬프트 저장소에 있는 종류(영수증 · 상업송장 · 보험 청구서)는 저장소에서 (화면에서 고친 버전 적용),
/// 없는 종류(이력서 등 새 팩)는 팩 파일 그대로. exe 는 PackPromptSource (팩 파일만) ➔ 차이는 문서 종류 화면이 경고 (4차-exe 0-3)
/// </summary>
public sealed class ManagedPromptSource(TextPrompts prompts) : IPromptSource
{
    private bool Managed(DocumentType pack) => prompts.Has($"extract.{pack.Id}");

    public async Task<string> SystemAsync(DocumentType pack, string model, CancellationToken ct) => Managed(pack)
        ? await prompts.RenderForModelAsync($"extract.{pack.Id}", model,
            new(pack.Variables.ToDictionary(v => v.Key, v => (object?)v.Value)), ct)
        : pack.SystemPromptFor(model);

    public async Task<string> UserAsync(DocumentType pack, DocumentText document, CancellationToken ct) =>
        Managed(pack) && pack.UserTemplate is not null
            ? await prompts.RenderAsync("extract.user", new() { ["ocr_text"] = document.Text, ["input_label"] = document.Label }, ct)
            : pack.UserMessage(document.Label, document.Text);

    public async Task<string> VlmUserAsync(DocumentType pack, DocumentText document, string issues, CancellationToken ct) =>
        Managed(pack)
            ? await prompts.RenderAsync("extract.vlm.user",
                new() { ["ocr_text"] = document.Text, ["input_label"] = document.Label, ["issues"] = issues }, ct)
            : await PackPromptSource.Instance.VlmUserAsync(pack, document, issues, ct);
}
