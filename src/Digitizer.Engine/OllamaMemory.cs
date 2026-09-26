using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Digitizer.Engine;

/// <summary>OCR 전에 GPU 의 LLM 을 내릴지. Never: 항상 유지 / Always: 항상 내림 / LargeImages: 기준 화소를 넘는 이미지만 (측정상 가장 빠름)</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnloadPolicy>))]
public enum UnloadPolicy
{
    Never,
    Always,
    LargeImages,
}

/// <summary>
/// Ollama 메모리 관리 (평가 도구 · exe 공용). 큰 이미지 OCR 과 LLM 이 GPU 를 같이 쓰면 느려지고,
/// CPU 로 올라간 모델이 남아 있으면 GPU 요청도 그 인스턴스로 처리되므로(4차-0단계) 필요할 때 모두 내린다
/// </summary>
public static class OllamaMemory
{
    public static bool ShouldUnloadBeforeOcr(long pixels, UnloadPolicy policy, double aboveMegapixels) => policy switch
    {
        UnloadPolicy.Always => true,
        UnloadPolicy.LargeImages => pixels > aboveMegapixels * 1_000_000,
        _ => false,
    };

    /// <summary>올라가 있는 모델을 모두 keep_alive=0 으로 내리고, 실제로 내려갈 때까지 최대 10초 기다린다</summary>
    public static async Task UnloadAllAsync(HttpClient ollama, CancellationToken ct = default)
    {
        var loaded = await LoadedModelsAsync(ollama, ct);
        foreach (var model in loaded)
        {
            using var _ = await ollama.PostAsJsonAsync("api/generate", new { model, keep_alive = 0 }, ct);
        }
        for (var i = 0; i < 20 && loaded.Count > 0 && (await LoadedModelsAsync(ollama, ct)).Count > 0; i++)
        {
            await Task.Delay(500, ct);
        }
    }

    public static async Task<List<string>> LoadedModelsAsync(HttpClient ollama, CancellationToken ct = default)
    {
        var ps = await ollama.GetFromJsonAsync<JsonObject>("api/ps", ct);
        return ps?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
    }
}
