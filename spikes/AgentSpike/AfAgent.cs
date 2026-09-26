using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace AgentSpike;

/// <summary>Runner 가 설정별로 부를 에이전트 (직접 루프 / Agent Framework)</summary>
public interface IAgentRunner
{
    Task<AgentTrace> RunAsync(string model, string question, bool think = false, CancellationToken ct = default);
}

/// <summary>
/// 4단계: 같은 도구·프롬프트·옵션을 Microsoft Agent Framework ChatClientAgent 로.
/// 직접 루프(SpikeAgent)와 같은 과제에서 결과가 같은지 비교하고, 쓰기 도구의 사람 승인 흐름을 확인한다.
/// </summary>
public sealed class AfAgent(HttpClient ollamaHttp, ApiTools tools) : IAgentRunner
{
    public async Task<AgentTrace> RunAsync(string model, string question, bool think = false, CancellationToken ct = default)
    {
        var guard = new ReasoningFallbackChatClient(new OllamaApiClient(ollamaHttp, model));
        var agent = Create(guard, tools.All(), think);
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await agent.RunAsync(question, cancellationToken: ct);
            return SpikeAgent.BuildTrace(model, think, question, response.Messages, response.Text, response.Usage, sw, guard, null);
        }
        catch (Exception e)
        {
            return SpikeAgent.BuildTrace(model, think, question, [], "", null, sw, guard, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static ChatClientAgent Create(IChatClient client, IList<AITool> agentTools, bool think)
    {
        var options = SpikeAgent.BuildOptions(agentTools, think);
        options.Instructions = SpikeAgent.SystemPrompt;
        return new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "experiment-analyst",
            ChatOptions = options,
        });
    }

    /// <summary>
    /// 사람 승인 흐름: 쓰기 도구(create_prompt_version, dry-run)를 승인 필요 도구로 두고
    /// ① 거절 ➔ 도구가 실행되지 않고 에이전트가 저장 안 됐다고 답하는지 ② 승인 ➔ 그때 실행되는지
    /// </summary>
    public async Task ApprovalDemoAsync(string model)
    {
        foreach (var approve in new[] { false, true })
        {
            var writes = new List<string>();

            [Description("프롬프트의 새 버전을 저장한다 (저장만 하고 적용은 하지 않음). 사람이 승인해야 실행된다")]
            string CreatePromptVersion(
                [Description("text, image, multimodal 중 하나")] string module,
                [Description("프롬프트 이름")] string name,
                [Description("새 버전의 전체 내용")] string content,
                [Description("바꾼 이유 한 줄")] string note)
            {
                // 스파이크는 DB 를 바꾸지 않음 (dry-run): 실행됐다는 사실과 인자만 기록
                writes.Add($"{module}/{name} ({content.Length}자, {note})");
                return """{"saved":true,"dry_run":true,"version":"(dry-run)"}""";
            }

            var guard = new ReasoningFallbackChatClient(new OllamaApiClient(ollamaHttp, model));
            IList<AITool> agentTools = [.. tools.All(),
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreatePromptVersion, "create_prompt_version"))];
            var agent = Create(guard, agentTools, think: false);
            var session = await agent.CreateSessionAsync();

            Console.WriteLine($"\n===== {model} · 승인 흐름 ({(approve ? "승인" : "거절")})");
            var first = await agent.RunAsync(
                "text 모듈 classify.system 프롬프트 끝에 '확실하지 않으면 other 로 답하세요.' 한 줄을 추가한 새 버전을 저장해줘",
                session);
            Print(first);

            var requests = first.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
            Console.WriteLine($"  승인 요청 {requests.Count}건, 이 시점 실제 실행 {writes.Count}건");
            if (requests.Count == 0) continue;

            var responses = requests.Select(r => (AIContent)r.CreateResponse(approve, approve ? null : "사람이 거절함")).ToList();
            var second = await agent.RunAsync(new ChatMessage(ChatRole.User, responses), session);
            Print(second);
            Console.WriteLine($"  실제 실행 {writes.Count}건: {string.Join(" | ", writes)}");
        }

        static void Print(AgentResponse r)
        {
            foreach (var c in r.Messages.SelectMany(m => m.Contents))
            {
                var text = c switch
                {
                    ToolApprovalRequestContent a when a.ToolCall is FunctionCallContent f =>
                        $"승인 요청: {f.Name}({string.Join(", ", f.Arguments?.Select(x => $"{x.Key}={Cut(x.Value?.ToString())}") ?? [])})",
                    FunctionCallContent f => $"호출: {f.Name}",
                    FunctionResultContent f => $"결과: {Cut(f.Result?.ToString())}",
                    TextContent t => $"답: {Cut(t.Text, 300)}",
                    _ => null,
                };
                if (text is not null) Console.WriteLine($"  {text.ReplaceLineEndings(" ")}");
            }
        }

        static string Cut(string? s, int max = 80) => s is null ? "" : s.Length > max ? s[..max] + "…" : s;
    }
}
