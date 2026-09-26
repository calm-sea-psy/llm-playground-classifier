using System.Text;
using Digitizer.Engine;

namespace Digitizer.App.Processing;

/// <summary>
/// 처리 뒤 원본 옮기기: 검증 통과 ➔ 처리됨\<팩>\<날짜>, 확인 필요 ➔ 확인 필요\<팩>, 실패 · 중복 ➔ 실패 (+ "<파일 이름>.실패 사유.txt").
/// 같은 이름이 있으면 "이름 (2).pdf" 처럼 번호를 붙임 (덮어쓰지 않음)
/// </summary>
public sealed class FileRouter(SettingsFile settings, TimeProvider clock)
{
    public const string ReasonSuffix = ".실패 사유.txt";

    public UserFolders Folders => new(settings.Current.ResolvedDocumentsRoot);

    public string MoveToDone(string path, string originalName, DocumentType pack) =>
        Move(path, Folders.Done(pack, DateOnly.FromDateTime(clock.GetLocalNow().DateTime)), originalName);

    public string MoveToReview(string path, string originalName, DocumentType pack) => Move(path, Folders.Review(pack), originalName);

    public string MoveToFailed(string path, string originalName, string reason)
    {
        var moved = Move(path, Folders.Failed, originalName);
        File.WriteAllText(moved + ReasonSuffix, $"{clock.GetLocalNow():yyyy-MM-dd HH:mm}\n{reason}\n", Encoding.UTF8);
        return moved;
    }

    private static string Move(string path, string folder, string originalName)
    {
        Directory.CreateDirectory(folder);
        var target = FreeName(folder, originalName);
        File.Move(path, target);
        return target;
    }

    public static string FreeName(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; File.Exists(target); i++) target = Path.Combine(folder, $"{stem} ({i}){ext}");
        return target;
    }
}
