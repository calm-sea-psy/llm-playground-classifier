using System.Text.Json.Nodes;
using Api.Modules.Text.Pipeline;
using Digitizer.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Api.Tests;

/// <summary>팩 관리 화면의 저장 (PackStore.Save): 버전 올림 · 이전 버전 보관 · 화면에 없는 키 보존 · 잘못된 정의는 파일을 건드리지 않음</summary>
public sealed class PackStoreTests : IDisposable
{
    private static readonly string Repo = FindRepo();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pack-store-test-{Guid.NewGuid():N}");
    private readonly PackStore _store;

    public PackStoreTests()
    {
        var resume = Path.Combine(_root, "resume");
        Directory.CreateDirectory(resume);
        foreach (var f in Directory.GetFiles(Path.Combine(Repo, "packs", "resume"))) File.Copy(f, Path.Combine(resume, Path.GetFileName(f)));
        _store = new PackStore(new Env(), Options.Create(new PipelineOptions { PacksRoot = _root }), TimeProvider.System);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void 저장소의_모든_팩은_검사를_통과한다()
    {
        foreach (var dir in Directory.GetDirectories(Path.Combine(Repo, "packs")))
            Assert.Empty(PackCheck.Check(DocumentType.Load(dir)));
    }

    [Fact]
    public void 버전을_올려야_저장된다()
    {
        var type = _store.TypeJson("resume")!;
        var errors = _store.Save("resume", type, new Dictionary<string, string?>(), null, create: false);
        Assert.Contains(errors, e => e.Contains("버전을 올려야"));
    }

    [Fact]
    public void 고치면_이전_버전을_보관하고_모르는_키와_변경_메모를_남긴다()
    {
        var type = _store.TypeJson("resume")!;
        type["version"] = "0.3.0";
        type["display_name"] = "이력서 (수정)";
        var errors = _store.Save("resume", type, new Dictionary<string, string?> { ["prompt.md"] = "새 지시문" }, "이름 변경", create: false);

        Assert.Empty(errors);
        var saved = _store.Get("resume")!;
        Assert.Equal("0.3.0", saved.Version);
        Assert.Equal("이력서 (수정)", saved.DisplayName);
        Assert.Equal("새 지시문", saved.Prompt);
        var json = _store.TypeJson("resume")!;
        Assert.NotNull(json["excel"]);                                           // 화면에 없는 키 보존
        Assert.Equal("이름 변경", json["changelog"]![0]!["changes"]!.GetValue<string>());
        Assert.Equal("0.2.0", Assert.Single(_store.History("resume")).Version);
        Assert.Equal("0.2.0", DocumentType.Load(Path.Combine(_root, "resume", PackStore.HistoryFolder, "0.2.0")).Version);
        Assert.Single(_store.All());                                             // .history 는 팩 목록에 안 나옴
    }

    [Fact]
    public void 잘못된_정의는_파일을_바꾸지_않는다()
    {
        var before = File.ReadAllText(Path.Combine(_root, "resume", "type.json"));
        var type = _store.TypeJson("resume")!;
        type["version"] = "0.3.0";
        type["fields"]!.AsArray().Add(new JsonObject { ["name"] = "name", ["label"] = "중복", ["type"] = "color" });
        type["rules"]!.AsArray().Add("no_such_rule");

        var errors = _store.Save("resume", type, new Dictionary<string, string?>(), null, create: false);

        Assert.NotEmpty(errors);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "resume", "type.json")));
        Assert.Empty(_store.History("resume"));
    }

    [Fact]
    public void 검사_항목()
    {
        var type = _store.Get("resume")! with
        {
            Fields = [new("name", "이름", "color"), new("career", "경력", "list", Key: "company", Ranged: true, Items: [new("title", "직함", "text")])],
            Prompt = "{{$unknown}}",
            UserTemplate = "원문 없음",
        };
        var errors = PackCheck.Check(type);
        Assert.Contains(errors, e => e.Contains("모르는 타입 color"));
        Assert.Contains(errors, e => e.Contains("구분 필드 company"));
        Assert.Contains(errors, e => e.Contains("start · end"));
        Assert.Contains(errors, e => e.Contains("unknown"));
        Assert.Contains(errors, e => e.Contains("user.md") && e.Contains("ocr_text"));
    }

    [Fact]
    public void 새_팩_추가()
    {
        var type = new JsonObject
        {
            ["id"] = "business_card",
            ["version"] = "0.1.0",
            ["display_name"] = "명함",
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "name", ["label"] = "이름", ["type"] = "text", ["required"] = true }),
            ["forbidden"] = new JsonArray(),
            ["rules"] = new JsonArray(),
        };
        Assert.Contains(_store.Save("business_card", type, new Dictionary<string, string?> { ["../x.md"] = "" }, null, create: true),
            e => e.Contains("둘 수 없는 파일"));
        Assert.Contains(_store.Save("business_card", type, new Dictionary<string, string?>(), null, create: true),
            e => e.Contains("prompt.md"));
        Assert.Empty(_store.Save("business_card", type, new Dictionary<string, string?> { ["prompt.md"] = "명함에서 이름을 옮기세요." }, "처음", create: true));
        Assert.Equal("명함", _store.Get("business_card")!.DisplayName);
        Assert.Contains(_store.Save("business_card", type, new Dictionary<string, string?> { ["prompt.md"] = "x" }, null, create: true),
            e => e.Contains("이미 있는"));
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packs"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("packs 폴더를 찾지 못했습니다");
    }
}
