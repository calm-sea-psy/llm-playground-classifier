using Digitizer.App.Data;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Processing;

/// <summary>
/// 처리 대기열 = DB 의 Queued 문서 (접수 순). 한 번에 1건 (GPU 하나를 OCR · LLM 이 나눠 씀).
/// 앱이 처리 중에 꺼지면 시작할 때 Processing ➔ Queued 로 되돌려 다시 처리 (원본은 queue\ 에 그대로 있음)
/// </summary>
public sealed class ProcessingQueue(
    IDbContextFactory<DigitizerDb> dbFactory,
    PackCatalog catalog,
    DocumentRunner runner,
    FileRouter router,
    TimeProvider clock,
    ILogger<ProcessingQueue> logger) : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>새 문서가 들어옴 ➔ 쉬고 있으면 깨움</summary>
    public void Notify()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    /// <summary>시작할 때: 처리 중이던 건을 다시 대기로. 되돌린 건수</summary>
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Documents.Where(d => d.Status == DocumentStatus.Processing)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DocumentStatus.Queued), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = await RecoverAsync(stoppingToken);
        if (recovered > 0) logger.LogInformation("처리 중에 멈췄던 문서 {Count}건을 다시 처리합니다", recovered);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                    await _signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);  // 알림을 놓쳐도 주기적으로 확인
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // DB · 파일 이동 오류: 같은 건을 계속 붙잡지 않게 잠시 쉬었다 다시 (해당 문서는 ProcessNextAsync 에서 실패 처리)
                logger.LogError(ex, "대기열 처리 오류");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>대기 중인 가장 오래된 1건 처리. 처리할 건이 없으면 false</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = await db.Documents.Where(d => d.Status == DocumentStatus.Queued).OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
        if (doc is null) return false;

        doc.Status = DocumentStatus.Processing;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("문서 #{Id} 처리 시작 ({Pack})", doc.Id, doc.PackId);

        try
        {
            if (doc.StoredPath is null || !File.Exists(doc.StoredPath))
            {
                Finish(doc, DocumentStatus.Failed, "처리 대기 중이던 원본 파일이 없습니다 (지워졌거나 옮겨짐)");
            }
            else if (catalog.Get(doc.PackId) is not { } pack)
            {
                Finish(doc, DocumentStatus.Failed, $"이 프로그램에 없는 문서 종류입니다 ({doc.PackId})");
                doc.StoredPath = router.MoveToFailed(doc.StoredPath, doc.OriginalName, doc.StatusReason!);
            }
            else
            {
                var result = await runner.RunAsync(doc, pack, clock.GetUtcNow(), ct);
                doc.PackVersion = pack.Version;
                if (result.Extraction is { } extraction) db.Extractions.Add(extraction);
                doc.TypeWarning = result.TypeWarning;
                switch (result.Outcome)
                {
                    case Outcome.Processed:
                        Finish(doc, DocumentStatus.Processed, null);
                        doc.StoredPath = router.MoveToDone(doc.StoredPath, doc.OriginalName, pack);
                        break;
                    case Outcome.NeedsReview:
                        Finish(doc, DocumentStatus.NeedsReview, result.Reason);
                        doc.StoredPath = router.MoveToReview(doc.StoredPath, doc.OriginalName, pack);
                        break;
                    default:
                        Finish(doc, DocumentStatus.Failed, result.Reason);
                        doc.StoredPath = router.MoveToFailed(doc.StoredPath, doc.OriginalName, result.Reason!);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 앱 종료: Processing 으로 남겨 두면 다음 시작 때 RecoverAsync 가 다시 대기로 돌림
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "문서 #{Id} 처리 중 오류", doc.Id);
            Finish(doc, DocumentStatus.Failed, $"처리 중 오류: {ex.Message}");
            if (doc.StoredPath is { } p && File.Exists(p))  // StoredPath 는 옮기기에 성공해야 바뀜 ➔ 아직 queue\ 에 있는 원본
            {
                try { doc.StoredPath = router.MoveToFailed(p, doc.OriginalName, doc.StatusReason!); }
                catch (Exception moveError) { logger.LogError(moveError, "문서 #{Id} 원본을 실패 폴더로 옮기지 못함", doc.Id); }
            }
        }
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("문서 #{Id} 처리 끝: {Status}", doc.Id, doc.Status);
        return true;
    }

    private void Finish(DocumentRecord doc, DocumentStatus status, string? reason)
    {
        doc.Status = status;
        doc.StatusReason = reason;
        doc.ProcessedAt = clock.GetUtcNow();
    }
}
