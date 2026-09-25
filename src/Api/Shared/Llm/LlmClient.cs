using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;

namespace Api.Shared.Llm;

public sealed record LlmImage(byte[] Data, string MimeType);

public sealed record LlmRequest(
    string Model,
    string SystemPrompt,
    string UserPrompt,
    JsonNode Schema,
    IReadOnlyList<LlmImage>? Images = null);

/// <param name="Truncated">응답이 최대 토큰(MaxOutputTokens)에서 잘림 ➔ JSON 이 끝나지 않았을 가능성이 큼</param>
public sealed record LlmResponse(string Content, int ElapsedMs, int? PromptTokens, int? CompletionTokens, bool Truncated = false);

/// <summary>
/// 모듈 공통 LLM 호출 진입점. 모델마다 SK IChatCompletionService 가 키(모델 태그)로 등록되어 있다.
/// Ollama 는 한 번에 1건만 처리 (VRAM 16GB 에 모델 하나 + PaddleOCR)
/// </summary>
public sealed class LlmClient(Kernel kernel, IOptions<LlmOptions> options, IHttpClientFactory httpFactory)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>이 크기(화소 수)의 이미지를 OCR 하기 전에 LLM 을 내려야 하는지</summary>
    public bool ShouldUnloadBeforeOcr(long pixels, UnloadPolicy policy) =>
        options.Value.Provider == "ollama" && policy switch
        {
            UnloadPolicy.Always => true,
            UnloadPolicy.LargeImages => pixels > options.Value.UnloadAboveMegapixels * 1_000_000,
            _ => false,
        };

    /// <summary>GPU 에 올라가 있는 Ollama 모델을 모두 내리고, 실제로 내려갈 때까지(최대 10초) 기다린다</summary>
    public async Task UnloadAllAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient(LlmServiceCollectionExtensions.HttpClientName);
        var loaded = await LoadedModelsAsync(http, ct);
        if (loaded.Count == 0)
        {
            return;
        }
        await Gate.WaitAsync(ct);
        try
        {
            foreach (var model in loaded)
            {
                using var _ = await http.PostAsJsonAsync("api/generate", new { model, keep_alive = 0 }, ct);
            }
            for (var i = 0; i < 20 && (await LoadedModelsAsync(http, ct)).Count > 0; i++)
            {
                await Task.Delay(500, ct);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<List<string>> LoadedModelsAsync(HttpClient http, CancellationToken ct)
    {
        var ps = await http.GetFromJsonAsync<JsonObject>("api/ps", ct);
        return ps?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
    }

    public IReadOnlyList<string> Models => options.Value.Models;

    public string DefaultModel => options.Value.DefaultModel;

    public bool IsKnownModel(string model) => options.Value.Models.Contains(model);

    public async Task<LlmResponse> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        var service = kernel.GetRequiredService<IChatCompletionService>(request.Model);

        var history = new ChatHistory(request.SystemPrompt);
        var items = new ChatMessageContentItemCollection { new TextContent(request.UserPrompt) };
        foreach (var image in request.Images ?? [])
        {
            items.Add(new ImageContent(image.Data, image.MimeType));
        }
        history.AddUserMessage(items);

        await Gate.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            var message = await service.GetChatMessageContentAsync(history, CreateSettings(request.Schema), kernel, ct);
            var elapsed = (int)watch.ElapsedMilliseconds;
            var (promptTokens, completionTokens) = ReadUsage(message.Metadata);
            var truncated = message.Metadata?.GetValueOrDefault(OllamaChatCompletionService.DoneReasonKey) as string == "length";
            return new LlmResponse(message.Content ?? "", elapsed, promptTokens, completionTokens, truncated);
        }
        finally
        {
            Gate.Release();
        }
    }

    private PromptExecutionSettings CreateSettings(JsonNode schema) =>
        options.Value.Provider == "openai"
            ? new OpenAIPromptExecutionSettings
            {
                Temperature = 0,
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    "result", BinaryData.FromString(schema.ToJsonString()), jsonSchemaIsStrict: false),
                ReasoningEffort = options.Value.Think ? null : "none",
            }
            : new LlmExecutionSettings { Temperature = 0, JsonSchema = schema };

    private static (int?, int?) ReadUsage(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
        {
            return (null, null);
        }
        if (metadata.TryGetValue("Usage", out var usage) && usage is ChatTokenUsage openAi)
        {
            return (openAi.InputTokenCount, openAi.OutputTokenCount);
        }
        return (
            metadata.GetValueOrDefault(OllamaChatCompletionService.PromptTokensKey) as int?,
            metadata.GetValueOrDefault(OllamaChatCompletionService.CompletionTokensKey) as int?);
    }
}
