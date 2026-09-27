using Digitizer.App.Data;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Processing;

/// <summary>
/// 보관 기한 (설정 RetentionDays, 기본 90일, 접수일 기준). 지난 문서는 원본 파일(처리됨 · 확인 필요 · 실패 폴더)과 실패 사유 파일을 지우고,
/// 내보낸 적 없는 건은 DB 기록도 모두 지움. 내보낸 건은 "언제 무엇을 내보냈는지" 기록만 남기고 원문 · 필드 값 · 고친 값은 비움.
/// 처리 중 · 대기 중인 건은 건드리지 않음. 시작할 때 + 6시간마다
/// </summary>
public sealed class RetentionService(IDbContextFactory<DigitizerDb> dbFactory, SettingsFile settings, FileRouter router, TimeProvider clock,
    ILogger<RetentionService> logger) : BackgroundService
{
    public sealed record Summary(int Deleted, int Cleared, int FilesDeleted);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = await PurgeAsync(stoppingToken);
                if (s.Deleted + s.Cleared > 0)
                    logger.LogInformation("보관 기한 지남: 기록 삭제 {Deleted}건, 내보낸 건 값 비움 {Cleared}건, 파일 {Files}개 삭제", s.Deleted, s.Cleared, s.FilesDeleted);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "보관 기한 정리 오류");
            }
            await Task.Delay(TimeSpan.FromHours(6), clock, stoppingToken);
        }
    }

    public async Task<Summary> PurgeAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().AddDays(-settings.Current.RetentionDays);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var expired = await db.Documents
            .Where(d => d.ReceivedAt < cutoff && d.PurgedAt == null
                && d.Status != DocumentStatus.Queued && d.Status != DocumentStatus.Processing)
            .Include(d => d.Extractions).Include(d => d.Corrections).AsSplitQuery()
            .ToListAsync(ct);

        int deleted = 0, cleared = 0, files = 0;
        foreach (var doc in expired)
        {
            if (doc.StoredPath is { } path)
            {
                // 못 지우면(열려 있음 등) 기록도 그대로 두고 다음 정리 때 다시
                if (!TryDelete(path, ref files) || !TryDelete(path + FileRouter.ReasonSuffix, ref files)) continue;
                doc.StoredPath = null;
            }
            if (doc.LastExportId is null)
            {
                db.Documents.Remove(doc);  // 추출 · 고친 값은 cascade
                deleted++;
                continue;
            }
            foreach (var x in doc.Extractions)
            {
                x.SourceText = null;
                x.Fields = null;
                x.Issues = null;
                x.Attempts = null;
            }
            foreach (var c in doc.Corrections)
            {
                c.ExtractedValue = null;
                c.CorrectedValue = null;
            }
            doc.StatusReason = null;  // 중복 사유에 다른 문서 파일 이름이 들어 있음
            doc.ReviewedFields = null;
            doc.ReviewNote = null;
            doc.PurgedAt = clock.GetUtcNow();
            cleared++;
        }
        await db.SaveChangesAsync(ct);
        RemoveEmptyFolders();
        return new Summary(deleted, cleared, files);
    }

    private bool TryDelete(string path, ref int deleted)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            deleted++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("보관 기한이 지난 파일을 지우지 못함, 다음 정리 때 다시: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>처리됨\<팩>\<날짜> 폴더가 비면 지움 (넣기 · 확인 필요 · 실패 폴더 자체는 둠)</summary>
    private void RemoveEmptyFolders()
    {
        var done = Path.Combine(router.Folders.Root, UserFolders.DoneName);
        if (!Directory.Exists(done)) return;
        foreach (var dateDir in Directory.GetDirectories(done).SelectMany(Directory.GetDirectories))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dateDir).Any()) Directory.Delete(dateDir);
            }
            catch (IOException) { }
        }
    }
}
