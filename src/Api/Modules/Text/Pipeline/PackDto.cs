using Digitizer.Engine;
using Digitizer.Engine.Rules;

namespace Api.Modules.Text.Pipeline;

/// <summary>화면용 문서 종류 팩 요약. AutoClassified = LLM 분류로 찾을 수 있는 종류 (아니면 문서 처리에서 직접 골라야 함)</summary>
public sealed record PackDto(
    string Id,
    string Version,
    string DisplayName,
    string? Description,
    bool AutoClassified,
    IReadOnlyList<FieldDef> Fields,
    IReadOnlyList<string> Rules,
    bool GenericChecks,
    IReadOnlyList<string> Forbidden)
{
    public static PackDto From(DocumentType p) => new(
        p.Id, p.Version, p.DisplayName, p.Description,
        DocumentTypes.Extractable.Contains(p.Id),
        p.Fields, p.Rules, p.GenericChecks,
        [.. p.Forbidden.Select(f => f.Label)]);
}
