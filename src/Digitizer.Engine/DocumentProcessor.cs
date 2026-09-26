using System.Text.Json.Nodes;
using Digitizer.Engine.Rules;

namespace Digitizer.Engine;

/// <summary>
/// LLM 에 넣을 문서 텍스트. Structured = 문서 파싱 엔진의 Markdown(표는 HTML), 아니면 OCR 줄 읽기 순서 텍스트.
/// SourceLabel = PDF 텍스트 층 · DOCX 처럼 OCR 이 아닌 원문일 때의 안내 (FieldExtractor.InputLabel)
/// </summary>
public sealed record DocumentText(string Text, bool Structured, string? SourceLabel = null)
{
    public string Label => SourceLabel ?? (Structured
        ? "문서 파싱 결과 (Markdown, 표는 HTML 로 칸 구조 유지)"
        : "OCR 텍스트 (위➔아래, 같은 줄은 왼쪽➔오른쪽 순서)");
}

/// <summary>추출 1회 기록. 텍스트 추출과 VLM 폴백 추출을 모두 남겨 모델 비교 지표로 쓴다</summary>
public sealed record ExtractionAttempt(
    string Source,
    string Model,
    int ElapsedMs,
    int? PromptTokens,
    int? CompletionTokens,
    bool SchemaValid,
    string? ParseError,
    JsonObject? Fields,
    List<ValidationIssue> Issues,
    string Raw,
    /// <summary>추출에 쓴 문서 종류 팩 "팩@버전" (예: receipt@1.0.0). 4차 통합 전 기록은 null (기존 추출)</summary>
    string? Engine = null)
{
    public const string Text = "text";
    public const string Vlm = "vlm";

    /// <summary>스키마 위반도 오류 1건으로 셈</summary>
    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error) + (SchemaValid ? 0 : 1);
}

/// <summary>
/// 추출에 쓰는 문장 (지시문 · 원문 전달 · 이미지 폴백 전달). exe 는 팩 파일 그대로(PackPromptSource),
/// 평가 도구는 프롬프트 관리에서 고친 버전을 쓸 수 있음. 어느 쪽이 쓰였는지가 측정과 배포의 차이가 되므로 한 곳으로 모음
/// </summary>
public interface IPromptSource
{
    Task<string> SystemAsync(DocumentType pack, string model, CancellationToken ct);
    Task<string> UserAsync(DocumentType pack, DocumentText document, CancellationToken ct);
    /// <param name="issues">텍스트 추출에서 걸린 문제 안내 (vlm.user.md 의 {{$issues}})</param>
    Task<string> VlmUserAsync(DocumentType pack, DocumentText document, string issues, CancellationToken ct);
}

/// <summary>팩 파일 그대로 (exe · 프롬프트 관리에 없는 종류)</summary>
public sealed class PackPromptSource : IPromptSource
{
    public static readonly PackPromptSource Instance = new();

    public Task<string> SystemAsync(DocumentType pack, string model, CancellationToken ct) =>
        Task.FromResult(pack.SystemPromptFor(model));

    public Task<string> UserAsync(DocumentType pack, DocumentText document, CancellationToken ct) =>
        Task.FromResult(pack.UserMessage(document.Label, document.Text));

    public Task<string> VlmUserAsync(DocumentType pack, DocumentText document, string issues, CancellationToken ct) =>
        Task.FromResult(DocumentType.Render(
            pack.VlmUserTemplate ?? throw new InvalidOperationException($"{pack.Id} 팩에 이미지 폴백 틀(vlm.user.md)이 없습니다"),
            new Dictionary<string, string> { ["ocr_text"] = document.Text, ["input_label"] = document.Label, ["issues"] = issues }));
}

public sealed record ProcessOptions(string Model, bool VlmFallback, double FallbackConfidence);

/// <param name="OcrConfidence">OCR 평균 신뢰도 (OCR 을 안 거친 원문이면 null)</param>
/// <param name="LoadImage">VLM 폴백용 이미지 (원본이 이미지일 때만, PDF · DOCX 는 null ➔ 폴백 안 함)</param>
public sealed record ProcessInput(DocumentText Document, double? OcrConfidence, Func<ImageInput>? LoadImage);

