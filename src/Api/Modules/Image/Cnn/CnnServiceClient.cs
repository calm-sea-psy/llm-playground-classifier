using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Api.Modules.Image.Cnn;

public sealed class CnnOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8002";
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>소아 폐렴 판정 엔진 (Kaggle 소아 폐렴 미세조정, 2차-1 선정)</summary>
    public string PneumoniaEngine { get; set; } = "pneumonia";
    /// <summary>성인 폐렴 신호로 쓸 소견 엔진의 출력 (xrv "Pneumonia" 라벨은 약하고 경화가 더 잘 맞음, 2차-1b)</summary>
    public string AdultPneumoniaLabel { get; set; } = "Consolidation";
    /// <summary>그 밖의 소견 엔진 (TorchXRayVision 18개 소견)</summary>
    public string FindingsEngine { get; set; } = "xrv";
}

/// <summary>CNN 서비스(src/CnnService) HTTP 클라이언트</summary>
public sealed class CnnServiceClient(HttpClient http)
{
    /// <param name="target">히트맵을 그릴 소견 (null 이면 가장 강한 소견)</param>
    public async Task<CnnResult> ClassifyAsync(
        Stream image, string fileName, string engine, bool heatmap, string? target, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        using var response = await http.PostAsync(
            $"classify?engine={Uri.EscapeDataString(engine)}&heatmap={(heatmap ? "true" : "false")}"
            + (target is null ? "" : $"&target={Uri.EscapeDataString(target)}"), content, ct);
        if (!response.IsSuccessStatusCode)
        {
            // FastAPI 오류 본문 {"detail": "..."} 를 그대로 전달 (예: 이미지를 읽을 수 없음)
            var body = await response.Content.ReadAsStringAsync(ct);
            var detail = TryDetail(body) ?? body;
            throw new InvalidOperationException($"CNN 서비스 오류 {(int)response.StatusCode}: {detail}");
        }
        return await response.Content.ReadFromJsonAsync<CnnResult>(CnnJson.Options, ct)
            ?? throw new InvalidOperationException("CNN 서비스가 빈 응답을 반환했습니다");
    }

    private static string? TryDetail(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["detail"]?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
