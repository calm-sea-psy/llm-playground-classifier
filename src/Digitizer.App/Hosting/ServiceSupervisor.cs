using System.Diagnostics;
using Digitizer.App.Processing;

namespace Digitizer.App.Hosting;

/// <summary>처리 전에 OCR 서비스가 준비됐는지 (대기열이 부름). 테스트 · 서비스를 따로 띄운 경우는 항상 준비됨</summary>
public interface IServiceReadiness
{
    Task WaitReadyAsync(CancellationToken ct);
}

public sealed class AlwaysReady : IServiceReadiness
{
    public Task WaitReadyAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// 외부 서비스 관리 (4단계):
/// - OCR: OcrUrl 에 이미 떠 있으면 그대로 씀 (개발 · 따로 띄운 경우). 없으면 OCR 폴더의 .venv 로 uvicorn 을 자식 프로세스로 실행,
///   작업 개체에 넣어 이 프로그램이 끝나면(강제 종료 포함) 같이 끝남. 갑자기 죽으면 3번까지 다시 실행. 출력은 로그 폴더 ocr-날짜.log
/// - Ollama: 꺼져 있으면 설치된 Ollama 를 실행 (Ollama 는 사용자 프로그램이라 끝낼 때 건드리지 않음)
/// 모델을 읽는 동안(수십 초) 대기열은 WaitReadyAsync 에서 기다림 ➔ 시작 직후 문서가 "연결 거부"로 실패하지 않음
/// </summary>
public sealed class ServiceSupervisor(SettingsFile settings, IHttpClientFactory http, AppPaths paths, ILogger<ServiceSupervisor> logger)
    : BackgroundService, IServiceReadiness
{
    public const int MaxRestarts = 3;
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly JobObject? _job = OperatingSystem.IsWindows() ? new JobObject() : null;
    private Process? _ocr;
    private volatile bool _stopping;

    /// <summary>상태 점검 화면용: external (이미 떠 있던 서비스) | managed (프로그램이 실행) | missing (폴더 없음) | starting | failed</summary>
    public string OcrMode { get; private set; } = "starting";
    public string? OcrProblem { get; private set; }

    public async Task WaitReadyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StartTimeout);
        try
        {
            await _ready.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 준비가 안 돼도 처리는 진행 (문서가 실패 사유와 함께 실패 폴더로 ➔ 사용자가 알 수 있음)
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var s = settings.Current;
        if (s.StartOllama) await StartOllamaAsync(stoppingToken);

        if (await HealthyAsync(stoppingToken))
        {
            OcrMode = "external";
            logger.LogInformation("OCR 서비스가 이미 실행 중 ({Url}) ➔ 그대로 씀", s.OcrUrl);
            _ready.TrySetResult();
            return;
        }

        var dir = OcrDir(s);
        var python = Path.Combine(dir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(Path.Combine(dir, "main.py")) || !File.Exists(python))
        {
            OcrMode = "missing";
            OcrProblem = $"OCR 서비스 폴더가 없습니다: {dir}";
            logger.LogWarning("{Problem} ➔ OCR 이 필요한 문서(이미지 · 스캔 PDF)는 실패합니다", OcrProblem);
            _ready.TrySetResult();
            return;
        }

        for (var attempt = 0; attempt <= MaxRestarts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            if (attempt > 0) logger.LogWarning("OCR 서비스가 멈춰 다시 실행합니다 ({Attempt}/{Max})", attempt, MaxRestarts);
            OcrMode = "starting";
            _ocr = StartOcr(dir, python, new Uri(s.OcrUrl).Port);
            if (await WaitHealthyAsync(_ocr, stoppingToken))
            {
                OcrMode = "managed";
                OcrProblem = null;
                logger.LogInformation("OCR 서비스 준비됨 (프로세스 {Pid})", _ocr.Id);
                _ready.TrySetResult();
                await _ocr.WaitForExitAsync(stoppingToken);
                if (_stopping || stoppingToken.IsCancellationRequested) return;
                logger.LogWarning("OCR 서비스가 끝났습니다 (종료 코드 {Code})", _ocr.ExitCode);
            }
            else if (!stoppingToken.IsCancellationRequested)
            {
                OcrProblem = $"OCR 서비스가 {StartTimeout.TotalMinutes:0}분 안에 준비되지 않았습니다 (로그 폴더의 ocr-날짜.log 확인)";
                logger.LogError("{Problem}", OcrProblem);
                Kill(_ocr);
            }
        }
        OcrMode = "failed";
        OcrProblem ??= "OCR 서비스가 계속 멈춥니다 (로그 폴더의 ocr-날짜.log 확인)";
        _ready.TrySetResult();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        Kill(_ocr);
        await base.StopAsync(cancellationToken);
        _job?.Dispose();
    }

    public static string OcrDir(AppSettings s) =>
        s.OcrServiceDir.Length > 0 ? Path.GetFullPath(s.OcrServiceDir) : Path.Combine(AppContext.BaseDirectory, "ocr");

    private Process StartOcr(string dir, string python, int port)
    {
        var logs = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "logs")).FullName;
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", port.ToString() }) info.ArgumentList.Add(a);
        info.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";
        // 설치판: OCR 모델을 설치 폴더 안(ocr\models)에 둠 ➔ 제거하면 같이 지워지고, 한글 · 공백 사용자 이름 경로를 피함 (설치 도우미가 만듦)
        if (Directory.Exists(Path.Combine(dir, "models"))) info.Environment["PADDLE_PDX_CACHE_HOME"] = Path.Combine(dir, "models");
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1";

        var process = Process.Start(info) ?? throw new InvalidOperationException("OCR 서비스를 실행하지 못했습니다");
        _job?.Add(process);
        // OCR 서비스 출력에는 문서 내용이 없음 (요청 로그 · 모델 적재) ➔ 그대로 날짜별 파일에
        var log = new StreamWriter(Path.Combine(logs, $"ocr-{DateTime.Now:yyyyMMdd}.log"), append: true) { AutoFlush = true };
        var gate = new Lock();
        void Write(string? line)
        {
            if (line is null) return;
            lock (gate) log.WriteLine(line);
        }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Exited += (_, _) => { lock (gate) log.Dispose(); };
        process.EnableRaisingEvents = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        logger.LogInformation("OCR 서비스 실행 (프로세스 {Pid}, 포트 {Port})", process.Id, port);
        return process;
    }

    private async Task<bool> WaitHealthyAsync(Process process, CancellationToken ct)
    {
        var until = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested && !process.HasExited)
        {
            if (await HealthyAsync(ct)) return true;
            await Task.Delay(1000, ct);
        }
        return false;
    }

    private async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.CreateClient(OcrSourceReader.OcrClient).GetAsync("health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task StartOllamaAsync(CancellationToken ct)
    {
        var client = http.CreateClient(OcrSourceReader.OllamaClient);
        async Task<bool> Up()
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var r = await client.GetAsync("api/version", timeout.Token);
                return r.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return false;
            }
        }
        if (await Up()) return;

        // 공식 설치 위치의 트레이 앱 (서버를 같이 띄움). 없으면 PATH 의 ollama serve
        var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama app.exe");
        try
        {
            if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo("ollama", "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            logger.LogInformation("Ollama 가 꺼져 있어 실행했습니다");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning("Ollama 를 실행하지 못했습니다: {Message}", ex.Message);
            return;
        }
        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            if (await Up()) return;
            await Task.Delay(1000, ct);
        }
        logger.LogWarning("Ollama 가 30초 안에 응답하지 않습니다");
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
