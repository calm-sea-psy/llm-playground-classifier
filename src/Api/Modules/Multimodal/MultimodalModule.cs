using System.Text.Json.Nodes;
using Api.Modules.Image;
using Api.Modules.Image.Pipeline;
using Api.Modules.Multimodal.Pipeline;
using Api.Shared.Data;
using Api.Shared.Jobs;
using Api.Shared.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

namespace Api.Modules.Multimodal;

/// <summary>
/// Step 3 통합: 흉부 X-ray + 소견서 ➔ X-ray 분석(Step 2) + 소견서 요약(OCR 은 Step 1) ➔ 일치 비교 ➔ 환자 종합 보고서.
/// Text·Image 모듈의 서비스를 쓰므로 둘에 의존 (Features 에서 Multimodal 만 켜도 함께 로드)
/// </summary>
public sealed class MultimodalModule : IPipelineModule
{
    public const string ModuleKey = "multimodal";
    public const int MaxReportChars = 20_000;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public string Key => ModuleKey;

    public string DisplayName => "X-ray + 소견서 통합";

    public IReadOnlyList<string> DependsOn => ["text", "image"];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder health)
    {
        services.AddScoped<MultimodalPrompts>();
        services.AddScoped<ReportGlossary>();
        services.AddScoped<MultimodalPipeline>();
        services.AddKeyedScoped<IJobHandler, MultimodalJobHandler>(Key);
        // 헬스 체크(ocr·cnn·llm)는 의존 모듈이 이미 등록
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // X-ray + 소견서(텍스트 또는 이미지 중 하나) ➔ 202 + jobId. X-ray 분석 설정은 판독 기본 설정 + 대상·모델
        group.MapPost("/jobs", async (
            [FromForm] IFormFile xray,
            [FromForm] string? reportText,
            IFormFile? reportFile,
            [FromForm] string? population,
            [FromForm] string? model,
            JobFactory factory,
            ImageSettingsStore store,
            UploadStorage storage,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (factory.Validate(xray) is { } xrayError)
            {
                return Results.Problem(xrayError, statusCode: StatusCodes.Status400BadRequest);
            }
            var hasText = !string.IsNullOrWhiteSpace(reportText);
            if (hasText == (reportFile is not null))
            {
                return Results.Problem("소견서는 텍스트(reportText) 또는 이미지(reportFile) 중 하나만 보내세요",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (hasText && reportText!.Length > MaxReportChars)
            {
                return Results.Problem($"소견서 텍스트는 {MaxReportChars:N0}자 이하여야 합니다", statusCode: StatusCodes.Status400BadRequest);
            }
            if (reportFile is not null && factory.Validate(reportFile) is { } reportError)
            {
                return Results.Problem(reportError, statusCode: StatusCodes.Status400BadRequest);
            }
            var image = (await store.GetDefaultAsync(ct)).Settings;
            if (!string.IsNullOrEmpty(population))
            {
                image = image with { Population = population };
            }
            if (!string.IsNullOrEmpty(model))
            {
                image = image with { Model = model }; // VLM 판독·소견서 요약·종합 보고서 모두 이 모델 (2차-5 모델 비교)
            }
            if (store.Validate(image) is { } settingsError)
            {
                return Results.Problem(settingsError, statusCode: StatusCodes.Status400BadRequest);
            }

            var reportName = hasText ? "report.txt" : "report" + Path.GetExtension(reportFile!.FileName).ToLowerInvariant();
            var settings = new MultimodalSettings(image, reportName);
            var job = await factory.CreateAsync(xray, Key, image.Model, settings.ToJson(), experimentId: null, comboIndex: null, ct);
            if (hasText)
            {
                await storage.WriteTextAsync(job.Id, reportName, reportText!, ct);
            }
            else
            {
                using var buffer = new MemoryStream();
                await reportFile!.CopyToAsync(buffer, ct);
                await storage.WriteBytesAsync(job.Id, reportName, buffer.ToArray(), ct);
            }
            await db.SaveChangesAsync(ct);
            if (!await factory.EnqueueAsync([job], ct))
            {
                return Results.Problem("작업 큐가 가득 찼습니다. 잠시 후 다시 시도하세요",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
        }).DisableAntiforgery();

        // 소견서 요약 + 일치 비교 + 종합 보고서 (X-ray 결과는 /api/image/jobs/{id}/result)
        group.MapGet("/jobs/{id:guid}/result", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.Set<MultimodalResultRecord>().AsNoTracking().FirstOrDefaultAsync(x => x.JobId == id, ct);
            if (r is null)
            {
                return Results.NotFound();
            }
            return Results.Json(new
            {
                r.JobId,
                r.ReportSource,
                r.ReportText,
                ReportSummary = r.ReportSummary is null ? null : JsonNode.Parse(r.ReportSummary),
                Concordance = JsonNode.Parse(r.Concordance),
                FinalReport = r.FinalReport is null ? null : JsonNode.Parse(r.FinalReport),
                Issues = JsonNode.Parse(r.Issues),
                // 소견서 요약 때 프롬프트에 붙인 용어 정의 (2차-6 용어집 RAG, 적용 전 작업은 null)
                Glossary = JsonNode.Parse(r.Steps)?["summary"]?["glossary"]?.DeepClone(),
                r.NeedsReview,
                r.LlmElapsedMs,
            }, ImageJson.Options);
        });

        // 올린 소견서 이미지 (텍스트로 보냈으면 404)
        group.MapGet("/jobs/{id:guid}/report-file", async (Guid id, AppDbContext db, UploadStorage storage, CancellationToken ct) =>
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.JobType == ModuleKey, ct);
            var settings = MultimodalSettings.FromJson(job?.Settings);
            if (job is null || settings is null || settings.ReportIsText)
            {
                return Results.NotFound();
            }
            var path = storage.PathOf(id, settings.ReportFile);
            return File.Exists(path)
                ? Results.File(path, ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream")
                : Results.NotFound();
        });
    }
}
