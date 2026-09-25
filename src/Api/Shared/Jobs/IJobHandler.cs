namespace Api.Shared.Jobs;

/// <summary>
/// 모듈별 작업 처리기. 모듈이 AddKeyedScoped&lt;IJobHandler, T&gt;(모듈 키) 로 등록하면
/// 워커가 Job.JobType 으로 찾아 실행한다.
/// </summary>
public interface IJobHandler
{
    /// <summary>중간 단계는 reporter 로 알리고, 완료 메시지를 반환한다 (실패는 예외로).</summary>
    Task<string> HandleAsync(Job job, JobReporter reporter, CancellationToken ct);
}
