namespace Monitor;

public enum TargetState
{
    Unknown,
    Starting,
    Up,
    Warning,
    Down,
    /// <summary>사용자가 중지함 (감시 시작 · 시작 버튼 전까지 자동 재시작 안 함)</summary>
    Stopped,
}

/// <summary>대상별 실행 상태 (워커 스레드에서만 수정, 화면에는 스냅샷 DTO 로 전달)</summary>
public sealed class TargetRuntime(TargetOptions options)
{
    public TargetOptions Options { get; } = options;
    public TargetState State { get; set; } = TargetState.Unknown;
    public string? Reason { get; set; }
    public Health? LastHealth { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public int LastCheckMs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? NextRestartAt { get; set; }
    public int BackoffStep { get; set; }
    public List<DateTimeOffset> RestartAttempts { get; } = [];
    public int RestartCount { get; set; }
    /// <summary>재시작 한도 초과로 자동 재시작 중단 ("수동 조치 필요")</summary>
    public bool GaveUp { get; set; }
    /// <summary>Monitor 가 띄운 프로세스가 종료됨 ➔ 다음 체크에서 바로 Down</summary>
    public bool ProcessExited { get; set; }
    /// <summary>중지 버튼 · 모두 중지로 멈춤 ➔ 자동 재시작 안 함. 감시 시작 · 시작 버튼이 해제</summary>
    public bool Stopped { get; set; }

    /// <summary>Monitor 가 시작 · 중지하는 대상 (자동 재시작 대상). 아니면 감시만 (예: Ollama)</summary>
    public bool Manageable => Options.Restart && Options.Command is not null;

    public TargetDto ToDto(int? pid) => new(
        Options.Name, State.ToString(), Reason, LastCheckAt, LastCheckMs, ConsecutiveFailures,
        RestartCount, GaveUp, Options.Restart, Manageable, Options.DependsOn, pid, Options.Check);
}

public sealed record TargetDto(
    string Name,
    string State,
    string? Reason,
    DateTimeOffset? LastCheckAt,
    int LastCheckMs,
    int ConsecutiveFailures,
    int RestartCount,
    bool GaveUp,
    bool AutoRestart,
    bool Manageable,
    IReadOnlyList<string> DependsOn,
    int? Pid,
    string Check);
