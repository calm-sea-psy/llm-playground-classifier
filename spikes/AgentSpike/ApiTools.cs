using System.ComponentModel;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AgentSpike;

/// <summary>
/// 실험 분석 에이전트의 읽기 전용 도구. API(127.0.0.1:5000) GET 엔드포인트를 감싸고,
/// 모델 문맥에 맞게 필요한 필드만 줄여서 돌려준다. 오류는 예외 대신 {"error": ...} 로 돌려 모델이 읽게 한다.
/// </summary>
public sealed class ApiTools(HttpClient http)
{
    // 기본 직렬화는 한글을 \uXXXX 로 바꿔 토큰이 늘고 모델이 잘못 읽을 수 있음
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int MaxRows = 30;

    public IList<AITool> All() =>
    [
        AIFunctionFactory.Create(ListExperiments, "list_experiments"),
        AIFunctionFactory.Create(GetExperiment, "get_experiment"),
        AIFunctionFactory.Create(ListExperimentDocs, "list_experiment_docs"),
        AIFunctionFactory.Create(GetExtractionResult, "get_extraction_result"),
        AIFunctionFactory.Create(GetOcrText, "get_ocr_text"),
        AIFunctionFactory.Create(FindInOcr, "find_in_ocr"),
        AIFunctionFactory.Create(GetPrompt, "get_prompt"),
    ];

    [Description("값 하나가 작업의 OCR 원문에 실제로 있는지 코드로 확인한다. 숫자(금액·날짜·번호)는 쉼표·기호를 빼고 숫자만 비교하고, " +
        "줄바꿈으로 나뉜 값도 찾는다. 추출 값이 원문에 있는지 확인할 때는 get_ocr_text 를 직접 읽지 말고 이 도구를 쓴다")]
    public async Task<string> FindInOcr(
        [Description("작업 id")] string job_id,
        [Description("찾을 값 (예: 28900, 2025-11-09, 207-31-32674, 상호명)")] string value)
    {
        var ocr = await Get($"api/text/jobs/{job_id}/ocr");
        if (ocr is null) return Error($"OCR 결과를 찾을 수 없습니다: {job_id}");
        var lines = ocr["lines"]!.AsArray().Select(l => l!["text"]?.GetValue<string>() ?? "").ToList();

        // 1단계에서 qwen3-vl 이 "28,900" 줄을 보고 28900 이 없다고 답함 ➔ 대조는 코드로.
        // FieldValidator.CheckGrounded 와 같은 방식: 숫자 값은 줄마다 숫자만 남기고, 다음 줄과 이은 것까지 찾음
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        var numeric = digits.Length >= 2 && digits.Length * 2 >= value.Count(c => !char.IsWhiteSpace(c));
        string Key(string s) => numeric
            ? new string(s.Where(char.IsAsciiDigit).ToArray())
            : new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var needle = numeric ? digits : Key(value);
        if (needle.Length == 0) return Error("찾을 값이 비었습니다");

        var matches = new JsonArray();
        for (var i = 0; i < lines.Count; i++)
        {
            if (Key(lines[i]).Contains(needle, StringComparison.Ordinal))
            {
                matches.Add($"{i + 1}. {lines[i]}");
            }
            else if (i + 1 < lines.Count && (Key(lines[i]) + Key(lines[i + 1])).Contains(needle, StringComparison.Ordinal))
            {
                matches.Add($"{i + 1}~{i + 2}. {lines[i]} / {lines[i + 1]}");
            }
        }
        return Serialize(new JsonObject
        {
            ["job_id"] = job_id,
            ["value"] = value,
            ["compared_as"] = numeric ? $"숫자만: {needle}" : $"공백 제외 문자열: {needle}",
            ["found"] = matches.Count > 0,
            ["matches"] = matches,
        });
    }

    [Description("모델 비교 실험 목록을 최신순으로 조회한다. module 은 text(문서 추출) 또는 image(흉부 X-ray)")]
    public async Task<string> ListExperiments(
        [Description("text 또는 image")] string module = "text")
    {
        if (module is not ("text" or "image")) return Error($"module 은 text 또는 image 입니다 (받은 값: {module})");
        var list = await Get($"api/{module}/experiments?take=50");
        if (list is null) return Error("실험 목록을 불러오지 못했습니다");
        var rows = list.AsArray().Select(e => new JsonObject
        {
            ["id"] = e!["id"]?.GetValue<string>(),
            ["name"] = e["name"]?.GetValue<string>(),
            ["created_at"] = e["createdAt"]?.GetValue<string>()[..10],
            ["docs"] = e["docCount"]?.GetValue<int>(),
            ["combos"] = e["comboCount"]?.GetValue<int>(),
            ["status"] = e["status"]?.GetValue<string>(),
            ["recommended"] = e["recommended"]?.GetValue<string>(),
            ["best_score"] = Round(e["bestScore"]),
        });
        return Serialize(new JsonObject { ["module"] = module, ["experiments"] = new JsonArray([.. rows]) });
    }

