using System.Text.Json;
using Api.Modules.Text.Settings;
using Api.Shared.Data;
using Api.Shared.Experiments;
using Api.Shared.Jobs;
using Api.Shared.Storage;
using Api.Shared.SystemInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text.Experiments;

/// <summary>모델 비교 실험 API (/api/text/experiments)</summary>
public static class ExperimentEndpoints
{
    public const int MaxFiles = 50;
    public const int MaxCombos = 4;

    public static void Map(RouteGroupBuilder group)
    {
        // 요청 본문 한도: Kestrel 기본 30MB·폼 기본 128MB 로는 사진 수십 장 실험이 413 ➔ 파일 수 × 파일당 한도 + 여유
        var maxUploadMb = ((IEndpointRouteBuilder)group).ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value.MaxUploadMb;
        long maxBodyBytes = (long)MaxFiles * maxUploadMb * 1024 * 1024 + 1024 * 1024;

        // 실험 생성: 파일 N장 × 조합 M개 작업을 만들어 큐에 넣음 (조합 순서대로 ➔ 같은 모델이 이어서 돌아 재적재가 적음)
        group.MapPost("/", async (
            [FromForm] IFormFileCollection files,
            [FromForm] string? name,
            [FromForm] string combos,
            SettingsStore settings,
            JobFactory factory,
            SystemInfoService systemInfo,
            IOptions<JobOptions> jobOptions,
            AppDbContext db,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            List<PipelineSettings>? comboList;
            try
            {
                comboList = JsonSerializer.Deserialize<List<PipelineSettings>>(combos, PipelineSettings.Json);
            }
            catch (JsonException ex)
            {
                return Results.Problem($"조합 형식 오류: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }
            if (files.Count is 0 or > MaxFiles)
            {
                return Results.Problem($"문서는 1~{MaxFiles}장이어야 합니다", statusCode: StatusCodes.Status400BadRequest);
            }
            if (comboList is null || comboList.Count is 0 or > MaxCombos)
            {
                return Results.Problem($"조합은 1~{MaxCombos}개여야 합니다", statusCode: StatusCodes.Status400BadRequest);
            }
            if (files.Select(factory.Validate).FirstOrDefault(e => e is not null) is { } fileError)
            {
                return Results.Problem(fileError, statusCode: StatusCodes.Status400BadRequest);
            }
            if (comboList.Select(settings.Validate).FirstOrDefault(e => e is not null) is { } comboError)
            {
                return Results.Problem(comboError, statusCode: StatusCodes.Status400BadRequest);
            }
            var jobCount = files.Count * comboList.Count;
            if (jobCount > factory.QueueRoom(jobOptions.Value.QueueCapacity))
            {
                return Results.Problem($"작업 큐 여유가 부족합니다 (필요 {jobCount}건). 문서나 조합 수를 줄이거나 잠시 후 다시 시도하세요",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var env = await systemInfo.CollectAsync(ct);
            var experiment = new Experiment
            {
                Id = Guid.CreateVersion7(),
                JobType = TextModule.ModuleKey,
                Name = string.IsNullOrWhiteSpace(name) ? $"실험 {clock.GetLocalNow():MM-dd HH:mm}" : name.Trim(),
                Combos = JsonSerializer.Serialize(comboList, PipelineSettings.Json),
                DocCount = files.Count,
                Environment = JsonSerializer.Serialize(new { env.Cpu, env.MemoryGb, env.Gpus, env.OllamaVersion, env.OcrEngines },
                    JsonSerializerOptions.Web),
                CreatedAt = clock.GetUtcNow(),
            };
            db.Add(experiment);

            var jobs = new List<Job>();
            for (var i = 0; i < comboList.Count; i++)
            {
                foreach (var file in files)
                {
                    jobs.Add(await factory.CreateAsync(
                        file, TextModule.ModuleKey, comboList[i].Model, comboList[i].ToJson(), experiment.Id, i, ct));
                }
            }
            await db.SaveChangesAsync(ct);
            await factory.EnqueueAsync(jobs, ct);
            return Results.Accepted($"/api/text/experiments/{experiment.Id}", new { experiment.Id, experiment.Name, Jobs = jobs.Count });
        })
        .DisableAntiforgery()
        .WithMetadata(new RequestSizeLimitAttribute(maxBodyBytes))
        .WithFormOptions(multipartBodyLengthLimit: maxBodyBytes);

        group.MapGet("/", async (AppDbContext db, ExperimentScorer scorer, int? take, CancellationToken ct) =>
        {
            var experiments = await db.Set<Experiment>().AsNoTracking()
                .Where(e => e.JobType == TextModule.ModuleKey)
                .OrderByDescending(e => e.CreatedAt).Take(Math.Clamp(take ?? 30, 1, 100)).ToListAsync(ct);
            var list = new List<ExperimentSummaryDto>();
            foreach (var e in experiments)
            {
                list.Add(ExperimentScorer.Summarize(await scorer.ScoreAsync(e, ct)));
            }
            return list;
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, ExperimentScorer scorer, CancellationToken ct) =>
            await db.Set<Experiment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.JobType == TextModule.ModuleKey, ct) is { } experiment
                ? Results.Json(await scorer.ScoreAsync(experiment, ct), PipelineSettings.Json)
                : Results.NotFound());

        // 실험의 조합 하나를 문서 처리 기본 설정으로 적용
        group.MapPost("/{id:guid}/combos/{index:int}/apply", async (
            Guid id, int index, AppDbContext db, SettingsStore store, ExperimentScorer scorer, CancellationToken ct) =>
        {
            var experiment = await db.Set<Experiment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            var combos = experiment?.ComboList();
            if (experiment is null || combos is null || index < 0 || index >= combos.Count)
            {
                return Results.NotFound();
            }
            var detail = await scorer.ScoreAsync(experiment, ct);
            var combo = detail.Combos[index];
            var note = $"실험 '{experiment.Name}' ({experiment.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}) 조합 {index + 1}"
                + (combo.Rank is { } rank ? $" · {rank}위 (점수 {combo.Score:0.000})" : "")
                + (detail.RecommendedIndex == index ? " · 추천" : "");
            return Results.Json(await store.SetDefaultAsync(combos[index], note, ct), PipelineSettings.Json);
        });

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var experiment = await db.Set<Experiment>().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (experiment is null)
            {
                return Results.NotFound();
            }
            if (await db.Jobs.AnyAsync(j => j.ExperimentId == id && j.Status != JobStatus.Completed && j.Status != JobStatus.Failed, ct))
            {
                return Results.Problem("진행 중인 작업이 있는 실험은 삭제할 수 없습니다", statusCode: StatusCodes.Status409Conflict);
            }
            // 작업(jobs)은 문서 처리 기록으로 남기고 실험 연결만 끊음
            await db.Jobs.Where(j => j.ExperimentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExperimentId, (Guid?)null), ct);
            db.Remove(experiment);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
