using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Modules.Image;
using Api.Modules.Image.Pipeline;
using Api.Shared.Llm;
using Api.Shared.Prompts;
using Microsoft.SemanticKernel;

namespace Api.Modules.Multimodal.Pipeline;

public sealed class MultimodalPrompts(Kernel kernel, PromptStore store, PromptUsage usage) : PromptLibrary(kernel, store, usage, MultimodalModule.ModuleKey);

/// <summary>
/// LLM 호출 기록 (원문 응답 포함). Glossary = 프롬프트에 붙인 용어 정의, FirstError·FirstRaw = 재시도 전 첫 응답의 오류·원문 (소견서 요약만)
/// </summary>
public sealed record LlmStep(
    string Model, int ElapsedMs, string? ParseError, JsonObject? Result, string Raw, IReadOnlyList<string>? Glossary = null,
    string? FirstError = null, string? FirstRaw = null);

/// <summary>소견서·CNN·VLM 을 한 소견 기준으로 나란히 (null = 그 출처에 해당 판단이 없음)</summary>
public sealed record ConcordanceRow(
    string Key,
    string Name,
    bool? Report,
    bool? Cnn,
    double? CnnProbability,
    string? CnnLabel,
    bool? Vlm,
    bool? Agree,
    /// <summary>CNN 이 기준값 바로 위(경계) ➔ 판단 보류, 소견서와 비교하지 않음</summary>
    bool CnnBorderline = false);

public sealed record MultimodalIssue(string Rule, IssueSeverity Severity, string Message);

/// <summary>Step 3: 소견서 요약(LLM) ➔ 소견서·CNN·VLM 일치 비교(C#) ➔ 종합 보고서(LLM)</summary>
public sealed class MultimodalPipeline(LlmClient llm, MultimodalPrompts prompts, ReportGlossary glossary)
{
    /// <summary>비교할 소견: 키 ➔ (한국어 이름, CNN 소견 라벨). 폐렴은 대상별 폐렴 신호를 쓰므로 라벨 없음</summary>
    public static readonly (string Key, string Name, string? CnnLabel)[] Targets =
    [
        ("pneumonia", "폐렴", null),
        ("cardiomegaly", "심장비대", "Cardiomegaly"),
        ("effusion", "흉수", "Effusion"),
        ("atelectasis", "무기폐", "Atelectasis"),
        ("edema", "폐부종", "Edema"),
    ];

