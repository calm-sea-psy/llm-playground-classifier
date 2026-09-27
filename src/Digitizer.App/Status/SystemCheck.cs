using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Digitizer.App.Hosting;
using Digitizer.App.Processing;

namespace Digitizer.App.Status;

/// <param name="Level">ok | warn | bad</param>
/// <param name="Fix">문제일 때 해결 방법 (사람이 할 일)</param>
public sealed record CheckItem(string Name, string Level, string Detail, string? Fix = null)
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Bad = "bad";
}

/// <summary>상태 점검 화면: Ollama 실행 · 모델 있음 · OCR 서비스 · GPU · 디스크 여유 (각각 해결 방법과 함께)</summary>
public sealed class SystemCheck(SettingsFile settings, IHttpClientFactory http, AppPaths paths, ServiceSupervisor? supervisor = null)
{
    public const long LowDiskBytes = 5L * 1024 * 1024 * 1024;

    public async Task<List<CheckItem>> RunAsync(CancellationToken ct = default)
    {
        var s = settings.Current;
        var items = new List<CheckItem>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var models = await OllamaModelsAsync(timeout.Token);
        if (models is null)
        {
            items.Add(new("Ollama", CheckItem.Bad, $"{s.OllamaUrl} 에 연결하지 못했습니다",
                "시작 메뉴에서 Ollama 를 실행하세요. 설치되어 있지 않으면 설치 도우미를 다시 실행하세요"));
            items.Add(new("모델", CheckItem.Bad, $"{s.Model}: Ollama 가 꺼져 있어 확인하지 못했습니다", "Ollama 를 먼저 실행하세요"));
        }
        else
        {
            items.Add(new("Ollama", CheckItem.Ok, $"실행 중 ({s.OllamaUrl}, 모델 {models.Count}개)"));
            items.Add(HasModel(models, s.Model)
                ? new("모델", CheckItem.Ok, $"{s.Model} 있음")
                : new("모델", CheckItem.Bad, $"{s.Model} 이(가) 없습니다", $"명령 창에서 ollama pull {s.Model} 을 실행하세요 (6~8GB)"));
        }

        items.Add(await OcrAsync(s, timeout.Token));
        items.Add(await GpuAsync(s, ct));
        items.Add(Disk("디스크 (문서 폴더)", s.ResolvedDocumentsRoot));
        items.Add(Disk("디스크 (프로그램 데이터)", paths.DataRoot));
        return items;
    }

    /// <summary>설정 화면의 모델 목록 (Ollama 에 있는 모델). 연결 못 하면 null</summary>
    public async Task<List<string>?> OllamaModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var tags = await http.CreateClient(OcrSourceReader.OllamaClient).GetFromJsonAsync<JsonObject>("api/tags", ct);
            return tags?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).OfType<string>().Order().ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>ollama 는 태그 없는 이름을 :latest 로 보여 줌</summary>
    public static bool HasModel(IEnumerable<string> models, string model) =>
        models.Any(m => m == model || m == $"{model}:latest");

    private async Task<CheckItem> OcrAsync(AppSettings s, CancellationToken ct)
    {
        try
        {
            var health = await http.CreateClient(OcrSourceReader.OcrClient).GetFromJsonAsync<JsonObject>("health", ct);
            var engine = health?["engines"]?[s.OcrEngine];
            if (engine is null)
                return new("OCR 서비스", CheckItem.Bad, $"실행 중이지만 {s.OcrEngine} 엔진이 없습니다", "설치 도우미로 OCR 을 다시 설치하세요");
            var loaded = engine["loaded"]?.GetValue<bool>() == true;
            var how = supervisor?.OcrMode switch { "managed" => ", 프로그램이 실행함", "external" => ", 따로 실행된 서비스", _ => "" };
            return new("OCR 서비스", CheckItem.Ok, $"실행 중 ({s.OcrEngine}{(loaded ? ", 모델 준비됨" : ", 첫 문서 때 모델을 읽음")}{how})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (supervisor?.OcrMode == "starting")
                return new("OCR 서비스", CheckItem.Warn, "시작하는 중 (모델을 읽는 데 1분 정도 걸림)", "잠시 뒤 다시 확인하세요. 그동안 들어온 문서는 준비되면 처리합니다");
            return new("OCR 서비스", CheckItem.Bad, supervisor?.OcrProblem ?? $"{s.OcrUrl} 에 연결하지 못했습니다",
                "프로그램을 다시 시작하세요. 계속되면 설치 도우미로 OCR 을 다시 설치하세요 (로그 폴더의 ocr-날짜.log 에 원인)");
        }
    }

    /// <summary>nvidia-smi 로 GPU 이름 · 메모리. 없으면 CPU 처리 안내 (느리지만 동작)</summary>
    private static async Task<CheckItem> GpuAsync(AppSettings s, CancellationToken ct)
    {
        if (s.CpuOnly) return new("GPU", CheckItem.Ok, "설정에서 CPU 로만 처리 (GPU 사용 안 함)");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            var parts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Split(',').Select(x => x.Trim()).ToArray();
            if (p.ExitCode != 0 || parts is not { Length: 3 }) throw new InvalidOperationException(output);
            var total = double.Parse(parts[1]) / 1024;
            var used = double.Parse(parts[2]) / 1024;
            return total < 8
                ? new("GPU", CheckItem.Warn, $"{parts[0]} ({total:0.#}GB, 사용 중 {used:0.#}GB)", "메모리 8GB 미만: 작은 모델(설정에서 hf.co/unsloth/gemma-4-E4B-it-GGUF:Q4_K_M)을 권장합니다")
                : new("GPU", CheckItem.Ok, $"{parts[0]} ({total:0.#}GB, 사용 중 {used:0.#}GB)");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FormatException or OperationCanceledException)
        {
            return new("GPU", CheckItem.Warn, "NVIDIA GPU 를 찾지 못했습니다 ➔ CPU 로 처리 (문서당 1~2분)",
                "GPU 가 있다면 NVIDIA 드라이버를 설치하세요. 없으면 작은 모델(설정에서 hf.co/unsloth/gemma-4-E4B-it-GGUF:Q4_K_M)을 쓰세요");
        }
    }

    private static CheckItem Disk(string name, string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            var drive = new DriveInfo(root!);
            var free = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            return drive.AvailableFreeSpace < LowDiskBytes
                ? new(name, CheckItem.Warn, $"{root} 남은 공간 {free:0.#}GB", "5GB 이상 비워 두세요 (보관 기한을 줄이면 원본이 빨리 지워짐)")
                : new(name, CheckItem.Ok, $"{root} 남은 공간 {free:0.#}GB");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new(name, CheckItem.Bad, $"{folder}: {ex.Message}", "설정에서 폴더 위치를 확인하세요");
        }
    }
}
