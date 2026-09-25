using Api.Shared.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Modules.Text;

/// <summary>ocr_results 테이블: 요약 열 + 원본 OcrResult JSON(좌표·신뢰도 포함). 엔진별 비교를 위해 engine·model_version 을 항상 기록</summary>
public sealed class OcrResultRecord
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public required string Engine { get; set; }
    public required string ModelVersion { get; set; }
    public int Page { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int LineCount { get; set; }
    public double? AvgConfidence { get; set; }
    /// <summary>엔진 추론 시간 (OcrService 가 측정)</summary>
    public int EngineElapsedMs { get; set; }
    /// <summary>API ➔ OcrService 요청 전체 시간 (업로드·직렬화 포함)</summary>
    public int RequestElapsedMs { get; set; }
    /// <summary>OcrResult 원본 JSON (snake_case)</summary>
    public required string Result { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class OcrResultRecordConfiguration : IEntityTypeConfiguration<OcrResultRecord>
{
    public void Configure(EntityTypeBuilder<OcrResultRecord> b)
    {
        b.ToTable("ocr_results");
        b.Property(r => r.Engine).HasMaxLength(64);
        b.Property(r => r.ModelVersion).HasMaxLength(128);
        b.Property(r => r.Result).HasColumnType("jsonb");
        b.HasOne<Job>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => new { r.JobId, r.Page }).IsUnique();
    }
}
