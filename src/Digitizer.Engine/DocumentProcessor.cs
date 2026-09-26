using System.Text.Json.Nodes;
using Digitizer.Engine.Rules;

namespace Digitizer.Engine;

/// <summary>문서 하나를 처리한 결과. NeedsReview = 검증 오류가 있어 사람이 확인해야 함</summary>
public sealed record ProcessResult(
    string DocumentType,
    string TypeVersion,
    SourceText Source,
    Extraction Extraction,
    List<ValidationIssue> Issues)
{
    public bool NeedsReview => Extraction.Fields is null || Issues.Any(i => i.Severity == IssueSeverity.Error);
    public JsonObject? Fields => Extraction.Fields;
}

/// <summary>정해진 흐름: 원문 추출 ➔ 필드 추출(LLM) ➔ 검증(코드). 흐름을 모델이 정하지 않음 (docs/agent_evaluation.md 결론)</summary>
public sealed class DocumentProcessor(TextExtractor text, FieldExtractor fields)
{
    public Task<ProcessResult> ProcessAsync(DocumentType type, string path, CancellationToken ct = default) =>
        ProcessAsync(type, () => text.ExtractAsync(path, ct), ct);

    public Task<ProcessResult> ProcessPagesAsync(DocumentType type, IEnumerable<string> imagePaths, CancellationToken ct = default) =>
        ProcessAsync(type, () => text.ExtractPagesAsync(imagePaths, ct), ct);

    public async Task<ProcessResult> ProcessFromTextAsync(DocumentType type, SourceText source, CancellationToken ct = default)
    {
        var extraction = await fields.ExtractAsync(type, source.Text, ct);
        var issues = extraction.Fields is null ? [] : Validator.Validate(type, extraction.Fields, source.Text);
        return new ProcessResult(type.Id, type.Version, source, extraction, issues);
    }

    private async Task<ProcessResult> ProcessAsync(DocumentType type, Func<Task<SourceText>> read, CancellationToken ct) =>
        await ProcessFromTextAsync(type, await read(), ct);
}
