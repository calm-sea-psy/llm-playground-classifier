using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Workflows;

namespace AgentSpike.Workflow;

/// <summary>워크플로 한 번 실행 동안 단계 사이에 넘기는 상태</summary>
public sealed class WfState
{
    public required string Question { get; init; }
    public required string Model { get; init; }
    public required Recorder Rec { get; init; }
    public RouteResult? Route { get; set; }
    public string? RouteError { get; set; }
    public ExperimentRef? Experiment { get; set; }
    public List<ExperimentRef> Candidates { get; set; } = [];
    public JsonObject Facts { get; set; } = [];
    public string Answer { get; set; } = "";
    public List<string> Ungrounded { get; set; } = [];
    public bool UsedTemplate { get; set; }
    public int LlmCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

/// <summary>
/// 3차-2: 순서는 코드(Agent Framework 워크플로 그래프), LLM 은 라우터와 요약만.
///   route(LLM) ➔ resolve(코드) ➔ switch(intent / 모호함) ➔ 실행기(코드) ➔ summarize(LLM) ➔ verify(코드) ➔ 출력
/// 실험 참조가 모호하면 되묻기 출력으로 끝냄 (화면이 있으면 RequestPort 로 사람 선택을 받을 자리)
/// </summary>
public sealed class AnalysisWorkflow(HttpClient ollamaHttp, ApiTools tools) : IAgentRunner
{
    private static readonly string[] NeedsExperiment = ["experiment_summary", "doc_query", "compare_combos"];

    public Microsoft.Agents.AI.Workflows.Workflow Build()
    {
        var route = Bind("route", async (WfState s, CancellationToken ct) =>
        {
            var (r, _, _, error) = await new Router(ollamaHttp).RouteAsync(s.Model, s.Question, ct);
            s.LlmCalls++;
            s.Route = r;
            s.RouteError = error;
            return s;
        });

        var resolve = Bind("resolve", async (WfState s, CancellationToken ct) =>
        {
            if (s.Route is { } r && NeedsExperiment.Contains(r.Intent))
            {
                var res = await Resolver.Resolve(s.Rec, r.Experiment, r.Module);
                s.Experiment = res.Experiment;
                s.Candidates = res.Candidates;
                if (res.Error is not null) s.Facts = Executors.Error(r.Intent, res.Error);
            }
            return s;
        });

        ExecutorBinding Exec(string intent, Func<WfState, Task<JsonObject>> run) => Bind(intent, async (WfState s, CancellationToken ct) =>
        {
            s.Facts = await run(s);
            return s;
        });

        var list = Exec("list_experiments", s => Executors.ListExperiments(s.Rec, s.Route!));
        var summary = Exec("experiment_summary", s => Executors.ExperimentSummary(s.Rec, s.Route!, s.Experiment!));
        var docs = Exec("doc_query", s => Executors.DocQuery(s.Rec, s.Route!, s.Experiment!));
        var grounding = Exec("grounding_check", s => Executors.GroundingCheck(s.Rec, s.Route!));
        var job = Exec("job_detail", s => Executors.JobDetail(s.Rec, s.Route!));
        var compare = Exec("compare_combos", s => Executors.CompareCombos(s.Rec, s.Route!, s.Experiment!));
        var prompt = Exec("prompt_info", s => Executors.PromptInfo(s.Rec, s.Route!));
        var unsupported = Exec("unsupported", s => Task.FromResult(s.Route is null
            ? Executors.Error("unknown", $"질문을 해석하지 못했습니다 ({s.RouteError})")
            : Executors.Unsupported("도구로 확인할 수 없는 질문이거나(예측 · 도구에 없는 데이터) 쓰기 요청(실험 실행 · 설정 적용)입니다")));

        var summarize = Bind("summarize", async (WfState s, CancellationToken ct) =>
        {
            var (answer, input, output) = await Summarizer.WriteAsync(ollamaHttp, s.Model, s.Question, s.Facts, ct);
            s.LlmCalls++;
            s.InputTokens += input ?? 0;
            s.OutputTokens += output ?? 0;
            s.Answer = answer;
            return s;
        });

        var verify = BindSync("verify", s =>
        {
            s.Ungrounded = Summarizer.Ungrounded(s.Answer, s.Question, s.Facts);
            if (s.Ungrounded.Count > 0 || string.IsNullOrWhiteSpace(s.Answer))
            {
                s.UsedTemplate = true;
                s.Answer = Summarizer.Template(s.Facts);
            }
            return s;
        });

        // 모호한 실험 참조 ➔ 되묻기 (요약 LLM 을 거치지 않음)
        var clarify = BindSync("clarify", s =>
        {
            s.Facts = new JsonObject
            {
                ["intent"] = "clarify",
                ["candidates"] = new JsonArray([.. s.Candidates.Select(c => new JsonObject { ["id"] = c.Id, ["module"] = c.Module, ["name"] = c.Name })]),
            };
            s.Answer = "어느 실험을 말씀하시는지 골라 주세요:\n" + string.Join("\n", s.Candidates.Select((c, i) => $"{i + 1}. {c.Name} ({c.Module}, {c.Id})"));
            return s;
        });

        bool Is(WfState s, string intent) => s.Route?.Intent == intent && s.Facts.Count == 0 && !IsAmbiguous(s);
        static bool IsAmbiguous(WfState s) => s.Route is { } r && NeedsExperiment.Contains(r.Intent) && s.Experiment is null && s.Facts.Count == 0;

        var builder = new WorkflowBuilder(route)
            .AddEdge(route, resolve)
            .AddSwitch(resolve, sw => sw
                .AddCase<WfState>(s => s!.Facts.Count > 0, [summarize])          // 참조 해석 오류 (없는 실험 등)
                .AddCase<WfState>(s => IsAmbiguous(s!), [clarify])
                .AddCase<WfState>(s => Is(s!, "list_experiments"), [list])
                .AddCase<WfState>(s => Is(s!, "experiment_summary"), [summary])
                .AddCase<WfState>(s => Is(s!, "doc_query"), [docs])
                .AddCase<WfState>(s => Is(s!, "grounding_check"), [grounding])
                .AddCase<WfState>(s => Is(s!, "job_detail"), [job])
                .AddCase<WfState>(s => Is(s!, "compare_combos"), [compare])
                .AddCase<WfState>(s => Is(s!, "prompt_info"), [prompt])
                .WithDefault([unsupported]));
        foreach (var e in new[] { list, summary, docs, grounding, job, compare, prompt, unsupported })
        {
            builder.AddEdge(e, summarize);
        }
        builder.AddEdge(summarize, verify);
        return builder.WithOutputFrom(verify, clarify).WithName("experiment-analysis").Build();
    }

