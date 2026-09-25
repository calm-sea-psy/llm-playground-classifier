using System.Text.Json;
using Api.Modules.Image.Cnn;
using Api.Modules.Image.Pipeline;
using Api.Shared.Data;
using Api.Shared.Imaging;
using Api.Shared.Jobs;
using Api.Shared.Llm;
using Api.Shared.Storage;
using Microsoft.Extensions.Options;

namespace Api.Modules.Image;

/// <summary>X-ray 분석 1회 결과 (CNN 폐렴 신호 + 소견 + VLM 판독 초안 + 교차 검증)</summary>
public sealed record ImageAnalysis(
    ImageSettings Settings,
    string Source,
    CnnResult PneumoniaResult,
    CnnFinding? Pneumonia,
    CnnResult Findings,
    ReportAttempt? Report,
    List<ImageIssue> Issues)
{
    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);
}

/// <summary>
/// X-ray 분석: CNN 폐렴 신호 ➔ CNN 소견 ➔ VLM 판독 초안 ➔ 교차 검증 ➔ 저장.
/// Step 2(Image) 작업과 Step 3(Multimodal) 작업이 같이 쓴다. 결과는 image_results 에 작업 ID 로 저장되어
/// 두 모듈 모두 /api/image/jobs/{id}/result · heatmap 으로 볼 수 있다
/// </summary>
public sealed class ImageAnalyzer(
    CnnServiceClient cnn,
    ImagePipeline pipeline,
    AppDbContext db,
    UploadStorage storage,
    IOptions<CnnOptions> cnnOptions,
    IOptions<ImageOptions> imageOptions,
    TimeProvider clock)
{
    /// <summary>히트맵 역할: 폐렴 신호(소아 = pneumonia 엔진, 성인 = xrv 경화) / 소견(xrv 가장 강한 소견)</summary>
    public const string PneumoniaRole = "pneumonia";
    public const string FindingsRole = "findings";

    public async Task<ImageAnalysis> AnalyzeAsync(Job job, ImageSettings settings, JobReporter reporter, CancellationToken ct)
    {
        var engines = cnnOptions.Value;
        var (engine, label) = PneumoniaSignal(settings.Population, engines);
        var source = $"{engine}:{label}";

        // 1) CNN: 폐렴 신호 + 18개 소견 (각 장당 약 12ms, 2차-1)
        await reporter.SetStatusAsync(job, JobStatus.CnnRunning,
            $"CNN 분석 중: 폐렴 신호 ({PopulationName(settings.Population)}, {source})", ct);
        var pneumoniaResult = await ClassifyAsync(job, engine, label, PneumoniaRole, ct);
        var pneumonia = pneumoniaResult.Find(label);
        await reporter.SetStatusAsync(job, JobStatus.CnnRunning,
            $"CNN 분석 중: 소견 ({engines.FindingsEngine}), {PneumoniaLine(pneumonia)}", ct);
        var findings = await ClassifyAsync(job, engines.FindingsEngine, target: null, FindingsRole, ct);

        // 2) VLM 판독 초안 (기본: CNN 결과를 보여 주지 않고 독립적으로, 2차-3b 실험으로 확인)
        ReportAttempt? report = null;
        if (settings.VlmReport)
        {
            await reporter.SetStatusAsync(job, JobStatus.LlmRunning,
                $"VLM 판독 초안 작성 중 ({settings.Model}{(settings.VlmSeesCnn ? ", CNN 결과 참고" : "")})", ct);
            LlmImage image;
            await using (var file = storage.OpenRead(job.Id, job.StoredFileName))
            {
                image = new LlmImage(ImageResizer.ToJpeg(file, imageOptions.Value.VlmMaxImageSide), "image/jpeg");
            }
            report = await pipeline.WriteReportAsync(settings.Model, image, pneumonia, source, findings, settings.VlmSeesCnn, ct);
        }

        // 3) 교차 검증
        var issues = ImagePipeline.Validate(pneumonia, source, findings, report);
        var analysis = new ImageAnalysis(settings, source, pneumoniaResult, pneumonia, findings, report, issues);
        await reporter.SetStatusAsync(job, JobStatus.Validating,
            report is null ? "검증: VLM 판독 없음 (CNN 결과만)"
            : analysis.Errors == 0 ? $"검증: CNN·VLM 일치 (경고 {issues.Count}건)"
            : $"검증: 불일치 {analysis.Errors}건 ➔ 사람 확인 필요", ct);
        return analysis;
    }

    public async Task SaveAsync(Job job, ImageAnalysis a, CancellationToken ct)
    {
        db.Set<ImageResultRecord>().Add(new ImageResultRecord
        {
            JobId = job.Id,
            Population = a.Settings.Population,
            PneumoniaSource = a.Source,
            Pneumonia = JsonSerializer.Serialize(a.PneumoniaResult, ImageJson.Options),
            Findings = JsonSerializer.Serialize(a.Findings, ImageJson.Options),
            PneumoniaProbability = a.Pneumonia?.Probability,
            PneumoniaPositive = a.Pneumonia?.Positive,
            PositiveCount = a.Findings.Positives.Count(),
            CnnElapsedMs = a.PneumoniaResult.ElapsedMs + a.Findings.ElapsedMs,
            Model = a.Report is null ? null : a.Settings.Model,
            Report = a.Report?.Report?.ToJsonString(ImageJson.Options),
            ReportAttempt = a.Report is null ? null : JsonSerializer.Serialize(a.Report, ImageJson.Options),
            LlmElapsedMs = a.Report?.ElapsedMs ?? 0,
            Issues = JsonSerializer.Serialize(a.Issues, ImageJson.Options),
            ValidationPassed = a.Errors == 0,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        await storage.WriteTextAsync(job.Id, "image.json", JsonSerializer.Serialize(new
        {
            settings = a.Settings, source = a.Source, pneumonia = a.PneumoniaResult, findings = a.Findings,
            report = a.Report, issues = a.Issues,
        }, ImageJson.Options), ct);
    }

    /// <summary>완료 메시지용 한 줄 요약</summary>
    public static string Describe(ImageAnalysis a) =>
        $"{PneumoniaLine(a.Pneumonia)}, 소견 양성 {a.Findings.Positives.Count()}개"
        + (a.Report is null ? "" : a.Errors == 0 ? ", CNN·VLM 일치" : ", CNN·VLM 불일치 (사람 확인 필요)");

    /// <summary>대상별 폐렴 신호 출처 (엔진, 소견) — 2차-1b 비교 결과</summary>
    public static (string Engine, string Label) PneumoniaSignal(string population, CnnOptions engines) =>
        population == Populations.Pediatric
            ? (engines.PneumoniaEngine, "Pneumonia")
            : (engines.FindingsEngine, engines.AdultPneumoniaLabel);

    public static string HeatmapFile(string role) => $"heatmap_{role}.png";

    private static string PopulationName(string population) => population == Populations.Pediatric ? "소아" : "성인";

    public static string PneumoniaLine(CnnFinding? p) =>
        p is null ? "폐렴 신호 없음" : $"폐렴 {(p.Positive ? "양성" : "음성")} {p.Probability:0.000}";

    private async Task<CnnResult> ClassifyAsync(Job job, string engine, string? target, string role, CancellationToken ct)
    {
        CnnResult result;
        await using (var file = storage.OpenRead(job.Id, job.StoredFileName))
        {
            result = await cnn.ClassifyAsync(file, job.FileName, engine, heatmap: true, target, ct);
        }
        // 히트맵 PNG 는 파일로, DB·JSON 에는 영역만 남김
        if (result.Heatmap is { PngBase64: { } png })
        {
            await storage.WriteBytesAsync(job.Id, HeatmapFile(role), Convert.FromBase64String(png), ct);
            result = result with { Heatmap = result.Heatmap with { PngBase64 = null } };
        }
        return result;
    }
}
