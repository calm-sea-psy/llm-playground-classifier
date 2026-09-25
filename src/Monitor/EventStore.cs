using System.Text.Json;
using Npgsql;

namespace Monitor;

public sealed record MonitorEvent(
    DateTimeOffset At,
    string Target,
    string Kind,
    string? FromState,
    string? ToState,
    string Message);

/// <summary>
/// 이벤트를 PostgreSQL monitor_events 에 저장. DB 가 죽어 있으면(PostgreSQL 도 감시 대상) 로컬 파일에 쌓아 두고
/// 다음 저장이 성공할 때 함께 반영한다. 화면용으로 최근 200건은 메모리에도 보관.
/// </summary>
public sealed class EventStore(IConfiguration config, ProcessManager processes, ILogger<EventStore> log)
{
    private const int RecentLimit = 200;
    private readonly string? _connectionString = config.GetConnectionString("Events");
    private readonly string _pendingPath = Path.Combine(processes.RepoRoot, "data", "monitor", "pending_events.jsonl");
    private readonly LinkedList<MonitorEvent> _recent = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _tableReady;

    public IReadOnlyList<MonitorEvent> Recent(int take)
    {
        lock (_recent)
        {
            return _recent.Take(take).ToList();
        }
    }

    /// <summary>시작 시 DB 의 최근 이벤트를 불러와 상태 페이지가 재시작 전 기록도 보여 주게 함 (실패하면 빈 채로 시작)</summary>
    public async Task LoadRecentAsync(CancellationToken ct)
    {
        if (_connectionString is null)
        {
            return;
        }
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await EnsureTableAsync(conn, ct);
            await using var cmd = new NpgsqlCommand(
                $"SELECT at, target, kind, from_state, to_state, message FROM monitor_events ORDER BY at DESC LIMIT {RecentLimit}", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            lock (_recent)
            {
                while (reader.Read())
                {
                    _recent.AddLast(new MonitorEvent(
                        reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5)));
                }
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            log.LogWarning("이전 이벤트를 불러오지 못했습니다: {Error}", ex.Message);
        }
    }

    public async Task AddAsync(MonitorEvent e, CancellationToken ct)
    {
        lock (_recent)
        {
            _recent.AddFirst(e);
            if (_recent.Count > RecentLimit)
            {
                _recent.RemoveLast();
            }
        }
        if (_connectionString is null)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var batch = ReadPending();
            batch.Add(e);
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(ct);
                await EnsureTableAsync(conn, ct);
                foreach (var item in batch)
                {
                    await using var cmd = new NpgsqlCommand(
                        "INSERT INTO monitor_events (at, target, kind, from_state, to_state, message) VALUES ($1, $2, $3, $4, $5, $6)", conn);
                    cmd.Parameters.AddWithValue(item.At);
                    cmd.Parameters.AddWithValue(item.Target);
                    cmd.Parameters.AddWithValue(item.Kind);
                    cmd.Parameters.AddWithValue((object?)item.FromState ?? DBNull.Value);
                    cmd.Parameters.AddWithValue((object?)item.ToState ?? DBNull.Value);
                    cmd.Parameters.AddWithValue(item.Message);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                if (batch.Count > 1)
                {
                    log.LogInformation("밀린 이벤트 {Count}건을 DB 에 반영했습니다", batch.Count - 1);
                }
                if (File.Exists(_pendingPath)) // 폴더가 없으면 File.Delete 가 예외를 던짐
                {
                    File.Delete(_pendingPath);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
                await File.AppendAllTextAsync(_pendingPath, JsonSerializer.Serialize(e) + "\n", ct);
                log.LogWarning("DB 에 이벤트를 저장하지 못해 파일에 보관합니다: {Error}", ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<MonitorEvent> ReadPending() =>
        File.Exists(_pendingPath)
            ? File.ReadAllLines(_pendingPath)
                .Where(l => l.Length > 0)
                .Select(l => JsonSerializer.Deserialize<MonitorEvent>(l)!)
                .ToList()
            : [];

    private async Task EnsureTableAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (_tableReady)
        {
            return;
        }
        // Monitor 는 API 와 코드 의존성이 없도록 EF 마이그레이션 대신 직접 생성
        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS monitor_events (
                id bigserial PRIMARY KEY,
                at timestamptz NOT NULL,
                target varchar(64) NOT NULL,
                kind varchar(32) NOT NULL,
                from_state varchar(16),
                to_state varchar(16),
                message text NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_monitor_events_at ON monitor_events (at);
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct);
        _tableReady = true;
    }
}
