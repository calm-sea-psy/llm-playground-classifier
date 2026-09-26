using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentSpike.Workflow;

/// <summary>도구 호출을 스파이크와 같은 형식(ToolStep)으로 남김 ➔ eval/agent_eval.py 를 그대로 씀</summary>
public sealed class Recorder(ApiTools tools)
{
    public List<ToolStep> Steps { get; } = [];
    public ApiTools Tools => tools;

    public async Task<JsonNode> Call(string name, Dictionary<string, object?> args, Func<Task<string>> call)
    {
        var result = await call();
        Steps.Add(new ToolStep(name, args.ToDictionary(a => a.Key, a => (object?)a.Value?.ToString()), result));
        return JsonNode.Parse(result)!;
    }

    public Task<JsonNode> ListExperiments(string module) =>
        Call("list_experiments", new() { ["module"] = module }, () => tools.ListExperiments(module));
    public Task<JsonNode> GetExperiment(string id) =>
        Call("get_experiment", new() { ["experiment_id"] = id }, () => tools.GetExperiment(id));
    public Task<JsonNode> ListDocs(string id, int combo, string filter) =>
        Call("list_experiment_docs", new() { ["experiment_id"] = id, ["combo_index"] = combo, ["filter"] = filter },
            () => tools.ListExperimentDocs(id, combo, filter));
    public Task<JsonNode> GetResult(string jobId) =>
        Call("get_extraction_result", new() { ["job_id"] = jobId }, () => tools.GetExtractionResult(jobId));
    public Task<JsonNode> GetOcr(string jobId, int maxLines) =>
        Call("get_ocr_text", new() { ["job_id"] = jobId, ["max_lines"] = maxLines }, () => tools.GetOcrText(jobId, maxLines));
    public Task<JsonNode> FindInOcr(string jobId, string value) =>
        Call("find_in_ocr", new() { ["job_id"] = jobId, ["value"] = value }, () => tools.FindInOcr(jobId, value));
    public Task<JsonNode> GetPrompt(string module, string name) =>
        Call("get_prompt", new() { ["module"] = module, ["name"] = name }, () => tools.GetPrompt(module, name));
}

public sealed record ExperimentRef(string Id, string Module, string Name, string CreatedAt);

/// <summary>해석 결과: 하나로 정해짐 / 후보 여럿(사람에게 물음) / 없음</summary>
public sealed record Resolution(ExperimentRef? Experiment, List<ExperimentRef> Candidates, string? Error);

/// <summary>
/// 실험 참조 해석 (코드, LLM 없음). 라우터는 질문에 적힌 이름 조각만 넘기고, id 는 여기서 목록에서 찾는다.
/// 이름 조각의 토큰이 **모두** 실험 이름에 있어야 후보. 여러 개면 모호 ➔ 되묻기
/// (예: "KORIE 150장 전체 평가" 는 "…KORIE 검증 통과…43장" 과 KORIE 하나만 겹쳐 후보가 아님)
/// </summary>
public static partial class Resolver
{
    private static readonly HashSet<string> StopWords = ["실험", "의", "에서", "the", "조합", "결과", "x-ray", "문서", "추출"];

    public static async Task<List<ExperimentRef>> AllExperiments(Recorder rec, string? module)
    {
        var modules = module is "text" or "image" ? [module] : new[] { "text", "image" };
        var all = new List<ExperimentRef>();
        foreach (var m in modules)
        {
            var list = await rec.ListExperiments(m);
            foreach (var e in list["experiments"]!.AsArray())
            {
                all.Add(new ExperimentRef(e!["id"]!.GetValue<string>(), m, e["name"]!.GetValue<string>(),
                    e["created_at"]?.GetValue<string>() ?? ""));
            }
        }
        return all;
    }

    public static async Task<Resolution> Resolve(Recorder rec, string? reference, string? module)
    {
        if (module == "multimodal") return new(null, [], "X-ray + 소견서 통합(multimodal) 모듈은 모델 비교 실험이 없습니다 (실험은 text · image 만)");
        var all = await AllExperiments(rec, module);
        if (string.IsNullOrWhiteSpace(reference))
            return all.Count == 1 ? new(all[0], [], null) : new(null, all, null);

        var r = reference.Trim();
        if (r.Equals("latest", StringComparison.OrdinalIgnoreCase) || r.Contains("최근"))
        {
            // 목록은 모듈별 최신순, created_at 은 날짜만이라 같은 날이면 id(UUIDv7, 시간순) 로
            var latest = all.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id, StringComparer.Ordinal).First();
            return new(latest, [], null);
        }
        if (GuidRegex().Match(r) is { Success: true } g)
        {
            var hit = all.FirstOrDefault(e => e.Id.Equals(g.Value, StringComparison.OrdinalIgnoreCase));
            return hit is null ? new(null, [], $"실험을 찾을 수 없습니다: {g.Value}") : new(hit, [], null);
        }

        var tokens = TokenRegex().Split(r.ToLowerInvariant())
            .Select(t => t.Trim()).Where(t => t.Length > 0 && !StopWords.Contains(t)).ToList();
        if (tokens.Count == 0) return new(null, all, null);
        var matches = all.Where(e => tokens.All(t => Norm(e.Name).Contains(Norm(t)))).ToList();
        return matches.Count switch
        {
            1 => new(matches[0], [], null),
            0 => new(null, [], $"'{reference}' 에 해당하는 실험을 찾을 수 없습니다"),
            _ => new(null, matches, null),
        };
    }

    private static string Norm(string s) => NonWordRegex().Replace(s.ToLowerInvariant(), "");

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();
    [GeneratedRegex(@"[\s·,()×/]+")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"[^\p{L}\p{N}]")]
    private static partial Regex NonWordRegex();
}
