using Api.Shared.Data;
using Api.Shared.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Api.Shared.Jobs;

/// <summary>큐에서 jobId 를 하나씩 꺼내 모듈 핸들러로 처리한다 (한 번에 1건: GPU·Ollama 자원 보호).</summary>
public sealed class JobWorker(JobQueue queue, IServiceScopeFactory scopes, TimeProvider clock, ILogger<JobWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var reporter = scope.ServiceProvider.GetRequiredService<JobReporter>();
            var prompts = scope.ServiceProvider.GetRequiredService<PromptUsage>();

            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
            if (job is null || job.IsFinished)
            {
                continue;
            }

            try
            {
                var handler = scope.ServiceProvider.GetKeyedService<IJobHandler>(job.JobType)
                    ?? throw new InvalidOperationException($"'{job.JobType}' 모듈이 비활성화되어 있습니다");
                job.StartedAt = clock.GetUtcNow();
                log.LogInformation("Job {JobId} ({JobType}) 시작: {FileName}", job.Id, job.JobType, job.FileName);

                var message = await handler.HandleAsync(job, reporter, stoppingToken);
                job.Prompts = prompts.ToJson();

                await reporter.SetStatusAsync(job, JobStatus.Completed, message, stoppingToken);
                log.LogInformation("Job {JobId} 완료: {Message}", job.Id, message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 서버 종료 중: 다음 시작 때 JobRecovery 가 Failed 로 정리
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Job {JobId} 실패", job.Id);
                job.Prompts = prompts.ToJson(); // 실패해도 어디까지 어떤 프롬프트를 썼는지 남김
                await reporter.FailAsync(job, ex.Message, CancellationToken.None);
            }
        }
    }
}
