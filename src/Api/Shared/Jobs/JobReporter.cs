using Api.Shared.Data;
using Api.Shared.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Api.Shared.Jobs;

/// <summary>Job 상태를 DB 에 저장하고 같은 내용을 SignalR 로 구독자에게 푸시한다.</summary>
public sealed class JobReporter(AppDbContext db, IHubContext<JobHub> hub, TimeProvider clock)
{
    public async Task SetStatusAsync(Job job, JobStatus status, string message, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        job.Status = status;
        job.StatusMessage = message;
        job.UpdatedAt = now;
        if (job.IsFinished)
        {
            job.CompletedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await hub.Clients.Group(JobHub.GroupName(job.Id)).SendAsync(JobHub.JobUpdated, JobDto.From(job), ct);
    }

    public Task FailAsync(Job job, string error, CancellationToken ct = default)
    {
        job.Error = error;
        return SetStatusAsync(job, JobStatus.Failed, $"실패: {error}", ct);
    }
}
