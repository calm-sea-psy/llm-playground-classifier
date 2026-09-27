using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Api.Modules.Text.Pipeline;
using Api.Shared.Data;
using Digitizer.Engine;

namespace Api.Modules.Text.Release;

/// <summary>
/// 문서 텍스트 추출 ➔ 배포 화면 (4차-exe): 이번 설치판에 들어갈 문서 종류 · 배포 전 점검 · 설치 파일 만들기(이 PC) · 릴리스 노트 미리 보기 ·
/// GitHub 배포 명령 안내. 설치판에 들어가는 것이 전부 이 모듈(Engine · 팩 · OCR)이라 여기에 둠.
/// GitHub 에 올리기(태그 푸시)는 공개 저장소에 올리는 일이라 명령만 보여 주고 사람이 실행
/// </summary>
public static partial class ReleaseEndpoints
{
    public sealed record PackRow(string Id, string Version, string DisplayName, PackRelease? Release, bool Included, List<string> Problems,
        List<PackEndpoints.PromptOverride> PromptOverrides);

    /// <param name="Level">ok | warn | bad</param>
    public sealed record Check(string Name, string Level, string Detail, string? Fix = null);

    public sealed record ParityRow(string Pack, string Input, int Count, int SameText, int SameFields, int SameErrors, int? SameFinal, double MedianSeconds);

    public sealed record ParityRun(string Name, DateTimeOffset At, List<ParityRow> Rows);

    public sealed record BuiltFile(string Name, long Bytes, DateTimeOffset At, string? Sha256);

    public sealed record Overview(string Version, string Tag, List<PackRow> Packs, List<Check> Checks, List<ParityRun> Parity,
        BuiltFile? Setup, string? ReleaseNotes, List<string> TagCommands, ReleaseBuilder.Snapshot Build);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("", async (PackStore packs, AppDbContext db, ReleaseBuilder builder, CancellationToken ct) =>
        {
            var repo = RepoRoot(packs);
            var version = AppVersion(repo);
            var tag = $"digitizer-v{version}";
            var rows = new List<PackRow>();
            foreach (var pack in packs.All().OrderBy(p => p.Release is null).ThenBy(p => p.Id))
            {
                var problems = PackCheck.Check(pack);
                var overrides = await PackEndpoints.OverridesAsync(db, pack.Id, ct);
                rows.Add(new PackRow(pack.Id, pack.Version, pack.DisplayName, pack.Release, pack.Release is not null && problems.Count == 0,
                    problems, overrides));
            }
            var outDir = Path.Combine(repo, "installer", "out");
            var setup = Directory.Exists(outDir) ? new DirectoryInfo(outDir).GetFiles("DocumentDigitizer-setup-*.exe").OrderByDescending(f => f.LastWriteTime).FirstOrDefault() : null;
            var notes = Path.Combine(outDir, "release-notes.md");
            return new Overview(
                version, tag, rows, await ChecksAsync(repo, rows, tag, ct), ParityRuns(repo),
                setup is null ? null : new BuiltFile(setup.Name, setup.Length, setup.LastWriteTime,
                    File.Exists(setup.FullName + ".sha256") ? File.ReadAllText(setup.FullName + ".sha256").Split(' ')[0].Trim() : null),
                File.Exists(notes) ? await File.ReadAllTextAsync(notes, ct) : null,
                ["git push", $"git tag {tag}", $"git push origin {tag}"],
                builder.Read(int.MaxValue));
        });

        // 설치 파일 만들기 (build.ps1 ➔ release-notes.ps1). 이 PC 에서 연 화면만 (빌드 도구를 실행하므로)
        group.MapPost("/build", (HttpContext http, PackStore packs, ReleaseBuilder builder) =>
        {
            if (!IsLocal(http)) return Results.Problem("이 PC 에서 연 화면에서만 설치 파일을 만들 수 있습니다", statusCode: 403);
            var repo = RepoRoot(packs);
            return builder.Start(repo, AppVersion(repo))
                ? Results.Accepted(value: builder.Read(0))
                : Results.Problem("이미 설치 파일을 만드는 중입니다", statusCode: 409);
        });

        // 진행 로그 (from 번째 줄부터)
        group.MapGet("/build", (int? from, ReleaseBuilder builder) => builder.Read(from ?? 0));

