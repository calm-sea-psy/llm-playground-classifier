using Microsoft.AspNetCore.SignalR;

namespace Api.Shared.Hubs;

/// <summary>
/// 작업 진행 알림 허브 (/hubs/jobs).
/// 클라이언트는 작업 생성 후 SubscribeJob(jobId) 를 호출하고 "JobUpdated" 이벤트(JobDto)를 받는다.
/// 구독 전에 지나간 상태는 GET /api/jobs/{id} 로 확인.
/// </summary>
public sealed class JobHub : Hub
{
    public const string JobUpdated = "JobUpdated";

    public static string GroupName(Guid jobId) => $"job:{jobId}";

    public Task SubscribeJob(Guid jobId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(jobId));

    public Task UnsubscribeJob(Guid jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(jobId));
}
