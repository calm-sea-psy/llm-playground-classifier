using Api.Modules;
using Api.Modules.Image;
using Api.Modules.Multimodal;
using Api.Modules.Text;
using Api.Shared.Data;
using Api.Shared.Features;
using Api.Shared.Health;
using Api.Shared.Http;
using Api.Shared.Hubs;
using Api.Shared.Jobs;
using Api.Shared.Llm;
using Api.Shared.Prompts;
using Api.Shared.Storage;
using Api.Shared.SystemInfo;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var connectionString = config.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default 가 없습니다 (appsettings.Development.json)");

// 구현된 모듈 목록 (메뉴 순서)
IReadOnlyList<IPipelineModule> moduleCatalog = [new TextModule(), new ImageModule(), new MultimodalModule()];
var features = FeatureRegistry.Build(config, moduleCatalog);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(features);
builder.Services.AddDbContext<AppDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());
builder.Services.Configure<StorageOptions>(config.GetSection("Storage"));
builder.Services.AddSingleton<UploadStorage>();

builder.Services.Configure<JobOptions>(config.GetSection("Jobs"));
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddScoped<JobReporter>();
builder.Services.AddScoped<JobFactory>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddSignalR();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddLlm(config);
builder.Services.AddHttpClient(nameof(SystemInfoService));
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<PromptStore>();
builder.Services.AddScoped<PromptUsage>();

var llmBaseUrl = config["Llm:BaseUrl"]!.TrimEnd('/');
var health = builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgres", tags: [HealthEndpoints.ReadyTag], timeout: TimeSpan.FromSeconds(3))
    // LLM 은 모듈 공통 (Ollama 는 /api/version, OpenAI 호환 서버는 /v1/models)
    .AddUrlGroup(
        new Uri(llmBaseUrl + (config["Llm:Provider"] == "openai" ? "/v1/models" : "/api/version")),
        name: "llm",
        tags: [HealthEndpoints.ReadyTag],
        timeout: TimeSpan.FromSeconds(3));

foreach (var module in features.EnabledModules)
{
    module.ConfigureServices(builder.Services, config, health);
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (config.GetValue("Database:MigrateOnStartup", false))
    {
        await db.Database.MigrateAsync();
    }
    // UI 에서 적용한 프롬프트 버전 (없으면 파일 기본값)
    await app.Services.GetRequiredService<PromptStore>().ReloadAsync();
    var failed = await JobRecovery.FailUnfinishedJobsAsync(db, TimeProvider.System.GetUtcNow());
    if (failed > 0)
    {
        app.Logger.LogWarning("재시작 전에 끝나지 않은 작업 {Count}건을 Failed 로 정리했습니다", failed);
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthEndpoints();
app.MapHub<JobHub>("/hubs/jobs");

var api = app.MapGroup("/api");
api.MapGet("/features", (FeatureRegistry registry) => registry.Features);
// 기기 사양 (모델 비교 페이지 상단, 실험 기록에도 저장)
api.MapGet("/system/info", (SystemInfoService info, CancellationToken ct) => info.CollectAsync(ct));
api.MapJobEndpoints();
api.MapPromptEndpoints();
foreach (var module in features.EnabledModules)
{
    module.MapEndpoints(api.MapGroup($"/{module.Key}"));
}

app.Logger.LogInformation("활성 모듈: {Modules}", string.Join(", ", features.EnabledModules.Select(m => m.Key)));
app.Run();
