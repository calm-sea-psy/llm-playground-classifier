using System.Text.Json.Serialization;
using Digitizer.App;
using Digitizer.App.Api;
using Digitizer.App.Data;
using Digitizer.App.Processing;
using Digitizer.App.Status;
using Microsoft.EntityFrameworkCore;

// 처리 코어(2단계) + 화면 · API(3단계). 트레이 · 자식 프로세스(4단계)는 이후 단계
// 화면 파일은 실행 폴더의 wwwroot (빌드가 ClientApp\dist 를 복사, dotnet run 에서도 같은 곳)
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
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
builder.Services.AddSingleton<SystemCheck>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
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

app.UseMiddleware<LocalOnly>();
app.UseDefaultFiles();
// ClientApp 빌드 결과 (wwwroot). PDF.js 의 글꼴 문자표(.bcmap) · 표준 글꼴(.pfb)은 기본 목록에 없는 형식이라 추가 (없으면 404 ➔ 한글이 안 보임)
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".bcmap"] = "application/octet-stream";
contentTypes.Mappings[".pfb"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
app.MapDigitizerApi();
app.MapFallbackToFile("index.html");

app.Run();