    private static readonly JsonNode SummarySchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "indication": { "type": ["string", "null"] },
            "key_symptoms": { "type": "array", "items": { "type": "string" } },
            "findings": { "type": "array", "items": { "type": "string" } },
            "final_diagnosis": { "type": "string" },
            "normal": { "type": "boolean" },
            "mentions": {
              "type": "object",
              "properties": {
                "pneumonia": { "type": "boolean" },
                "cardiomegaly": { "type": "boolean" },
                "effusion": { "type": "boolean" },
                "atelectasis": { "type": "boolean" },
                "edema": { "type": "boolean" }
              },
              "required": ["pneumonia", "cardiomegaly", "effusion", "atelectasis", "edema"]
            }
          },
          "required": ["indication", "key_symptoms", "findings", "final_diagnosis", "normal", "mentions"]
        }
        """)!;

    private static readonly JsonNode FinalSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "key_findings": { "type": "array", "items": { "type": "string" } },
            "concordance_note": { "type": "string" },
            "recommendations": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "key_findings", "concordance_note", "recommendations"]
        }
        """)!;

    /// <summary>
    /// 소견서 요약. 소견서에 나온 용어의 정의를 용어집에서 찾아 system 프롬프트에 붙임 (2차-6: 새 120건에서 오답 24 ➔ 21, 형식 실패 3 ➔ 0).
    /// 형식 오류(잘림·파싱 실패·필드 누락)면 오류를 알려 주고 1회 재시도. temperature 0 이라 같은 입력은 같은 답이 나오므로 user 프롬프트에 재시도 안내를 붙임
    /// </summary>
    public async Task<LlmStep> SummarizeReportAsync(string model, string reportText, CancellationToken ct)
    {
        var hits = glossary.Retrieve(reportText);
        var system = (await prompts.RenderForModelAsync("summarize.system", model, ct: ct)).TrimEnd() + ReportGlossary.Block(hits);
        var user = await prompts.RenderAsync("summarize.user", new KernelArguments { ["report"] = reportText }, ct);
        var first = await CallAsync(model, SummarySchema, system, user, ct);
        if (first.ParseError is not { } error)
        {
            return first with { Glossary = hits };
        }
        var retry = await CallAsync(model, SummarySchema, system,
            user.TrimEnd() + $"\n\n(재시도) 이전 응답이 형식 오류였습니다: {error}. 스키마에 있는 필드만 쓰고, findings 는 5개 이하의 짧은 문장으로 답하세요.", ct);
        return retry with { ElapsedMs = first.ElapsedMs + retry.ElapsedMs, Glossary = hits, FirstError = error, FirstRaw = first.Raw };
    }

    public async Task<LlmStep> SynthesizeAsync(
        string model, ImageAnalysis image, JsonObject? summary, List<ConcordanceRow> rows, CancellationToken ct) =>
        await CallAsync(model, FinalSchema,
            await prompts.RenderForModelAsync("synthesize.system", model, ct: ct),
            await prompts.RenderAsync("synthesize.user", new KernelArguments { ["context"] = Context(image, summary, rows) }, ct), ct);

    /// <summary>소견별로 소견서 언급 · CNN 판정 · VLM 판단(폐렴만)을 비교</summary>
    public static (List<ConcordanceRow> Rows, List<MultimodalIssue> Issues) Compare(ImageAnalysis image, JsonObject? summary)
    {
        var rows = new List<ConcordanceRow>();
        var issues = new List<MultimodalIssue>();
        var mentions = summary?["mentions"] as JsonObject;
        bool? vlmPneumonia = image.Report?.Report?["pneumonia_suspected"]?.GetValue<bool>();
        foreach (var (key, name, cnnLabel) in Targets)
        {
            bool? report = mentions?[key]?.GetValue<bool>();
            var finding = cnnLabel is null ? image.Pneumonia : image.Findings.Find(cnnLabel);
            bool? vlm = key == "pneumonia" ? vlmPneumonia : null;
            var borderline = finding?.Borderline == true;
            bool? agree = report is { } r && finding is { } f && !borderline ? r == f.Positive : null;
            rows.Add(new ConcordanceRow(key, name, report, finding?.Positive, finding?.Probability,
                finding?.Label, vlm, agree, borderline));
            if (agree == false)
            {
                issues.Add(new($"report_vs_cnn:{key}", key == "pneumonia" ? IssueSeverity.Error : IssueSeverity.Warning,
                    $"{name}: 문서 {(report == true ? "있음" : "없음")} vs CNN {(finding!.Positive ? "양성" : "음성")} ({finding.Probability:0.00})"));
            }
            if (report is { } rr && vlm is { } v && rr != v)
            {
                issues.Add(new($"report_vs_vlm:{key}", IssueSeverity.Warning,
                    $"{name}: 문서 {(rr ? "있음" : "없음")} vs VLM {(v ? "의심" : "의심 아님")}"));
            }
        }
        return (rows, issues);
    }

    private static string Context(ImageAnalysis image, JsonObject? summary, List<ConcordanceRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## 소견서 요약");
        sb.AppendLine(summary is null ? "(요약 실패)" : summary.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        sb.AppendLine();
        sb.AppendLine("## CNN 자동 분석 (확률, 판정 기준 이상이면 양성)");
        if (image.Pneumonia is { } p)
        {
            sb.AppendLine($"- 폐렴 신호({image.Source}): {p.Probability:0.00} (기준 {p.Threshold:0.###}) ➔ {(p.Borderline ? "경계(판단 보류)" : p.Positive ? "양성" : "음성")}");
        }
        var positives = image.Findings.DefinitePositives.ToList();
        var borderlines = image.Findings.Findings.Where(f => f.Borderline).ToList();
        sb.AppendLine($"- 양성 소견: {(positives.Count == 0 ? "없음" : string.Join(", ", positives.Select(ImagePipeline.Describe)))}");
        if (borderlines.Count > 0)
        {
            sb.AppendLine($"- 경계(판단 보류, 기준값 바로 위라 대부분 틀림 ➔ 핵심 소견·가능성으로 적지 말 것): {string.Join(", ", borderlines.Select(ImagePipeline.Describe))}");
        }
        sb.AppendLine();
        sb.AppendLine("## VLM 판독 초안 (영상만 보고 독립 판독)");
        sb.AppendLine(image.Report?.Report is { } r
            ? $"- 결론: {r["impression"]}\n- 폐렴 의심: {r["pneumonia_suspected"]}, 정상: {r["normal"]}"
            : "(판독 없음)");
        sb.AppendLine();
        sb.AppendLine("## 소견별 일치 비교 (소견서 / CNN / VLM)");
        foreach (var row in rows)
        {
            sb.AppendLine($"- {row.Name}: 소견서 {Mark(row.Report)} / CNN {Mark(row.Cnn)}"
                + (row.CnnProbability is { } prob ? $" ({prob:0.00})" : "")
                + (row.Key == "pneumonia" ? $" / VLM {Mark(row.Vlm)}" : "")
                + (row.CnnBorderline ? " (CNN 경계, 판단 보류)" : "")
                + (row.Agree == false ? " ➔ 불일치" : ""));
        }
        return sb.ToString();
    }

    private static string Mark(bool? v) => v is null ? "판단 없음" : v.Value ? "있음" : "없음";

    private async Task<LlmStep> CallAsync(string model, JsonNode schema, string system, string user, CancellationToken ct)
    {
        var response = await llm.CompleteJsonAsync(new LlmRequest(model, system, user, schema, null), ct);
        JsonObject? result = null;
        string? error = null;
        try
        {
            result = JsonNode.Parse(response.Content) as JsonObject;
            var missing = schema["required"]!.AsArray().Select(n => n!.GetValue<string>())
                .Where(k => result is null || !result.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                error = $"필드 누락: {string.Join(", ", missing)}";
            }
        }
        catch (JsonException ex)
        {
            error = response.Truncated ? $"응답이 최대 길이에서 잘림 ({response.CompletionTokens} 토큰)" : $"JSON 파싱 실패: {ex.Message}";
        }
        return new LlmStep(model, response.ElapsedMs, error, error is null ? result : null, response.Content);
    }
}
