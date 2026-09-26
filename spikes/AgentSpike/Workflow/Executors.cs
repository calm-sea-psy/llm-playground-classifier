using System.Text.Json.Nodes;

namespace AgentSpike.Workflow;

/// <summary>
/// 의도별 실행기 (코드, LLM 없음). 조회·반복·계산을 모두 여기서 끝내고 사실(facts JSON)만 넘긴다.
/// 이웃 의도와 겹치는 사실도 넉넉히 넣음 (라우터가 experiment_summary ↔ doc_query 를 헷갈려도 답할 수 있게)
/// </summary>
public static class Executors
{
    private const int ReasonLimit = 10;

    public static readonly string[] TextFilters = ["failed", "fallback", "passed_but_wrong"];
    public static readonly string[] ImageFilters = ["failed", "wrong", "disagree"];

    // 필드 이름 ➔ find_in_ocr 에 넘길 추출 값
    private static readonly string[] GroundableFields = ["date", "time", "total", "business_no", "store_name"];

    public static async Task<JsonObject> ListExperiments(Recorder rec, RouteResult route)
    {
        if (route.Module == "multimodal")
            return Unsupported("X-ray + 소견서 통합(multimodal) 모듈은 모델 비교 실험이 없습니다 (실험은 text 문서 추출 · image 흉부 X-ray 만)");
        var modules = route.Module is "text" or "image" ? [route.Module] : new[] { "text", "image" };
        var facts = new JsonObject { ["intent"] = "list_experiments" };
        foreach (var m in modules)
        {
            var list = (await rec.ListExperiments(m))["experiments"]!.AsArray();
            facts[m] = new JsonObject
            {
                ["count"] = list.Count,
                ["experiments"] = list.DeepClone(),
                ["largest"] = list.OrderByDescending(e => e!["docs"]!.GetValue<int>()).First()!.DeepClone(),
            };
        }
        return facts;
    }

    public static async Task<JsonObject> ExperimentSummary(Recorder rec, RouteResult route, ExperimentRef exp)
    {
        var detail = await rec.GetExperiment(exp.Id);
        var combos = detail["combos"]!.AsArray();
        var facts = new JsonObject
        {
            ["intent"] = "experiment_summary",
            ["experiment"] = Describe(exp, detail),
            ["metric_notes"] = detail["metric_notes"]!.DeepClone(),
            ["combos"] = new JsonArray([.. combos.Select(c => WithPercents(c!.AsObject()))]),
        };
        if (route.Metric is { } metric && MetricKey(metric, exp.Module) is var key && key is null && ModuleOfMetric(metric) is { } other && other != exp.Module)
        {
            facts["metric_not_available"] = $"'{metric}' 는 {(other == "image" ? "흉부 X-ray(image)" : "문서 추출(text)")} 실험의 지표라 이 {(exp.Module == "text" ? "문서 추출" : "X-ray")} 실험에는 없습니다";
        }
        var ranked = combos.OrderBy(c => c!["rank"]!.GetValue<int>()).ToList();
        if (ranked.Count >= 2)
        {
            var gap = ranked[0]!["score"]!.GetValue<double>() - ranked[1]!["score"]!.GetValue<double>();
            facts["top_gap"] = new JsonObject { ["first"] = ranked[0]!["index"]!.GetValue<int>(), ["second"] = ranked[1]!["index"]!.GetValue<int>(), ["score_gap"] = Math.Round(gap, 3) };
        }
        facts["doc_counts"] = await DocCounts(rec, exp, combos.Count);
        return facts;
    }

