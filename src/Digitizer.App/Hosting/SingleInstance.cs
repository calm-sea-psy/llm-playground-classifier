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
    private readonly Mutex _mutex;

    public bool IsFirst { get; }

    public SingleInstance(string dataRoot)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToLowerInvariant())))[..16];
        _mutex = new Mutex(initiallyOwned: true, $@"Local\DocumentDigitizer-{id}", out var created);
        IsFirst = created;
    }

    public void Dispose()
    {
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
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
