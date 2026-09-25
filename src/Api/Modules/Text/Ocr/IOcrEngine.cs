namespace Api.Modules.Text.Ocr;

/// <summary>
/// OCR 엔진 추상화. LLM 단계는 OcrResult(표준 계약)만 보므로 엔진이 바뀌어도 영향이 없다.
/// </summary>
public interface IOcrEngine
{
    /// <summary>설정의 기본 엔진 이름 (예: "paddleocr")</summary>
    string Name { get; }

    /// <param name="engine">이번 작업에 쓸 엔진 (작업별 설정 조합). null 이면 기본 엔진</param>
    Task<OcrResult> RecognizeAsync(Stream file, string fileName, string? engine, CancellationToken ct);
}

public sealed class OcrOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8001";
    /// <summary>OcrService 의 엔진 이름. 엔진 변경 = 이 값 1줄 수정</summary>
    public string Engine { get; set; } = "paddleocr";
    /// <summary>
    /// 작업별로 고를 수 있는 OcrService 엔진 (모델 비교 페이지). 기본값을 넣어 두면 설정 파일 값이 덧붙어 중복되므로 비워 둠
    /// </summary>
    public List<string> Engines { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
    public int Retries { get; set; } = 1;
}
