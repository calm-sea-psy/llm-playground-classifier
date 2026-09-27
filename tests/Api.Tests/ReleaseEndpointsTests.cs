using Api.Modules.Text.Release;

namespace Api.Tests;

/// <summary>문서 텍스트 추출 ➔ 배포 화면: 설치판 버전 읽기 · 내려받기 허용 이름</summary>
public class ReleaseEndpointsTests
{
    [Fact]
    public void 설치판_버전은_Digitizer_App_csproj_에서_읽는다()
    {
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "llm-playground-classifier.slnx"))) repo = Path.GetDirectoryName(repo)!;
        var version = ReleaseEndpoints.AppVersion(repo);
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Contains($"<Version>{version}</Version>", File.ReadAllText(Path.Combine(repo, "src", "Digitizer.App", "Digitizer.App.csproj")));
    }

    [Theory]
    [InlineData("DocumentDigitizer-setup-0.1.0.exe", true)]
    [InlineData("DocumentDigitizer-setup-0.1.0.exe.sha256", true)]
    [InlineData("release-notes.md", true)]
    [InlineData(@"..\..\src\Api\appsettings.json", false)]
    [InlineData("../build.ps1", false)]
    [InlineData(@"app\Digitizer.exe", false)]
    [InlineData("DocumentDigitizer-setup-0.1.0.exe.bak", false)]
    public void 내려받기는_설치_파일_체크섬_릴리스_노트만(string name, bool allowed)
    {
        Assert.Equal(allowed, ReleaseEndpoints.IsAllowedFile(name));
    }
}
