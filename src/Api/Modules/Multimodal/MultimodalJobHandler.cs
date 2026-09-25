using System.Text.Json;
using Api.Modules.Image;
using Api.Modules.Image.Pipeline;
using Api.Modules.Multimodal.Pipeline;
using Api.Modules.Text.Ocr;
using ReadingOrder = Api.Modules.Text.Pipeline.ReadingOrder;
using Api.Shared.Data;
using Api.Shared.Jobs;
using Api.Shared.Storage;

namespace Api.Modules.Multimodal;

/// <summary>
/// Step 3 작업 처리: X-ray 분석(Step 2 ImageAnalyzer) ➔ 소견서(텍스트, 또는 이미지면 OCR) ➔ 소견서 요약(LLM)
/// ➔ 소견서·CNN·VLM 일치 비교(C#) ➔ 종합 보고서(LLM) ➔ 저장
/// </summary>
public sealed class MultimodalJobHandler(
    ImageAnalyzer analyzer,
    IOcrEngine ocr,
    MultimodalPipeline pipeline,
    AppDbContext db,
    UploadStorage storage,
    TimeProvider clock)
    : IJobHandler
{
    public async Task<string> HandleAsync(Job job, JobReporter reporter, CancellationToken ct)
    {
        var settings = MultimodalSettings.FromJson(job.Settings)
            ?? throw new InvalidOperationException("통합 작업 설정(소견서 파일)이 없습니다");
        var model = settings.Image.Model;

        // 1) X-ray: CNN 폐렴 신호·소견 ➔ VLM 독립 판독 ➔ 교차 검증 (Step 2 와 같음, image_results 에 저장)
        var image = await analyzer.AnalyzeAsync(job, settings.Image, reporter, ct);
        await analyzer.SaveAsync(job, image, ct);

        // 2) 소견서 텍스트: 붙여넣기 그대로, 이미지면 OCR (Step 1 OCR 서비스 + 읽기 순서)
        string reportText;
        string source;
        if (settings.ReportIsText)
        {
            reportText = await File.ReadAllTextAsync(storage.PathOf(job.Id, settings.ReportFile), ct);
            source = "text";
        }
        else
        {
            await reporter.SetStatusAsync(job, JobStatus.OcrRunning, "소견서 OCR 중", ct);
            OcrResult result;
            await using (var file = storage.OpenRead(job.Id, settings.ReportFile))
            {
                result = await ocr.RecognizeAsync(file, settings.ReportFile, engine: null, ct);
            }
            await storage.WriteTextAsync(job.Id, "report_ocr.json", JsonSerializer.Serialize(result, OcrJson.Options), ct);
            reportText = ReadingOrder.Build(result);
            source = "ocr";
        }

        // 3) 소견서 요약 (증상·소견·최종 진단·소견별 언급 여부)
        await reporter.SetStatusAsync(job, JobStatus.LlmRunning, $"소견서 요약 중 ({model})", ct);
        var summary = await pipeline.SummarizeReportAsync(model, reportText, ct); // 형식 오류면 안에서 1회 재시도

        // 4) 일치 비교: 소견서 vs CNN vs VLM
        var (rows, reportIssues) = MultimodalPipeline.Compare(image, summary.Result);
        var disagree = rows.Count(r => r.Agree == false);
        await reporter.SetStatusAsync(job, JobStatus.Validating,
            $"일치 비교: 소견 {rows.Count(r => r.Agree is not null)}개 중 {disagree}개 불일치", ct);

        // 5) 종합 보고서 (주어진 정보만으로, 불일치는 명시)
        await reporter.SetStatusAsync(job, JobStatus.LlmRunning, $"종합 보고서 작성 중 ({model})", ct);
        var final = await pipeline.SynthesizeAsync(model, image, summary.Result, rows, ct);

        // 6) 저장
        var issues = image.Issues.Select(i => new MultimodalIssue(i.Rule, i.Severity, $"[영상] {i.Message}"))
            .Concat(reportIssues).ToList();
        if (summary.ParseError is { } se)
        {
            issues.Add(new("summary_schema", IssueSeverity.Error,
                summary.FirstError is null ? $"소견서 요약 형식 오류 ({se})" : $"소견서 요약 형식 오류, 재시도도 실패 ({se})"));
        }
        else if (summary.FirstError is { } fe)
        {
            issues.Add(new("summary_retry", IssueSeverity.Warning, $"소견서 요약 첫 응답 형식 오류 ➔ 재시도로 성공 ({fe})"));
        }
        var needsReview = issues.Any(i => i.Severity == IssueSeverity.Error);
        var steps = new[] { ("summary", summary), ("final", final) };
        db.Set<MultimodalResultRecord>().Add(new MultimodalResultRecord
        {
            JobId = job.Id,
            ReportSource = source,
            ReportText = reportText,
            ReportSummary = summary.Result?.ToJsonString(ImageJson.Options),
            Concordance = JsonSerializer.Serialize(rows, ImageJson.Options),
            FinalReport = final.Result?.ToJsonString(ImageJson.Options),
            Issues = JsonSerializer.Serialize(issues, ImageJson.Options),
            NeedsReview = needsReview,
            Steps = JsonSerializer.Serialize(steps.ToDictionary(s => s.Item1, s => s.Item2), ImageJson.Options),
            LlmElapsedMs = summary.ElapsedMs + final.ElapsedMs + (image.Report?.ElapsedMs ?? 0),
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        await storage.WriteTextAsync(job.Id, "multimodal.json", JsonSerializer.Serialize(new
        {
            settings, source, reportText, summary, rows, final, issues, needsReview,
        }, ImageJson.Options), ct);

        return $"종합 보고서 완료: {ImageAnalyzer.Describe(image)}, 소견서 불일치 {disagree}개"
            + (needsReview ? " ➔ 사람 확인 필요" : "");
    }
}
