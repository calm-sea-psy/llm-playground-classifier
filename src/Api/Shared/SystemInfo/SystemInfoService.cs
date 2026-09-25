using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Api.Shared.Llm;
using Microsoft.Extensions.Options;

namespace Api.Shared.SystemInfo;

public sealed record GpuInfo(string Name, int MemoryTotalMb, int MemoryUsedMb, string Driver, int? TemperatureC, int? UtilizationPercent);

public sealed record LoadedModel(string Name, long VramBytes);

public sealed record SystemInfoDto(
    string Os,
    string Cpu,
    int LogicalCores,
    double MemoryGb,
    IReadOnlyList<GpuInfo> Gpus,
    string Runtime,
    string? OllamaVersion,
    IReadOnlyList<LoadedModel> OllamaLoaded,
    IReadOnlyList<string> LlmModels,
    JsonNode? OcrEngines,
    DateTimeOffset CollectedAt);

/// <summary>
/// 모델 비교 페이지 상단의 기기 사양. 실험 결과는 기기(특히 GPU·VRAM)에 따라 달라지므로 실험 기록에도 함께 저장한다.
/// GPU 는 nvidia-smi, CPU 이름은 Windows 레지스트리(읽기), 버전은 Ollama·OcrService API 에서 읽는다
/// </summary>
public sealed class SystemInfoService(IHttpClientFactory httpFactory, IOptions<LlmOptions> llm, IConfiguration config, ILogger<SystemInfoService> log)
{
    private static readonly Lazy<string> CpuName = new(ReadCpuName);

    public async Task<SystemInfoDto> CollectAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient(nameof(SystemInfoService));
        http.Timeout = TimeSpan.FromSeconds(3);
        var llmBase = llm.Value.BaseUrl.TrimEnd('/');

        var ollamaVersion = await TryGetAsync(http, $"{llmBase}/api/version", ct);
        var ps = await TryGetAsync(http, $"{llmBase}/api/ps", ct);
        var ocr = await TryGetAsync(http, $"{config["Ocr:BaseUrl"]?.TrimEnd('/')}/health", ct);

        var loaded = ps?["models"]?.AsArray()
            .Select(m => new LoadedModel(m?["name"]?.GetValue<string>() ?? "?", m?["size_vram"]?.GetValue<long>() ?? 0))
            .ToList() ?? [];

        return new SystemInfoDto(
            RuntimeInformation.OSDescription,
            CpuName.Value,
            Environment.ProcessorCount,
            Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024, 1),
            await ReadGpusAsync(ct),
            RuntimeInformation.FrameworkDescription,
            ollamaVersion?["version"]?.GetValue<string>(),
            loaded,
            llm.Value.Models,
            ocr?["engines"],
            DateTimeOffset.UtcNow);
    }

    private async Task<JsonNode?> TryGetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            return JsonNode.Parse(await http.GetStringAsync(url, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogDebug("시스템 정보 조회 실패 {Url}: {Error}", url, ex.Message);
            return null;
        }
    }

    private static async Task<IReadOnlyList<GpuInfo>> ReadGpusAsync(CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total,memory.used,driver_version,temperature.gpu,utilization.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return [];
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(',', StringSplitOptions.TrimEntries))
                .Where(c => c.Length >= 6)
                .Select(c => new GpuInfo(c[0], int.Parse(c[1]), int.Parse(c[2]), c[3],
                    int.TryParse(c[4], out var t) ? t : null, int.TryParse(c[5], out var u) ? u : null))
                .ToList();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return []; // NVIDIA 드라이버 없음
        }
    }

    private static string ReadCpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name)
            {
                return name.Trim();
            }
        }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }
}