        // 만든 설치 파일 · 체크섬 · 릴리스 노트 내려받기 (installer\out 안의 정해진 이름만)
        group.MapGet("/files/{name}", (string name, HttpContext http, PackStore packs) =>
        {
            if (!IsLocal(http)) return Results.Problem("이 PC 에서 연 화면에서만 내려받을 수 있습니다", statusCode: 403);
            if (!IsAllowedFile(name)) return Results.NotFound();
            var path = Path.Combine(RepoRoot(packs), "installer", "out", name);
            return File.Exists(path)
                ? Results.File(path, name.EndsWith(".exe") ? "application/vnd.microsoft.portable-executable" : "text/plain; charset=utf-8", name)
                : Results.NotFound();
        });
    }

    [GeneratedRegex(@"^(DocumentDigitizer-setup-[0-9.]+\.exe(\.sha256)?|release-notes\.md)$")]
    private static partial Regex FileNameRegex();

    /// <summary>내려받을 수 있는 이름: 설치 파일 · 그 체크섬 · 릴리스 노트만 (경로 문자 없음)</summary>
    public static bool IsAllowedFile(string name) => FileNameRegex().IsMatch(name);

    private static bool IsLocal(HttpContext http) => http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    /// <summary>저장소 루트 = 팩 폴더(packs)의 부모</summary>
    public static string RepoRoot(PackStore packs) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(packs.Root))!;

    /// <summary>설치판 버전 = src/Digitizer.App/Digitizer.App.csproj 의 &lt;Version&gt; (배포 태그와 같아야 함)</summary>
    public static string AppVersion(string repo)
    {
        var csproj = Path.Combine(repo, "src", "Digitizer.App", "Digitizer.App.csproj");
        return File.Exists(csproj) ? XDocument.Load(csproj).Descendants("Version").FirstOrDefault()?.Value ?? "?" : "?";
    }

    private static async Task<List<Check>> ChecksAsync(string repo, List<PackRow> packs, string tag, CancellationToken ct)
    {
        var checks = new List<Check>();
        var included = packs.Where(p => p.Release is not null).ToList();

        checks.Add(included.Count == 0
            ? new("합격한 문서 종류", "bad", "합격 표시(release)가 붙은 팩이 없습니다", "문서 종류 화면에서 측정해 기준을 넘은 팩에 합격 표시")
            : included.Any(p => p.Problems.Count > 0)
                ? new("팩 검사", "bad", string.Join(" / ", included.Where(p => p.Problems.Count > 0).Select(p => $"{p.DisplayName}: {string.Join("; ", p.Problems)}")),
                    "문서 종류 화면에서 고친 뒤 다시 측정")
                : new("팩 검사", "ok", $"{string.Join(" · ", included.Select(p => $"{p.DisplayName} v{p.Version}"))} 통과"));

        var overridden = included.Where(p => p.PromptOverrides.Count > 0).ToList();
        checks.Add(overridden.Count == 0
            ? new("측정한 문장 = 설치판 문장", "ok", "프롬프트 관리에서 고친 버전을 적용 중인 팩이 없습니다 (설치판은 팩 파일 문장을 씀)")
            : new("측정한 문장 = 설치판 문장", "bad",
                string.Join(" / ", overridden.Select(p => $"{p.DisplayName}: {string.Join(", ", p.PromptOverrides.Select(o => $"{o.Name} v{o.Version}"))}")) +
                " 적용 중 ➔ 평가 도구 결과가 설치판과 다를 수 있음",
                "프롬프트 관리에서 파일 기본값으로 되돌리거나, 고친 문장을 팩 파일에 반영하고 다시 측정"));

        checks.Add(OcrConstraintsCheck(repo));
        checks.Add(InnoCheck());
        checks.AddRange(await GitChecksAsync(repo, tag, ct));
        return checks;
    }

    /// <summary>installer\ocr-constraints.txt (설치판 OCR 버전) vs 측정에 쓴 src\OcrService\.venv 의 실제 설치 버전</summary>
    private static Check OcrConstraintsCheck(string repo)
    {
        var file = Path.Combine(repo, "installer", "ocr-constraints.txt");
        var site = Path.Combine(repo, "src", "OcrService", ".venv", "Lib", "site-packages");
        if (!File.Exists(file)) return new("OCR 버전 고정", "bad", "installer\\ocr-constraints.txt 가 없습니다", "측정 환경에서 installer\\build.ps1 -UpdateConstraints");
        static string Norm(string name) => Regex.Replace(name.ToLowerInvariant(), "[-_.]+", "-");
        var pinned = File.ReadAllLines(file).Where(l => l.Contains("==") && !l.StartsWith('#'))
            .Select(l => l.Split("==")).ToDictionary(p => Norm(p[0].Trim()), p => p[1].Trim());
        if (!Directory.Exists(site)) return new("OCR 버전 고정", "warn", $"측정 환경(src\\OcrService\\.venv)이 없어 대조하지 못했습니다 · 고정 {pinned.Count}개");
        var installed = Directory.GetDirectories(site, "*.dist-info").Select(Path.GetFileName)
            .Select(n => Regex.Match(n!, @"^(.+)-([^-]+)\.dist-info$")).Where(m => m.Success)
            .ToDictionary(m => Norm(m.Groups[1].Value), m => m.Groups[2].Value);
        var diff = pinned.Where(p => installed.GetValueOrDefault(p.Key) != p.Value).Select(p => $"{p.Key} {p.Value} ➔ {installed.GetValueOrDefault(p.Key) ?? "없음"}").ToList();
        diff.AddRange(installed.Keys.Except(pinned.Keys).Select(k => $"{k} {installed[k]} (고정 목록에 없음)"));
        var key = string.Join(" · ", new[] { "paddlepaddle-gpu", "paddleocr", "paddlex" }.Where(pinned.ContainsKey).Select(k => $"{k} {pinned[k]}"));
        return diff.Count == 0
            ? new("OCR 버전 고정", "ok", $"측정 환경과 같음 ({pinned.Count}개, {key})")
            : new("OCR 버전 고정", "bad", $"측정 환경과 다름: {string.Join(", ", diff.Take(5))}{(diff.Count > 5 ? $" 외 {diff.Count - 5}개" : "")}",
                "측정 환경을 바꿨다면 재측정 후 installer\\build.ps1 -UpdateConstraints");
    }

    private static Check InnoCheck()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inno Setup 6", "ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Inno Setup 6", "ISCC.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) is { } iscc
            ? new("설치 파일 도구 (Inno Setup)", "ok", iscc)
            : new("설치 파일 도구 (Inno Setup)", "bad", "Inno Setup 6 이 없어 설치 파일을 만들 수 없습니다",
                "installer\\install-innosetup.ps1 실행 (버전 고정 · 서명 확인) 또는 jrsoftware.org 에서 설치");
    }

    /// <summary>커밋하지 않은 변경 (배포는 푸시한 커밋으로 만들어짐) · 같은 버전 태그가 이미 있는지</summary>
    private static async Task<List<Check>> GitChecksAsync(string repo, string tag, CancellationToken ct)
    {
        var status = await GitAsync(repo, "status --porcelain", ct);
        var tags = await GitAsync(repo, $"tag --list {tag}", ct);
        var ahead = await GitAsync(repo, "rev-list --count @{upstream}..HEAD", ct);
        var checks = new List<Check>();
        if (status is null) return [new("Git", "warn", "git 을 실행하지 못했습니다")];
        var changed = status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        checks.Add(changed == 0
            ? new("커밋하지 않은 변경", "ok", "없음")
            : new("커밋하지 않은 변경", "warn", $"{changed}개 파일", "배포는 GitHub 에 올린 커밋으로 만들어짐 ➔ 커밋 · 푸시 뒤 태그"));
        if (int.TryParse(ahead?.Trim(), out var n) && n > 0)
            checks.Add(new("푸시하지 않은 커밋", "warn", $"{n}개", "태그보다 먼저 git push"));
        checks.Add(string.IsNullOrWhiteSpace(tags)
            ? new("배포 태그", "ok", $"{tag} 아직 없음")
            : new("배포 태그", "bad", $"{tag} 가 이미 있습니다", "src\\Digitizer.App\\Digitizer.App.csproj 의 <Version> 을 올리기"));
        return checks;
    }

    private static async Task<string?> GitAsync(string repo, string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>설치판 결과 대조 (eval/digitizer/AppParity ➔ eval/results/digitizer/app-*/parity.jsonl), 최근 5개</summary>
    private static List<ParityRun> ParityRuns(string repo)
    {
        var root = Path.Combine(repo, "eval", "results", "digitizer");
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root, "app-*")
            .Select(d => (Dir: d, File: Path.Combine(d, "parity.jsonl")))
            .Where(x => File.Exists(x.File))
            .OrderByDescending(x => File.GetLastWriteTime(x.File))
            .Take(5)
            .Select(x =>
            {
                var lines = File.ReadAllLines(x.File).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).ToList();
                var rows = lines.GroupBy(l => (Pack: l["pack"]!.GetValue<string>(), Input: l["input"]!.GetValue<string>()))
                    .Select(g =>
                    {
                        int Count(string key) => g.Count(l => l[key]?.GetValue<bool>() == true);
                        var ms = g.Select(l => l["ms"]?.GetValue<long>() ?? 0).Order().ToList();
                        // 추출 기준이 없는 입력(원문 · 시간만 비교)은 필드 · 오류 대조를 세지 않음
                        var timeOnly = g.All(l => l["expected_errors"]?.GetValue<int>() == -1);
                        return new ParityRow(g.Key.Pack, g.Key.Input, g.Count(), Count("same_text"),
                            timeOnly ? -1 : Count("same_fields"), timeOnly ? -1 : Count("same_errors"),
                            g.Key.Pack == "receipt" ? Count("same_final") : null, ms.Count == 0 ? 0 : ms[ms.Count / 2] / 1000.0);
                    }).ToList();
                return new ParityRun(Path.GetFileName(x.Dir), File.GetLastWriteTime(x.File), rows);
            }).ToList();
    }
}
