using System.Text.Json.Nodes;
using Digitizer.App.Data;
using Digitizer.App.Processing;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Tests;

/// <summary>4차-exe 2단계 처리 코어: 설정 파일 · DB · 대기열 · 감시 폴더 · 파일 이동 · 종류 의심 · 보관 기한</summary>
public sealed class ProcessingCoreTests
{
    // 2-1 설정 파일

    [Fact]
    public void 처음_실행하면_기본값으로_설정_파일을_만든다()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"digitizer-settings-{Guid.NewGuid():N}");
        try
        {
            var file = new SettingsFile(Path.Combine(dir, "settings.json"));
            Assert.Equal(90, file.Current.RetentionDays);
            Assert.True(File.Exists(file.Path));
            Assert.Contains("\"RetentionDays\": 90", File.ReadAllText(file.Path));
            Assert.DoesNotContain("ResolvedDocumentsRoot", File.ReadAllText(file.Path));  // 계산값은 파일에 쓰지 않음
            Assert.EndsWith("문서 전산화", file.Current.ResolvedDocumentsRoot);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 빠진_항목은_기본값으로_채우고_잘못된_값은_저장하지_않는다()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"digitizer-settings-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{ "RetentionDays": 30, "Model": "gemma-4-E4B" }""");
            var file = new SettingsFile(path);

            Assert.Equal(30, file.Current.RetentionDays);
            Assert.Equal("gemma-4-E4B", file.Current.Model);
            Assert.Equal(0.9, file.Current.FallbackConfidence);  // 측정한 기본값
            Assert.Contains("\"VlmMaxImageSide\": 1600", File.ReadAllText(path));

            Assert.Throws<ArgumentException>(() => file.Save(file.Current with { RetentionDays = -1 }));
            Assert.Equal(30, new SettingsFile(path).Current.RetentionDays);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // 2-2 마이그레이션

    [Fact]
    public void 새_DB_에_마이그레이션이_모두_적용되고_모델과_어긋나지_않는다()
    {
        using var h = new CoreHarness();
        using var db = h.NewDb();
        Assert.Equal(db.Database.GetMigrations(), db.Database.GetAppliedMigrations());
        Assert.False(db.Database.HasPendingModelChanges(), "모델이 바뀌었는데 마이그레이션이 없음 ➔ dotnet ef migrations add");
        Assert.Equal(0, db.Documents.Count());
        Assert.Equal(0, db.Exports.Count());
    }

    // 2-3 대기열

    [Fact]
    public async Task 처리_중에_멈춘_건은_다시_시작하면_다시_처리한다()
    {
        using var h = new CoreHarness();
        var doc = await h.Intake.AcceptAsync(h.Drop("receipt", "a.pdf", Samples.ReceiptText), h.Pack("receipt"), "watch");
        await using (var db = h.NewDb())
        {
            // 앱이 처리 도중 꺼진 상태
            await db.Documents.Where(d => d.Id == doc.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DocumentStatus.Processing));
        }
        Assert.False(await h.Queue.ProcessNextAsync());  // Processing 은 대기열에 없음

        Assert.Equal(1, await h.Queue.RecoverAsync());
        Assert.True(await h.Queue.ProcessNextAsync());

        var done = h.Load(doc.Id);
        Assert.Equal(DocumentStatus.Processed, done.Status);
        Assert.Single(done.Extractions);
    }

    [Fact]
    public async Task 한_번에_한_건씩_접수한_순서대로_처리한다()
    {
        using var h = new CoreHarness();
        foreach (var n in new[] { "1.pdf", "2.pdf", "3.pdf" })
            await h.Intake.AcceptAsync(h.Drop("receipt", n, Samples.ReceiptText + "\n" + n), h.Pack("receipt"), "watch");

        Assert.True(await h.Queue.ProcessNextAsync());
        Assert.Equal([DocumentStatus.Processed, DocumentStatus.Queued, DocumentStatus.Queued], h.All().Select(d => d.Status));
        while (await h.Queue.ProcessNextAsync()) { }
        Assert.Equal(["1.pdf", "2.pdf", "3.pdf"], h.All().Select(d => d.OriginalName));
        Assert.Equal(3, h.Reader.Read.Count);
        Assert.All(h.All(), d => Assert.Equal(DocumentStatus.Processed, d.Status));
    }

    // 2-4 감시 폴더

    [Fact]
    public async Task 쓰는_중인_파일은_닫히고_크기가_그대로일_때까지_기다린다()
    {
        using var h = new CoreHarness(stableSeconds: 2);
        var path = Path.Combine(h.Folders.Inbox(h.Pack("receipt")), "scan.pdf");
        await using (var writing = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await writing.WriteAsync("테스트마트"u8.ToArray());
            await writing.FlushAsync();
            Assert.Equal(0, await h.Watcher.ScanAsync());  // 처음 봄
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await h.Watcher.ScanAsync());  // 크기는 그대로지만 아직 열려 있음
        }

        await File.AppendAllTextAsync(path, "\n2026-09-01");  // 닫힌 뒤 더 씀 ➔ 다시 안정될 때까지
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await h.Watcher.ScanAsync());
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, await h.Watcher.ScanAsync());  // 바뀐 뒤 1초
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await h.Watcher.ScanAsync());

        Assert.False(File.Exists(path));
        var doc = Assert.Single(h.All());
        Assert.Equal(("scan.pdf", "receipt", "watch", DocumentStatus.Queued), (doc.OriginalName, doc.PackId, doc.Source, doc.Status));
        Assert.StartsWith(h.Paths.Queue, doc.StoredPath);
    }

    [Fact]
    public async Task 임시_파일은_접수하지_않는다()
    {
        using var h = new CoreHarness();
        h.Drop("receipt", "~$문서.docx", "잠금");
        h.Drop("receipt", "받는중.pdf.crdownload", "..");
        Assert.Equal(0, await h.Watcher.ScanAsync());
        Assert.Empty(h.All());
    }

    [Fact]
    public async Task 같은_내용을_두_번_넣으면_두_번째는_처리하지_않고_실패_폴더로()
    {
        using var h = new CoreHarness();
        h.Drop("receipt", "영수증.pdf", Samples.ReceiptText);
        Assert.Equal(1, await h.Watcher.ScanAsync());
        while (await h.Queue.ProcessNextAsync()) { }

        h.Drop("receipt", "영수증 사본.pdf", Samples.ReceiptText);
        Assert.Equal(1, await h.Watcher.ScanAsync());
        Assert.False(await h.Queue.ProcessNextAsync());

        var docs = h.All();
        Assert.Equal([DocumentStatus.Processed, DocumentStatus.Duplicate], docs.Select(d => d.Status));
        Assert.Equal(1, h.Extractor.Calls);
        var dup = Path.Combine(h.Folders.Failed, "영수증 사본.pdf");
        Assert.True(File.Exists(dup));
        Assert.Contains($"#{docs[0].Id}", File.ReadAllText(dup + FileRouter.ReasonSuffix));

        // 다른 종류 폴더라면 같은 내용이어도 받음 (종류를 잘못 골랐다가 다시 넣는 경우)
        var other = await h.Intake.AcceptAsync(h.Drop("resume", "영수증.pdf", Samples.ReceiptText), h.Pack("resume"), "watch");
        Assert.Equal(DocumentStatus.Queued, other.Status);
    }

    [Fact]
    public async Task 실패한_건은_같은_내용을_다시_넣으면_처리한다()
    {
        using var h = new CoreHarness();
        var failed = await h.AcceptAndProcessAsync("receipt", "a.pdf", "!fail");
        Assert.Equal(DocumentStatus.Failed, failed.Status);

        var again = await h.AcceptAndProcessAsync("receipt", "a.pdf", "!fail");
        Assert.NotEqual(DocumentStatus.Duplicate, again.Status);
    }

    // 2-5 파일 이동

    [Fact]
    public async Task 검증을_통과하면_처리됨_날짜_폴더로()
    {
        using var h = new CoreHarness();
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);

        Assert.True(doc.Status == DocumentStatus.Processed, $"{doc.StatusReason} {doc.Extractions.SingleOrDefault()?.Issues}");
        Assert.Null(doc.StatusReason);
        Assert.Equal(Path.Combine(h.Folders.Root, "처리됨", "영수증", "2026-09-27", "영수증.pdf"), doc.StoredPath);
        Assert.True(File.Exists(doc.StoredPath));
        Assert.Empty(Directory.GetFiles(h.Paths.Queue));

        var x = Assert.Single(doc.Extractions);
        Assert.Equal(("receipt@1.0.0", "gemma4:12b", "text", 0), (x.Engine, x.Model, x.FinalSource, x.ErrorCount));
        Assert.Equal(Samples.ReceiptText, x.SourceText);
        Assert.Equal(3000, JsonNode.Parse(x.Fields!)!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task 검증_문제가_있으면_확인_필요_폴더로()
    {
        using var h = new CoreHarness(_ => Samples.Receipt().Also(f => f["total"] = 9999));  // 원문에 없는 합계
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);

        Assert.Equal(DocumentStatus.NeedsReview, doc.Status);
        Assert.Contains("검증 문제", doc.StatusReason);
        Assert.Equal(Path.Combine(h.Folders.Root, "확인 필요", "영수증", "영수증.pdf"), doc.StoredPath);
        Assert.True(doc.Extractions.Single().ErrorCount > 0);
    }

    [Fact]
    public async Task 조건부_합격_종류는_검증을_통과해도_확인_필요로()
    {
        const string text = "홍길동\n010-1234-5678\nhong@example.com";
        using var h = new CoreHarness(_ => Samples.ResumeFromReceipt().Also(f =>
        {
            f["name"] = "홍길동";
            f["phone"] = "010-1234-5678";
            f["email"] = "hong@example.com";
            f["address"] = null;
        }));
        var doc = await h.AcceptAndProcessAsync("resume", "이력서.pdf", text);

        Assert.Equal(0, doc.Extractions.Single().ErrorCount);
        Assert.Equal(DocumentStatus.NeedsReview, doc.Status);
        Assert.Contains("조건부", doc.StatusReason);
        Assert.False(doc.TypeWarning);
        Assert.Equal(Path.Combine(h.Folders.Root, "확인 필요", "이력서", "이력서.pdf"), doc.StoredPath);
    }

    [Fact]
    public async Task 원문_추출에_실패하면_실패_폴더와_사유_파일()
    {
        using var h = new CoreHarness();
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", "!fail");

        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.Contains("연결하지 못했습니다", doc.StatusReason);
        Assert.Equal(Path.Combine(h.Folders.Failed, "영수증.pdf"), doc.StoredPath);
        Assert.Contains("원문 추출 실패", File.ReadAllText(doc.StoredPath + FileRouter.ReasonSuffix));
        Assert.Equal(0, h.Extractor.Calls);
    }

    [Fact]
    public async Task 지원하지_않는_형식은_접수하자마자_실패_폴더로()
    {
        using var h = new CoreHarness();
        h.Drop("receipt", "메모.txt", "hello");
        Assert.Equal(1, await h.Watcher.ScanAsync());

        var doc = Assert.Single(h.All());
        Assert.Equal(DocumentStatus.Failed, doc.Status);
        Assert.Contains(".txt", doc.StatusReason);
        Assert.True(File.Exists(Path.Combine(h.Folders.Failed, "메모.txt" + FileRouter.ReasonSuffix)));
        Assert.False(await h.Queue.ProcessNextAsync());
    }

    [Fact]
    public async Task 같은_이름이_있으면_번호를_붙이고_덮어쓰지_않는다()
    {
        using var h = new CoreHarness();
        var first = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);
        var second = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText + "\n두 번째");

        Assert.EndsWith("영수증.pdf", first.StoredPath);
        Assert.EndsWith("영수증 (2).pdf", second.StoredPath);
        Assert.True(File.Exists(first.StoredPath) && File.Exists(second.StoredPath));
    }

    // 2-6 종류 의심

    [Fact]
    public async Task 필수_필드가_대부분_비면_종류_확인_경고()
    {
        using var h = new CoreHarness(_ => Samples.ResumeFromReceipt());
        var doc = await h.AcceptAndProcessAsync("resume", "영수증인데.pdf", Samples.ReceiptText);

        Assert.True(doc.TypeWarning);
        Assert.Equal(DocumentStatus.NeedsReview, doc.Status);
        Assert.Contains("폴더(문서 종류)가 맞는지", doc.StatusReason);
    }

    [Fact]
    public void 필수_필드만_빠지고_나머지가_차_있으면_종류_의심이_아니다()
    {
        using var h = new CoreHarness();
        var receipt = h.Pack("receipt");
        Assert.False(TypeMismatch.Suspect(receipt, Samples.Receipt()));
        Assert.False(TypeMismatch.Suspect(receipt, Samples.Receipt().Also(f => f["total"] = null)));  // 합계만 못 찾음 ➔ 검증이 다룸
        Assert.True(TypeMismatch.Suspect(receipt, new JsonObject
        {
            ["store_name"] = "홍길동", ["business_no"] = null, ["date"] = null, ["time"] = null, ["receipt_no"] = null,
            ["items"] = new JsonArray(), ["subtotal"] = null, ["tax"] = null, ["gross_total"] = null, ["total"] = null, ["payment_method"] = "",
        }));
        Assert.True(TypeMismatch.Suspect(h.Pack("resume"), Samples.ResumeFromReceipt()));
    }

    // 2-7 보관 기한

    [Fact]
    public async Task 보관_기한이_지나면_원본을_지우고_내보낸_건만_기록을_남긴다()
    {
        using var h = new CoreHarness();
        var kept = await h.AcceptAndProcessAsync("receipt", "내보냄.pdf", Samples.ReceiptText);
        var dropped = await h.AcceptAndProcessAsync("receipt", "안내보냄.pdf", Samples.ReceiptText + "\n2");
        var failed = await h.AcceptAndProcessAsync("receipt", "실패.pdf", "!fail");
        await using (var db = h.NewDb())
        {
            var export = new ExportRecord { PackId = "receipt", FilePath = "x.xlsx", DocumentCount = 1, CreatedAt = h.Clock.GetUtcNow() };
            db.Exports.Add(export);
            await db.SaveChangesAsync();
            await db.Documents.Where(d => d.Id == kept.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.LastExportId, export.Id));
            db.Corrections.Add(new CorrectionRecord
            {
                DocumentId = kept.Id, ExtractionId = kept.Extractions[0].Id, FieldPath = "total",
                ExtractedValue = "3000", CorrectedValue = "3500", CreatedAt = h.Clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        h.Clock.Advance(TimeSpan.FromDays(89));
        var late = await h.AcceptAndProcessAsync("receipt", "최근.pdf", Samples.ReceiptText + "\n3");  // 89일 뒤 접수 ➔ 아직 기한 안
        Assert.Equal(new RetentionService.Summary(0, 0, 0), await h.Retention.PurgeAsync());

        h.Clock.Advance(TimeSpan.FromDays(2));  // 앞의 3건은 91일
        var summary = await h.Retention.PurgeAsync();

        // 원본 3 + 실패 사유 1, 내보낸 기록(x.xlsx, 파일 없음)도 엑셀 보관 기한(90일)이 지나 지운 것으로 표시
        Assert.Equal(new RetentionService.Summary(Deleted: 2, Cleared: 1, FilesDeleted: 4, ExportsDeleted: 1), summary);
        Assert.False(File.Exists(kept.StoredPath));
        Assert.False(File.Exists(dropped.StoredPath));
        Assert.False(File.Exists(failed.StoredPath + FileRouter.ReasonSuffix));
        Assert.True(File.Exists(late.StoredPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(kept.StoredPath)));  // 빈 날짜 폴더

        Assert.Equal([kept.Id, late.Id], h.All().Select(d => d.Id));
        var record = h.Load(kept.Id);
        Assert.NotNull(record.PurgedAt);
        Assert.Null(record.StoredPath);
        Assert.Equal("내보냄.pdf", record.OriginalName);
        var x = Assert.Single(record.Extractions);
        Assert.Null(x.SourceText);
        Assert.Null(x.Fields);
        Assert.Null(x.Attempts);
        var c = Assert.Single(record.Corrections);
        Assert.Equal(("total", null, null), (c.FieldPath, c.ExtractedValue, c.CorrectedValue));
        Assert.NotNull(h.Load(late.Id).Extractions.Single().Fields);

        // 지운 건과 같은 내용은 다시 받음
        var again = await h.Intake.AcceptAsync(h.Drop("receipt", "내보냄.pdf", Samples.ReceiptText), h.Pack("receipt"), "watch");
        Assert.Equal(DocumentStatus.Queued, again.Status);
    }

    [Fact]
    public async Task 대기_중인_건은_기한이_지나도_지우지_않는다()
    {
        using var h = new CoreHarness();
        var doc = await h.Intake.AcceptAsync(h.Drop("receipt", "a.pdf", Samples.ReceiptText), h.Pack("receipt"), "watch");
        h.Clock.Advance(TimeSpan.FromDays(100));
        Assert.Equal(new RetentionService.Summary(0, 0, 0), await h.Retention.PurgeAsync());
        Assert.True(File.Exists(doc.StoredPath));
    }
}

internal static class JsonTestExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}