    public async Task<AgentTrace> RunAsync(string model, string question, bool think = false, CancellationToken ct = default)
    {
        var state = new WfState { Question = question, Model = model, Rec = new Recorder(tools) };
        var sw = Stopwatch.StartNew();
        string? error = null;
        try
        {
            await using var run = await InProcessExecution.RunAsync(Build(), state, cancellationToken: ct);
            foreach (var e in run.OutgoingEvents)
            {
                if (e is WorkflowErrorEvent err) error = err.Exception?.Message ?? "워크플로 오류";
                if (e is ExecutorFailedEvent failed) error = $"{failed.ExecutorId}: {failed.Data}";
            }
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
        }

        var extra = new JsonObject
        {
            ["route"] = state.Route is null ? null : JsonSerializer.SerializeToNode(state.Route, ApiTools.Json),
            ["experiment"] = state.Experiment?.Id,
            ["candidates"] = state.Candidates.Count,
            ["ungrounded"] = new JsonArray([.. state.Ungrounded.Select(u => (JsonNode)JsonValue.Create(u)!)]),
            ["used_template"] = state.UsedTemplate,
        };
        return new AgentTrace(model, false, question, state.Rec.Steps, state.Answer,
            state.LlmCalls, state.InputTokens, state.OutputTokens, Math.Round(sw.Elapsed.TotalSeconds, 1), 0, 0, error, extra);
    }

    private static ExecutorBinding Bind(string id, Func<WfState, CancellationToken, Task<WfState>> f) =>
        ((Func<WfState, IWorkflowContext, CancellationToken, ValueTask<WfState>>)(async (s, _, ct) => await f(s, ct))).BindAsExecutor(id);

    private static ExecutorBinding BindSync(string id, Func<WfState, WfState> f) =>
        ((Func<WfState, IWorkflowContext, CancellationToken, ValueTask<WfState>>)((s, _, _) => ValueTask.FromResult(f(s)))).BindAsExecutor(id);
}
