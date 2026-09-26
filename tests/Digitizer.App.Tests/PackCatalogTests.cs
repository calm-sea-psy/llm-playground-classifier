using Digitizer.App;
using Digitizer.Engine;

namespace Digitizer.App.Tests;

/// <summary>exe 에는 합격 표시(release)된 팩만: 빌드가 고른 팩 = 저장소에서 release 있는 팩, 앱도 시작할 때 다시 걸러 냄</summary>
public sealed class PackCatalogTests : IDisposable
{
    private static readonly string Repo = FindRepo();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"pack-catalog-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public void 빌드_출력에는_합격한_팩만_들어간다()
    {
        var released = Directory.GetDirectories(Path.Combine(Repo, "packs"))
            .Select(DocumentType.Load).Where(p => p.Release is not null).Select(p => p.Id).Order().ToList();
        var copied = Directory.GetDirectories(PackCatalog.DefaultRoot).Select(d => Path.GetFileName(d)).Order().ToList();

        Assert.Equal(["receipt", "resume"], released);
        Assert.Equal(released, copied);
        Assert.Empty(Directory.GetDirectories(PackCatalog.DefaultRoot, ".history", SearchOption.AllDirectories));
    }

    [Fact]
    public void 출력_폴더의_팩을_모두_쓴다()
    {
        var catalog = new PackCatalog(PackCatalog.DefaultRoot);
        Assert.Empty(catalog.SkippedPacks);
        Assert.Equal("conditional", catalog.Get("resume")!.Release!.Status);
        Assert.Equal("passed", catalog.Get("receipt")!.Release!.Status);
    }

    [Fact]
    public void 합격_표시가_없거나_폴더_이름이_다른_팩은_쓰지_않는다()
    {
        Copy("commercial_invoice", "commercial_invoice");  // release 없음
        Copy("resume", "resume_copy");                      // 폴더 이름 ≠ id
        Copy("receipt", "receipt");

        var catalog = new PackCatalog(_temp);

        Assert.Equal(["receipt"], catalog.Packs.Select(p => p.Id));
        Assert.Contains(catalog.SkippedPacks, s => s.Folder == "commercial_invoice" && s.Reason.Contains("release"));
        Assert.Contains(catalog.SkippedPacks, s => s.Folder == "resume_copy" && s.Reason.Contains("폴더 이름"));
    }

    private void Copy(string pack, string folder)
    {
        var target = Path.Combine(_temp, folder);
        Directory.CreateDirectory(target);
        foreach (var f in Directory.GetFiles(Path.Combine(Repo, "packs", pack))) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
    }

    private static string FindRepo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "llm-playground-classifier.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("저장소 루트를 찾지 못했습니다");
    }
}
