using Microsoft.Extensions.AI;

namespace AgentSpike;

/// <summary>
/// qwen3-vl 은 think=false 여도 답을 thinking 필드에만 넣을 때가 있음 (기존 OllamaChatCompletionService 와 같은 안전장치).
/// 도구 호출이 있는 턴에서는 추론을 답으로 오인하므로 적용하지 않는다. 적용 횟수는 AppliedCount 로 집계.
/// </summary>
public sealed class ReasoningFallbackChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public const string AppliedKey = "reasoning_fallback";

    /// <summary>함수 호출 루프 바깥에서는 응답 속성이 합쳐지며 사라질 수 있어 따로 셈</summary>
    public int AppliedCount { get; set; }

    /// <summary>문맥·출력 길이 제한으로 잘린 응답 수 (done_reason=length). 0이 아니면 채점에서 따로 표시</summary>
    public int LengthCount { get; set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        if (response.FinishReason == ChatFinishReason.Length) LengthCount++;
        var last = response.Messages.LastOrDefault();
        if (last is null
            || last.Contents.OfType<FunctionCallContent>().Any()
            || !string.IsNullOrWhiteSpace(last.Text))
        {
            return response;
        }

        var reasoning = string.Join("\n", last.Contents.OfType<TextReasoningContent>().Select(r => r.Text))
            .Replace("<think>", "").Replace("</think>", "").Trim();
        if (reasoning.Length == 0) return response;

        last.Contents.Add(new TextContent(reasoning));
        response.AdditionalProperties ??= [];
        response.AdditionalProperties[AppliedKey] = true;
        AppliedCount++;
        return response;
    }
}
