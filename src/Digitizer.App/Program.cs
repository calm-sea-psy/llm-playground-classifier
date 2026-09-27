using System.Text.Json.Serialization;
using Digitizer.App;
using Digitizer.App.Api;
using Digitizer.App.Data;
using Digitizer.App.Hosting;
using Digitizer.App.Processing;
using Digitizer.App.Status;
using Microsoft.EntityFrameworkCore;

// 한 프로세스: 웹 서버(127.0.0.1, 화면 · API) + 처리 코어 + 알림 영역 아이콘 + OCR 서비스(자식 프로세스).
// 인자: --background (로그인 자동 시작: 브라우저를 열지 않음) · --no-tray (아이콘 없이, 개발 · 시험)
// 화면 파일은 실행 폴더의 wwwroot (빌드가 ClientApp\dist 를 복사, dotnet run 에서도 같은 곳)
Console.OutputEncoding = System.Text.Encoding.UTF8;  // dotnet run · 출력 연결 시 한글 (기본 코드 페이지면 깨짐)
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
var url = (builder.Configuration["Urls"] ?? "http://127.0.0.1:5310").Split(';')[0].TrimEnd('/');
builder.WebHost.UseUrls(url);

var paths = AppPaths.Default();
Directory.CreateDirectory(paths.DataRoot);

// 이미 실행 중이면 (바로가기를 다시 누름 · 자동 시작 뒤 수동 실행) 화면만 열고 끝냄
using var instance = new SingleInstance(paths.DataRoot);
if (!instance.IsFirst)
{
    if (!args.Contains(AutoStart.BackgroundArg)) SingleInstance.OpenBrowser(url);
    return;
}

var settingsFile = new SettingsFile(paths.Settings);
var settings = settingsFile.Current;  // 없으면 기본값으로 만듦
if (settings.Problems() is { Count: > 0 } problems)
    throw new InvalidDataException($"설정 파일 {paths.Settings} 확인 필요: {string.Join("; ", problems)}");

// exe 에는 콘솔 창이 없음 ➔ 날짜별 로그 파일 (개인정보 가림)
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(paths.DataRoot, "logs")));

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
builder.Services.AddSingleton(new AutoStart());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton(sp => DocumentRunner.OllamaExtractors(sp.GetRequiredService<IHttpClientFactory>()));
// OCR 서비스 · Ollama 를 먼저 (대기열은 OCR 이 준비될 때까지 기다림)
builder.Services.AddSingleton<ServiceSupervisor>();
builder.Services.AddSingleton<IServiceReadiness>(sp => sp.GetRequiredService<ServiceSupervisor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceSupervisor>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessingQueue>());
builder.Services.AddHostedService<FolderWatcher>();
builder.Services.AddHostedService<RetentionService>();
if (!args.Contains("--no-tray"))
    builder.Services.AddHostedService(sp => ActivatorUtilities.CreateInstance<TrayIcon>(sp, url));

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

// 바로가기로 실행하면 화면을 엶 (로그인 자동 시작은 알림 영역에만)
if (!args.Contains(AutoStart.BackgroundArg))
    app.Lifetime.ApplicationStarted.Register(() => SingleInstance.OpenBrowser(url));

app.Run();
