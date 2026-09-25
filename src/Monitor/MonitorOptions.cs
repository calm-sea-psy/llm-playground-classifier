namespace Monitor;

public sealed class MonitorOptions
{
    /// <summary>저장소 루트 (ContentRoot 기준 상대 경로). 대상의 Command·Cwd 는 이 경로 기준</summary>
    public string RepoRoot { get; set; } = "../..";
    public int IntervalSeconds { get; set; } = 10;
    /// <summary>체크가 한 번 실패하면 다음 확인을 이 간격으로 앞당김 (Down 판정을 30초 ➔ 약 16초로)</summary>
    public int FailureRecheckSeconds { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 3;
    /// <summary>연속 실패 이 횟수면 Down</summary>
    public int FailuresToDown { get; set; } = 3;
    /// <summary>켜진 Step (API Features 와 같은 키). 대상의 RequiredBy 와 겹칠 때만 감시</summary>
    public List<string> EnabledSteps { get; set; } = ["text"];
    public RestartPolicy Restart { get; set; } = new();
    public WebhookOptions Webhook { get; set; } = new();
    /// <summary>Monitor 종료 시 자신이 띄운 프로세스도 종료할지 (기본: 남겨 둠)</summary>
    public bool KillChildrenOnExit { get; set; }
    public List<TargetOptions> Targets { get; set; } = [];
}

public sealed class RestartPolicy
{
    /// <summary>재시작 대기: 5초 ➔ 10초 ➔ 20초 … 최대 MaxBackoffSeconds</summary>
    public int InitialBackoffSeconds { get; set; } = 5;
    public int MaxBackoffSeconds { get; set; } = 120;
    /// <summary>WindowMinutes 안에 MaxAttempts 번을 넘으면 자동 재시작 중단 + "수동 조치 필요" 알림 1회</summary>
    public int MaxAttempts { get; set; } = 5;
    public int WindowMinutes { get; set; } = 10;
}

public sealed class WebhookOptions
{
    /// <summary>Discord·Slack 호환 웹훅 URL. 비어 있으면 끔 (본문에 content 와 text 를 함께 보냄)</summary>
    public string? Url { get; set; }
}

public sealed class TargetOptions
{
    public required string Name { get; set; }
    /// <summary>http(s)://... (2xx = 정상) 또는 tcp://host:port (접속되면 정상)</summary>
    public required string Check { get; set; }
    /// <summary>응답 본문에 이 문자열이 없으면 Degraded (예: Ollama 에 선정 모델이 없음)</summary>
    public string? DegradedUnlessContains { get; set; }
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public string Cwd { get; set; } = ".";
    public Dictionary<string, string> Env { get; set; } = [];
    /// <summary>실행 후 바로 끝나는 명령 (docker compose up -d). 프로세스를 추적하지 않음</summary>
    public bool OneShot { get; set; }
    /// <summary>시작 직후 이 시간 동안은 체크 실패를 세지 않음 (모델 로딩 등)</summary>
    public int StartupSeconds { get; set; } = 30;
    public bool Restart { get; set; }
    public List<string> DependsOn { get; set; } = [];
    public List<string> RequiredBy { get; set; } = [];
}
