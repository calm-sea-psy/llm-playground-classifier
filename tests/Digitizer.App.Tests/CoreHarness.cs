using System.Text.Json.Nodes;
using Digitizer.App;
using Digitizer.App.Data;
using Digitizer.App.Processing;
using Digitizer.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Digitizer.App.Tests;

/// <summary>시각을 손으로 옮기는 시계 (보관 기한 · 안정 대기 테스트)</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>원문 추출 대신: 파일 내용(UTF-8 텍스트)을 그대로 원문으로. "!fail" 이면 OCR 서비스 연결 실패 흉내</summary>
public sealed class FakeReader : ISourceReader
{
    public List<string> Read { get; } = [];

    public async Task<SourceText> ReadAsync(string path, DocumentType pack, CancellationToken ct)
    {
        Read.Add(Path.GetFileName(path));
        var text = await File.ReadAllTextAsync(path, ct);
        if (text == "!fail") throw new HttpRequestException("연결 거부");
        return new SourceText(text, "pdf-text", 1, 5);
    }
}

/// <summary>LLM 대신: 원문 첫 줄이 "#키" 면 Responses[키], 아니면 Default</summary>
public sealed class FakeExtractor(Func<string, JsonObject?> respond) : IFieldExtractor
{
    public int Calls { get; private set; }

    public Task<Extraction> ExtractMessagesAsync(DocumentType type, string system, string user, IReadOnlyList<ImageInput>? images, CancellationToken ct = default)
    {
        Calls++;
        var fields = respond(user);
        return Task.FromResult(new Extraction(fields, fields?.ToJsonString() ?? "not json", 10, 100, 50, 1, fields is null ? "JSON 형식 오류" : null));
    }
}

/// <summary>임시 데이터 · 문서 폴더 + 실제 팩(출력 폴더 packs\) + 가짜 원문 · LLM 으로 처리 코어 조립</summary>
public sealed class CoreHarness : IDisposable
{
    public string Temp { get; } = Path.Combine(Path.GetTempPath(), $"digitizer-core-{Guid.NewGuid():N}");
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero));
    public FakeReader Reader { get; } = new();
    public FakeExtractor Extractor { get; }
    public ServiceProvider Services { get; }
    public AppPaths Paths { get; }
    public SettingsFile Settings { get; }

    public CoreHarness(Func<string, JsonObject?>? respond = null, double stableSeconds = 0)
    {
        Extractor = new FakeExtractor(respond ?? (user => Samples.Receipt()));
        Paths = new AppPaths(Path.Combine(Temp, "data"));
        Directory.CreateDirectory(Paths.DataRoot);
        Settings = new SettingsFile(Paths.Settings);
        Settings.Save(Settings.Current with { DocumentsRoot = Path.Combine(Temp, "docs"), StableSeconds = stableSeconds });

        var services = new ServiceCollection().AddLogging();
        services.AddDigitizerCore(Paths, Settings);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton<ISourceReader>(Reader);
        services.AddSingleton<Func<AppSettings, string, IFieldExtractor>>((_, _) => Extractor);
        services.AddSingleton<FolderWatcher>();
        services.AddSingleton<RetentionService>();
        Services = services.BuildServiceProvider();

        using var db = NewDb();
        db.Database.Migrate();
        Folders.Ensure(Catalog.Packs);
    }

    public PackCatalog Catalog => Services.GetRequiredService<PackCatalog>();
    public UserFolders Folders => Services.GetRequiredService<FileRouter>().Folders;
    public ProcessingQueue Queue => Services.GetRequiredService<ProcessingQueue>();
    public FolderWatcher Watcher => Services.GetRequiredService<FolderWatcher>();
    public DocumentIntake Intake => Services.GetRequiredService<DocumentIntake>();
    public RetentionService Retention => Services.GetRequiredService<RetentionService>();
    public DocumentType Pack(string id) => Catalog.Get(id)!;
    public DigitizerDb NewDb() => Services.GetRequiredService<IDbContextFactory<DigitizerDb>>().CreateDbContext();

    /// <summary>넣기\<팩> 에 파일 만들기</summary>
    public string Drop(string pack, string name, string content)
    {
        var path = Path.Combine(Folders.Inbox(Pack(pack)), name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>접수 + 대기열을 모두 처리</summary>
    public async Task<DocumentRecord> AcceptAndProcessAsync(string pack, string name, string content)
    {
        var doc = await Intake.AcceptAsync(Drop(pack, name, content), Pack(pack), "upload");
        while (await Queue.ProcessNextAsync()) { }
        return Load(doc.Id);
    }

    public DocumentRecord Load(long id)
    {
        using var db = NewDb();
        return db.Documents.AsNoTracking().Include(d => d.Extractions).Include(d => d.Corrections).Single(d => d.Id == id);
    }

    public List<DocumentRecord> All()
    {
        using var db = NewDb();
        return db.Documents.AsNoTracking().OrderBy(d => d.Id).ToList();
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();  // DB 파일 잠금 풀기
        if (Directory.Exists(Temp)) Directory.Delete(Temp, recursive: true);
    }
}

/// <summary>검증을 통과하는 영수증 원문 · 필드, 이력서 필드</summary>
public static class Samples
{
    public const string ReceiptText = "테스트마트\n사업자 123-45-67891\n2026-09-01 12:30\n김밥 1 3,000 3,000\n합계 3,000";

    public static JsonObject Receipt() => new()
    {
        ["store_name"] = "테스트마트", ["business_no"] = "123-45-67891", ["date"] = "2026-09-01", ["time"] = "12:30", ["receipt_no"] = null,
        ["items"] = new JsonArray(new JsonObject { ["name"] = "김밥", ["qty"] = 1, ["unit_price"] = 3000, ["amount"] = 3000 }),
        ["subtotal"] = null, ["tax"] = null, ["gross_total"] = null, ["total"] = 3000, ["payment_method"] = null,
    };

    /// <summary>영수증을 이력서 폴더에 넣으면 나올 법한 결과: 이름 없음, 대부분 빔</summary>
    public static JsonObject ResumeFromReceipt() => new()
    {
        ["name"] = null, ["phone"] = null, ["email"] = null, ["address"] = "서울시 어딘가", ["birth_date"] = null,
        ["education"] = new JsonArray(), ["career"] = new JsonArray(), ["certificates"] = new JsonArray(), ["languages"] = new JsonArray(),
        ["skills"] = new JsonArray(),
    };
}
