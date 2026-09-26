using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace AgentSpike.Workflow;

/// <summary>라우터가 뽑는 질문 해석 결과. 실험 id 는 LLM 이 옮겨 적지 않고 이름 조각만 뽑는다 (스파이크 M04: id 한 글자 누락)</summary>
public sealed record RouteResult(
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("module")] string? Module,
    [property: JsonPropertyName("experiment")] string? Experiment,
    [property: JsonPropertyName("combo")] int? Combo,
    [property: JsonPropertyName("filter")] string? Filter,
    [property: JsonPropertyName("job_id")] string? JobId,
    [property: JsonPropertyName("nth")] int? Nth,
    [property: JsonPropertyName("fields")] List<string>? Fields,
    [property: JsonPropertyName("metric")] string? Metric,
    [property: JsonPropertyName("prompt_name")] string? PromptName);

/// <summary>
/// 3차-2 1단계: 질문 ➔ 의도 + 파라미터 (LLM 1회, 도구 없음, JSON 스키마).
/// 도구가 없는 호출이라 format 을 써도 됨 (스파이크 0단계: format + tools 를 같이 주면 도구를 안 부름).
/// </summary>
public sealed class Router(HttpClient ollamaHttp)
{
    public static readonly string[] Intents =
    [
        // 1차 측정: doc_filter · failure_reasons · 실험 문서의 grounding_check 를 서로 헷갈림 (정확도 87~90%)
        // ➔ doc_query 하나로 합치고 이유 조회·원문 대조는 실행기(코드)가 파라미터로 결정
        "list_experiments", "experiment_summary", "doc_query",
        "grounding_check", "job_detail", "compare_combos", "prompt_info", "unsupported",
    ];

    public const string SystemPrompt = """
        당신은 로컬 LLM 평가 도구의 질문 해석기입니다. 사용자의 질문을 읽고 아래 의도 중 하나와 파라미터를 JSON 으로만 답합니다.
        질문에 답하지 말고, 해석만 합니다. 질문에 없는 값은 null 로 둡니다. 값을 추측해서 채우지 않습니다.

        의도
        - list_experiments: 실험 목록, 실험 개수, 가장 최근/가장 큰 실험 등 목록에서 고르는 질문
        - experiment_summary: 실험 하나의 조합별 점수·순위·지표(정확도, 통과율, 폴백 비율, 시간, AUC, 민감도, 판독 성공률 등) 질문
        - doc_query: 실험 안의 문서에 관한 모든 질문. 조건에 맞는 문서의 수·목록(검증 실패, 폴백, 통과했지만 틀림, CNN 오답, CNN·VLM 불일치),
          그 문서들의 실패·폴백 이유, 목록에서 몇 번째 문서의 값이 OCR 원문에 있는지까지 여기에 포함
        - grounding_check: 작업 id 가 주어진 작업 하나에서, 추출한 값(날짜, 시각, 합계, 사업자번호 등)이 OCR 원문에 실제로 있는지 확인
        - job_detail: 작업 id 가 주어진 작업 하나의 추출 값, 검증 문제·경고, 실패 이유, OCR 평균 신뢰도
        - compare_combos: 같은 실험 안에서 두 조합의 지표 차이
        - prompt_info: 프롬프트 내용, 프롬프트 이름 목록, 적용 버전
        - unsupported: 위에 없는 것 (미래 예측, 도구에 없는 데이터, 실험 실행·설정 변경·적용 같은 쓰기 요청, 인사·도움말)

        파라미터
        - module: text(문서 추출·영수증), image(흉부 X-ray), multimodal(X-ray + 소견서 통합). 질문에서 알 수 있을 때만
        - experiment: 실험을 가리키는 말을 질문에 적힌 그대로 (id 면 id 그대로, 이름이면 이름 조각 그대로. 예: "폴백 신뢰도 기준", "소아 Kaggle 29장 qwen3-vl instruct 재비교"). "가장 최근" 같은 표현이면 "latest"
        - combo: 조합 번호 (0부터). 조합을 지정하지 않았거나 "두 조합 각각", "조합별" 이면 null
        - filter: doc_query 의 문서 조건. failed(검증 실패·작업 실패), fallback(VLM 폴백), passed_but_wrong(검증은 통과했지만 틀림), wrong(CNN 이 틀림), disagree(CNN·VLM 판단 불일치)
        - job_id: 작업 id 를 질문에 적힌 그대로
        - nth: "첫 번째 문서", "두 번째 문서" 처럼 목록에서 몇 번째인지 (1부터)
        - fields: doc_query · grounding_check · job_detail 에서 묻는 필드. date, time, total, business_no, store_name, avg_confidence, issues, fallback_reason 중에서
        - metric: experiment_summary · compare_combos 에서 묻는 지표 이름 (질문에 적힌 말 그대로, 예: "필드 정확도", "합계 일치 비율", "판독 성공률")
        - prompt_name: 프롬프트 이름 (예: classify.system). 이름 목록을 묻는 질문이면 null
        """;

    private static readonly JsonElement Schema = JsonDocument.Parse($$"""
        {
          "type": "object",
          "properties": {
            "intent": { "type": "string", "enum": {{JsonSerializer.Serialize(Intents)}} },
            "module": { "type": ["string", "null"], "enum": ["text", "image", "multimodal", null] },
            "experiment": { "type": ["string", "null"] },
            "combo": { "type": ["integer", "null"] },
            "filter": { "type": ["string", "null"], "enum": ["failed", "fallback", "passed_but_wrong", "wrong", "disagree", null] },
            "job_id": { "type": ["string", "null"] },
            "nth": { "type": ["integer", "null"] },
            "fields": { "type": ["array", "null"], "items": { "type": "string" } },
            "metric": { "type": ["string", "null"] },
            "prompt_name": { "type": ["string", "null"] }
          },
          "required": ["intent", "module", "experiment", "combo", "filter", "job_id", "nth", "fields", "metric", "prompt_name"]
        }
        """).RootElement.Clone();

    public async Task<(RouteResult? Route, string Raw, double Sec, string? Error)> RouteAsync(string model, string question, CancellationToken ct = default)
    {
        IChatClient client = new OllamaApiClient(ollamaHttp, model);
        var options = SpikeAgent.BuildOptions([], think: false);
        options.Tools = null;
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "route");

        var sw = Stopwatch.StartNew();
        var response = await client.GetResponseAsync(
            [new(ChatRole.System, SystemPrompt), new(ChatRole.User, question)], options, ct);
        var raw = response.Text;
        try
        {
            var route = JsonSerializer.Deserialize<RouteResult>(raw);
            return (route, raw, Math.Round(sw.Elapsed.TotalSeconds, 1), route is null ? "빈 결과" : null);
        }
        catch (JsonException e)
        {
            return (null, raw, Math.Round(sw.Elapsed.TotalSeconds, 1), $"JSON 오류: {e.Message}");
        }
    }
}
