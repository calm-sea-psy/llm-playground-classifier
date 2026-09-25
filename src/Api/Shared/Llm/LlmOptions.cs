using System.Text.Json.Serialization;

namespace Api.Shared.Llm;

/// <summary>API 본문(설정 PUT 등)에서도 이름으로 주고받도록 문자열 변환기를 붙인다</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UnloadPolicy>))]
public enum UnloadPolicy
{
    Never,
    Always,
    LargeImages,
}

public sealed class LlmOptions
{
    /// <summary>
    /// "ollama": Ollama 네이티브 API(/api/chat)를 SK IChatCompletionService 로 직접 구현 (think 끄기, num_ctx 지정 가능)
    /// "openai": SK OpenAI 커넥터로 OpenAI 호환 엔드포인트({BaseUrl}/v1) 호출 (vLLM 등)
    /// </summary>
    public string Provider { get; set; } = "ollama";
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string ApiKey { get; set; } = "local";

    /// <summary>선택 가능한 모델 태그 (작업 생성 시 model 로 지정, 생략 시 DefaultModel)</summary>
    public List<string> Models { get; set; } = [];
    public string DefaultModel { get; set; } = "";

    /// <summary>Ollama 기본값 4096 은 긴 문서 프롬프트가 잘리므로 늘려서 사용</summary>
    public int ContextLength { get; set; } = 16384;
    /// <summary>thinking 을 켜면 추론 토큰이 수천 개라 문서 1건에 1분 가까이 걸림 ➔ 기본 끔</summary>
    public bool Think { get; set; }
    public string KeepAlive { get; set; } = "10m";
    /// <summary>
    /// OCR 전에 Ollama 모델을 GPU 에서 내릴지. Paddle 이 큰 이미지에서 남은 VRAM 을 모두 쓰면서 LLM(8GB+)과 16GB 를 넘으면
    /// 공유 메모리로 밀려나 OCR 이 1초 ➔ 15초로 느려진다 (6MP 이상에서 발생). 대신 내리면 LLM 재적재가 약 4초 든다.
    /// Never: 항상 유지 / Always: 항상 내림 / LargeImages: UnloadAboveMegapixels 를 넘는 이미지만 내림 (측정상 가장 빠름)
    /// </summary>
    public UnloadPolicy UnloadBeforeOcr { get; set; } = UnloadPolicy.LargeImages;
    public double UnloadAboveMegapixels { get; set; } = 5.0;
    /// <summary>
    /// 응답 최대 토큰 (Ollama num_predict). 정상 추출은 최대 약 1,400 토큰(KORIE 150장)인데, VLM 이 같은 내용을 반복하기 시작하면
    /// 끝없이 생성하다 TimeoutSeconds 에 걸려 작업 전체가 실패함 ➔ 잘라서 "이 시도만 실패(JSON 파싱 오류)"로 끝나게 한다
    /// </summary>
    public int MaxOutputTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 300;
}