/// <param name="Final">최종 결과 (원문이 비었고 폴백도 못 하면 null)</param>
public sealed record ProcessResult(ExtractionAttempt? Final, IReadOnlyList<ExtractionAttempt> Attempts, string? FallbackReason)
{
    public ExtractionAttempt? TextAttempt => Attempts.FirstOrDefault(a => a.Source == ExtractionAttempt.Text);
}

public enum ProcessStage
{
    Extracting,
    Validated,
    FallbackExtracting,
    Revalidated,
}

/// <summary>
/// 필드 추출 ➔ 검증 ➔ (조건이 맞으면) VLM 폴백 ➔ 재검증 ➔ 최종 선택. 평가 도구(Api 문서 처리 · 모델 비교)와 exe 가 같은 흐름
/// (4차-exe 1단계에서 Api TextJobHandler · TextPipeline 에서 옮김). 원문 추출 · 종류 분류 · 저장은 부르는 쪽 몫
/// </summary>
/// <param name="extractorFor">모델 이름 ➔ 추출기 (Ollama 연결 · 문맥 길이 등은 부르는 쪽 설정)</param>
public sealed class DocumentProcessor(Func<string, IFieldExtractor> extractorFor, IPromptSource prompts)
{
    /// <summary>진행 알림 (단계, 화면에 보일 문장). 평가 도구의 진행 표시가 이 문장으로 단계를 찾음</summary>
    public Func<ProcessStage, string, CancellationToken, Task>? OnStatus { get; init; }

    public async Task<ProcessResult> ProcessAsync(DocumentType pack, ProcessInput input, ProcessOptions options, CancellationToken ct = default)
    {
        var model = options.Model;
        var attempts = new List<ExtractionAttempt>();
        ExtractionAttempt? textAttempt = null;
        if (input.Document.Text.Length > 0)
        {
            await StatusAsync(ProcessStage.Extracting, $"LLM 구조화 중: 필드 추출 ({pack.DisplayName}, {model})", ct);
            textAttempt = await ExtractAsync(pack, model, input.Document, image: null, previousIssues: null, ct);
            attempts.Add(textAttempt);
            await StatusAsync(ProcessStage.Validated, $"검증: {Summary(textAttempt)}", ct);
        }

        // 검증 실패 or 신뢰도 미달 ➔ VLM 폴백 (원본이 이미지이고 팩에 이미지 폴백 틀이 있을 때만)
        var reasons = new List<string>();
        if (textAttempt is null) reasons.Add("OCR 텍스트 없음");
        else if (textAttempt.ErrorCount > 0) reasons.Add($"검증 오류 {textAttempt.ErrorCount}건");
        if (input.OcrConfidence is { } confidence && confidence < options.FallbackConfidence)
            reasons.Add($"OCR 평균 신뢰도 {confidence:0.00} < {options.FallbackConfidence:0.00}");

        var fallbackReason = options.VlmFallback && input.LoadImage is not null && pack.VlmUserTemplate is not null && reasons.Count > 0
            ? string.Join(", ", reasons) : null;
        if (fallbackReason is not null)
        {
            await StatusAsync(ProcessStage.FallbackExtracting, $"VLM 폴백 추출 중 ({fallbackReason})", ct);
            var vlmAttempt = await ExtractAsync(pack, model, input.Document, input.LoadImage!(), textAttempt?.Issues, ct);
            attempts.Add(vlmAttempt);
            await StatusAsync(ProcessStage.Revalidated, $"재검증: {Summary(vlmAttempt)}", ct);
        }

        var final = attempts.Count == 0 ? null : ChooseFinal(textAttempt, attempts.FirstOrDefault(a => a.Source == ExtractionAttempt.Vlm));
        return new ProcessResult(final, attempts, fallbackReason);
    }

