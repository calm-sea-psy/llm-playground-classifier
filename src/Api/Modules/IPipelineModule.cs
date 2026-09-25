namespace Api.Modules;

/// <summary>
/// Step 모듈 공통 인터페이스 (todo 7번). 모듈 추가 = 이 인터페이스 구현 클래스 1개 + Features 설정 1줄.
/// 비활성 모듈은 서비스·엔드포인트·헬스 체크가 등록되지 않아 /api/{key}/... 는 404 가 된다.
/// </summary>
public interface IPipelineModule
{
    /// <summary>"text" | "image" | "multimodal". Features 설정 키, Job.JobType, URL 경로에 공통으로 사용</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>이 모듈이 켜지면 함께 켜야 하는 모듈 (예: multimodal ➔ text, image)</summary>
    IReadOnlyList<string> DependsOn => [];

    /// <summary>모듈 전용 서비스, IJobHandler(키 = Key), 의존 서비스 헬스 체크 등록</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder health);

    /// <summary>group = /api/{Key}</summary>
    void MapEndpoints(RouteGroupBuilder group);
}
