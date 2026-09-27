using System.Diagnostics;
using System.Text;

namespace Api.Modules.Text.Release;

/// <summary>
/// 배포 화면의 "설치 파일 만들기": installer\build.ps1 ➔ installer\release-notes.ps1 을 이 PC 에서 실행 (한 번에 하나).
/// 진행 로그는 줄 단위로 모아 화면이 이어 받음 (from 이후 줄). 평가 도구를 끄면 빌드도 같이 끝냄
/// </summary>
public sealed class ReleaseBuilder(IHostApplicationLifetime lifetime, ILogger<ReleaseBuilder> logger)
{
    public const int MaxLines = 20_000;

    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];
    private Process? _process;

    public string State { get; private set; } = "idle";  // idle | running | succeeded | failed
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? Tag { get; private set; }

    public sealed record Snapshot(string State, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Tag, int Next, IReadOnlyList<string> Lines);

    public Snapshot Read(int from)
    {
        lock (_gate)
        {
            from = Math.Clamp(from, 0, _lines.Count);
            return new Snapshot(State, StartedAt, FinishedAt, Tag, _lines.Count, _lines.Skip(from).ToList());
        }
    }

    /// <summary>빌드 시작. 이미 돌고 있으면 false</summary>
    public bool Start(string repo, string version)
    {
        lock (_gate)
        {
            if (State == "running") return false;
            _lines.Clear();
            State = "running";
            StartedAt = DateTimeOffset.Now;
            FinishedAt = null;
            Tag = $"digitizer-v{version}";
        }
        _ = Task.Run(() => RunAsync(repo, Tag!));
        return true;
    }

    private async Task RunAsync(string repo, string tag)
    {
        var ok = false;
        try
        {
            ok = await RunScriptAsync(repo, "installer\\build.ps1", "")
                 && await RunScriptAsync(repo, "installer\\release-notes.ps1", $"-Ref {tag}");
        }
        catch (Exception ex)
        {
            Add($"[오류] {ex.Message}");
            logger.LogError(ex, "설치 파일 빌드 실패");
        }
        lock (_gate)
        {
            State = ok ? "succeeded" : "failed";
            FinishedAt = DateTimeOffset.Now;
            _process = null;
        }
        Add(ok ? "== 완료 ==" : "== 실패 (위 로그 확인) ==");
    }

    /// <summary>Windows PowerShell 5.1 (설치 스크립트를 시험한 셸) 로 실행, 출력은 UTF-8 로 받음 (기본 코드 페이지면 한글이 깨짐)</summary>
    private async Task<bool> RunScriptAsync(string repo, string script, string args)
    {
        Add($"> {script} {args}".TrimEnd());
        var info = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                     $"[Console]::OutputEncoding = [Text.Encoding]::UTF8; & '{Path.Combine(repo, script)}' {args}; exit $LASTEXITCODE" })
            info.ArgumentList.Add(a);
        info.Environment["DOTNET_CLI_UI_LANGUAGE"] = "ko";

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{script} 를 실행하지 못했습니다");
        lock (_gate) _process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Add(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var stop = lifetime.ApplicationStopping.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        await process.WaitForExitAsync();
        Add($"(종료 코드 {process.ExitCode})");
        return process.ExitCode == 0;
    }

    private void Add(string line)
    {
        lock (_gate)
        {
            if (_lines.Count < MaxLines) _lines.Add(line);
        }
    }
}
