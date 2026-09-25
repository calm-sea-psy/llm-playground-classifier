using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Monitor;

/// <summary>상태 페이지·API 가 읽는 스냅샷 + 수동 재시작 요청 창구</summary>
public sealed class MonitorState
{
    private volatile IReadOnlyList<TargetDto> _snapshot = [];
    internal readonly Channel<string> RestartRequests = Channel.CreateUnbounded<string>();
    internal readonly Channel<bool> Wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public IReadOnlyList<TargetDto> Snapshot => _snapshot;
    public DateTimeOffset? LastTickAt { get; internal set; }

    internal void Publish(IReadOnlyList<TargetDto> snapshot) => _snapshot = snapshot;

    public bool RequestRestart(string name)
    {
        if (_snapshot.All(t => t.Name != name))
        {
            return false;
        }
        RestartRequests.Writer.TryWrite(name);
        Wake.Writer.TryWrite(true);
        return true;
    }
}

/// <summary>
/// HealthChecker ➔ 상태 판정(Up/Warning/Down) ➔ Notifier ➔ ProcessManager(재시작) 를 주기적으로 돈다 (todo 6번).
/// - 연속 FailuresToDown 회 실패 또는 Monitor 가 띄운 프로세스 종료 ➔ Down
/// - 응답은 있지만 Degraded, 또는 의존 항목이 Down ➔ Warning
/// - 상태가 바뀔 때만 알림 (같은 상태 반복은 알리지 않음)
/// - 재시작: 의존 항목이 올라올 때까지 대기, 지수 백오프, 한도 초과 시 중단 + "수동 조치 필요" 1회
/// </summary>
public sealed class MonitorWorker(
    HealthChecker checker,
    ProcessManager processes,
    Notifier notifier,
    EventStore events,
    MonitorState state,
    IOptions<MonitorOptions> options,
    TimeProvider clock,
    ILogger<MonitorWorker> log) : BackgroundService
{
    private readonly ConcurrentQueue<string> _exited = new();
    private List<TargetRuntime> _targets = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _targets = Order(options.Value).Select(t => new TargetRuntime(t)).ToList();
        log.LogInformation("감시 대상 (시작 순서): {Targets} / 저장소: {Root}",
            string.Join(" ➔ ", _targets.Select(t => t.Options.Name)), processes.RepoRoot);
        processes.Exited += name =>
        {
            _exited.Enqueue(name);
            state.Wake.Writer.TryWrite(true);
        };
        await events.LoadRecentAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "감시 주기 처리 중 오류");
            }
            // 다음 주기: 기본 IntervalSeconds, 실패 중인 대상이 있으면 FailureRecheckSeconds,
            // 예정된 재시작이 더 빠르면 그 시각에 맞춰 깨어남
            var wait = TimeSpan.FromSeconds(_targets.Any(t => t.ConsecutiveFailures > 0 && t.State is TargetState.Up or TargetState.Warning)
                ? options.Value.FailureRecheckSeconds
                : options.Value.IntervalSeconds);
            var now = clock.GetUtcNow();
            foreach (var t in _targets.Where(t => t is { State: TargetState.Down, GaveUp: false, NextRestartAt: not null } && t.Options.Restart))
            {
                var until = t.NextRestartAt!.Value - now;
                if (until < wait)
                {
                    wait = until < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : until;
                }
            }
            using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delay.CancelAfter(wait);
            try
            {
                await state.Wake.Reader.ReadAsync(delay.Token); // 프로세스 종료·수동 재시작이면 바로 다음 주기
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (options.Value.KillChildrenOnExit)
        {
            processes.StopAllOwned();
        }
        return base.StopAsync(cancellationToken);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        while (_exited.TryDequeue(out var name))
        {
            if (Find(name) is { } exited)
            {
                exited.ProcessExited = true;
            }
        }
        var manual = new HashSet<string>();
        while (state.RestartRequests.Reader.TryRead(out var name))
        {
            manual.Add(name);
        }

        // 체크는 동시에, 판정은 의존 순서대로 (의존 항목의 이번 주기 상태를 보고 Warning 결정)
        var results = await Task.WhenAll(_targets.Select(t => checker.CheckAsync(t.Options, ct)));
        for (var i = 0; i < _targets.Count; i++)
        {
            try
            {
                await ProcessTargetAsync(_targets[i], results[i], now, manual, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 한 대상의 오류가 나머지 대상 처리를 막지 않도록
                log.LogError(ex, "{Target} 처리 중 오류", _targets[i].Options.Name);
            }
        }

        state.Publish(_targets.Select(t => t.ToDto(processes.OwnedPid(t.Options.Name))).ToList());
        state.LastTickAt = now;
    }

    private async Task ProcessTargetAsync(
        TargetRuntime t, CheckResult result, DateTimeOffset now, HashSet<string> manual, CancellationToken ct)
    {
        t.LastHealth = result.Health;
        t.LastCheckAt = now;
        t.LastCheckMs = result.ElapsedMs;

        var previous = t.State;
        var (next, reason) = Judge(t, result, now);
        t.ProcessExited = false;

        if (next is TargetState.Up && DownDependencies(t) is { Count: > 0 } downDeps)
        {
            (next, reason) = (TargetState.Warning, $"의존 항목 중단: {string.Join(", ", downDeps)}");
        }
        t.State = next;
        t.Reason = reason;
        await OnTransitionAsync(t, previous, now, ct);

        if (manual.Contains(t.Options.Name))
        {
            await ManualRestartAsync(t, now, ct);
        }
        else
        {
            await MaybeRestartAsync(t, now, ct);
        }
    }

    private (TargetState, string?) Judge(TargetRuntime t, CheckResult result, DateTimeOffset now)
    {
        switch (result.Health)
        {
            case Health.Healthy:
                t.ConsecutiveFailures = 0;
                return (TargetState.Up, null);
            case Health.Degraded:
                t.ConsecutiveFailures = 0;
                return (TargetState.Warning, result.Detail);
        }

        t.ConsecutiveFailures++;
        var inStartup = t.StartedAt is { } started && now - started < TimeSpan.FromSeconds(t.Options.StartupSeconds);
        if (t.ProcessExited)
        {
            return (TargetState.Down, "프로세스 종료됨");
        }
        if (inStartup)
        {
            var elapsed = (int)(now - t.StartedAt!.Value).TotalSeconds;
            return (TargetState.Starting, $"시작 중 {elapsed}/{t.Options.StartupSeconds}초 ({result.Detail})");
        }
        // Monitor 를 켰을 때 이미 꺼져 있던 대상은 3회를 기다리지 않고 바로 Down (기동 시 스택 올리기)
        if (t.State is TargetState.Unknown or TargetState.Starting or TargetState.Down
            || t.ConsecutiveFailures >= options.Value.FailuresToDown)
        {
            return (TargetState.Down, $"{result.Detail} ({t.ConsecutiveFailures}회 연속)");
        }
        return (t.State, $"체크 실패 {t.ConsecutiveFailures}/{options.Value.FailuresToDown}: {result.Detail}");
    }

    private async Task OnTransitionAsync(TargetRuntime t, TargetState previous, DateTimeOffset now, CancellationToken ct)
    {
        if (t.State == previous)
        {
            return;
        }
        var name = t.Options.Name;
        switch (t.State)
        {
            case TargetState.Down:
                // 다음 재시작까지 대기: 기동 직후 발견이면 즉시, 아니면 5초 ➔ 10초 ➔ 20초 …
                t.NextRestartAt = previous == TargetState.Unknown ? now : now + Backoff(t.BackoffStep);
                var kind = previous == TargetState.Starting ? "RestartFailed" : "Down";
                var message = previous == TargetState.Starting
                    ? $"재시작 실패: {t.Reason}"
                    : $"Down: {t.Reason}" + (t.Options.Restart ? "" : " (자동 재시작 안 함)");
                await notifier.NotifyAsync(name, kind, previous, t.State, message, ct);
                break;
            case TargetState.Up or TargetState.Warning when previous == TargetState.Starting:
                await notifier.NotifyAsync(name, "RestartSucceeded", previous, t.State,
                    $"재시작 성공 ({(int)(now - t.StartedAt!.Value).TotalSeconds}초)", ct);
                ResetRestart(t);
                break;
            case TargetState.Up when previous == TargetState.Down:
                await notifier.NotifyAsync(name, "Up", previous, t.State, "복구됨", ct);
                ResetRestart(t);
                break;
            case TargetState.Warning:
                await notifier.NotifyAsync(name, "Warning", previous, t.State, $"Warning: {t.Reason}", ct);
                break;
            case TargetState.Up when previous == TargetState.Warning:
                await notifier.NotifyAsync(name, "Up", previous, t.State, "Warning 해제", ct);
                break;
        }
        await notifier.PublishStateAsync(t.ToDto(processes.OwnedPid(name)), ct);
    }

    private async Task MaybeRestartAsync(TargetRuntime t, DateTimeOffset now, CancellationToken ct)
    {
        if (t.State != TargetState.Down || !t.Options.Restart || t.GaveUp || t.Options.Command is null)
        {
            return;
        }
        var waiting = t.Options.DependsOn.Where(d => Find(d) is { State: not (TargetState.Up or TargetState.Warning) }).ToList();
        if (waiting.Count > 0)
        {
            t.Reason += $" · 의존 항목 대기: {string.Join(", ", waiting)}";
            return;
        }
        if (t.NextRestartAt is { } at && at > now)
        {
            t.Reason += $" · {(int)(at - now).TotalSeconds}초 뒤 재시작";
            return;
        }

        var policy = options.Value.Restart;
        t.RestartAttempts.RemoveAll(a => now - a > TimeSpan.FromMinutes(policy.WindowMinutes));
        if (t.RestartAttempts.Count >= policy.MaxAttempts)
        {
            t.GaveUp = true;
            t.Reason = $"자동 재시작 중단: {policy.WindowMinutes}분 안에 {policy.MaxAttempts}회 실패";
            await notifier.NotifyAsync(t.Options.Name, "GaveUp", t.State, t.State,
                $"{t.Reason}. 수동 조치 필요 (상태 페이지에서 재시작 가능)", ct);
            await notifier.PublishStateAsync(t.ToDto(null), ct);
            return;
        }
        await StartAsync(t, now, $"재시작 시도 {t.RestartAttempts.Count + 1}/{policy.MaxAttempts}", ct);
    }

    /// <summary>상태 페이지의 재시작 버튼: 한도·백오프를 초기화하고 즉시 종료 ➔ 시작</summary>
    private async Task ManualRestartAsync(TargetRuntime t, DateTimeOffset now, CancellationToken ct)
    {
        if (t.Options.Command is null)
        {
            return;
        }
        t.GaveUp = false;
        t.RestartAttempts.Clear();
        t.BackoffStep = 0;
        await StartAsync(t, now, "수동 재시작", ct);
    }

    private async Task StartAsync(TargetRuntime t, DateTimeOffset now, string message, CancellationToken ct)
    {
        var previous = t.State;
        try
        {
            processes.Stop(t.Options);
            processes.Start(t.Options);
            t.RestartAttempts.Add(now);
            t.RestartCount++;
            t.BackoffStep++;
            t.StartedAt = now;
            t.ConsecutiveFailures = 0;
            t.State = TargetState.Starting;
            t.Reason = message;
            await notifier.NotifyAsync(t.Options.Name, "RestartAttempt", previous, t.State, message, ct);
        }
        catch (Exception ex)
        {
            t.RestartAttempts.Add(now);
            t.NextRestartAt = now + Backoff(t.BackoffStep++);
            await notifier.NotifyAsync(t.Options.Name, "RestartFailed", previous, previous, $"시작 명령 실패: {ex.Message}", ct);
        }
        await notifier.PublishStateAsync(t.ToDto(processes.OwnedPid(t.Options.Name)), ct);
    }

    private void ResetRestart(TargetRuntime t)
    {
        t.BackoffStep = 0;
        t.NextRestartAt = null;
        t.GaveUp = false;
    }

    private TimeSpan Backoff(int step)
    {
        var policy = options.Value.Restart;
        var seconds = Math.Min(policy.MaxBackoffSeconds, policy.InitialBackoffSeconds * Math.Pow(2, step));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Down 이거나 재시작 중(Starting)인 의존 항목 ➔ 완전히 복구될 때까지 이 대상도 Warning 유지</summary>
    private List<string> DownDependencies(TargetRuntime t) =>
        t.Options.DependsOn
            .Where(d => Find(d) is { State: TargetState.Down or TargetState.Starting })
            .Select(d => Find(d)!.State == TargetState.Starting ? $"{d}(재시작 중)" : d)
            .ToList();

    private TargetRuntime? Find(string name) => _targets.FirstOrDefault(t => t.Options.Name == name);

    /// <summary>켜진 Step 이 요구하는 대상만 골라 의존 순서(postgres ➔ ocr ➔ ollama ➔ api ➔ web)로 정렬</summary>
    private static List<TargetOptions> Order(MonitorOptions options)
    {
        var active = options.Targets
            .Where(t => t.RequiredBy.Count == 0 || t.RequiredBy.Intersect(options.EnabledSteps, StringComparer.OrdinalIgnoreCase).Any())
            .ToList();
        var names = active.Select(t => t.Name).ToHashSet();
        foreach (var t in active)
        {
            t.DependsOn = t.DependsOn.Where(names.Contains).ToList();
        }

        var ordered = new List<TargetOptions>();
        var remaining = active.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(t => t.DependsOn.All(d => ordered.Any(o => o.Name == d))).ToList();
            if (ready.Count == 0)
            {
                throw new InvalidOperationException(
                    $"monitor.json 의존 관계에 순환이 있습니다: {string.Join(", ", remaining.Select(t => t.Name))}");
            }
            ordered.AddRange(ready);
            remaining.RemoveAll(ready.Contains);
        }
        return ordered;
    }
}