    [Description("실험 하나의 설명, 점수 공식, 조합별 지표(점수·순위·정확도 등)를 조회한다")]
    public async Task<string> GetExperiment(
        [Description("실험 id (list_experiments 의 id)")] string experiment_id)
    {
        var (exp, module) = await FindExperiment(experiment_id);
        if (exp is null) return Error($"실험을 찾을 수 없습니다: {experiment_id}");
        var combos = exp["combos"]!.AsArray().Select(c =>
        {
            var o = c!.DeepClone().AsObject();
            o.Remove("settings");
            return RoundAll(o);
        });
        return Serialize(new JsonObject
        {
            ["id"] = experiment_id,
            ["module"] = module,
            ["name"] = exp["name"]?.GetValue<string>(),
            ["description"] = Cut(exp["description"]?.GetValue<string>(), 400),
            ["docs"] = exp["docCount"]?.GetValue<int>(),
            ["labeled"] = exp["labeled"]?.GetValue<bool>(),
            ["score_formula"] = exp["scoreFormula"]?.GetValue<string>(),
            ["recommended_index"] = exp["recommendedIndex"]?.DeepClone(),
            ["metric_notes"] = module == "text" ? TextMetricNotes.DeepClone() : ImageMetricNotes.DeepClone(),
            ["combos"] = new JsonArray([.. combos]),
        });
    }

    // 1단계에서 qwen3-vl 이 passedWrongTotal(합계 틀림 6)을 "필드가 틀린 문서"(passedWrongAny 40)로 읽음 ➔ 지표 설명을 같이 줌
    private static readonly JsonObject TextMetricNotes = new()
    {
        ["passRate"] = "검증 통과율",
        ["fallbackRate"] = "VLM 폴백 비율",
        ["fieldAccuracy"] = "정답 라벨과 일치한 필드 비율",
        ["amountAccuracy"] = "합계 일치 비율",
        ["passedLabeled"] = "검증 통과한 문서 중 정답 라벨이 있는 문서 수",
        ["passedWrongTotal"] = "검증 통과했지만 합계가 틀린 문서 수",
        ["passedWrongAny"] = "검증 통과했지만 필드가 하나라도 틀린 문서 수",
        ["medianJobSec"] = "작업 시간 중앙값(초)",
        ["score"] = "점수 (score_formula)",
        ["rank"] = "점수 순위 (1이 최고)",
    };

    private static readonly JsonObject ImageMetricNotes = new()
    {
        ["cnnSensitivity"] = "CNN 폐렴 민감도",
        ["cnnSpecificity"] = "CNN 폐렴 특이도",
        ["cnnAuc"] = "CNN 폐렴 AUC",
        ["vlmSensitivity"] = "VLM 폐렴 민감도",
        ["vlmSpecificity"] = "VLM 폐렴 특이도",
        ["vlmSuccessRate"] = "VLM 판독 형식 성공률",
        ["agreementRate"] = "CNN·VLM 폐렴 판단 일치율",
        ["cnnErrors"] = "CNN 폐렴 오답 수",
        ["flagRecall"] = "CNN 오답 중 불일치(사람 확인)로 걸러진 비율",
        ["score"] = "점수 (score_formula)",
        ["rank"] = "점수 순위 (1이 최고)",
    };

