using System.Text.Json.Nodes;
using Api.Modules.Image.Cnn;
using Api.Modules.Image.Experiments;
using Api.Modules.Image.Pipeline;
using Api.Shared.Data;
using Api.Shared.Health;
using Api.Shared.Jobs;
using Api.Shared.Llm;
using Api.Shared.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Modules.Image;

/// <summary>Step 2 이미지: 흉부 X-ray 업로드 ➔ CNN(폐렴 판정 + 18개 소견, Grad-CAM) ➔ VLM 판독 초안 ➔ 교차 검증</summary>
public sealed class ImageModule : IPipelineModule
{
    public const string ModuleKey = "image";

    public string Key => ModuleKey;

    public string DisplayName => "CNN + VLM 판독";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder health)
    {
        var section = configuration.GetSection("Cnn");
        services.Configure<CnnOptions>(section);
        services.Configure<ImageOptions>(configuration.GetSection("Image"));
        services.AddHttpClient<CnnServiceClient>((sp, http) =>
        {
            var cnn = sp.GetRequiredService<IOptions<CnnOptions>>().Value;
            http.BaseAddress = new Uri(cnn.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(cnn.TimeoutSeconds);
        });
        services.AddScoped<ImagePrompts>();
        services.AddScoped<ImagePipeline>();
        services.AddScoped<ImageAnalyzer>();
        services.AddScoped<ImageSettingsStore>();
        services.Configure<ImageEvalOptions>(configuration.GetSection("Eval"));
        services.AddSingleton<ImageLabels>();
        services.AddScoped<ImageExperimentScorer>();
        services.AddKeyedScoped<IJobHandler, ImageJobHandler>(Key);

        var cnnOptions = section.Get<CnnOptions>() ?? new CnnOptions();
        health.AddUrlGroup(
            new Uri(cnnOptions.BaseUrl.TrimEnd('/') + "/health"),
            name: "cnn",
            tags: [HealthEndpoints.ReadyTag],
            timeout: TimeSpan.FromSeconds(3));
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // X-ray 업로드 ➔ 202 + jobId. 설정은 DB 의 판독 기본 설정 (모델 비교 실험에서 적용), 대상·모델만 바꿔 보낼 수 있음
        group.MapPost("/jobs", async (
            IFormFile file,
            [FromForm] string? model,
            [FromForm] string? population,
            JobFactory factory,
            ImageSettingsStore store,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (factory.Validate(file) is { } fileError)
            {
                return Results.Problem(fileError, statusCode: StatusCodes.Status400BadRequest);
            }
            var settings = (await store.GetDefaultAsync(ct)).Settings;
            if (!string.IsNullOrEmpty(model))
            {
                settings = settings with { Model = model };
            }
            if (!string.IsNullOrEmpty(population))
            {
                settings = settings with { Population = population };
            }
            if (store.Validate(settings) is { } settingsError)
            {
                return Results.Problem(settingsError, statusCode: StatusCodes.Status400BadRequest);
            }

            var job = await factory.CreateAsync(file, Key, settings.Model, settings.ToJson(), experimentId: null, comboIndex: null, ct);
            await db.SaveChangesAsync(ct);
            if (!await factory.EnqueueAsync([job], ct))
            {
                return Results.Problem("작업 큐가 가득 찼습니다. 잠시 후 다시 시도하세요",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
        }).DisableAntiforgery();

        // CNN 결과 + VLM 판독 초안 + 교차 검증
        group.MapGet("/jobs/{id:guid}/result", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.Set<ImageResultRecord>().AsNoTracking().FirstOrDefaultAsync(x => x.JobId == id, ct);
            if (r is null)
            {
                return Results.NotFound();
            }
            return Results.Json(new
            {
                r.JobId,
                r.Population,
                r.PneumoniaSource,
                r.PneumoniaProbability,
                r.PneumoniaPositive,
                Pneumonia = JsonNode.Parse(r.Pneumonia),
                Findings = JsonNode.Parse(r.Findings),
                r.CnnElapsedMs,
                r.Model,
                Report = r.Report is null ? null : JsonNode.Parse(r.Report),
                ReportAttempt = r.ReportAttempt is null ? null : JsonNode.Parse(r.ReportAttempt),
                r.LlmElapsedMs,
                Issues = JsonNode.Parse(r.Issues),
                r.ValidationPassed,
            }, ImageJson.Options);
        });

        // Grad-CAM 히트맵 PNG. role = pneumonia(폐렴 신호) | findings(소견). 영역은 result 의 pneumonia/findings.heatmap.region
        group.MapGet("/jobs/{id:guid}/heatmap/{role}", (Guid id, string role, UploadStorage storage) =>
        {
            if (role is not (ImageAnalyzer.PneumoniaRole or ImageAnalyzer.FindingsRole))
            {
                return Results.NotFound();
            }
            var path = storage.PathOf(id, ImageAnalyzer.HeatmapFile(role));
            return File.Exists(path) ? Results.File(path, "image/png") : Results.NotFound();
        });

        // 판독 기본 설정: 선택지 / 조회 / 변경 (모델 비교 실험의 [기본으로 적용])
        group.MapGet("/settings/options", (ImageSettingsStore store) => store.Options());
        group.MapGet("/settings", async (ImageSettingsStore store, CancellationToken ct) =>
            Results.Json(await store.GetDefaultAsync(ct), ImageSettings.Json));
        group.MapPut("/settings", async (SetImageDefaultRequest request, ImageSettingsStore store, CancellationToken ct) =>
            store.Validate(request.Settings) is { } error
                ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Json(await store.SetDefaultAsync(request.Settings, request.Note, ct), ImageSettings.Json));

        ImageExperimentEndpoints.Map(group.MapGroup("/experiments"));
    }
}

public sealed record SetImageDefaultRequest(ImageSettings Settings, string? Note);
