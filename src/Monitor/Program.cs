using Monitor;

// 감시 서버 (todo 6번). 실행: dotnet run --launch-profile http ➔ http://127.0.0.1:5100
// Windows 서비스로 등록하면(sc create) OS 가 Monitor 자신을 감시 ("모니터는 누가 감시하나" 문제)
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();
builder.Configuration.AddJsonFile("monitor.json", optional: false, reloadOnChange: false);

builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient(nameof(HealthChecker));
builder.Services.AddHttpClient(nameof(Notifier), http => http.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<HealthChecker>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<EventStore>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<MonitorState>();
builder.Services.AddHostedService<MonitorWorker>();
builder.Services.AddSignalR();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<StatusHub>("/hubs/status");

app.MapGet("/status", (MonitorState state) => new { state.LastTickAt, targets = state.Snapshot });
app.MapGet("/events", (EventStore events, int? take) => events.Recent(Math.Clamp(take ?? 50, 1, 200)));
app.MapPost("/targets/{name}/restart", (string name, MonitorState state) =>
    state.RequestRestart(name) ? Results.Accepted() : Results.NotFound());

app.Run();
