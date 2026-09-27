using Digitizer.App.Data;
using Digitizer.App.Processing;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Tests;

/// <summary>보관 기한 규칙: 엑셀(ExportRetentionDays) · 원본(RetentionDays) 모두 기본 90일, 0 = 지우지 않음, 1 = 1일(24시간)이 되면</summary>
public sealed class ExportRetentionTests
{
    /// <summary>내보내기 폴더에 엑셀 파일 + 기록 (내보낸 때 = 지금)</summary>
    private static async Task<(long Id, string Path)> ExportAsync(CoreHarness h, string name = "영수증_20260927.xlsx")
    {
        var path = Path.Combine(h.Folders.Exports, name);
        Directory.CreateDirectory(h.Folders.Exports);
        await File.WriteAllTextAsync(path, "xlsx");
        await using var db = h.NewDb();
        var export = new ExportRecord { PackId = "receipt", FilePath = path, DocumentCount = 1, CreatedAt = h.Clock.GetUtcNow() };
        db.Exports.Add(export);
        await db.SaveChangesAsync();
        return (export.Id, path);
    }

    private static ExportRecord Load(CoreHarness h, long id)
    {
        using var db = h.NewDb();
        return db.Exports.AsNoTracking().Single(e => e.Id == id);
    }

    private static void SetDays(CoreHarness h, int days) => h.Settings.Save(h.Settings.Current with { ExportRetentionDays = days });

    [Fact]
    public void 기본은_90일이고_음수는_저장하지_않는다()
    {
        using var h = new CoreHarness();
        Assert.Equal(90, h.Settings.Current.ExportRetentionDays);
        Assert.Throws<ArgumentException>(() => SetDays(h, -1));
        SetDays(h, 0);
        Assert.Equal(0, new SettingsFile(h.Settings.Path).Current.ExportRetentionDays);  // 설정 파일에 남음
        Assert.Contains("\"ExportRetentionDays\": 0", File.ReadAllText(h.Settings.Path));
    }

    [Fact]
    public async Task 기한이_1일이면_내보낸_때로부터_1일이_지나야_지운다()
    {
        using var h = new CoreHarness();
        SetDays(h, 1);
        var (id, path) = await ExportAsync(h);

        h.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, (await h.Retention.PurgeAsync()).ExportsDeleted);
        Assert.True(File.Exists(path));

        h.Clock.Advance(TimeSpan.FromHours(1));  // 딱 1일
        Assert.Equal(1, (await h.Retention.PurgeAsync()).ExportsDeleted);
        Assert.False(File.Exists(path));
        Assert.Equal(h.Clock.GetUtcNow(), Load(h, id).DeletedAt);

        Assert.Equal(0, (await h.Retention.PurgeAsync()).ExportsDeleted);  // 이미 지운 것은 다시 세지 않음
    }

    [Fact]
    public async Task 기한이_0이면_지우지_않는다()
    {
        using var h = new CoreHarness();
        SetDays(h, 0);
        var (id, path) = await ExportAsync(h);

        h.Clock.Advance(TimeSpan.FromDays(3650));
        Assert.Equal(0, (await h.Retention.PurgeAsync()).ExportsDeleted);
        Assert.True(File.Exists(path));
        Assert.Null(Load(h, id).DeletedAt);
    }

    [Fact]
    public async Task 기본_90일은_89일에는_남고_90일에_지운다()
    {
        using var h = new CoreHarness();
        var (_, path) = await ExportAsync(h);

        h.Clock.Advance(TimeSpan.FromDays(89));
        await h.Retention.PurgeAsync();
        Assert.True(File.Exists(path));

        h.Clock.Advance(TimeSpan.FromDays(1));
        await h.Retention.PurgeAsync();
        Assert.False(File.Exists(path));
    }

    // 원본 보관 기한(RetentionDays)도 같은 규칙: 0 = 지우지 않음, 1 = 접수한 때로부터 1일(24시간)이 되면

    [Fact]
    public async Task 원본_보관_기한이_0이면_원본과_원문을_지우지_않는다()
    {
        using var h = new CoreHarness();
        h.Settings.Save(h.Settings.Current with { RetentionDays = 0 });
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);

        h.Clock.Advance(TimeSpan.FromDays(3650));
        Assert.Equal(new RetentionService.Summary(0, 0, 0), await h.Retention.PurgeAsync());
        Assert.True(File.Exists(doc.StoredPath));
        Assert.NotNull(h.Load(doc.Id).Extractions.Single().SourceText);
        Assert.Throws<ArgumentException>(() => h.Settings.Save(h.Settings.Current with { RetentionDays = -1 }));
    }

    [Fact]
    public async Task 원본_보관_기한이_1이면_접수한_때로부터_1일이_되면_지운다()
    {
        using var h = new CoreHarness();
        h.Settings.Save(h.Settings.Current with { RetentionDays = 1 });
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);

        h.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, (await h.Retention.PurgeAsync()).Deleted);
        Assert.True(File.Exists(doc.StoredPath));

        h.Clock.Advance(TimeSpan.FromHours(1));  // 딱 1일 (엑셀과 같은 판정)
        Assert.Equal(1, (await h.Retention.PurgeAsync()).Deleted);
        Assert.False(File.Exists(doc.StoredPath));
        Assert.Empty(h.All());  // 내보낸 적 없음 ➔ 기록도 지움
    }

    [Fact]
    public async Task 원본을_지우지_않아도_엑셀_보관_기한은_따로_적용된다()
    {
        using var h = new CoreHarness();
        h.Settings.Save(h.Settings.Current with { RetentionDays = 0, ExportRetentionDays = 1 });
        var (_, path) = await ExportAsync(h);

        h.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, (await h.Retention.PurgeAsync()).ExportsDeleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task 사용자가_이미_옮긴_파일은_지운_것으로_표시하고_열려_있는_파일은_다음에_다시()
    {
        using var h = new CoreHarness();
        SetDays(h, 1);
        var (moved, movedPath) = await ExportAsync(h, "옮김.xlsx");
        var (open, openPath) = await ExportAsync(h, "열림.xlsx");
        File.Delete(movedPath);
        h.Clock.Advance(TimeSpan.FromDays(2));

        await using (new FileStream(openPath, FileMode.Open, FileAccess.Read, FileShare.None))  // 엑셀로 열어 둔 상태
        {
            Assert.Equal(1, (await h.Retention.PurgeAsync()).ExportsDeleted);
        }
        Assert.NotNull(Load(h, moved).DeletedAt);
        Assert.Null(Load(h, open).DeletedAt);
        Assert.True(File.Exists(openPath));

        Assert.Equal(1, (await h.Retention.PurgeAsync()).ExportsDeleted);  // 닫은 뒤 다음 정리 때
        Assert.False(File.Exists(openPath));
    }
}