    public static async Task<JsonObject> DocQuery(Recorder rec, RouteResult route, ExperimentRef exp)
    {
        var detail = await rec.GetExperiment(exp.Id);
        var comboCount = detail["combos"]!.AsArray().Count;
        var facts = new JsonObject { ["intent"] = "doc_query", ["experiment"] = Describe(exp, detail) };
        var allowed = exp.Module == "text" ? TextFilters : ImageFilters;
        var filter = route.Filter ?? "all";
        if (filter != "all" && !allowed.Contains(filter))
        {
            facts["error"] = $"{(exp.Module == "text" ? "문서 추출" : "X-ray")} 실험에는 '{filter}' 조건이 없습니다 (가능: {string.Join(", ", allowed)})" +
                (filter is "wrong" or "disagree" ? " — CNN 판정은 X-ray 실험에만 있습니다" : "");
            return facts;
        }
        if (route.Combo is { } c && (c < 0 || c >= comboCount))
        {
            facts["error"] = $"{c}번 조합은 없습니다. 이 실험의 조합은 0~{comboCount - 1} ({comboCount}개) 입니다";
            return facts;
        }

        var combos = route.Combo is { } one ? [one] : Enumerable.Range(0, comboCount).ToArray();
        var perCombo = new JsonArray();
        foreach (var combo in combos)
        {
            var list = await rec.ListDocs(exp.Id, combo, filter);
            var docs = list["docs"]!.AsArray();
            var entry = new JsonObject
            {
                ["combo"] = combo,
                ["summary"] = detail["combos"]![combo]!["summary"]!.GetValue<string>(),
                ["filter"] = filter,
                ["count"] = list["count"]!.DeepClone(),
                ["docs"] = docs.DeepClone(),
            };

            // 스파이크 M02·M09: 문서마다 이유 조회를 LLM 이 건너뜀 ➔ 실패·폴백 목록이 작으면 항상 코드가 조회
            if (exp.Module == "text" && filter is "failed" or "fallback" && docs.Count <= ReasonLimit)
            {
                var reasons = new JsonArray();
                foreach (var d in docs)
                {
                    var r = await rec.GetResult(d!["job_id"]!.GetValue<string>());
                    reasons.Add(Reason(d, r));
                }
                entry["reasons"] = reasons;
            }

            // "n번째 문서의 값이 원문에 있는지" ➔ 추출 결과 + 필드마다 find_in_ocr
            if (route.Nth is { } nth && exp.Module == "text")
            {
                if (nth < 1 || nth > docs.Count)
                {
                    entry["nth_error"] = $"{nth}번째 문서가 없습니다 (목록 {docs.Count}개)";
                }
                else
                {
                    var doc = docs[nth - 1]!;
                    entry["nth"] = nth;
                    entry["nth_doc"] = await Grounding(rec, doc["job_id"]!.GetValue<string>(), route.Fields);
                }
            }
            perCombo.Add(entry);
        }
        facts["results"] = perCombo;
        return facts;
    }

    public static async Task<JsonObject> GroundingCheck(Recorder rec, RouteResult route)
    {
        if (route.JobId is not { } jobId) return Error("grounding_check", "작업 id 가 없습니다");
        var facts = new JsonObject { ["intent"] = "grounding_check" };
        facts["job"] = await Grounding(rec, jobId, route.Fields);
        return facts;
    }

    public static async Task<JsonObject> JobDetail(Recorder rec, RouteResult route)
    {
        if (route.JobId is not { } jobId) return Error("job_detail", "작업 id 가 없습니다");
        var facts = new JsonObject { ["intent"] = "job_detail" };
        var result = await rec.GetResult(jobId);
        facts["result"] = result.DeepClone();
        if (result["error"] is null)
        {
            var ocr = await rec.GetOcr(jobId, 1);
            facts["ocr_avg_confidence"] = ocr["avg_confidence"]?.DeepClone();
            facts["ocr_line_count"] = ocr["line_count"]?.DeepClone();
        }
        return facts;
    }

