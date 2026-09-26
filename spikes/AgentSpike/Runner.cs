using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSpike;

/// <summary>
/// 3단계: 과제 × 설정 × 반복을 돌려 기록을 JSONL 로 쌓음. 이미 있는 (설정, 과제, 회차)는 건너뛰어 중간에 멈춰도 이어서 돈다.
/// 모델을 바깥 루프에 둬서 Ollama 모델 교체(VRAM 재적재)를 설정당 한 번으로.
/// </summary>
public static class Runner
{
    /// <summary>"모델", "모델+think", 끝에 "@af" 면 Agent Framework 에이전트, "@wf" 면 3차-2 워크플로 (예: gemma4:12b@wf)</summary>
    public static (string Model, bool Think, string Kind) ParseConfig(string config)
    {
        var kind = "direct";
        foreach (var k in new[] { "af", "wf" })
        {
            if (config.EndsWith("@" + k)) { kind = k; config = config[..^(k.Length + 1)]; }
        }
        var think = config.EndsWith("+think");
        if (think) config = config[..^"+think".Length];
        return (config, think, kind);
    }

    public static async Task RunAsync(IReadOnlyDictionary<string, IAgentRunner> agents, string tasksPath, string outPath, string[] configs, int reps, string[]? only)
    {
        var tasks = JsonNode.Parse(await File.ReadAllTextAsync(tasksPath))!["tasks"]!.AsArray()
            .Where(t => only is null || only.Contains(t!["id"]!.GetValue<string>()))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        var done = new HashSet<string>();
        if (File.Exists(outPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(outPath))
            {
                if (line.Length == 0) continue;
                var r = JsonNode.Parse(line)!;
                done.Add($"{r["config"]}|{r["task_id"]}|{r["rep"]}");
            }
        }

        var total = configs.Length * reps * tasks.Count;
        var count = 0;
        foreach (var config in configs)
        {
            var (model, think, kind) = ParseConfig(config);
            var agent = agents[kind];
            for (var rep = 1; rep <= reps; rep++)
            {
                foreach (var task in tasks)
                {
                    count++;
                    var id = task!["id"]!.GetValue<string>();
                    if (!done.Add($"{config}|{id}|{rep}")) continue;

                    var trace = await agent.RunAsync(model, task["question"]!.GetValue<string>(), think);
                    var record = new JsonObject
                    {
                        ["config"] = config,
                        ["task_id"] = id,
                        ["rep"] = rep,
                        ["at"] = DateTimeOffset.Now.ToString("O"),
                        ["trace"] = JsonSerializer.SerializeToNode(trace, ApiTools.Json),
                    };
                    await File.AppendAllTextAsync(outPath, record.ToJsonString(ApiTools.Json) + "\n");
                    Console.WriteLine($"[{count}/{total}] {config} r{rep} {id} · 도구 {trace.Steps.Count} · {trace.ElapsedSec}초" +
                        (trace.Error is null ? "" : $" · 오류 {trace.Error}") +
                        (trace.LengthCutoffs > 0 ? $" · 길이 잘림 {trace.LengthCutoffs}" : ""));
                }
            }
        }
    }
}
