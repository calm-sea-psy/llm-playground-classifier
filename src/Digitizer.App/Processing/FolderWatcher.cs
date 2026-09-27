namespace Digitizer.App.Processing;

/// <summary>
/// 감시 폴더 (넣기\<팩>). 몇 초마다 훑어 크기 · 수정 시각이 StableSeconds 동안 그대로이고 다른 프로그램이 쓰고 있지 않은 파일만 접수.
/// FileSystemWatcher 대신 훑기: 네트워크 드라이브 · 버퍼 넘침으로 알림을 놓치는 일이 없고, 앱이 꺼져 있을 때 넣은 파일도 받음
/// </summary>
public sealed class FolderWatcher(PackCatalog catalog, FileRouter router, DocumentIntake intake, SettingsFile settings, TimeProvider clock,
    ILogger<FolderWatcher> logger) : BackgroundService
{
    private sealed record Seen(long Size, DateTime WriteTime, DateTimeOffset Since);

    private readonly Dictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        router.Folders.Ensure(catalog.Packs);
        logger.LogInformation("감시 폴더: {Root}", router.Folders.InboxRoot);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "감시 폴더 확인 오류");
            }
            await Task.Delay(Interval, clock, stoppingToken);
        }
    }

    /// <summary>한 번 훑기. 접수한 파일 수</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var folders = router.Folders;
        var stable = TimeSpan.FromSeconds(settings.Current.StableSeconds);
        var now = clock.GetUtcNow();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = 0;
        foreach (var pack in catalog.Packs)
        {
            var inbox = folders.Inbox(pack);
            if (!Directory.Exists(inbox))
            {
                Directory.CreateDirectory(inbox);
                continue;
            }
            foreach (var path in Directory.EnumerateFiles(inbox))
            {
                var info = new FileInfo(path);
                if (Ignored(info)) continue;
                present.Add(path);
                var current = new Seen(info.Length, info.LastWriteTimeUtc, now);
                if (!_seen.TryGetValue(path, out var seen) || seen.Size != current.Size || seen.WriteTime != current.WriteTime)
                {
                    _seen[path] = current;  // 처음 봄 · 아직 바뀌는 중 ➔ 다음 훑기에서 다시
                    if (stable > TimeSpan.Zero) continue;
                    seen = current;
                }
                if (now - seen.Since < stable || IsLocked(path)) continue;

                try
                {
                    var doc = await intake.AcceptAsync(path, pack, "watch", ct);
                    logger.LogInformation("접수 #{Id} ({Pack}): {Status}", doc.Id, pack.Id, doc.Status);
                    accepted++;
                }
                catch (IOException ex)
                {
                    logger.LogWarning("{Pack} 넣기 폴더의 파일을 아직 접수하지 못함, 다시 시도: {Message}", pack.Id, ex.Message);  // 파일 이름은 개인정보일 수 있어 남기지 않음
                }
                _seen.Remove(path);
            }
        }
        foreach (var gone in _seen.Keys.Where(k => !present.Contains(k)).ToList()) _seen.Remove(gone);
        return accepted;
    }

    /// <summary>임시 · 숨김 파일 (Office 잠금 파일 ~$, 내려받는 중 .crdownload · .part · .tmp, desktop.ini, 실패 사유 .txt)</summary>
    public static bool Ignored(FileInfo info) =>
        info.Name.StartsWith("~$", StringComparison.Ordinal) || info.Name.StartsWith('.')
        || info.Extension.ToLowerInvariant() is ".tmp" or ".crdownload" or ".part" or ".partial" or ".download"
        || info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || info.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    /// <summary>다른 프로그램이 열고 있으면 true (공유 없이 열 수 없음). 읽기로만 열어 읽기 전용 파일도 받음</summary>
    public static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