    [Description("실험의 문서별 결과를 조합 하나 기준으로 조회한다. 각 문서의 job_id 로 상세 결과를 볼 수 있다. " +
        "filter: all | failed(검증 실패 또는 작업 실패) | fallback(VLM 폴백 사용, 문서 추출만) | " +
        "passed_but_wrong(검증 통과했지만 정답과 다른 필드가 있음, 문서 추출만) | wrong(X-ray: CNN 판정이 정답과 다름) | disagree(X-ray: CNN·VLM 불일치)")]
    public async Task<string> ListExperimentDocs(
        [Description("실험 id")] string experiment_id,
        [Description("조합 번호 (0부터)")] int combo_index = 0,
        [Description("all, failed, fallback, passed_but_wrong, wrong, disagree 중 하나")] string filter = "all")
    {
        var (exp, module) = await FindExperiment(experiment_id);
        if (exp is null) return Error($"실험을 찾을 수 없습니다: {experiment_id}");
        var comboCount = exp["combos"]!.AsArray().Count;
        if (combo_index < 0 || combo_index >= comboCount)
            return Error($"combo_index 는 0 ~ {comboCount - 1} 입니다 (받은 값: {combo_index})");

        var textFilters = new[] { "all", "failed", "fallback", "passed_but_wrong" };
        var imageFilters = new[] { "all", "failed", "wrong", "disagree" };
        var allowed = module == "text" ? textFilters : imageFilters;
        if (!allowed.Contains(filter))
            return Error($"{module} 실험의 filter 는 {string.Join(", ", allowed)} 중 하나입니다 (받은 값: {filter})");

        var rows = new List<JsonObject>();
        foreach (var doc in exp["docs"]!.AsArray())
        {
            var cell = doc!["cells"]![combo_index]!;
            var status = cell["status"]?.GetValue<string>();
            JsonObject row;
            bool keep;
            if (module == "text")
            {
                var passed = cell["passed"]?.GetValue<bool?>();
                var correct = cell["correctFields"]?.GetValue<int?>();
                var labeled = cell["labeledFields"]?.GetValue<int?>();
                row = new JsonObject
                {
                    ["file"] = doc["fileName"]?.GetValue<string>(),
                    ["job_id"] = cell["jobId"]?.GetValue<string>(),
                    ["status"] = status,
                    ["passed"] = passed,
                    ["fallback"] = cell["fallback"]?.GetValue<bool?>(),
                    ["correct_fields"] = labeled is null ? null : $"{correct}/{labeled}",
                    ["total"] = cell["total"]?.GetValue<string>(),
                };
                keep = filter switch
                {
                    "failed" => status != "Completed" || passed == false,
                    "fallback" => cell["fallback"]?.GetValue<bool?>() == true,
                    "passed_but_wrong" => passed == true && labeled is not null && correct < labeled,
                    _ => true,
                };
            }
            else
            {
                var truth = doc["truth"]?["pneumonia"]?.GetValue<bool?>();
                var cnn = cell["cnnPositive"]?.GetValue<bool?>();
                var agree = cell["agree"]?.GetValue<bool?>();
                row = new JsonObject
                {
                    ["file"] = doc["fileName"]?.GetValue<string>(),
                    ["job_id"] = cell["jobId"]?.GetValue<string>(),
                    ["status"] = status,
                    ["truth_pneumonia"] = truth,
                    ["cnn_positive"] = cnn,
                    ["cnn_probability"] = Round(cell["cnnProbability"]),
                    ["vlm_pneumonia"] = cell["vlmPneumonia"]?.GetValue<bool?>(),
                    ["agree"] = agree,
                };
                keep = filter switch
                {
                    "failed" => status != "Completed",
                    "wrong" => truth is not null && cnn is not null && truth != cnn,
                    "disagree" => agree == false,
                    _ => true,
                };
            }
            if (keep) rows.Add(row);
        }

        return Serialize(new JsonObject
        {
            ["experiment_id"] = experiment_id,
            ["combo_index"] = combo_index,
            ["filter"] = filter,
            ["count"] = rows.Count,
            ["shown"] = Math.Min(rows.Count, MaxRows),
            ["docs"] = new JsonArray([.. rows.Take(MaxRows)]),
        });
    }

    [Description("문서 추출 작업 하나의 결과를 조회한다: 추출 필드, 검증 문제, 폴백 여부와 이유, 처리 설정")]
    public async Task<string> GetExtractionResult(
        [Description("작업 id (list_experiment_docs 의 job_id)")] string job_id)
    {
        var job = await Get($"api/jobs/{job_id}");
        if (job is null) return Error($"작업을 찾을 수 없습니다: {job_id}");
        if (job["jobType"]?.GetValue<string>() != "text")
            return Error($"문서 추출 작업이 아닙니다 (종류: {job["jobType"]})");
        var result = await Get($"api/text/jobs/{job_id}/result");
        if (result is null)
        {
            return Serialize(new JsonObject
            {
                ["error"] = "추출 결과가 없습니다",
                ["job_id"] = job_id,
                ["file"] = job["fileName"]?.GetValue<string>(),
                ["status"] = job["status"]?.GetValue<string>(),
                ["job_error"] = Cut(job["error"]?.GetValue<string>(), 300),
            });
        }

        var fields = result["fields"]?.DeepClone().AsObject();
        if (fields?["items"] is JsonArray items && items.Count > 15)
        {
            fields["items"] = new JsonArray([.. items.Take(15).Select(i => i!.DeepClone())]);
            fields["items_note"] = $"품목 {items.Count}개 중 15개만 표시";
        }
        var issues = result["issues"]?.AsArray().Select(i => new JsonObject
        {
            ["field"] = i!["field"]?.GetValue<string>(),
            ["severity"] = i["severity"]?.GetValue<string>(),
            ["message"] = i["message"]?.GetValue<string>(),
        });
        return Serialize(new JsonObject
        {
            ["job_id"] = job_id,
            ["file"] = job["fileName"]?.GetValue<string>(),
            ["model"] = result["model"]?.GetValue<string>(),
            ["document_type"] = result["documentType"]?.GetValue<string>(),
            ["final_source"] = result["finalSource"]?.GetValue<string>(),
            ["fallback_used"] = result["fallbackUsed"]?.GetValue<bool>(),
            ["fallback_reason"] = result["fallbackReason"]?.GetValue<string>(),
            ["validation_passed"] = result["validationPassed"]?.GetValue<bool>(),
            ["fields"] = fields,
            ["issues"] = new JsonArray([.. issues ?? []]),
            ["attempts"] = result["attempts"]?.AsArray().Count,
            ["settings"] = job["settings"]?.DeepClone(),
        });
    }

