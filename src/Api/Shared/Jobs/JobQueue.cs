using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Api.Shared.Jobs;

public sealed class JobOptions
{
    public int QueueCapacity { get; set; } = 100;
}

/// <summary>In-memory 작업 큐. jobId 만 넣고 워커가 DB 에서 Job 을 다시 읽는다 (서버 재시작 시 유실은 감수).</summary>
public sealed class JobQueue
{
    private readonly Channel<Guid> _channel;

    public JobQueue(IOptions<JobOptions> options)
    {
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>큐가 가득 차면 false (호출 측에서 503 으로 응답)</summary>
    public bool TryEnqueue(Guid jobId) => _channel.Writer.TryWrite(jobId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public int Count => _channel.Reader.Count;
}
