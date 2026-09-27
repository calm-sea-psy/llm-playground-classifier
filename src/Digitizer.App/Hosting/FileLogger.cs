using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Digitizer.App.Hosting;

/// <summary>
/// 날짜별 로그 파일 (데이터 폴더 logs\digitizer-yyyyMMdd.log, UTF-8, 30일 보관). 콘솔 창이 없는 exe 에서 문제를 찾는 곳.
/// 개인정보를 남기지 않음: 코드는 문서 번호만 기록하고, 예외 메시지에 섞일 수 있는 파일 이름 · 전화번호 · 이메일 · 주민등록번호는 여기서 한 번 더 가림
/// </summary>
public sealed partial class FileLoggerProvider : ILoggerProvider
{
    public const int KeepDays = 30;

    private readonly string _folder;
    private readonly Func<DateTime> _now;
    private readonly BlockingCollection<(DateTime Time, string Line)> _queue = new(10_000);
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string folder, Func<DateTime>? now = null)
    {
        _folder = Directory.CreateDirectory(folder).FullName;
        _now = now ?? (() => DateTime.Now);
        DeleteOld();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "file-logger" };
        _writer.Start();
    }

    public string PathFor(DateTime day) => Path.Combine(_folder, $"digitizer-{day:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>개인정보로 보이는 값 가리기 (파일 이름은 폴더만 남김)</summary>
    public static string Scrub(string text)
    {
        text = FileNameRegex().Replace(text, m => m.Groups[1].Value + "<파일>");
        text = ResidentIdRegex().Replace(text, "<주민번호>");
        text = PhoneRegex().Replace(text, "<전화>");
        text = EmailRegex().Replace(text, "<이메일>");
        return text;
    }

    internal void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd((_now(), line));
    }

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        var day = DateTime.MinValue;
        try
        {
            foreach (var (time, line) in _queue.GetConsumingEnumerable())
            {
                if (writer is null || time.Date != day)
                {
                    writer?.Dispose();
                    day = time.Date;
                    writer = new StreamWriter(new FileStream(PathFor(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                }
                writer.WriteLine(line);
                if (_queue.Count == 0) writer.Flush();
            }
        }
        catch (IOException) { }  // 디스크 문제로 로그를 못 써도 프로그램은 계속
        finally
        {
            writer?.Dispose();
        }
    }

    private void DeleteOld()
    {
        foreach (var file in Directory.GetFiles(_folder, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < _now().AddDays(-KeepDays)) File.Delete(file);
            }
            catch (IOException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _short = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT",
            };
            var line = $"{provider._now():yyyy-MM-dd HH:mm:ss.fff} {level} {_short}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Enqueue(Scrub(line));
        }
    }

    // 경로의 마지막 파일 이름 (문서 · 엑셀 · 사유 파일). 폴더 부분(그룹 1)은 남김
    [GeneratedRegex(@"([A-Za-z]:[\\/](?:[^\\/:*?""<>|\r\n]+[\\/])*)[^\\/:*?""<>|\r\n]+\.(?:pdf|docx|png|jpe?g|tiff?|bmp|xlsx|txt)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    [GeneratedRegex(@"\d{6}\s*-\s*[1-4]\d{6}")]
    private static partial Regex ResidentIdRegex();

    [GeneratedRegex(@"(?<!\d)0\d{1,2}[-. ]?\d{3,4}[-. ]?\d{4}(?!\d)")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailRegex();
}
