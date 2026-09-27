using Digitizer.Engine;

namespace Digitizer.App;

/// <summary>
/// 프로그램 데이터 위치 (DB · 설정 · 처리 대기 원본): %LocalAppData%\Digitizer, 환경 변수 DIGITIZER_DATA 로 바꿀 수 있음 (테스트 · 여러 사본)
/// </summary>
public sealed class AppPaths(string dataRoot)
{
    public string DataRoot { get; } = dataRoot;
    public string Database => Path.Combine(DataRoot, "digitizer.db");
    public string Settings => Path.Combine(DataRoot, "settings.json");
    /// <summary>접수한 원본이 처리 전까지 있는 곳 (감시 폴더 · 업로드 공통). 처리가 끝나면 사용자 폴더로 옮김</summary>
    public string Queue => Path.Combine(DataRoot, "queue");

    public static AppPaths Default() => new(Environment.GetEnvironmentVariable("DIGITIZER_DATA") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)  // 상대 경로는 실행 위치 기준 (dotnet run 은 프로젝트 폴더)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Digitizer"));
}

/// <summary>
/// 사용자가 보는 폴더 (문서\문서 전산화). 팩 폴더 이름은 표시 이름 (넣기\이력서).
/// 넣기\<팩> ➔ 처리됨\<팩>\<날짜> (검증 통과) · 확인 필요\<팩> (검증 문제 · 종류 의심 · 조건부 팩) · 실패 (+ 사유 .txt) · 내보내기
/// </summary>
public sealed class UserFolders(string root)
{
    public const string InboxName = "넣기";
    public const string DoneName = "처리됨";
    public const string ReviewName = "확인 필요";
    public const string FailedName = "실패";
    public const string ExportsName = "내보내기";

    public string Root { get; } = root;
    public string InboxRoot => Path.Combine(Root, InboxName);
    public string Failed => Path.Combine(Root, FailedName);
    public string Exports => Path.Combine(Root, ExportsName);

    public string Inbox(DocumentType pack) => Path.Combine(InboxRoot, FolderName(pack));
    public string Done(DocumentType pack, DateOnly date) => Path.Combine(Root, DoneName, FolderName(pack), date.ToString("yyyy-MM-dd"));
    public string DoneRoot(DocumentType pack) => Path.Combine(Root, DoneName, FolderName(pack));
    public string Review(DocumentType pack) => Path.Combine(Root, ReviewName, FolderName(pack));

    /// <summary>표시 이름에서 파일 이름에 못 쓰는 글자만 뺌</summary>
    public static string FolderName(DocumentType pack)
    {
        var name = string.Concat(pack.DisplayName.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        return name.Length > 0 ? name : pack.Id;
    }

    /// <summary>팩마다 넣기 폴더 + 실패 · 내보내기 폴더를 만듦 (시작할 때마다, 팩이 늘었을 수 있으므로)</summary>
    public void Ensure(IEnumerable<DocumentType> packs)
    {
        foreach (var pack in packs) Directory.CreateDirectory(Inbox(pack));
        Directory.CreateDirectory(Failed);
        Directory.CreateDirectory(Exports);
    }
}
