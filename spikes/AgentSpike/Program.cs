using System.Text;
using AgentSpike;

// 3차 스파이크 (doc/todo2.md): 로컬 모델 도구 호출 측정
//   probe [모델...]                 0단계: OllamaSharp 옵션 · 도구 자동 루프 확인
//   tools                           1단계: LLM 없이 도구 7개를 직접 불러 결과 확인
//   ask <질문> [모델] [--think]     1단계: 질문 하나를 에이전트로 풀고 도구 호출 과정 출력
//   truth                           2단계: 정답 작성용 값을 도구 코드로 계산
//   run <tasks> <out.jsonl> --configs a,b+think,c@af --reps 3 [--only S01]   3·4단계: 측정 (@af = Agent Framework)
//   approve [모델]                  4단계: 쓰기 도구 사람 승인 흐름 (dry-run)
Console.OutputEncoding = Encoding.UTF8;

var api = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000/"), Timeout = TimeSpan.FromSeconds(30) };
// AGENT_DEBUG=파일경로 ➔ Ollama 요청·응답 원문을 파일에 남김
var debugPath = Environment.GetEnvironmentVariable("AGENT_DEBUG");
HttpMessageHandler ollamaHandler = debugPath is null ? new HttpClientHandler() : new RawLogHandler(debugPath, new HttpClientHandler());
var ollama = new HttpClient(ollamaHandler) { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromMinutes(5) };
var tools = new ApiTools(api);

switch (args.FirstOrDefault())
{
    case "probe":
        await Probe.RunAsync(args[1..]);
        break;

    case "tools":
        var list = await tools.ListExperiments("text");
        Console.WriteLine($"list_experiments(text)\n{list}\n");
        var expId = System.Text.Json.Nodes.JsonNode.Parse(list)!["experiments"]![0]!["id"]!.GetValue<string>();
        Console.WriteLine($"get_experiment\n{await tools.GetExperiment(expId)}\n");
        var docs = await tools.ListExperimentDocs(expId, 0, "passed_but_wrong");
        Console.WriteLine($"list_experiment_docs(passed_but_wrong)\n{docs}\n");
        var jobId = System.Text.Json.Nodes.JsonNode.Parse(docs)!["docs"]![0]!["job_id"]!.GetValue<string>();
        Console.WriteLine($"get_extraction_result\n{await tools.GetExtractionResult(jobId)}\n");
        Console.WriteLine($"get_ocr_text\n{await tools.GetOcrText(jobId, 15)}\n");
        Console.WriteLine($"find_in_ocr(28900)\n{await tools.FindInOcr(jobId, "28900")}\n");
        Console.WriteLine($"find_in_ocr(2025-11-09)\n{await tools.FindInOcr(jobId, "2025-11-09")}\n");
        Console.WriteLine($"find_in_ocr(99999)\n{await tools.FindInOcr(jobId, "99999")}\n");
        Console.WriteLine($"get_prompt(text, '')\n{await tools.GetPrompt("text", "")}\n");
        Console.WriteLine($"get_prompt(text, classify.system)\n{await tools.GetPrompt("text", "classify.system")}\n");
        Console.WriteLine($"오류: get_experiment(없는 id)\n{await tools.GetExperiment("01a0d2ae-0000-0000-0000-000000000000")}");
        Console.WriteLine($"오류: list_experiment_docs(filter=wrong, text)\n{await tools.ListExperimentDocs(expId, 0, "wrong")}");
        Console.WriteLine($"오류: get_extraction_result(X-ray 작업)\n{await tools.GetExtractionResult("01a0d7b7-cbdc-774d-83b9-9786f4727ea4")}");
        break;

    case "find":
        // find <작업 id> <값>: 정답 작성용, find_in_ocr 를 LLM 없이
        Console.WriteLine(await tools.FindInOcr(args[1], args[2]));
        break;

    case "truth":
        await Truth.RunAsync(tools);
        break;

    case "ask":
        var question = args[1];
        var model = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--")) ?? "gemma4:12b";
        var agent = new SpikeAgent(ollama, tools);
        SpikeAgent.Print(await agent.RunAsync(model, question, think: args.Contains("--think")));
        break;

    case "run":
        // run <tasks.json> <out.jsonl> --configs a,b+think --reps 3 [--only S01,M02]
        string Opt(string name, string fallback) =>
            args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
        await Runner.RunAsync(
            new Dictionary<string, IAgentRunner>
            {
                ["direct"] = new SpikeAgent(ollama, tools),
                ["af"] = new AfAgent(ollama, tools),
                ["wf"] = new AgentSpike.Workflow.AnalysisWorkflow(ollama, tools),
            },
            args[1], args[2],
            Opt("--configs", "gemma4:12b").Split(','),
            int.Parse(Opt("--reps", "1")),
            args.Contains("--only") ? Opt("--only", "").Split(',') : null);
        break;

    case "route":
        // route <질문> [모델]: 3차-2 라우터 한 번
        var (route, raw, sec, err) = await new AgentSpike.Workflow.Router(ollama).RouteAsync(args.ElementAtOrDefault(2) ?? "gemma4:12b", args[1]);
        Console.WriteLine($"{sec}초 {err}\n{raw}");
        break;

    case "route-eval":
        // route-eval <tasks> <out.jsonl> --configs a,b --reps 3
        await AgentSpike.Workflow.RouteEval.RunAsync(new AgentSpike.Workflow.Router(ollama), args[1], args[2],
            (args.SkipWhile(a => a != "--configs").Skip(1).FirstOrDefault() ?? "gemma4:12b").Split(","),
            int.Parse(args.SkipWhile(a => a != "--reps").Skip(1).FirstOrDefault() ?? "1"));
        break;

    case "wf":
        // wf <질문> [모델]: 3차-2 워크플로 한 번
        var wfTrace = await new AgentSpike.Workflow.AnalysisWorkflow(ollama, tools).RunAsync(args.ElementAtOrDefault(2) ?? "gemma4:12b", args[1]);
        SpikeAgent.Print(wfTrace);
        Console.WriteLine($"  부가: {wfTrace.Extra?.ToJsonString(ApiTools.Json)}");
        break;

    case "approve":
        await new AfAgent(ollama, tools).ApprovalDemoAsync(args.ElementAtOrDefault(1) ?? "gemma4:12b");
        break;

    default:
        Console.WriteLine("사용법: probe [모델...] | tools | ask <질문> [모델] [--think]");
        break;
}