    public static async Task<JsonObject> CompareCombos(Recorder rec, RouteResult route, ExperimentRef exp)
    {
        var detail = await rec.GetExperiment(exp.Id);
        var combos = detail["combos"]!.AsArray();
        var facts = new JsonObject { ["intent"] = "compare_combos", ["experiment"] = Describe(exp, detail) };
        var key = route.Metric is { } m ? MetricKey(m, exp.Module) : null;
        var keys = key is null
            ? combos[0]!.AsObject().Where(p => p.Value is JsonValue v && v.TryGetValue<double>(out var d) && d is >= 0 and <= 1 && p.Key != "index").Select(p => p.Key).ToList()
            : [key];
        if (route.Metric is not null && key is null) facts["metric_note"] = $"'{route.Metric}' 에 맞는 지표를 찾지 못해 비율 지표를 모두 비교";

        var comparisons = new JsonArray();
        foreach (var k in keys)
        {
            var values = combos.Select(c => (Index: c!["index"]!.GetValue<int>(), Summary: c["summary"]!.GetValue<string>(), Value: c[k]?.GetValue<double>())).ToList();
            var best = values.Where(v => v.Value is not null).OrderByDescending(v => v.Value).ToList();
            var cmp = new JsonObject
            {
                ["metric"] = k,
                ["metric_name"] = detail["metric_notes"]?[k]?.GetValue<string>() ?? k,
                ["values"] = new JsonArray([.. values.Select(v => new JsonObject { ["combo"] = v.Index, ["summary"] = v.Summary, ["value"] = Round(v.Value), ["percent"] = Round(v.Value * 100, 1) })]),
            };
            if (best.Count >= 2)
            {
                cmp["higher_combo"] = best[0].Index;
                cmp["higher_summary"] = best[0].Summary;
                cmp["difference"] = Round(best[0].Value - best[1].Value);
                cmp["difference_percent_points"] = Round((best[0].Value - best[1].Value) * 100, 1);
            }
            comparisons.Add(cmp);
        }
        facts["comparisons"] = comparisons;
        return facts;
    }

    public static async Task<JsonObject> PromptInfo(Recorder rec, RouteResult route)
    {
        var module = route.Module ?? "text";
        var facts = new JsonObject { ["intent"] = "prompt_info", ["module"] = module };
        var names = await rec.GetPrompt(module, "");
        facts["names"] = names["names"]?.DeepClone();
        if (!string.IsNullOrWhiteSpace(route.PromptName))
        {
            facts["prompt"] = (await rec.GetPrompt(module, route.PromptName)).DeepClone();
        }
        return facts;
    }

    public static JsonObject Unsupported(string reason) => new()
    {
        ["intent"] = "unsupported",
        ["reason"] = reason,
        ["can_do"] = "실험 목록 · 실험 요약 · 조건별 문서와 이유 · 추출 값의 원문 근거 확인 · 작업 상세 · 조합 비교 · 프롬프트 조회 (읽기 전용, 실험 실행·설정 적용은 화면에서)",
    };

    public static JsonObject Error(string intent, string message) => new() { ["intent"] = intent, ["error"] = message };

    private static async Task<JsonObject> Grounding(Recorder rec, string jobId, List<string>? fields)
    {
        var result = await rec.GetResult(jobId);
        if (result["error"] is not null) return result.DeepClone().AsObject();
        var extracted = result["fields"]!.AsObject();
        var wanted = (fields ?? []).Where(GroundableFields.Contains).ToList();
        if (wanted.Count == 0) wanted = ["date", "total"];
        var checks = new JsonArray();
        foreach (var f in wanted)
        {
            var value = extracted[f]?.ToString();
            if (string.IsNullOrEmpty(value))
            {
                checks.Add(new JsonObject { ["field"] = f, ["value"] = null, ["found"] = null, ["note"] = "추출된 값 없음" });
                continue;
            }
            var hit = await rec.FindInOcr(jobId, value);
            checks.Add(new JsonObject { ["field"] = f, ["value"] = value, ["found"] = hit["found"]?.DeepClone(), ["matches"] = hit["matches"]?.DeepClone() });
        }
        return new JsonObject { ["job_id"] = jobId, ["file"] = result["file"]?.DeepClone(), ["checks"] = checks };
    }

