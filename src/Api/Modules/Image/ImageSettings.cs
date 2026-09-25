using System.Text.Json;
using System.Text.Json.Serialization;

namespace Api.Modules.Image;

/// <summary>
/// 이미지 작업 설정 스냅숏 (jobs.settings). 모델 비교 실험(2차-3)에서 조합으로 바꿔 볼 값들.
/// </summary>
/// <param name="Model">VLM 판독 초안 모델</param>
/// <param name="VlmReport">VLM 판독 초안 작성 여부 (끄면 CNN 결과만)</param>
/// <param name="VlmSeesCnn">
/// VLM 에 CNN 결과를 보여 줄지. 기본 false: 보여 주면 VLM 이 그대로 따라 써서(1차의 "힌트가 판단을 끌고 간다")
/// 두 판단의 불일치로 사람 확인 대상을 가려내는 검증이 무의미해짐
/// </param>
/// <param name="Population">
/// 대상 "adult" | "pediatric". 폐렴 판단 출처가 달라짐 (2차-1b): 소아 = 소아 데이터로 파인튜닝한 pneumonia 엔진(소아 AUC 0.98),
/// 성인 = xrv 의 경화(Consolidation) 출력 (성인 IU 에서 파인튜닝 엔진 0.76, xrv 경화 0.87)
/// </param>
public sealed record ImageSettings(string Model, bool VlmReport = true, bool VlmSeesCnn = false, string Population = Populations.Adult)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ImageSettings? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ImageSettings>(json, Json);

    /// <summary>화면 표시용 한 줄 요약 (저장 JSON 에는 넣지 않음)</summary>
    [JsonIgnore]
    public string Summary => ImageSettingsText.Describe(this);
}

public static class Populations
{
    public const string Adult = "adult";
    public const string Pediatric = "pediatric";
    public static readonly string[] All = [Adult, Pediatric];
}

public sealed class ImageOptions
{
    /// <summary>기본 대상 (업로드 때 바꿀 수 있음)</summary>
    public string Population { get; set; } = Populations.Adult;
    /// <summary>생략 시 Llm:DefaultModel</summary>
    public string? Model { get; set; }
    public bool VlmReport { get; set; } = true;
    public bool VlmSeesCnn { get; set; }
    /// <summary>VLM 에 보낼 이미지 긴 변 (X-ray 2048px ➔ 1024px)</summary>
    public int VlmMaxImageSide { get; set; } = 1024;
}
