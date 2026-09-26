using System.Text.Json.Nodes;
using Api.Modules.Text.Experiments;
using Api.Modules.Text.Ocr;
using Api.Modules.Text.Pipeline;
using Api.Modules.Text.Settings;
using Api.Shared.Data;
using Api.Shared.Health;
using Api.Shared.Jobs;
using Api.Shared.Llm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text;

/// <summary>Step 1 텍스트: 문서 업로드 ➔ OCR ➔ LLM 구조화 ➔ 검증 ➔ JSON</summary>
public sealed class TextModule : IPipelineModule
{
    public const string ModuleKey = "text";

    public string Key => ModuleKey;

    public string DisplayName => "문서 텍스트 추출";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder health)
    {
        var section = configuration.GetSection("Ocr");
        services.Configure<OcrOptions>(section);
        services.AddHttpClient<IOcrEngine, OcrServiceEngine>((sp, http) =>
        {
            var ocr = sp.GetRequiredService<IOptions<OcrOptions>>().Value;
            http.BaseAddress = new Uri(ocr.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(ocr.TimeoutSeconds);
        });
        services.Configure<PipelineOptions>(configuration.GetSection("Pipeline"));
        services.Configure<EvalOptions>(configuration.GetSection("Eval"));
        services.AddScoped<TextPrompts>();
        services.AddScoped<TextPipeline>();
        services.AddSingleton<PackStore>();
        services.AddHttpClient(TextPipeline.EngineHttpClient, (sp, http) =>
        {
            var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            http.BaseAddress = new Uri(llm.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds);
        });
        services.AddScoped<SettingsStore>();
        services.AddScoped<ExperimentScorer>();
        services.AddSingleton<KorieLabels>();
        services.AddKeyedScoped<IJobHandler, TextJobHandler>(Key);

        var ocrOptions = section.Get<OcrOptions>() ?? new OcrOptions();
        health.AddUrlGroup(
            new Uri(ocrOptions.BaseUrl.TrimEnd('/') + "/health"),
            name: "ocr",
            tags: [HealthEndpoints.ReadyTag],
            timeout: TimeSpan.FromSeconds(3));
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        // 문서 업로드 ➔ 202 + jobId. 설정은 DB 의 문서 처리 기본값 (model 만 바꿔 보낼 수 있음)
        // 진행 상황은 SignalR(/hubs/jobs) 또는 GET /api/jobs/{id}
        group.MapPost("/jobs", async (
            IFormFile file,
            [FromForm] string? model,
            SettingsStore settingsStore,
            JobFactory factory,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (factory.Validate(file) is { } fileError)
            {
                return Results.Problem(fileError, statusCode: StatusCodes.Status400BadRequest);
            }
            var settings = (await settingsStore.GetDefaultAsync(ct)).Settings;
            if (!string.IsNullOrEmpty(model))
            {
                settings = settings with { Model = model };
            }
            if (settingsStore.Validate(settings) is { } settingsError)
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

        // OCR 결과 원본 (OcrService 응답과 같은 형식)
        group.MapGet("/jobs/{id:guid}/ocr", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Set<OcrResultRecord>().AsNoTracking()
                    .Where(r => r.JobId == id)
                    .OrderBy(r => r.Page)
                    .Select(r => r.Result)
                    .FirstOrDefaultAsync(ct) is { } json
                ? Results.Text(json, "application/json; charset=utf-8")
                : Results.NotFound());

        // LLM 구조화 결과 (최종 필드 + 검증 문제 + 시도 기록)
        group.MapGet("/jobs/{id:guid}/result", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.Set<ExtractionResultRecord>().AsNoTracking().FirstOrDefaultAsync(x => x.JobId == id, ct);
            if (r is null)
            {
                return Results.NotFound();
            }
            return Results.Json(new
            {
                r.JobId,
                r.Model,
                r.DocumentType,
                r.FinalSource,
                r.FallbackUsed,
                r.FallbackReason,
                r.ValidationPassed,
                r.ErrorCount,
                r.WarningCount,
                Fields = r.Fields is null ? null : JsonNode.Parse(r.Fields),
                Issues = JsonNode.Parse(r.Issues),
                Attempts = JsonNode.Parse(r.Attempts),
                r.ReadingText,
                r.ClassifyMs,
                r.LlmElapsedMs,
            }, PipelineJson.Options);
        });

        // 선택 가능한 LLM 모델 (프론트 모델 선택용)
        group.MapGet("/models", async (SettingsStore store, CancellationToken ct) =>
        {
            var options = store.Options();
            return new { DefaultModel = (await store.GetDefaultAsync(ct)).Settings.Model, options.Models };
        });

        // 문서 처리 기본 설정: 조회 / 변경 (모델 비교 실험의 [기본으로 적용])
        group.MapGet("/settings/options", (SettingsStore store) => store.Options());
        group.MapGet("/settings", async (SettingsStore store, CancellationToken ct) =>
            Results.Json(await store.GetDefaultAsync(ct), PipelineSettings.Json));
        group.MapPut("/settings", async (SetDefaultRequest request, SettingsStore store, CancellationToken ct) =>
            store.Validate(request.Settings) is { } error
                ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Json(await store.SetDefaultAsync(request.Settings, request.Note, ct), PipelineSettings.Json));

        ExperimentEndpoints.Map(group.MapGroup("/experiments"));
    }
}

public sealed record SetDefaultRequest(PipelineSettings Settings, string? Note);
