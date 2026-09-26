using System.Security.Cryptography;
using Digitizer.App.Data;
using Digitizer.Engine;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Processing;

/// <summary>
/// 문서 접수 (감시 폴더 · 화면 업로드 공통): 내용 해시 ➔ 같은 종류로 이미 접수한 내용이면 중복(실패 폴더로) ➔
/// 아니면 원본을 데이터 폴더 queue\ 로 옮기고 대기열에 올림. 형식이 맞지 않으면 바로 실패 폴더로
/// </summary>
public sealed class DocumentIntake(IDbContextFactory<DigitizerDb> dbFactory, AppPaths paths, FileRouter router, ProcessingQueue queue, TimeProvider clock)
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".docx", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp" };

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp" };

    /// <summary>원본 파일을 접수 (파일은 옮겨짐: queue\ 또는 실패 폴더). source = watch | upload</summary>
    public async Task<DocumentRecord> AcceptAsync(string path, DocumentType pack, string source, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        var hash = await HashAsync(path, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var doc = new DocumentRecord
        {
            PackId = pack.Id,
            PackVersion = pack.Version,
            OriginalName = info.Name,
            StoredPath = path,
            Sha256 = hash,
            SizeBytes = info.Length,
            Source = source,
            Status = DocumentStatus.Queued,
            ReceivedAt = clock.GetUtcNow(),
        };

        // 실패한 건은 다시 넣으면 처리 (OCR 서비스가 꺼져 있었던 경우 등). 보관 기한이 지나 지운 건도 다시 받음
        var earlier = await db.Documents.AsNoTracking()
            .Where(d => d.PackId == pack.Id && d.Sha256 == hash && d.PurgedAt == null
                && d.Status != DocumentStatus.Failed && d.Status != DocumentStatus.Duplicate)
            .OrderBy(d => d.Id).FirstOrDefaultAsync(ct);

        if (!Supported.Contains(info.Extension))
        {
            doc.Status = DocumentStatus.Failed;
            doc.StatusReason = $"지원하지 않는 형식입니다 ({info.Extension}). PDF · DOCX · 이미지(PNG · JPG · TIF · BMP)만 처리합니다";
        }
        else if (earlier is not null)
        {
            doc.Status = DocumentStatus.Duplicate;
            doc.StatusReason = $"같은 내용의 파일을 이미 접수했습니다 (#{earlier.Id} {earlier.OriginalName}, " +
                $"{earlier.ReceivedAt.ToLocalTime():yyyy-MM-dd HH:mm}) ➔ 처리하지 않았습니다";
        }

        db.Documents.Add(doc);
        await db.SaveChangesAsync(ct);  // Id 를 먼저 받아 queue\ 파일 이름에 씀

        if (doc.Status == DocumentStatus.Queued)
        {
            Directory.CreateDirectory(paths.Queue);
            var queued = Path.Combine(paths.Queue, $"{doc.Id}{info.Extension.ToLowerInvariant()}");
            File.Move(path, queued, overwrite: true);
            doc.StoredPath = queued;
        }
        else
        {
            doc.StoredPath = router.MoveToFailed(path, doc.OriginalName, doc.StatusReason!);
            doc.ProcessedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        if (doc.Status == DocumentStatus.Queued) queue.Notify();
        return doc;
    }

    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
