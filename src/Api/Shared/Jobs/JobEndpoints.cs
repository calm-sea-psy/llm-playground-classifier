using Api.Shared.Data;
using Api.Shared.Storage;
using Microsoft.EntityFrameworkCore;

namespace Api.Shared.Jobs;

/// <summary>모듈 공통 Job 조회 API. 작업 생성은 각 모듈의 /api/{key}/jobs 에서 한다.</summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder api)
    {
        var jobs = api.MapGroup("/jobs");

        jobs.MapGet("/", async (AppDbContext db, string? type, int? take, CancellationToken ct) =>
        {
            var query = db.Jobs.AsNoTracking();
            if (!string.IsNullOrEmpty(type))
            {
                query = query.Where(j => j.JobType == type);
            }
            var list = await query.OrderByDescending(j => j.CreatedAt)
                .Take(Math.Clamp(take ?? 50, 1, 500))
                .ToListAsync(ct);
            return list.Select(JobDto.From);
        });

        jobs.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) is { } job
                ? Results.Ok(JobDto.From(job))
                : Results.NotFound());

        // 결과 화면에서 원본 이미지를 보여주기 위한 용도
        jobs.MapGet("/{id:guid}/file", async (Guid id, AppDbContext db, UploadStorage storage, CancellationToken ct) =>
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null)
            {
                return Results.NotFound();
            }
            var path = storage.PathOf(job.Id, job.StoredFileName);
            return File.Exists(path)
                ? Results.File(path, job.ContentType ?? "application/octet-stream")
                : Results.NotFound();
        });
    }
}
