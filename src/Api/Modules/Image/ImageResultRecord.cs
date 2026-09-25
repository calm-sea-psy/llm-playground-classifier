using Api.Shared.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Modules.Image;

/// <summary>
/// image_results 테이블: CNN 두 엔진 결과 + VLM 판독 초안 + 교차 검증.
/// 히트맵 PNG 는 DB 가 아니라 data/uploads/{jobId}/heatmap_{엔진}.png (여기엔 원본 좌표 영역만)
/// </summary>
public sealed class ImageResultRecord
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    /// <summary>대상 "adult" | "pediatric" (폐렴 판단 출처가 달라짐)</summary>
    public required string Population { get; set; }
    /// <summary>폐렴 판단 출처 "엔진:소견" (소아 "pneumonia:Pneumonia", 성인 "xrv:Consolidation")</summary>
    public required string PneumoniaSource { get; set; }
    /// <summary>폐렴 신호 호출 결과 (CnnResult JSON, 히트맵 PNG 제외)</summary>
    public required string Pneumonia { get; set; }
    /// <summary>소견 엔진 결과 (CnnResult JSON, 히트맵 PNG 제외)</summary>
    public required string Findings { get; set; }
    public double? PneumoniaProbability { get; set; }
    public bool? PneumoniaPositive { get; set; }
    /// <summary>소견 엔진 양성 개수</summary>
    public int PositiveCount { get; set; }
    public int CnnElapsedMs { get; set; }

    /// <summary>VLM 판독 모델 (판독을 끄면 null)</summary>
    public string? Model { get; set; }
    /// <summary>VLM 판독 초안 JSON (형식 오류면 null)</summary>
    public string? Report { get; set; }
    /// <summary>VLM 시도 기록 (원문 응답 포함)</summary>
    public string? ReportAttempt { get; set; }
    public int LlmElapsedMs { get; set; }

    /// <summary>교차 검증 문제 목록 JSON</summary>
    public required string Issues { get; set; }
    /// <summary>Error 등급 문제 없음 (CNN·VLM 판단 일치)</summary>
    public bool ValidationPassed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ImageResultRecordConfiguration : IEntityTypeConfiguration<ImageResultRecord>
{
    public void Configure(EntityTypeBuilder<ImageResultRecord> b)
    {
        b.ToTable("image_results");
        b.Property(r => r.Pneumonia).HasColumnType("jsonb");
        b.Property(r => r.Findings).HasColumnType("jsonb");
        b.Property(r => r.Model).HasMaxLength(64);
        b.Property(r => r.Population).HasMaxLength(16);
        b.Property(r => r.PneumoniaSource).HasMaxLength(64);
        b.Property(r => r.Report).HasColumnType("jsonb");
        b.Property(r => r.ReportAttempt).HasColumnType("jsonb");
        b.Property(r => r.Issues).HasColumnType("jsonb");
        b.HasOne<Job>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => r.JobId).IsUnique();
    }
}