    [Description("문서 추출 작업의 OCR 결과(줄 단위 텍스트와 신뢰도)를 조회한다. 추출 값이 원문에 실제로 있는지 확인할 때 쓴다")]
    public async Task<string> GetOcrText(
        [Description("작업 id")] string job_id,
        [Description("돌려받을 최대 줄 수")] int max_lines = 80)
    {
        var ocr = await Get($"api/text/jobs/{job_id}/ocr");
        if (ocr is null) return Error($"OCR 결과를 찾을 수 없습니다: {job_id}");
        var lines = ocr["lines"]!.AsArray();
        max_lines = Math.Clamp(max_lines, 1, 200);
        var text = lines.Take(max_lines).Select((l, i) =>
            $"{i + 1}. {l!["text"]?.GetValue<string>()} ({l["confidence"]?.GetValue<double>():0.00})");
        return Serialize(new JsonObject
        {
            ["job_id"] = job_id,
            ["engine"] = ocr["engine"]?.GetValue<string>(),
            ["avg_confidence"] = Round(ocr["avg_confidence"]),
            ["line_count"] = lines.Count,
            ["lines"] = string.Join("\n", text),
        });
    }

    [Description("프롬프트 하나의 현재 내용과 버전 정보를 조회한다. name 을 모르면 빈 문자열로 부르면 모듈의 프롬프트 이름 목록을 돌려준다")]
    public async Task<string> GetPrompt(
        [Description("text, image, multimodal 중 하나")] string module,
        [Description("프롬프트 이름 (예: classify.system, extract.receipt)")] string name = "")
    {
        if (string.IsNullOrWhiteSpace(name) || (await Get($"api/prompts/{module}/{name}")) is not { } prompt)
        {
            var all = await Get("api/prompts");
            var names = all?["prompts"]?.AsArray()
                .Where(p => p!["module"]?.GetValue<string>() == module)
                .Select(p => JsonValue.Create(p!["name"]!.GetValue<string>()));
            var message = string.IsNullOrWhiteSpace(name) ? "프롬프트 이름 목록" : $"프롬프트를 찾을 수 없습니다: {module}/{name}";
            return Serialize(new JsonObject
            {
                [string.IsNullOrWhiteSpace(name) ? "message" : "error"] = message,
                ["module"] = module,
                ["names"] = new JsonArray([.. names ?? []]),
            });
        }
        var info = prompt["info"]!;
        return Serialize(new JsonObject
        {
            ["module"] = module,
            ["name"] = name,
            ["description"] = info["description"]?.GetValue<string>(),
            ["active_version"] = info["activeVersion"]?.DeepClone(),
            ["version_count"] = info["versionCount"]?.DeepClone(),
            ["variables"] = info["variables"]?.DeepClone(),
            ["content"] = Cut(prompt["content"]?.GetValue<string>(), 3000),
        });
    }

    private async Task<(JsonNode? Exp, string Module)> FindExperiment(string id)
    {
        if (!Guid.TryParse(id, out _)) return (null, "");
        foreach (var module in new[] { "text", "image" })
        {
            if (await Get($"api/{module}/experiments/{id}") is { } exp) return (exp, module);
        }
        return (null, "");
    }

    private async Task<JsonNode?> Get(string path)
    {
        using var response = await http.GetAsync(path);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(Json);
    private static string Error(string message) => Serialize(new JsonObject { ["error"] = message });
    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";

    private static JsonNode? Round(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? JsonValue.Create(Math.Round(d, 3)) : n?.DeepClone();

    private static JsonObject RoundAll(JsonObject o)
    {
        foreach (var key in o.Select(p => p.Key).ToList())
        {
            if (o[key] is JsonValue v && v.TryGetValue<double>(out var d) && d % 1 != 0) o[key] = Math.Round(d, 3);
        }
        return o;
    }
}
