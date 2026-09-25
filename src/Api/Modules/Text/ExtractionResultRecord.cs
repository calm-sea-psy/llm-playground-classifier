using Api.Shared.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Modules.Text;

/// <summary>
/// extraction_results 테이블: LLM 구조화 최종 결과 + 시도 기록(텍스트 추출, VLM 폴백).
/// 5단계 지표(필드 정확도, 스키마 준수율, 검증 통과율, 폴백 발생률, 처리 시간)를 여기서 계산한다.
/// </summary>
public sealed class ExtractionResultRecord
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public required string Model { get; set; }
    public required string DocumentType { get; set; }
    public int ClassifyMs { get; set; }
    /// <summary>최종 결과를 만든 시도: "text" | "vlm" (other 문서는 null)</summary>
    public string? FinalSource { get; set; }
    public bool FallbackUsed { get; set; }
    public string? FallbackReason { get; set; }
    /// <summary>스키마 준수 + Error 등급 문제 없음 (other 문서는 null)</summary>
    public bool? ValidationPassed { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    /// <summary>최종 필드 JSON (금액은 숫자로 정규화)</summary>
    public string? Fields { get; set; }
    /// <summary>최종 결과의 검증 문제 목록 JSON</summary>
    public required string Issues { get; set; }
    /// <summary>모든 시도(ExtractionAttempt[]) JSON, LLM 원문 응답 포함</summary>
    public required string Attempts { get; set; }
    /// <summary>LLM 에 넣은 읽기 순서 텍스트</summary>
    public required string ReadingText { get; set; }
    /// <summary>분류 + 추출 + 폴백 LLM 시간 합계</summary>
    public int LlmElapsedMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ExtractionResultRecordConfiguration : IEntityTypeConfiguration<ExtractionResultRecord>
{
    public void Configure(EntityTypeBuilder<ExtractionResultRecord> b)
    {
        b.ToTable("extraction_results");
        b.Property(r => r.Model).HasMaxLength(64);
        b.Property(r => r.DocumentType).HasMaxLength(32);
        b.Property(r => r.FinalSource).HasMaxLength(8);
        b.Property(r => r.Fields).HasColumnType("jsonb");
        b.Property(r => r.Issues).HasColumnType("jsonb");
        b.Property(r => r.Attempts).HasColumnType("jsonb");
        b.HasOne<Job>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => r.JobId).IsUnique();
        b.HasIndex(r => new { r.Model, r.DocumentType });
    }
}
