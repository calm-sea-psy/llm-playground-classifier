namespace Api.Modules.Image.Pipeline;

/// <summary>CNN 소견 영문 라벨 ➔ 한국어 (VLM 프롬프트·완료 메시지용. 화면은 프론트에서 따로 표시)</summary>
public static class FindingNames
{
    private static readonly Dictionary<string, string> Korean = new()
    {
        ["Atelectasis"] = "무기폐",
        ["Consolidation"] = "경화",
        ["Infiltration"] = "침윤",
        ["Pneumothorax"] = "기흉",
        ["Edema"] = "폐부종",
        ["Emphysema"] = "폐기종",
        ["Fibrosis"] = "섬유화",
        ["Effusion"] = "흉수",
        ["Pneumonia"] = "폐렴",
        ["Pleural_Thickening"] = "흉막 비후",
        ["Cardiomegaly"] = "심장비대",
        ["Nodule"] = "결절",
        ["Mass"] = "종괴",
        ["Hernia"] = "탈장",
        ["Lung Lesion"] = "폐 병변",
        ["Fracture"] = "골절",
        ["Lung Opacity"] = "폐 음영",
        ["Enlarged Cardiomediastinum"] = "종격동 확대",
    };

    public static string Of(string label) => Korean.TryGetValue(label, out var name) ? name : label;
}
