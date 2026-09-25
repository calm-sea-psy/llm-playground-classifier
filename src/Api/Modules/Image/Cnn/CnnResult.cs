using System.Text.Json;

namespace Api.Modules.Image.Cnn;

/// <summary>CNN 서비스 표준 응답 (src/CnnService/contract.py 와 1:1, JSON 은 snake_case)</summary>
public sealed record CnnResult(
    string Engine,
    string ModelVersion,
    int Width,
    int Height,
    List<CnnFinding> Findings,
    CnnHeatmap? Heatmap,
    int ElapsedMs)
{
    public CnnFinding? Find(string label) => Findings.FirstOrDefault(f => f.Label == label);

    public IEnumerable<CnnFinding> Positives => Findings.Where(f => f.Positive);
}

public sealed record CnnFinding(string Label, double Probability, double Threshold, bool Positive);

/// <summary>Grad-CAM. Region = 히트맵이 대응하는 원본 픽셀 영역 [x0, y0, x1, y1]</summary>
public sealed record CnnHeatmap(string Label, string? PngBase64, int[] Region);

public static class CnnJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
