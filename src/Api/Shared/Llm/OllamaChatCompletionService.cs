using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Api.Shared.Llm;

/// <summary>JSON 스키마 강제 등 이 프로젝트에서 쓰는 LLM 호출 옵션</summary>
public sealed class LlmExecutionSettings : PromptExecutionSettings
{
    public double Temperature { get; set; }
    /// <summary>응답을 강제할 JSON 스키마 (null 이면 자유 텍스트)</summary>
    public JsonNode? JsonSchema { get; set; }
}

/// <summary>
/// Ollama 네이티브 /api/chat 을 SK IChatCompletionService 로 구현.
/// OpenAI 호환(/v1) 엔드포인트로는 num_ctx 를 지정할 수 없고(기본 4096), qwen3-vl 은 think 를 끄면
/// 답이 content 가 아닌 thinking/reasoning 필드로 와서 SK OpenAI 커넥터가 읽지 못하므로 직접 호출한다.
/// </summary>
public sealed class OllamaChatCompletionService(string modelId, HttpClient http, LlmOptions options)
    : IChatCompletionService
{
    public const string PromptTokensKey = "PromptTokens";
    public const string CompletionTokensKey = "CompletionTokens";
    /// <summary>"length" 면 MaxOutputTokens 에서 잘린 응답</summary>
    public const string DoneReasonKey = "DoneReason";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public IReadOnlyDictionary<string, object?> Attributes { get; } =
        new Dictionary<string, object?> { ["ModelId"] = modelId };

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        var settings = executionSettings as LlmExecutionSettings ?? new LlmExecutionSettings();
        var request = new OllamaChatRequest(
            Model: modelId,
            Messages: chatHistory.Select(ToOllamaMessage).ToList(),
            Stream: false,
            Think: options.Think,
            Format: settings.JsonSchema,
            KeepAlive: options.KeepAlive,
            Options: new OllamaRequestOptions(settings.Temperature, options.ContextLength, options.MaxOutputTokens));

        using var response = await http.PostAsJsonAsync("api/chat", request, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Ollama 오류 {(int)response.StatusCode}: {body}", null, response.StatusCode);
        }
        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Ollama 가 빈 응답을 반환했습니다");

        // qwen3-vl(Ollama 0.34.3)은 think=false 에서도 답을 thinking 필드에 넣어 줌
        var content = result.Message?.Content;
        if (string.IsNullOrWhiteSpace(content) && !options.Think)
        {
            content = result.Message?.Thinking;
        }

        var metadata = new Dictionary<string, object?>
        {
            [PromptTokensKey] = result.PromptEvalCount,
            [CompletionTokensKey] = result.EvalCount,
            [DoneReasonKey] = result.DoneReason,
        };
        return [new ChatMessageContent(AuthorRole.Assistant, content ?? "", modelId, metadata: metadata)];
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 파이프라인은 JSON 전체가 필요하므로 스트리밍은 한 번에 돌려줌
        var messages = await GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
        foreach (var message in messages)
        {
            yield return new StreamingChatMessageContent(message.Role, message.Content, modelId: modelId);
        }
    }

    private static OllamaMessage ToOllamaMessage(ChatMessageContent message)
    {
        var text = string.Join("\n", message.Items.OfType<TextContent>().Select(t => t.Text));
        var images = message.Items.OfType<ImageContent>()
            .Where(i => i.Data.HasValue)
            .Select(i => Convert.ToBase64String(i.Data!.Value.Span))
            .ToList();
        return new OllamaMessage(message.Role.Label, text, images.Count > 0 ? images : null);
    }

    private sealed record OllamaChatRequest(
        string Model,
        List<OllamaMessage> Messages,
        bool Stream,
        bool Think,
        JsonNode? Format,
        [property: JsonPropertyName("keep_alive")] string KeepAlive,
        OllamaRequestOptions Options);

    private sealed record OllamaRequestOptions(
        double Temperature,
        [property: JsonPropertyName("num_ctx")] int NumCtx,
        [property: JsonPropertyName("num_predict")] int NumPredict);

    private sealed record OllamaMessage(string Role, string Content, List<string>? Images);

    private sealed record OllamaChatResponse(
        OllamaResponseMessage? Message,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int? EvalCount,
        [property: JsonPropertyName("done_reason")] string? DoneReason);

    private sealed record OllamaResponseMessage(string? Content, string? Thinking);
}
