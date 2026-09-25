using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Monitor;

/// <summary>
/// 대상 프로세스 시작·종료. 출력은 data/monitor/logs/{name}.log 에 추가.
/// Monitor 가 띄운 프로세스는 PID 를 들고 있다가 종료(Exited) 시 즉시 알린다.
/// </summary>
public sealed partial class ProcessManager(IOptions<MonitorOptions> options, IHostEnvironment env, ILogger<ProcessManager> log)
{
    private readonly Dictionary<string, Process> _owned = [];
    private readonly Lock _lock = new();

    public string RepoRoot { get; } = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.RepoRoot));

    /// <summary>프로세스가 끝나면 대상 이름으로 호출 (워커가 다음 체크를 앞당김)</summary>
    public event Action<string>? Exited;

    public int? OwnedPid(string name)
    {
        lock (_lock)
        {
            return _owned.TryGetValue(name, out var p) && !p.HasExited ? p.Id : null;
        }
    }

    public void Start(TargetOptions target)
    {
        if (target.Command is null)
        {
            throw new InvalidOperationException($"{target.Name}: 시작 명령이 없습니다");
        }
        // 저장소 기준 상대 경로 실행 파일은 절대 경로로, 나머지(docker, dotnet, cmd)는 PATH 에서 찾음
        var candidate = Path.Combine(RepoRoot, target.Command);
        var fileName = File.Exists(candidate) ? Path.GetFullPath(candidate) : target.Command;
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = Path.GetFullPath(Path.Combine(RepoRoot, target.Cwd)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in target.Args)
        {
            info.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in target.Env)
        {
            info.Environment[key] = value;
        }

        var logDir = Path.Combine(RepoRoot, "data", "monitor", "logs");
        Directory.CreateDirectory(logDir);
        var writer = new StreamWriter(Path.Combine(logDir, $"{target.Name}.log"), append: true) { AutoFlush = true };
        writer.WriteLine($"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} 시작: {fileName} {string.Join(' ', target.Args)}");

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var writeLock = new Lock();
        void Write(string? line)
        {
            if (line is null) return;
            lock (writeLock) writer.WriteLine(line);
        }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Exited += (_, _) =>
        {
            Write($"==== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} 종료 (코드 {SafeExitCode(process)})");
            writer.Dispose();
            if (!target.OneShot)
            {
                Exited?.Invoke(target.Name);
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        log.LogInformation("{Target} 시작 (PID {Pid})", target.Name, process.Id);

        if (!target.OneShot)
        {
            lock (_lock)
            {
                _owned[target.Name] = process;
            }
        }
    }

    /// <summary>
    /// 남아 있는 프로세스를 종료: Monitor 가 띄운 것은 프로세스 트리째, 아니면 체크 포트를 점유한 프로세스.
    /// "떠 있는데 응답이 없는" 상태에서 새로 띄우면 포트 충돌이 나기 때문
    /// </summary>
    public void Stop(TargetOptions target)
    {
        Process? owned;
        lock (_lock)
        {
            _owned.Remove(target.Name, out owned);
        }
        if (owned is { HasExited: false })
        {
            log.LogInformation("{Target} 종료 (PID {Pid}, 트리째)", target.Name, owned.Id);
            owned.Kill(entireProcessTree: true);
            owned.WaitForExit(5000);
            return;
        }
        if (PortOwner(target.Check) is { } pid && pid != Environment.ProcessId)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                log.LogInformation("{Target} 포트 점유 프로세스 종료 (PID {Pid} {Name})", target.Name, pid, p.ProcessName);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                // 이미 종료됨
            }
        }
    }

    public void StopAllOwned()
    {
        lock (_lock)
        {
            foreach (var p in _owned.Values.Where(p => !p.HasExited))
            {
                p.Kill(entireProcessTree: true);
            }
            _owned.Clear();
        }
    }

    /// <summary>체크 주소의 포트를 LISTENING 중인 PID (Windows netstat). Docker·원격 주소면 null</summary>
    private static int? PortOwner(string check)
    {
        if (!OperatingSystem.IsWindows() || !Uri.TryCreate(check, UriKind.Absolute, out var uri) || !uri.IsLoopback)
        {
            return null;
        }
        using var netstat = Process.Start(new ProcessStartInfo("netstat", "-ano -p TCP")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (netstat is null)
        {
            return null;
        }
        var output = netstat.StandardOutput.ReadToEnd();
        netstat.WaitForExit(5000);
        foreach (Match m in ListeningRegex().Matches(output))
        {
            if (int.Parse(m.Groups["port"].Value) == uri.Port)
            {
                return int.Parse(m.Groups["pid"].Value);
            }
        }
        return null;
    }

    private static string SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode.ToString();
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    [GeneratedRegex(@"TCP\s+(?:127\.0\.0\.1|0\.0\.0\.0|\[::1?\]):(?<port>\d+)\s+\S+\s+LISTENING\s+(?<pid>\d+)")]
    private static partial Regex ListeningRegex();
}
