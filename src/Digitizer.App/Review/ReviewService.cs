using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Digitizer.App.Data;
using Digitizer.App.Processing;
using Digitizer.Engine;
using Digitizer.Engine.Rules;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Review;

public enum ReviewAction
{
    /// <summary>고친 값만 저장 (상태 그대로)</summary>
    Save,
    Approve,
    Reject,
}

/// <param name="AcknowledgeIssues">검증 문제가 남아 있어도 승인 (사람이 원본과 대조해 확인함). 없으면 문제가 남은 승인은 거부</param>
public sealed record ReviewRequest(JsonObject Fields, ReviewAction Action, bool AcknowledgeIssues = false, string? Note = null);

public sealed class ReviewException(string message, IReadOnlyList<ValidationIssue>? issues = null) : Exception(message)
{
    public IReadOnlyList<ValidationIssue>? Issues { get; } = issues;
}

/// <summary>
/// 검수: 고친 값을 팩 규칙으로 다시 검사 ➔ 추출값과 달라진 칸을 corrections 에 (문서마다 마지막 검수 기준으로 교체) ➔ 승인 · 반려면 상태 · 파일 이동.
/// 승인은 이 길(사람이 필드를 보고 보냄)로만 가능 ➔ 조건부 팩(이력서)은 자동 승인이 없음. 검증을 끄는 옵션도 없음
/// </summary>
public sealed class ReviewService(IDbContextFactory<DigitizerDb> dbFactory, PackCatalog catalog, FileRouter router, TimeProvider clock)
{
    public static readonly IReadOnlySet<DocumentStatus> Reviewable = new HashSet<DocumentStatus>
        { DocumentStatus.Processed, DocumentStatus.NeedsReview, DocumentStatus.Approved, DocumentStatus.Rejected };

    /// <summary>내보내기 대상: 승인한 건 + 합격(passed) 팩에서 검증을 통과한 건. 조건부 팩은 승인한 건만</summary>
    public static bool Exportable(DocumentRecord doc, DocumentType? pack) =>
        doc.PurgedAt is null && (doc.Status == DocumentStatus.Approved
            || (doc.Status == DocumentStatus.Processed && pack?.Release?.Status == PackRelease.Passed));

    /// <summary>검수 화면이 보여 줄 값: 검수에서 저장한 값이 있으면 그 값, 아니면 추출값</summary>
    public static JsonObject? CurrentFields(DocumentRecord doc, ExtractionRecord? extraction) =>
        JsonNode.Parse(doc.ReviewedFields ?? extraction?.Fields ?? "null") as JsonObject;

    /// <summary>저장하지 않고 검사만 (화면의 "다시 검사")</summary>
    public async Task<List<ValidationIssue>> CheckAsync(long id, JsonObject fields, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (doc, pack, extraction) = await LoadAsync(db, id, ct);
        return Validate(pack, extraction, FieldPaths.Normalize(pack, fields)).Issues;
    }

    public async Task<(DocumentRecord Document, List<ValidationIssue> Issues)> SubmitAsync(long id, ReviewRequest request, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var (doc, pack, extraction) = await LoadAsync(db, id, ct);
        var (fields, issues) = Validate(pack, extraction, FieldPaths.Normalize(pack, request.Fields));
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);

        if (request.Action == ReviewAction.Approve && errors > 0 && !request.AcknowledgeIssues)
            throw new ReviewException($"검증 문제 {errors}건이 남아 있습니다. 원본과 대조해 고치거나, 확인했다고 표시한 뒤 승인하세요", issues);
        if (request.Action == ReviewAction.Reject && string.IsNullOrWhiteSpace(request.Note))
            throw new ReviewException("반려 사유를 적어 주세요");

        var now = clock.GetUtcNow();
        var (changes, slots) = FieldPaths.Diff(pack, JsonNode.Parse(extraction.Fields ?? "null") as JsonObject, fields);
        db.Corrections.RemoveRange(db.Corrections.Where(c => c.DocumentId == doc.Id));
        db.Corrections.AddRange(changes.Select(c => new CorrectionRecord
        {
            DocumentId = doc.Id,
            ExtractionId = extraction.Id,
            FieldPath = c.Path,
            ExtractedValue = c.Extracted,
            CorrectedValue = c.Corrected,
            CreatedAt = now,
        }));
        doc.ReviewedFields = fields.ToJsonString(DocumentRunner.Json);
        doc.ReviewedSlots = slots;
        doc.ReviewedAt = now;

