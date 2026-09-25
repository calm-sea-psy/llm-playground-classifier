using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Monitor;

/// <summary>/hubs/status: 상태가 바뀌면 "TargetChanged"(TargetDto), 이벤트마다 "Event"(MonitorEvent)</summary>
public sealed class StatusHub : Hub;

/// <summary>상태 변화 알림: 이벤트 저장 + SignalR(상태 페이지·웹 위젯) + 웹훅(Discord/Slack)</summary>
public sealed class Notifier(
    EventStore events,
    IHubContext<StatusHub> hub,
    IHttpClientFactory httpFactory,
    IOptions<MonitorOptions> options,
    TimeProvider clock,
    ILogger<Notifier> log)
{
    public async Task NotifyAsync(string target, string kind, TargetState? from, TargetState? to, string message, CancellationToken ct)
    {
        var e = new MonitorEvent(clock.GetUtcNow(), target, kind, from?.ToString(), to?.ToString(), message);
        log.LogInformation("[{Target}] {Kind}: {Message}", target, kind, message);
        // 기록 실패가 알림(웹훅)을 막으면 안 됨 ➔ 저장 오류는 로그만 남기고 계속
        try
        {
            await events.AddAsync(e, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "이벤트 저장 실패");
        }
        await hub.Clients.All.SendAsync("Event", e, ct);

        if (options.Value.Webhook.Url is { Length: > 0 } url)
        {
            var text = $"[{target}] {message}";
            try
            {
                var http = httpFactory.CreateClient(nameof(Notifier));
                using var response = await http.PostAsJsonAsync(url, new { content = text, text }, ct);
                if (!response.IsSuccessStatusCode)
                {
                    log.LogWarning("웹훅 전송 실패: HTTP {Status}", (int)response.StatusCode);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("웹훅 전송 실패: {Error}", ex.Message);
            }
        }
    }

    public Task PublishStateAsync(TargetDto dto, CancellationToken ct) =>
        hub.Clients.All.SendAsync("TargetChanged", dto, ct);
}