    /// <summary>추출 1회 + 필수 필드 확인 + 검증</summary>
    /// <param name="image">null 이면 원문 텍스트만으로, 있으면 VLM 폴백 (이미지 + 원문)</param>
    /// <param name="previousIssues">폴백 때 텍스트 추출에서 발견된 문제를 힌트로 전달</param>
    public async Task<ExtractionAttempt> ExtractAsync(
        DocumentType pack, string model, DocumentText document, ImageInput? image,
        IReadOnlyList<ValidationIssue>? previousIssues, CancellationToken ct = default)
    {
        var system = await prompts.SystemAsync(pack, model, ct);
        var user = image is null
            ? await prompts.UserAsync(pack, document, ct)
            : await prompts.VlmUserAsync(pack, document, IssueHint(previousIssues), ct);

        var result = await extractorFor(model).ExtractMessagesAsync(pack, system, user, image is null ? null : [image], ct);

        var fields = result.Fields;
        var parseError = result.Error;
        if (fields is not null)
        {
            var missing = pack.Fields.Select(f => f.Name).Where(k => !fields.ContainsKey(k)).ToList();
            if (missing.Count > 0) parseError = $"필드 누락: {string.Join(", ", missing)}";
        }
        // 팩 rules: 보정(영수증 수량) ➔ 종류별 규칙 ➔ 근거 확인(텍스트 추출만, VLM 은 이미지를 직접 봄)
        var issues = fields is null ? [] : Validator.Validate(pack, fields, document.Text, fromImage: image is not null);
        return new ExtractionAttempt(image is null ? ExtractionAttempt.Text : ExtractionAttempt.Vlm, model, (int)result.ElapsedMs,
            (int?)result.InputTokens, (int?)result.OutputTokens, SchemaValid: parseError is null, parseError, fields, issues, result.Raw,
            Engine: $"{pack.Id}@{pack.Version}");
    }

    /// <summary>
    /// 폴백 힌트. 규칙을 "맞추라"고 하면 모델이 값을 계산해 바꿔 버림 (7×1,980=13,860 을 만들어 냄) ➔ 이미지 확인만 요청
    /// </summary>
    public static string IssueHint(IReadOnlyList<ValidationIssue>? previousIssues) => previousIssues is { Count: > 0 }
        ? "OCR 텍스트로 먼저 추출한 결과를 규칙으로 검사했더니 아래 항목이 맞지 않았습니다. " +
          "OCR 오인식일 수 있으니 해당 값들을 이미지에서 직접 확인해 인쇄된 그대로 옮기세요. " +
          "규칙을 맞추려고 값을 계산하거나 바꾸지 마세요. 이미지와 같다면 그대로 둡니다.\n" +
          string.Join("\n", previousIssues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"- {i.Message}"))
        : "";

    /// <summary>
    /// VLM 결과가 검증을 통과하면 VLM, 아니면 통과한 텍스트 결과, 둘 다 실패면 오류가 적은 쪽 (같으면 숫자 환각 위험이 낮은 텍스트)
    /// </summary>
    public static ExtractionAttempt ChooseFinal(ExtractionAttempt? text, ExtractionAttempt? vlm) => (text, vlm) switch
    {
        (not null, null) => text,
        (null, not null) => vlm,
        (not null, not null) when vlm.ErrorCount == 0 => vlm,
        (not null, not null) when text.ErrorCount == 0 => text,
        (not null, not null) => vlm.ErrorCount < text.ErrorCount ? vlm : text,
        _ => throw new InvalidOperationException("추출 시도가 없습니다"),
    };

    public static string Summary(ExtractionAttempt attempt)
    {
        if (!attempt.SchemaValid) return $"스키마 위반 ({attempt.ParseError})";
        var errors = attempt.Issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = attempt.Issues.Count - errors;
        return errors == 0 ? $"통과 (경고 {warnings}건)" : $"오류 {errors}건, 경고 {warnings}건";
    }

    private Task StatusAsync(ProcessStage stage, string message, CancellationToken ct) =>
        OnStatus?.Invoke(stage, message, ct) ?? Task.CompletedTask;
}
