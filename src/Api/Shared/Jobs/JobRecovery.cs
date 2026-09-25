using Api.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Shared.Jobs;

public static class JobRecovery
{
    /// <summary>큐는 메모리에만 있으므로 재시작 전에 끝나지 않은 작업은 Failed 로 정리한다.</summary>
    public static Task<int> FailUnfinishedJobsAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct = default) =>
        db.Jobs
            .Where(j => j.Status != JobStatus.Completed && j.Status != JobStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.Error, "서버 재시작으로 중단됨")
                .SetProperty(j => j.StatusMessage, "실패: 서버 재시작으로 중단됨")
                .SetProperty(j => j.UpdatedAt, now)
                .SetProperty(j => j.CompletedAt, now), ct);
}
