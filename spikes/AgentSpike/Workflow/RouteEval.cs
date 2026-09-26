using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentSpike.Workflow;

/// <summary>3차-2 1단계: 라우터만 따로 측정 (과제의 intent 라벨과 비교, 회차 간 일치)</summary>
public static class RouteEval
{
    public static async Task RunAsync(Router router, string tasksPath, string outPath, string[] models, int reps)
    {
        var tasks = JsonNode.Parse(await File.ReadAllTextAsync(tasksPath))!["tasks"]!.AsArray().Select(t => t!).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        await File.WriteAllTextAsync(outPath, "");

        foreach (var model in models)
        {
            var results = new Dictionary<string, List<(string? Intent, string Raw, double Sec)>>();
            for (var rep = 1; rep <= reps; rep++)
            {
                foreach (var task in tasks)
                {
                    var id = task["id"]!.GetValue<string>();
                    var (route, raw, sec, error) = await router.RouteAsync(model, task["question"]!.GetValue<string>());
                    (results.TryGetValue(id, out var list) ? list : results[id] = []).Add((route?.Intent, raw, sec));
                    var record = new JsonObject
                    {
                        ["model"] = model, ["task_id"] = id, ["rep"] = rep, ["sec"] = sec, ["error"] = error,
                        ["route"] = route is null ? null : JsonSerializer.SerializeToNode(route, ApiTools.Json),
                    };
                    await File.AppendAllTextAsync(outPath, record.ToJsonString(ApiTools.Json) + "\n");
                }
            }

            int correct = 0, total = 0, stable = 0;
            var wrong = new List<string>();
            foreach (var task in tasks)
            {
                var id = task["id"]!.GetValue<string>();
                var expected = Expected(task);
                var got = results[id];
                total += got.Count;
                correct += got.Count(g => g.Intent is not null && expected.Contains(g.Intent));
                if (got.All(g => g.Intent == got[0].Intent)) stable++;
                foreach (var g in got.Where(g => g.Intent is null || !expected.Contains(g.Intent)))
                    wrong.Add($"{id} 정답 {string.Join("/", expected)} ← {g.Intent ?? "(해석 실패)"}");
            }
            var secs = results.Values.SelectMany(v => v).Select(v => v.Sec).Order().ToList();
            Console.WriteLine($"\n== {model}: 의도 정확도 {correct}/{total} ({100.0 * correct / total:0}%), " +
                $"회차 간 일치 {stable}/{tasks.Count}, 시간 중앙값 {secs[secs.Count / 2]}초");
            foreach (var w in wrong.Distinct()) Console.WriteLine($"  {w} ×{wrong.Count(x => x == w)}");
        }
    }

    public static string[] Expected(JsonNode task) => task["intent"] switch
    {
        JsonArray a => a.Select(x => x!.GetValue<string>()).ToArray(),
        var v => [v!.GetValue<string>()],
    };
}
