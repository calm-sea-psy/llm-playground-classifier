using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Digitizer.App.Data;
using Digitizer.App.Processing;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Hosting;

/// <summary>
/// 알림 영역 아이콘 (WinForms NotifyIcon, 자기 STA 스레드): 열기 · 확인 필요 N건 · 일시 정지 · 문서 폴더 · 종료.
/// 확인 필요 · 실패 건이 늘면 풍선 알림 (누르면 해당 목록). 종료 = 웹 서버 · 대기열 · OCR 자식 프로세스까지 정리
/// </summary>
public sealed class TrayIcon : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IDbContextFactory<DigitizerDb> _dbFactory;
    private readonly ProcessingQueue _queue;
    private readonly FileRouter _router;
    private readonly string _url;
    private readonly ILogger<TrayIcon> _logger;
    private Thread? _thread;
    private ApplicationContext? _context;
    private SynchronizationContext? _ui;

    public TrayIcon(IHostApplicationLifetime lifetime, IDbContextFactory<DigitizerDb> dbFactory, ProcessingQueue queue, FileRouter router,
        string url, ILogger<TrayIcon> logger)
    {
        _lifetime = lifetime;
        _dbFactory = dbFactory;
        _queue = queue;
        _router = router;
        _url = url;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource();
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return started.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ui?.Post(_ => _context?.ExitThread(), null);
        _thread?.Join(TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    private void Run(TaskCompletionSource started)
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var icon = new NotifyIcon { Icon = MakeIcon(), Text = "문서 전산화", Visible = true };
        var review = new ToolStripMenuItem("확인 필요 0건") { Enabled = false };
        var pause = new ToolStripMenuItem("처리 일시 정지");
        var menu = new ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => Open(""));
        menu.Items.Add(review);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(pause);
        menu.Items.Add("문서 폴더 열기", null, (_, _) => SingleInstance.OpenBrowser(_router.Folders.Root));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => _lifetime.StopApplication());
        menu.Items[0].Font = new Font(menu.Items[0].Font, FontStyle.Bold);
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => Open("");
        review.Click += (_, _) => Open("#/?status=NeedsReview");
        pause.Click += (_, _) =>
        {
            _queue.Paused = !_queue.Paused;
            Refresh();
        };
        string? balloonTarget = null;
        icon.BalloonTipClicked += (_, _) => Open(balloonTarget ?? "");

        int? lastReview = null, lastFailed = null;
        void Refresh()
        {
            pause.Text = _queue.Paused ? "처리 다시 시작" : "처리 일시 정지";
            icon.Text = _queue.Paused ? "문서 전산화 (일시 정지)" : "문서 전산화";
        }

        // 30초마다 확인 필요 · 실패 건수 (DB 는 스레드 풀에서 읽고 결과만 UI 스레드로)
        var timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        async void Poll()
        {
            try
            {
                var (needsReview, failed) = await Task.Run(CountsAsync);
                review.Text = $"확인 필요 {needsReview}건";
                review.Enabled = needsReview > 0;
                if (lastReview is { } r && needsReview > r)
                {
                    balloonTarget = "#/?status=NeedsReview";
                    icon.ShowBalloonTip(5000, "확인 필요", $"검수할 문서가 {needsReview - r}건 늘었습니다 (모두 {needsReview}건)", ToolTipIcon.Info);
                }
                else if (lastFailed is { } f && failed > f)
                {
                    balloonTarget = "#/?status=Failed,Duplicate";
                    icon.ShowBalloonTip(5000, "처리 실패", $"처리하지 못한 문서가 {failed - f}건 있습니다", ToolTipIcon.Warning);
                }
                lastReview = needsReview;
                lastFailed = failed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("알림 영역 건수 확인 실패: {Message}", ex.Message);
            }
        }
        timer.Tick += (_, _) => Poll();
        timer.Start();

        _context = new ApplicationContext();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);
        Refresh();
        Poll();
        started.TrySetResult();
        Application.Run(_context);
        timer.Dispose();
        icon.Visible = false;  // 끝나도 아이콘이 남아 보이는 것 방지
    }

    private async Task<(int NeedsReview, int Failed)> CountsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var needsReview = await db.Documents.CountAsync(d => d.Status == DocumentStatus.NeedsReview && d.PurgedAt == null);
        var failed = await db.Documents.CountAsync(d => d.Status == DocumentStatus.Failed && d.PurgedAt == null);
        return (needsReview, failed);
    }

    private void Open(string hash) => SingleInstance.OpenBrowser(_url + "/" + hash);

    /// <summary>파란 바탕에 흰 문서 모양 (아이콘 파일 없이 그림)</summary>
    public static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(0x2f, 0x5b, 0xea));
            using var path = new GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            g.FillPath(back, path);
            g.FillRectangle(Brushes.White, 9, 6, 14, 20);
            using var line = new Pen(Color.FromArgb(0x2f, 0x5b, 0xea), 2);
            foreach (var y in new[] { 11, 15, 19 }) g.DrawLine(line, 12, y, 20, y);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
