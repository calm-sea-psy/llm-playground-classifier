using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Digitizer.App.Hosting;

/// <summary>
/// 한 번에 하나만 실행 (같은 데이터 폴더 기준: 개발 · 시험용 사본은 DIGITIZER_DATA 가 달라 같이 띄울 수 있음).
/// 이미 실행 중이면 새로 띄우지 않고 화면만 열고 끝냄
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>설치 · 제거 프로그램(Inno Setup AppMutex)이 "실행 중이니 먼저 끄세요" 를 알리는 데 쓰는 고정 이름 (데이터 폴더와 무관)</summary>
    public const string RunningMutex = "DocumentDigitizerRunning";

    private readonly Mutex _mutex;
    private readonly Mutex? _running;

    public bool IsFirst { get; }

    public SingleInstance(string dataRoot)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToLowerInvariant())))[..16];
        _mutex = new Mutex(initiallyOwned: true, $@"Local\DocumentDigitizer-{id}", out var created);
        IsFirst = created;
        if (IsFirst) _running = new Mutex(initiallyOwned: false, RunningMutex);
    }

    public void Dispose()
    {
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _running?.Dispose();
    }

    /// <summary>기본 브라우저로 화면 열기</summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
