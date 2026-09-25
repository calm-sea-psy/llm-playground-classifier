using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Monitor;

public enum Health
{
    Healthy,
    Degraded,
    Unhealthy,
}

public sealed record CheckResult(Health Health, string? Detail, int ElapsedMs);

/// <summary>http(s):// ➔ 2xx 정상 (DegradedUnlessContains 가 본문에 없으면 Degraded), tcp:// ➔ 접속되면 정상</summary>
public sealed class HealthChecker(IHttpClientFactory httpFactory, IOptions<MonitorOptions> options)
{
    public async Task<CheckResult> CheckAsync(TargetOptions target, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        try
        {
            var uri = new Uri(target.Check);
            if (uri.Scheme == "tcp")
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                return new CheckResult(Health.Healthy, null, (int)watch.ElapsedMilliseconds);
            }

            var http = httpFactory.CreateClient(nameof(HealthChecker));
            using var response = await http.GetAsync(uri, timeout.Token);
            var ms = (int)watch.ElapsedMilliseconds;
            if (!response.IsSuccessStatusCode)
            {
                return new CheckResult(Health.Unhealthy, $"HTTP {(int)response.StatusCode}", ms);
            }
            if (target.DegradedUnlessContains is { } expected)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!body.Contains(expected, StringComparison.Ordinal))
                {
                    return new CheckResult(Health.Degraded, $"응답에 '{expected}' 없음", ms);
                }
            }
            return new CheckResult(Health.Healthy, null, ms);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CheckResult(Health.Unhealthy, $"응답 없음 ({options.Value.TimeoutSeconds}초 초과)", (int)watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException)
        {
            return new CheckResult(Health.Unhealthy, "연결 거부", (int)watch.ElapsedMilliseconds);
        }
    }
}
