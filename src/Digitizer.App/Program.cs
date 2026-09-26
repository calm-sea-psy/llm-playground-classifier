using Digitizer.App;

// 0단계 골격: 합격한 팩을 읽어 localhost 에서 확인만. 처리 코어(2단계) · 화면(3단계) · 트레이(4단계)는 이후 단계
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:5310");
builder.Services.AddSingleton(new PackCatalog(PackCatalog.DefaultRoot));

var app = builder.Build();
var catalog = app.Services.GetRequiredService<PackCatalog>();
foreach (var s in catalog.SkippedPacks) app.Logger.LogWarning("팩 {Folder} 을(를) 쓰지 않음: {Reason}", s.Folder, s.Reason);
app.Logger.LogInformation("문서 종류: {Packs}", string.Join(", ", catalog.Packs.Select(p => $"{p.Id}@{p.Version} ({p.Release!.Status})")));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/packs", (PackCatalog c) => c.Packs.Select(p => new
{
    p.Id,
    p.Version,
    p.DisplayName,
    p.Description,
    p.Release,
}));

app.Run();