    private static JsonObject Reason(JsonNode doc, JsonNode result)
    {
        var o = new JsonObject { ["file"] = doc["file"]?.DeepClone(), ["job_id"] = doc["job_id"]?.DeepClone() };
        if (result["error"] is not null)
        {
            o["status"] = result["status"]?.DeepClone();
            o["failure_reason"] = $"작업 실패: {result["job_error"]}";
            return o;
        }
        // 개발 세트 M02: 폴백 이유("검증 오류 1건")를 실패 이유로 옮김 ➔ 실패 이유는 코드가 검증 오류 메시지로 채워 따로 둠
        var errors = result["issues"]!.AsArray().Where(i => i!["severity"]?.GetValue<string>() == "Error").Select(i => i!["message"]!.GetValue<string>()).ToList();
        if (result["validation_passed"]?.GetValue<bool>() == false)
            o["failure_reason"] = errors.Count > 0 ? $"검증 실패: {string.Join(" / ", errors)}" : "검증 실패";
        o["validation_passed"] = result["validation_passed"]?.DeepClone();
        o["fallback_reason"] = result["fallback_reason"]?.DeepClone();
        o["final_source"] = result["final_source"]?.DeepClone();
        o["errors"] = new JsonArray([.. errors.Select(e => (JsonNode)JsonValue.Create(e)!)]);
        o["warnings"] = new JsonArray([.. result["issues"]!.AsArray().Where(i => i!["severity"]?.GetValue<string>() == "Warning").Select(i => (JsonNode)JsonValue.Create(i!["message"]!.GetValue<string>())!)]);
        return o;
    }

    private static async Task<JsonArray> DocCounts(Recorder rec, ExperimentRef exp, int comboCount)
    {
        var filters = exp.Module == "text" ? TextFilters : ImageFilters;
        var counts = new JsonArray();
        for (var c = 0; c < comboCount; c++)
        {
            var o = new JsonObject { ["combo"] = c };
            foreach (var f in filters) o[f] = (await rec.ListDocs(exp.Id, c, f))["count"]!.DeepClone();
            counts.Add(o);
        }
        return counts;
    }

    private static JsonObject Describe(ExperimentRef exp, JsonNode detail) => new()
    {
        ["id"] = exp.Id,
        ["module"] = exp.Module,
        ["name"] = exp.Name,
        ["docs"] = detail["docs"]?.DeepClone(),
        ["combo_count"] = detail["combos"]!.AsArray().Count,
        ["recommended_index"] = detail["recommended_index"]?.DeepClone(),
    };

    /// <summary>0~1 비율 지표 옆에 % 값을 붙임 (요약 LLM 이 환산하다 틀리지 않게)</summary>
    private static JsonObject WithPercents(JsonObject combo)
    {
        var o = combo.DeepClone().AsObject();
        foreach (var (k, v) in combo)
        {
            if (k is "score" or "index" or "rank") continue;
            if (v is JsonValue jv && jv.TryGetValue<double>(out var d) && d is > 0 and < 1) o[$"{k}_percent"] = Math.Round(d * 100, 1);
        }
        return o;
    }

    private static readonly (string Key, string Module, string[] Words)[] Metrics =
    [
        ("fieldAccuracy", "text", ["필드 정확도", "필드정확도", "정확도"]),
        ("amountAccuracy", "text", ["합계 일치", "합계", "금액"]),
        ("passRate", "text", ["통과율", "검증 통과"]),
        ("fallbackRate", "text", ["폴백"]),
        ("medianJobSec", "text", ["시간", "처리 시간"]),
        ("cnnAuc", "image", ["auc"]),
        ("cnnSensitivity", "image", ["cnn 민감도"]),
        ("cnnSpecificity", "image", ["cnn 특이도"]),
        ("vlmSuccessRate", "image", ["판독 성공률", "성공률"]),
        ("vlmSensitivity", "image", ["vlm 민감도", "민감도"]),
        ("vlmSpecificity", "image", ["vlm 특이도", "특이도"]),
        ("agreementRate", "image", ["일치율"]),
        ("score", "any", ["점수"]),
    ];

    private static string? MetricKey(string metric, string module)
    {
        var m = metric.ToLowerInvariant();
        return Metrics.Where(x => x.Module == module || x.Module == "any")
            .FirstOrDefault(x => x.Words.Any(w => m.Contains(w))).Key;
    }

    private static string? ModuleOfMetric(string metric)
    {
        var m = metric.ToLowerInvariant();
        return Metrics.FirstOrDefault(x => x.Module != "any" && x.Words.Any(w => m.Contains(w))).Module;
    }

    private static double? Round(double? v, int digits = 3) => v is null ? null : Math.Round(v.Value, digits);
}
