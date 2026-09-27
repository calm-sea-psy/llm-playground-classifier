using Microsoft.Win32;

namespace Digitizer.App.Hosting;

/// <summary>
/// 로그인 시 자동 시작 = HKCU\...\Run 값 (관리자 권한 불필요, 작업 관리자 "시작 앱" 에 보임).
/// 계획은 시작 프로그램 폴더 바로가기였지만 .lnk 는 COM(WScript.Shell)이 필요해 레지스트리로 바꿈. 설치 도우미(5단계)가 켜고, 설정 화면에서 끄고 켬.
/// 자동 시작 때는 --background 로 실행 ➔ 브라우저를 열지 않고 트레이에만
/// </summary>
public sealed class AutoStart(string keyPath = AutoStart.RunKey, string valueName = AutoStart.Name)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string Name = "DocumentDigitizer";
    public const string BackgroundArg = "--background";

    public static string Command(string exe) => $"\"{exe}\" {BackgroundArg}";

    /// <summary>등록된 실행 파일이 지금 실행 중인 파일과 같을 때만 켜짐으로 봄 (다른 위치의 옛 설치를 가리키면 꺼짐)</summary>
    public bool IsEnabled(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is string v && string.Equals(v, Command(exe), StringComparison.OrdinalIgnoreCase);
    }

    public void Set(bool enabled, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled) key.SetValue(valueName, Command(exe));
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>지금 실행 중인 exe (dotnet run 이면 bin 의 Digitizer.exe)</summary>
    public static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Digitizer.exe");
}
