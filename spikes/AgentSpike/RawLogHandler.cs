namespace AgentSpike;

/// <summary>Ollama 요청·응답 본문을 그대로 파일에 덧붙임 (응답이 잘리거나 비는 원인 확인용)</summary>
public sealed class RawLogHandler(string path, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var response = await base.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        await File.AppendAllTextAsync(path, $"### REQUEST {request.RequestUri}\n{body}\n### RESPONSE {(int)response.StatusCode}\n{text}\n\n", ct);
        // 본문을 읽었으므로 다시 채워 돌려줌
        var copy = new StringContent(text, System.Text.Encoding.UTF8);
        foreach (var h in response.Content.Headers) copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        response.Content = copy;
        return response;
    }
}