        switch (request.Action)
        {
            case ReviewAction.Approve:
                doc.Status = DocumentStatus.Approved;
                doc.ReviewNote = request.Note;
                doc.StatusReason = errors > 0 ? $"검증 문제 {errors}건을 확인하고 승인" : null;
                if (doc.StoredPath is { } p && File.Exists(p) && !InDoneFolder(p, pack))
                    doc.StoredPath = router.MoveToDone(p, doc.OriginalName, pack);
                break;
            case ReviewAction.Reject:
                doc.Status = DocumentStatus.Rejected;
                doc.ReviewNote = request.Note;
                doc.StatusReason = $"반려: {request.Note}";
                if (doc.StoredPath is { } r && File.Exists(r) && !r.StartsWith(router.Folders.Failed, StringComparison.OrdinalIgnoreCase))
                    doc.StoredPath = router.MoveToFailed(r, doc.OriginalName, doc.StatusReason);
                break;
        }
        await db.SaveChangesAsync(ct);
        return (doc, issues);
    }

    private bool InDoneFolder(string path, DocumentType pack) =>
        path.StartsWith(router.Folders.DoneRoot(pack), StringComparison.OrdinalIgnoreCase);

    private async Task<(DocumentRecord, DocumentType, ExtractionRecord)> LoadAsync(DigitizerDb db, long id, CancellationToken ct)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new KeyNotFoundException($"문서 #{id} 가 없습니다");
        if (!Reviewable.Contains(doc.Status) || doc.PurgedAt is not null)
            throw new ReviewException($"검수할 수 없는 상태입니다 ({doc.Status}{(doc.PurgedAt is null ? "" : ", 보관 기한이 지나 값을 지움")})");
        var pack = catalog.Get(doc.PackId) ?? throw new ReviewException($"이 프로그램에 없는 문서 종류입니다 ({doc.PackId})");
        var extraction = await db.Extractions.Where(x => x.DocumentId == id).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct)
            ?? throw new ReviewException("추출 결과가 없습니다");
        return (doc, pack, extraction);
    }

    public const string HumanGrounded = "grounded_human";

    /// <summary>
    /// 처리 때와 같은 검증 (보정 규칙은 복사본에 적용되어 저장값에도 반영됨). VLM 결과였으면 원문 근거 확인은 빼고.
    /// 사람이 고친 칸의 원문 근거 오류는 경고로 낮춤: OCR 이 잘못 읽은 값을 원본을 보고 고치면 당연히 OCR 원문에 없음
    /// (검수 화면에서만, 처리 · 측정 때 검증은 그대로)
    /// </summary>
    private static (JsonObject Fields, List<ValidationIssue> Issues) Validate(DocumentType pack, ExtractionRecord extraction, JsonObject fields)
    {
        var issues = Validator.Validate(pack, fields, extraction.SourceText ?? "", fromImage: extraction.FinalSource == ExtractionAttempt.Vlm);
        var changed = FieldPaths.Diff(pack, JsonNode.Parse(extraction.Fields ?? "null") as JsonObject, fields).Changes.Select(c => c.Path).ToHashSet();
        return (fields, issues.Select(i => i.Rule == "grounded" && i.Field is { } f && changed.Contains(f)
            ? new ValidationIssue(HumanGrounded, f, IssueSeverity.Warning,
                i.Message.Replace("(지어낸 값 의심)", "") + "➔ 사람이 고친 값이라 경고만 (원본과 대조했는지 확인)")
            : i).ToList());
    }

    /// <summary>검수 화면에 보일 원문: 수집 금지 패턴(주민등록번호 등)을 가림</summary>
    public static string Mask(DocumentType pack, string text)
    {
        foreach (var fb in pack.Forbidden.Where(f => f.Pattern is not null))
            text = Regex.Replace(text, fb.Pattern!, m => new string('●', Math.Min(m.Value.Length, 14)));
        return text;
    }

    public static List<ValidationIssue> ParseIssues(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<ValidationIssue>>(json, DocumentRunner.Json) ?? [];
}
