using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Api.Shared.Health;

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// /health/live  : 프로세스 생존 (의존 서비스 검사 안 함)
    /// /health/ready : DB + 켜진 모듈의 의존 서비스(OCR, Ollama 등) ➔ Healthy / Degraded(200) / Unhealthy(503)
    /// </summary>
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteJson,
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteJson,
        });
    }

    private static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            totalMs = (int)report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    durationMs = (int)e.Value.Duration.TotalMilliseconds,
                    error = e.Value.Exception?.Message ?? e.Value.Description,
                }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }

    // 한글 오류 메시지를 \uXXXX 로 이스케이프하지 않도록
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
