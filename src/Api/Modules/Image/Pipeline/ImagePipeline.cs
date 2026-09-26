using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Modules.Image.Cnn;
using Api.Shared.Llm;
using Microsoft.SemanticKernel;

namespace Api.Modules.Image.Pipeline;

public enum IssueSeverity
{
    /// <summary>두 판단이 어긋남 ➔ 사람 확인 대상 (검증 실패)</summary>
    Error,
    /// <summary>참고용 (검증 통과에는 영향 없음)</summary>
    Warning,
}

public sealed record ImageIssue(string Rule, IssueSeverity Severity, string Message);

/// <summary>VLM 판독 초안 1회 기록 (원문 응답 포함)</summary>
public sealed record ReportAttempt(
    string Model,
    bool SawCnn,
    int ElapsedMs,
    int? PromptTokens,
    int? CompletionTokens,
    string? ParseError,
    JsonObject? Report,
    string Raw);

public static class ImageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>Step 2 LLM 단계: VLM 판독 초안 + CNN·VLM 교차 검증 (C# 규칙)</summary>
public sealed class ImagePipeline(LlmClient llm, ImagePrompts prompts)
{
    private static readonly JsonNode ReportSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "findings": { "type": "array", "items": { "type": "string" } },
            "impression": { "type": "string" },
            "normal": { "type": "boolean" },
            "pneumonia_suspected": { "type": "boolean" },
            "recommendation": { "type": ["string", "null"] }
          },
          "required": ["findings", "impression", "normal", "pneumonia_suspected", "recommendation"]
        }
        """)!;

    public async Task<ReportAttempt> WriteReportAsync(
        string model, LlmImage image, CnnFinding? pneumonia, string pneumoniaSource, CnnResult findings, bool showCnn,
        CancellationToken ct)
    {
        var system = await prompts.RenderForModelAsync("report.system", model, ct: ct);
        var user = await prompts.RenderAsync("report.user", new KernelArguments
        {
            ["cnn_section"] = showCnn ? CnnSummary(pneumonia, pneumoniaSource, findings) + "\n\n" : "",
        }, ct);
        var response = await llm.CompleteJsonAsync(new LlmRequest(model, system, user, ReportSchema, [image]), ct);

        JsonObject? report = null;
        string? parseError = null;
        try
        {
            report = JsonNode.Parse(response.Content) as JsonObject;
            var required = ReportSchema["required"]!.AsArray().Select(n => n!.GetValue<string>());
            var missing = report is null ? ["(객체 아님)"] : required.Where(k => !report.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                parseError = $"필드 누락: {string.Join(", ", missing)}";
            }
        }
        catch (JsonException ex)
        {
            parseError = response.Truncated
                ? $"응답이 최대 길이({response.CompletionTokens} 토큰)에서 잘림 (같은 내용 반복 생성 의심)"
                : $"JSON 파싱 실패: {ex.Message}";
        }
        return new ReportAttempt(model, showCnn, response.ElapsedMs, response.PromptTokens, response.CompletionTokens,
            parseError, parseError is null ? report : null, response.Content);
    }

    /// <summary>CNN(폐렴 신호 + 소견)과 VLM 판독의 교차 검증</summary>
    public static List<ImageIssue> Validate(
        CnnFinding? cnnPneumonia, string pneumoniaSource, CnnResult findings, ReportAttempt? report)
    {
        var issues = new List<ImageIssue>();
        var positives = findings.Positives.ToList();
        if (report is null)
        {
            return issues;
        }
        if (report.Report is null)
        {
            issues.Add(new("report_schema", IssueSeverity.Error, $"VLM 판독 초안 형식 오류 ({report.ParseError})"));
            return issues;
        }
        var vlmPneumonia = report.Report["pneumonia_suspected"]?.GetValue<bool>() ?? false;
        var vlmNormal = report.Report["normal"]?.GetValue<bool>() ?? false;

        if (cnnPneumonia is not null && cnnPneumonia.Positive != vlmPneumonia)
        {
            issues.Add(new("pneumonia_disagree", IssueSeverity.Error,
                $"이진 판단 불일치: CNN({pneumoniaSource}) {(cnnPneumonia.Positive ? "양성" : "음성")} (확률 {cnnPneumonia.Probability:0.000}, 기준 {cnnPneumonia.Threshold:0.###}) " +
                $"vs VLM {(vlmPneumonia ? "양성 의심" : "양성 의심 아님")} ➔ 사람 확인 필요"));
        }
        if (vlmNormal && positives.Count > 0)
        {
            issues.Add(new("normal_vs_findings", IssueSeverity.Warning,
                $"VLM 은 정상으로 판독했지만 CNN 양성 소견이 있음: {string.Join(", ", positives.Select(Describe))}"));
        }
        else if (!vlmNormal && positives.Count == 0 && cnnPneumonia is { Positive: false })
        {
            issues.Add(new("abnormal_without_findings", IssueSeverity.Warning,
                "VLM 은 이상 소견을 적었지만 CNN 은 양성 소견이 없음"));
        }
        return issues;
    }

    private static string CnnSummary(CnnFinding? p, string pneumoniaSource, CnnResult findings)
    {
        var positives = findings.Positives.ToList();
        return "자동 분석(CNN) 결과 (참고용, 틀릴 수 있음. 이미지와 다르면 이미지를 따르세요):\n"
            + (p is null ? "" : $"- 폐렴 판단({pneumoniaSource}): 확률 {p.Probability:0.000} (기준 {p.Threshold:0.###}) ➔ {(p.Positive ? "양성" : "음성")}\n")
            + $"- 소견 모델 양성: {(positives.Count == 0 ? "없음" : string.Join(", ", positives.Select(Describe)))}";
    }

    public static string Describe(CnnFinding f) => $"{FindingNames.Of(f.Label)} {f.Probability:0.00}";
}
