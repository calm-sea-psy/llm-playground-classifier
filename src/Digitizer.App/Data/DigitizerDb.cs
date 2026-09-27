using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Data;

/// <summary>문서 상태. 내보내기 대상 = Approved + (합격 팩의) Processed</summary>
public enum DocumentStatus
{
    /// <summary>접수, 처리 대기 (원본은 데이터 폴더 queue\)</summary>
    Queued,
    Processing,
    /// <summary>검증 통과 (처리됨 폴더)</summary>
    Processed,
    /// <summary>검증 문제 · 종류 의심 · 조건부 팩 (확인 필요 폴더)</summary>
    NeedsReview,
    /// <summary>원문 추출 · LLM 오류, 지원하지 않는 형식 (실패 폴더 + 사유 .txt)</summary>
    Failed,
    /// <summary>같은 종류로 같은 내용을 이미 접수함 (실패 폴더 + 사유 .txt, 처리하지 않음)</summary>
    Duplicate,
    /// <summary>검수 화면에서 승인 (처리됨 폴더). 조건부 팩은 이 길로만 내보내기 대상이 됨</summary>
    Approved,
    /// <summary>검수 화면에서 반려 (실패 폴더 + 사유 .txt, 내보내지 않음)</summary>
    Rejected,
}

/// <summary>접수한 문서 1건 (원본 파일 기준)</summary>
public sealed class DocumentRecord
{
    public long Id { get; set; }
    public required string PackId { get; set; }
    /// <summary>처리에 쓴 팩 버전 (접수 때 값, 처리하면 실제 쓴 버전으로)</summary>
    public required string PackVersion { get; set; }
    /// <summary>사용자가 넣은 파일 이름</summary>
    public required string OriginalName { get; set; }
    /// <summary>원본이 지금 있는 곳 (queue ➔ 처리됨 · 확인 필요 · 실패). 보관 기한이 지나 지우면 null</summary>
    public string? StoredPath { get; set; }
    /// <summary>내용 SHA-256 (소문자 hex). 같은 종류로 같은 내용이 다시 들어오면 건너뜀</summary>
    public required string Sha256 { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>watch (감시 폴더) | upload (화면)</summary>
    public required string Source { get; set; }
    public DocumentStatus Status { get; set; }
    /// <summary>실패 · 중복 사유, 확인 필요 사유 (사람이 읽는 문장)</summary>
    public string? StatusReason { get; set; }
    /// <summary>필수 필드가 대부분 비어 종류가 달라 보임 (TypeMismatch)</summary>
    public bool TypeWarning { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    /// <summary>보관 기한이 지나 원본 · 원문 · 필드 값을 지운 때 (내보낸 건만 기록이 남음)</summary>
    public DateTimeOffset? PurgedAt { get; set; }
    public long? LastExportId { get; set; }

    /// <summary>검수에서 저장한 필드 JSON (없으면 추출값 그대로). 내보내기는 이 값을 씀</summary>
    public string? ReviewedFields { get; set; }
    /// <summary>검수 저장 · 승인 · 반려 시각</summary>
    public DateTimeOffset? ReviewedAt { get; set; }
    /// <summary>반려 사유 등 검수 메모</summary>
    public string? ReviewNote { get; set; }
    /// <summary>검수 때 비교한 칸 수 (수정률의 분모, 보관 기한이 지나 값을 지워도 남음)</summary>
    public int? ReviewedSlots { get; set; }

    public List<ExtractionRecord> Extractions { get; set; } = [];
    public List<CorrectionRecord> Corrections { get; set; } = [];
}

/// <summary>처리 1회 결과 (다시 처리하면 한 줄 더). 값 JSON 은 Engine 형식 그대로</summary>
public sealed class ExtractionRecord
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    /// <summary>"팩@버전"</summary>
    public required string Engine { get; set; }
    public required string Model { get; set; }
    /// <summary>원문 추출 방식: pdf-text · docx · ocr</summary>
    public required string SourceKind { get; set; }
    public int Pages { get; set; }
    /// <summary>LLM 에 넣은 원문 (보관 기한이 지나면 null)</summary>
    public string? SourceText { get; set; }
    public double? OcrConfidence { get; set; }
    /// <summary>최종 필드 JSON (추출 실패 · 보관 기한 지나면 null)</summary>
    public string? Fields { get; set; }
    /// <summary>최종 결과 검증 문제 JSON 배열</summary>
    public string? Issues { get; set; }
    /// <summary>모든 추출 시도 JSON (텍스트 · VLM 폴백, 응답 원문 포함)</summary>
    public string? Attempts { get; set; }
    /// <summary>text | vlm</summary>
    public string? FinalSource { get; set; }
    public bool FallbackUsed { get; set; }
    public string? FallbackReason { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public long TextMs { get; set; }
    public long LlmMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>검수에서 고친 값 1건 (3단계 화면). 종류별 수정률 = 운영 중 정확도</summary>
public sealed class CorrectionRecord
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public long ExtractionId { get; set; }
    /// <summary>필드 경로 (예: phone, career[1].company)</summary>
    public required string FieldPath { get; set; }
    /// <summary>추출값 · 고친 값 JSON (보관 기한이 지나면 null, 경로만 남음)</summary>
    public string? ExtractedValue { get; set; }
    public string? CorrectedValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>엑셀 내보내기 1회 (3단계)</summary>
public sealed class ExportRecord
{
    public long Id { get; set; }
    public required string PackId { get; set; }
    public required string FilePath { get; set; }
    public int DocumentCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>엑셀 보관 기한(ExportRetentionDays)이 지나 파일을 지운 때</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class DigitizerDb(DbContextOptions<DigitizerDb> options) : DbContext(options)
{
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();
    public DbSet<ExtractionRecord> Extractions => Set<ExtractionRecord>();
    public DbSet<CorrectionRecord> Corrections => Set<CorrectionRecord>();
    public DbSet<ExportRecord> Exports => Set<ExportRecord>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DocumentRecord>(e =>
        {
            e.ToTable("documents");
            e.Property(d => d.Status).HasConversion<string>();
            e.HasIndex(d => new { d.PackId, d.Sha256 });
            e.HasIndex(d => d.Status);
            e.HasMany(d => d.Extractions).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(d => d.Corrections).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ExportRecord>().WithMany().HasForeignKey(d => d.LastExportId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ExtractionRecord>(e => e.ToTable("extractions"));
        b.Entity<CorrectionRecord>(e =>
        {
            e.ToTable("corrections");
            e.HasOne<ExtractionRecord>().WithMany().HasForeignKey(c => c.ExtractionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ExportRecord>(e => e.ToTable("exports"));

        // SQLite 는 DateTimeOffset 정렬 · 비교를 못 함 ➔ UTC 틱(long)으로 저장
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var p in entity.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            p.SetValueConverter(p.ClrType == typeof(DateTimeOffset) ? UtcTicks : NullableUtcTicks);
    }

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long> UtcTicks =
        new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    private static readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?> NullableUtcTicks =
        new(v => v.HasValue ? v.Value.UtcTicks : null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);
}
