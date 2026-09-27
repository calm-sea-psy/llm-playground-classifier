using System.Diagnostics;
using Digitizer.App.Data;
using Digitizer.App.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Digitizer.App.Tests;

/// <summary>4차-exe 4단계 실행 형태: 일시 정지 · 로그 파일(개인정보 없음) · 자동 시작 · 단일 인스턴스 · 자식 프로세스 정리</summary>
public sealed class HostingTests
{
    [Fact]
    public async Task 일시_정지하면_접수는_되고_처리는_멈춘다()
    {
        using var h = new CoreHarness();
        h.Queue.Paused = true;
        var doc = await h.Intake.AcceptAsync(h.Drop("receipt", "a.pdf", Samples.ReceiptText), h.Pack("receipt"), "watch");

        Assert.False(await h.Queue.ProcessNextAsync());
        Assert.Equal(DocumentStatus.Queued, h.Load(doc.Id).Status);

        h.Queue.Paused = false;
        Assert.True(await h.Queue.ProcessNextAsync());
        Assert.Equal(DocumentStatus.Processed, h.Load(doc.Id).Status);
    }

    [Fact]
    public void 로그에서_파일_이름_전화번호_이메일_주민번호를_가린다()
    {
        var line = FileLoggerProvider.Scrub(
            @"Could not find file 'C:\Users\kim\Documents\문서 전산화\넣기\이력서\홍길동_이력서.pdf'. 연락처 010-1234-5678, hong@example.com, 900101-1234567");
        Assert.DoesNotContain("홍길동", line);
        Assert.DoesNotContain("1234-5678", line);
        Assert.DoesNotContain("hong@", line);
        Assert.DoesNotContain("1234567", line);
        Assert.Contains(@"넣기\이력서\<파일>", line);  // 폴더는 남김 (어느 단계 문제인지)
        Assert.Equal("문서 #12 처리 끝: Processed", FileLoggerProvider.Scrub("문서 #12 처리 끝: Processed"));
    }

    [Fact]
    public async Task 처리_로그_파일에_이름과_전화번호가_남지_않는다()
    {
        var logs = Path.Combine(Path.GetTempPath(), $"digitizer-logs-{Guid.NewGuid():N}");
        var provider = new FileLoggerProvider(logs);
        try
        {
            const string text = "홍길동\n010-1234-5678\nhong@example.com";
            using (var h = new CoreHarness(_ => new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = "홍길동", ["phone"] = "010-1234-5678", ["email"] = "hong@example.com", ["address"] = null, ["birth_date"] = null,
                ["education"] = new System.Text.Json.Nodes.JsonArray(), ["career"] = new System.Text.Json.Nodes.JsonArray(),
                ["certificates"] = new System.Text.Json.Nodes.JsonArray(), ["languages"] = new System.Text.Json.Nodes.JsonArray(),
                ["skills"] = new System.Text.Json.Nodes.JsonArray(),
            }, log: provider))
            {
                await h.AcceptAndProcessAsync("resume", "홍길동_010-1234-5678.pdf", text);
                await h.AcceptAndProcessAsync("resume", "홍길동_실패.pdf", "!fail");
                h.Drop("resume", "홍길동_중복.pdf", text);
                await h.Watcher.ScanAsync();
            }
            provider.Dispose();

            var log = string.Concat(Directory.GetFiles(logs).Select(File.ReadAllText));
            Assert.Contains("문서 #1 처리 끝", log);
            Assert.DoesNotContain("홍길동", log);
            Assert.DoesNotContain("1234-5678", log);
            Assert.DoesNotContain("hong@", log);
        }
        finally
        {
            provider.Dispose();
            Directory.Delete(logs, recursive: true);
        }
    }

    [Fact]
    public void 오래된_로그_파일은_시작할_때_지운다()
    {
        var logs = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"digitizer-logs-{Guid.NewGuid():N}")).FullName;
        try
        {
            var old = Path.Combine(logs, "digitizer-20260101.log");
            var recent = Path.Combine(logs, "digitizer-20260920.log");
            File.WriteAllText(old, "x");
            File.WriteAllText(recent, "x");
            File.SetLastWriteTime(old, new DateTime(2026, 1, 1));
            File.SetLastWriteTime(recent, new DateTime(2026, 9, 20));

            using (new FileLoggerProvider(logs, () => new DateTime(2026, 9, 27))) { }

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }
        finally
        {
            Directory.Delete(logs, recursive: true);
        }
    }

    [Fact]
    public void 자동_시작은_현재_exe_를_background_로_등록하고_끌_수_있다()
    {
        var key = $@"Software\DigitizerTest-{Guid.NewGuid():N}";
        var auto = new AutoStart(key);
        const string exe = @"C:\Users\u\AppData\Local\Programs\문서 전산화\Digitizer.exe";
        try
        {
            Assert.False(auto.IsEnabled(exe));
            auto.Set(true, exe);
            Assert.True(auto.IsEnabled(exe));
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal($"\"{exe}\" --background", k!.GetValue(AutoStart.Name));
            Assert.False(auto.IsEnabled(@"D:\old\Digitizer.exe"));  // 다른 위치의 옛 설치를 가리키면 꺼짐으로
            auto.Set(false, exe);
            Assert.False(auto.IsEnabled(exe));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void 같은_데이터_폴더로는_하나만_실행된다()
    {
        var root = Path.Combine(Path.GetTempPath(), $"digitizer-single-{Guid.NewGuid():N}");
        using var first = new SingleInstance(root);
        Assert.True(first.IsFirst);
        // 뮤텍스는 스레드가 가지므로 다른 스레드에서 두 번째 실행을 흉내 냄
        var (second, other) = Task.Run(() =>
        {
            using var s = new SingleInstance(root.ToUpperInvariant() + Path.DirectorySeparatorChar);
            using var o = new SingleInstance(root + "-other");
            return (s.IsFirst, o.IsFirst);
        }).Result;
        Assert.False(second);  // 대소문자 · 끝 구분자가 달라도 같은 폴더
        Assert.True(other);
    }

    [Fact]
    public void 작업_개체를_닫으면_자식_프로세스가_끝난다()
    {
        var job = new JobObject();
        using var child = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            job.Add(child);
            Assert.False(child.HasExited);
            job.Dispose();  // 프로그램이 끝나는 것과 같음 (강제 종료여도 운영체제가 핸들을 닫음)
            Assert.True(child.WaitForExit(5000));
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }
}
