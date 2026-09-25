using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text.Ocr;

public sealed class OcrServiceException(string message, bool transient, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>
/// Python OcrService(/ocr?engine=...) 를 호출하는 엔진. PaddleOCR·EasyOCR·DeepSeek-OCR 처럼
/// OcrService 에 어댑터가 있는 엔진은 모두 이 클래스로 처리하고 Ocr:Engine 값만 바꾼다.
/// (Tesseract in-process, 상용 API 같은 다른 방식은 IOcrEngine 구현을 따로 추가)
/// </summary>
public sealed class OcrServiceEngine(HttpClient http, IOptions<OcrOptions> options, ILogger<OcrServiceEngine> log)
    : IOcrEngine
{
    public string Name => options.Value.Engine;

    public async Task<OcrResult> RecognizeAsync(Stream file, string fileName, string? engine, CancellationToken ct)
    {
        // 재시도 때 다시 보내야 하므로 메모리에 한 번 읽어 둠 (문서 이미지는 수 MB 이하)
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendAsync(bytes, fileName, engine ?? Name, ct);
            }
            catch (OcrServiceException ex) when (ex.Transient && attempt < options.Value.Retries)
            {
                log.LogWarning("OCR 요청 실패, 재시도 {Attempt}/{Retries}: {Error}", attempt + 1, options.Value.Retries, ex.Message);
            }
        }
    }

    private async Task<OcrResult> SendAsync(byte[] bytes, string fileName, string engine, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", fileName);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync($"ocr?engine={Uri.EscapeDataString(engine)}", content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OcrServiceException($"OCR 서비스에 연결할 수 없습니다 ({http.BaseAddress})", transient: true, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OcrServiceException($"OCR 서비스 응답 시간 초과 ({http.Timeout.TotalSeconds:0}초)", transient: true, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadDetailAsync(response, ct);
                // 4xx 는 요청 자체 문제(이미지 손상, 엔진 이름 오류)라 재시도해도 같은 결과
                throw new OcrServiceException(
                    $"OCR 서비스 오류 {(int)response.StatusCode}: {detail}",
                    transient: (int)response.StatusCode >= 500);
            }
            return await response.Content.ReadFromJsonAsync<OcrResult>(OcrJson.Options, ct)
                ?? throw new OcrServiceException("OCR 서비스가 빈 응답을 반환했습니다", transient: false);
        }
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.ValueKind == JsonValueKind.String ? detail.GetString()! : detail.GetRawText();
            }
        }
        catch (JsonException)
        {
        }
        return body.Length > 300 ? body[..300] : body;
    }
}
