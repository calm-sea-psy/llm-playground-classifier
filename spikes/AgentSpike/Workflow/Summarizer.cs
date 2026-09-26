using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace AgentSpike.Workflow;

/// <summary>
/// 3차-2 3단계: 사실 JSON ➔ 한국어 답 (LLM, 도구 없음) ➔ 답의 숫자·파일명·id 가 사실에 있는지 코드로 대조.
/// 어긋나면 템플릿 답으로 바꾸고 기록 (스파이크: think 가 id·파일명을 지어냄, 거절된 저장을 "저장했다"고 답함)
/// </summary>
public static partial class Summarizer
{
    public const string SystemPrompt = """
        당신은 실험 결과 분석 도구의 답변 작성기입니다. 사용자 질문과, 코드가 조회·계산한 사실(JSON)을 받아 한국어로 답합니다.

        규칙
        - 사실 JSON 에 있는 값만 씁니다. 숫자·파일명·id 는 사실에 적힌 그대로 옮깁니다. 새로 계산하지 않습니다 (필요한 계산은 사실에 이미 있음: *_percent, difference, difference_percent_points, count, top_gap).
        - 사실에 error, metric_not_available, nth_error 가 있으면 그 내용을 그대로 알리고 추측하지 않습니다.
        - intent 가 unsupported 면 reason 을 알리고 할 수 있는 것(can_do)을 짧게 안내합니다. 하지 않은 일을 했다고 말하지 않습니다.
        - 문서가 실패한 이유는 failure_reason, VLM 폴백한 이유는 fallback_reason 입니다. 둘을 섞지 않습니다.
        - 사실의 found 가 true 면 "원문에 있음", false 면 "원문에 없음" 입니다.
        - 질문이 묻는 것에만 답하고, 결론을 먼저 짧게 씁니다.
        """;

    public static async Task<(string Answer, long? In, long? Out)> WriteAsync(HttpClient ollamaHttp, string model, string question, JsonObject facts, CancellationToken ct)
    {
        IChatClient client = new OllamaApiClient(ollamaHttp, model);
        var options = SpikeAgent.BuildOptions([], think: false);
        options.Tools = null;
        var response = await client.GetResponseAsync(
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, $"질문: {question}\n\n사실:\n{facts.ToJsonString(ApiTools.Json)}"),
        ], options, ct);
        return (response.Text, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
    }

    /// <summary>답의 숫자·파일명·id 중 사실에 없는 것 (agent_eval 의 근거 없는 숫자 규칙과 같은 기준 + 파일명·id)</summary>
    public static List<string> Ungrounded(string answer, string question, JsonObject facts)
    {
        var factText = facts.ToJsonString(ApiTools.Json);
        var pool = Numbers(factText).Concat(Numbers(question)).ToList();
        var bad = new List<string>();
        foreach (var x in Numbers(answer).Distinct())
        {
            if (x < 10 && x == Math.Floor(x)) continue;
            if (!pool.Any(v => Math.Abs(x - v) <= 0.0051 * Math.Max(1, Math.Abs(v))
                    || (Math.Abs(v) <= 1 && (Math.Abs(x - v * 100) <= 0.051 || (x == Math.Floor(x) && Math.Abs(x - v * 100) <= 0.5)))))
                bad.Add(x.ToString(CultureInfo.InvariantCulture));
        }
        foreach (Match m in NameRegex().Matches(answer))
        {
            if (!factText.Contains(m.Value, StringComparison.OrdinalIgnoreCase) && !question.Contains(m.Value, StringComparison.OrdinalIgnoreCase))
                bad.Add(m.Value);
        }
        return bad;
    }

    /// <summary>대조에 실패했을 때 쓰는 답: 사실을 그대로 보여 줌 (문장은 투박하지만 지어낸 값이 없음)</summary>
    public static string Template(JsonObject facts)
    {
        if (facts["error"] is { } e) return $"확인할 수 없습니다: {e}";
        if (facts["intent"]?.GetValue<string>() == "unsupported") return $"{facts["reason"]}. 할 수 있는 것: {facts["can_do"]}";
        return "요약 문장이 조회 결과와 맞지 않아, 조회 결과를 그대로 보여 드립니다:\n" + facts.ToJsonString(ApiTools.Json);
    }

    private static IEnumerable<double> Numbers(string text)
    {
        foreach (Match m in NumberRegex().Matches(text))
        {
            if (double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) yield return d;
        }
    }

    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    // 파일명 (IMG00605.png, person154_bacteria_728.jpeg, 3411_IM-1649-0001-0001.dcm.png …) 과 실험·작업 id
    [GeneratedRegex(@"\b[\w\-]+\.(?:png|jpe?g)\b|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();
}
