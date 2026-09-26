using Digitizer.App;
using Digitizer.App.Data;
using Digitizer.App.Processing;
using Microsoft.EntityFrameworkCore;

// 2단계: 처리 코어 (설정 파일 · SQLite · 대기열 · 감시 폴더 · 파일 이동 · 보관 기한). 화면(3단계) · 트레이(4단계)는 이후 단계
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5310");

var paths = AppPaths.Default();
Directory.CreateDirectory(paths.DataRoot);
var settingsFile = new SettingsFile(paths.Settings);
var settings = settingsFile.Current;  // 없으면 기본값으로 만듦
if (settings.Problems() is { Count: > 0 } problems)
    throw new InvalidDataException($"설정 파일 {paths.Settings} 확인 필요: {string.Join("; ", problems)}");

builder.Services.AddDigitizerCore(paths, settingsFile);
// 주소 · 시간 제한은 시작할 때 설정값 (바꾸면 다시 시작)
builder.Services.AddHttpClient(OcrSourceReader.OcrClient, c =>
{
    c.BaseAddress = new Uri(settings.OcrUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(settings.OcrTimeoutSeconds);
});
builder.Services.AddHttpClient(OcrSourceReader.OllamaClient, c =>
{
    c.BaseAddress = new Uri(settings.OllamaUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(settings.LlmTimeoutSeconds);
});
builder.Services.AddSingleton<ISourceReader, OcrSourceReader>();
builder.Services.AddSingleton(sp => DocumentRunner.OllamaExtractors(sp.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingQueue>());
builder.Services.AddHostedService<FolderWatcher>();
builder.Services.AddHostedService<RetentionService>();

var app = builder.Build();
var catalog = app.Services.GetRequiredService<PackCatalog>();
foreach (var s in catalog.SkippedPacks) app.Logger.LogWarning("팩 {Folder} 을(를) 쓰지 않음: {Reason}", s.Folder, s.Reason);
app.Logger.LogInformation("문서 종류: {Packs}", string.Join(", ", catalog.Packs.Select(p => $"{p.Id}@{p.Version} ({p.Release!.Status})")));
app.Logger.LogInformation("데이터 {Data} · 문서 폴더 {Docs} · 모델 {Model}", paths.DataRoot, settings.ResolvedDocumentsRoot, settings.Model);

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<DigitizerDb>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/packs", (PackCatalog c) => c.Packs.Select(p => new
{
    p.Id,
    p.Version,
    p.DisplayName,
    p.Description,
    p.Release,
}));
// 3단계 화면 전까지 확인용: 최근 문서 (값 없이 상태만)
app.MapGet("/api/documents", async (IDbContextFactory<DigitizerDb> f) =>
{
    await using var db = await f.CreateDbContextAsync();
    var docs = await db.Documents.AsNoTracking().OrderByDescending(d => d.Id).Take(100).ToListAsync();
    return docs.Select(d => new { d.Id, d.PackId, d.PackVersion, d.OriginalName, Status = d.Status.ToString(), d.StatusReason, d.TypeWarning, d.ReceivedAt, d.ProcessedAt });
});

app.Run();
