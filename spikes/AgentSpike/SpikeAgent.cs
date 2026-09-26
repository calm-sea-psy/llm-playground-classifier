using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace AgentSpike;

/// <summary>도구 호출 한 번의 기록</summary>
public sealed record ToolStep(string Name, Dictionary<string, object?> Arguments, string Result);

/// <summary>과제 하나를 푼 기록 (3단계 채점 입력)</summary>
public sealed record AgentTrace(
    string Model,
    bool Think,
    string Question,
    List<ToolStep> Steps,
    string Answer,
    int LlmCalls,
    long? InputTokens,
    long? OutputTokens,
    double ElapsedSec,
    int ReasoningFallbacks,
    int LengthCutoffs,
    string? Error);

/// <summary>
/// 직접 만든 도구 호출 루프 (MEAI FunctionInvokingChatClient). 4단계에서 Agent Framework ChatClientAgent 와 비교할 기준.
/// </summary>
public sealed class SpikeAgent(HttpClient ollamaHttp, ApiTools tools) : IAgentRunner
{
    public const string SystemPrompt = """
        당신은 로컬 LLM 평가 도구의 실험 결과를 분석하는 도우미입니다. 한국어로 답합니다.

        규칙
        - 실험·작업·프롬프트에 관한 사실은 반드시 도구로 조회한 뒤 답합니다. 기억이나 추측으로 답하지 않습니다.
        - 답에 쓰는 숫자와 id 는 도구 결과에 있는 값만 씁니다. 계산이 필요하면 도구 결과의 값으로 계산하고 계산식을 적습니다.
        - 도구가 오류를 돌려주거나 필요한 정보가 없으면, 없다고 답합니다. 같은 인자로 같은 도구를 반복해서 부르지 않습니다.
        - 도구로 알 수 없는 질문(미래 결과, 도구에 없는 데이터 등)은 알 수 없다고 답합니다.
        - 답은 짧게, 결론을 먼저 씁니다.
        """;

    public const int MaxIterations = 8;

    public async Task<AgentTrace> RunAsync(string model, string question, bool think = false, CancellationToken ct = default)
    {
        var reasoning = new ReasoningFallbackChatClient(new OllamaApiClient(ollamaHttp, model));
        var client = reasoning.AsBuilder()
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = MaxIterations;
                f.IncludeDetailedErrors = true;
            })
            .Build();

        var options = BuildOptions(tools.All(), think);

        List<ChatMessage> messages = [new(ChatRole.System, SystemPrompt), new(ChatRole.User, question)];
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(messages, options, ct);
            return BuildTrace(model, think, question, response.Messages, response.Text, response.Usage, sw, reasoning, null);
        }
        catch (Exception e)
        {
            return BuildTrace(model, think, question, [], "", null, sw, reasoning, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>에이전트 호출 옵션 (Agent Framework 쪽도 같이 씀)</summary>
    public static ChatOptions BuildOptions(IList<AITool> tools, bool think)
    {
        // AddOllamaOption 도 AdditionalProperties 에 넣으므로 덮어쓰지 말 것
        // (덮어써서 num_ctx 가 빠지자 기본 4096 에서 답이 14토큰 만에 잘림: done_reason=length)
        var options = new ChatOptions { Temperature = 0, Tools = tools, AdditionalProperties = [] };
        options.AdditionalProperties["think"] = think;
        options.AdditionalProperties["keep_alive"] = "10m";
        options.AddOllamaOption(OllamaOption.NumCtx, 16384);
        return options;
    }

    /// <summary>응답 메시지에서 도구 호출·결과를 짝지어 기록으로</summary>
    public static AgentTrace BuildTrace(string model, bool think, string question, IList<ChatMessage> output, string answer,
        UsageDetails? usage, Stopwatch sw, ReasoningFallbackChatClient guard, string? error)
    {
        var results = output.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .ToDictionary(r => r.CallId, r => r.Result?.ToString() ?? "");
        var steps = output.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Select(c => new ToolStep(
                c.Name,
                c.Arguments?.ToDictionary(a => a.Key, a => (object?)a.Value?.ToString()) ?? [],
                results.GetValueOrDefault(c.CallId, "")))
            .ToList();
        return new AgentTrace(
            model, think, question, steps, answer,
            LlmCalls: output.Count(m => m.Role == ChatRole.Assistant),
            usage?.InputTokenCount, usage?.OutputTokenCount,
            Math.Round(sw.Elapsed.TotalSeconds, 1),
            guard.AppliedCount, guard.LengthCount, error);
    }

    public static void Print(AgentTrace t)
    {
        Console.WriteLine($"== {t.Model}{(t.Think ? " (think)" : "")} · LLM {t.LlmCalls}회 · {t.ElapsedSec}초 · 토큰 {t.InputTokens}/{t.OutputTokens}" +
            (t.ReasoningFallbacks > 0 ? $" · 추론→답 대체 {t.ReasoningFallbacks}" : "") +
            (t.LengthCutoffs > 0 ? $" · 길이 잘림 {t.LengthCutoffs}" : ""));
        foreach (var s in t.Steps)
        {
            var args = string.Join(", ", s.Arguments.Select(a => $"{a.Key}={a.Value}"));
            var result = s.Result.Length > 160 ? s.Result[..160] + "…" : s.Result;
            Console.WriteLine($"  → {s.Name}({args})\n    ← {result.ReplaceLineEndings(" ")}");
        }
        if (t.Error is not null) Console.WriteLine($"  ! {t.Error}");
        Console.WriteLine($"  답: {t.Answer.ReplaceLineEndings("\n      ")}");
    }
}
