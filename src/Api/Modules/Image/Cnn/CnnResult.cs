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

    /// <summary>경계를 뺀 확정 양성 (종합 보고서·소견서 비교용)</summary>
    public IEnumerable<CnnFinding> DefinitePositives => Findings.Where(f => f.Positive && !f.Borderline);
}

public sealed record CnnFinding(string Label, double Probability, double Threshold, bool Positive)
{
    /// <summary>경계 폭: 기준값 바로 위 0.02 이내 양성은 "판단 보류"</summary>
    public const double BorderlineMargin = 0.02;

    /// <summary>
    /// 기준값 바로 위(+0.02 미만)의 양성 = 경계(판단 보류). 운영 기준이 0.5 로 변환된 xrv 소견에만 (기준 0.9 이상인 소아 폐렴 모델 제외).
    /// 2차-5 IU 120건: 이 구간 양성 44개 중 실제 양성 11개(25%) ➔ 양성으로 보고서에 넣으면 대부분 틀림 (4차 리뷰)
    /// </summary>
    public bool Borderline => Positive && Threshold < 0.9 && Probability < Threshold + BorderlineMargin;
}

/// <summary>Grad-CAM. Region = 히트맵이 대응하는 원본 픽셀 영역 [x0, y0, x1, y1]</summary>
public sealed record CnnHeatmap(string Label, string? PngBase64, int[] Region);

public static class CnnJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
