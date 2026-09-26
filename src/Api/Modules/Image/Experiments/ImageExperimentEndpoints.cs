using System.Text.Json;
using Api.Shared.Data;
using Api.Shared.Experiments;
using Api.Shared.Jobs;
using Api.Shared.Storage;
using Api.Shared.SystemInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Modules.Image.Experiments;

/// <summary>X-ray 모델 비교 실험 API (/api/image/experiments). Text 실험(ExperimentEndpoints)과 같은 흐름, 조합·채점만 이미지용</summary>
public static class ImageExperimentEndpoints
{
    public const int MaxFiles = 50;
    public const int MaxCombos = 4;

    public static void Map(RouteGroupBuilder group)
    {
        // 요청 본문 한도: X-ray PNG 는 장당 수 MB ➔ 파일 수 × 파일당 한도 + 여유 (Kestrel 기본 30MB 로는 413)
        var maxUploadMb = ((IEndpointRouteBuilder)group).ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value.MaxUploadMb;
        long maxBodyBytes = (long)MaxFiles * maxUploadMb * 1024 * 1024 + 1024 * 1024;

        group.MapPost("/", async (
            [FromForm] IFormFileCollection files,
            [FromForm] string? name,
            [FromForm] string? description,
            [FromForm] string combos,
            ImageSettingsStore store,
            JobFactory factory,
            SystemInfoService systemInfo,
            IOptions<JobOptions> jobOptions,
            AppDbContext db,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            List<ImageSettings>? comboList;
            try
            {
                comboList = JsonSerializer.Deserialize<List<ImageSettings>>(combos, ImageSettings.Json);
            }
            catch (JsonException ex)
            {
                return Results.Problem($"조합 형식 오류: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }
            if (description?.Length > 2000)
            {
                return Results.Problem("설명은 2,000자 이하여야 합니다", statusCode: StatusCodes.Status400BadRequest);
            }
            if (files.Count is 0 or > MaxFiles)
            {
                return Results.Problem($"이미지는 1~{MaxFiles}장이어야 합니다", statusCode: StatusCodes.Status400BadRequest);
            }
            if (comboList is null || comboList.Count is 0 or > MaxCombos)
            {
                return Results.Problem($"조합은 1~{MaxCombos}개여야 합니다", statusCode: StatusCodes.Status400BadRequest);
            }
            if (files.Select(factory.Validate).FirstOrDefault(e => e is not null) is { } fileError)
            {
                return Results.Problem(fileError, statusCode: StatusCodes.Status400BadRequest);
            }
            if (comboList.Select(store.Validate).FirstOrDefault(e => e is not null) is { } comboError)
            {
                return Results.Problem(comboError, statusCode: StatusCodes.Status400BadRequest);
            }
            var jobCount = files.Count * comboList.Count;
            if (jobCount > factory.QueueRoom(jobOptions.Value.QueueCapacity))
            {
                return Results.Problem($"작업 큐 여유가 부족합니다 (필요 {jobCount}건)", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var env = await systemInfo.CollectAsync(ct);
            var experiment = new Experiment
            {
                Id = Guid.CreateVersion7(),
                JobType = ImageModule.ModuleKey,
                Name = string.IsNullOrWhiteSpace(name) ? $"이미지 실험 {clock.GetLocalNow():MM-dd HH:mm}" : name.Trim(),
                Description = Clean(description),
                Combos = JsonSerializer.Serialize(comboList, ImageSettings.Json),
                DocCount = files.Count,
                Environment = JsonSerializer.Serialize(new { env.Cpu, env.MemoryGb, env.Gpus, env.OllamaVersion },
                    JsonSerializerOptions.Web),
                CreatedAt = clock.GetUtcNow(),
            };
            db.Add(experiment);

            // 조합 순서대로 (같은 VLM 모델이 이어서 돌아 재적재가 적음)
            var jobs = new List<Job>();
            for (var i = 0; i < comboList.Count; i++)
            {
                foreach (var file in files)
                {
                    jobs.Add(await factory.CreateAsync(
                        file, ImageModule.ModuleKey, comboList[i].Model, comboList[i].ToJson(), experiment.Id, i, ct));
                }
            }
            await db.SaveChangesAsync(ct);
            await factory.EnqueueAsync(jobs, ct);
            return Results.Accepted($"/api/image/experiments/{experiment.Id}", new { experiment.Id, experiment.Name, Jobs = jobs.Count });
        })
        .DisableAntiforgery()
        .WithMetadata(new RequestSizeLimitAttribute(maxBodyBytes))
        .WithFormOptions(multipartBodyLengthLimit: maxBodyBytes);

        group.MapGet("/", async (AppDbContext db, ImageExperimentScorer scorer, int? take, CancellationToken ct) =>
        {
            var experiments = await db.Set<Experiment>().AsNoTracking()
                .Where(e => e.JobType == ImageModule.ModuleKey)
                .OrderByDescending(e => e.CreatedAt).Take(Math.Clamp(take ?? 30, 1, 100)).ToListAsync(ct);
            var list = new List<ImageExperimentSummaryDto>();
            foreach (var e in experiments)
            {
                list.Add(ImageExperimentScorer.Summarize(await scorer.ScoreAsync(e, ct)));
            }
            return list;
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, ImageExperimentScorer scorer, CancellationToken ct) =>
            await db.Set<Experiment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id && e.JobType == ImageModule.ModuleKey, ct)
                is { } experiment
                ? Results.Json(await scorer.ScoreAsync(experiment, ct), ImageSettings.Json)
                : Results.NotFound());

        // 조합 하나를 X-ray 판독 기본 설정으로 적용 (대상은 업로드 때 고르므로 기본 대상만 바뀜)
        group.MapPost("/{id:guid}/combos/{index:int}/apply", async (
            Guid id, int index, AppDbContext db, ImageSettingsStore store, ImageExperimentScorer scorer, CancellationToken ct) =>
        {
            var experiment = await db.Set<Experiment>().AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id && e.JobType == ImageModule.ModuleKey, ct);
            var combos = experiment?.ImageCombos();
            if (experiment is null || combos is null || index < 0 || index >= combos.Count)
            {
                return Results.NotFound();
            }
            var detail = await scorer.ScoreAsync(experiment, ct);
            var combo = detail.Combos[index];
            var note = $"실험 '{experiment.Name}' ({experiment.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}) 조합 {index + 1}"
                + (combo.Rank is { } rank ? $" · {rank}위 (적용 당시 점수 {combo.Score:0.000})" : "")
                + (detail.RecommendedIndex == index ? " · 추천" : "");
            return Results.Json(await store.SetDefaultAsync(combos[index], note, ct), ImageSettings.Json);
        });

        // 실험 설명 고치기 (끝난 실험에도: 문서를 어떻게 골랐는지 등)
        group.MapPut("/{id:guid}/description", async (Guid id, DescriptionRequest request, AppDbContext db, CancellationToken ct) =>
        {
            var experiment = await db.Set<Experiment>().FirstOrDefaultAsync(e => e.Id == id && e.JobType == ImageModule.ModuleKey, ct);
            if (experiment is null)
            {
                return Results.NotFound();
            }
            if (request.Description?.Length > 2000)
            {
                return Results.Problem("설명은 2,000자 이하여야 합니다", statusCode: StatusCodes.Status400BadRequest);
            }
            experiment.Description = Clean(request.Description);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var experiment = await db.Set<Experiment>().FirstOrDefaultAsync(e => e.Id == id && e.JobType == ImageModule.ModuleKey, ct);
            if (experiment is null)
            {
                return Results.NotFound();
            }
            if (await db.Jobs.AnyAsync(j => j.ExperimentId == id && j.Status != JobStatus.Completed && j.Status != JobStatus.Failed, ct))
            {
                return Results.Problem("진행 중인 작업이 있는 실험은 삭제할 수 없습니다", statusCode: StatusCodes.Status409Conflict);
            }
            await db.Jobs.Where(j => j.ExperimentId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExperimentId, (Guid?)null), ct);
            db.Remove(experiment);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
