using System.Text.Json;
using Api.Modules.Image;
using Api.Shared.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Modules.Multimodal;

/// <summary>
/// 통합 작업 설정 스냅숏 (jobs.settings). X-ray 분석 설정 + 소견서 파일
/// </summary>
/// <param name="ReportFile">작업 폴더 안의 소견서 파일 이름: report.txt(붙여넣은 텍스트) 또는 report.png 등(이미지 ➔ OCR)</param>
public sealed record MultimodalSettings(ImageSettings Image, string ReportFile)
{
    public bool ReportIsText => ReportFile.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    public string ToJson() => JsonSerializer.Serialize(this, ImageSettings.Json);

    public static MultimodalSettings? FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<MultimodalSettings>(json, ImageSettings.Json);
}

/// <summary>
/// multimodal_results 테이블: 소견서 요약 + 소견서·CNN·VLM 일치 비교 + 종합 보고서.
/// X-ray 분석 결과는 image_results 에 같은 작업 ID 로 저장 (히트맵·결과 화면 재사용)
/// </summary>
public sealed class MultimodalResultRecord
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    /// <summary>"text"(붙여넣기) | "ocr"(소견서 이미지)</summary>
    public required string ReportSource { get; set; }
    /// <summary>LLM 에 넣은 소견서 텍스트 (OCR 이면 읽기 순서 텍스트)</summary>
    public required string ReportText { get; set; }
    /// <summary>소견서 요약 JSON (형식 오류면 null)</summary>
    public string? ReportSummary { get; set; }
    /// <summary>소견별 일치 비교 JSON (ConcordanceRow[])</summary>
    public required string Concordance { get; set; }
    /// <summary>종합 보고서 JSON (형식 오류면 null)</summary>
    public string? FinalReport { get; set; }
    /// <summary>문제 목록 JSON (X-ray 교차 검증 + 소견서 불일치)</summary>
    public required string Issues { get; set; }
    /// <summary>사람 확인 필요 (X-ray CNN·VLM 불일치 또는 소견서와 CNN 의 폐렴 판단 불일치)</summary>
    public bool NeedsReview { get; set; }
    /// <summary>LLM 호출 기록 (요약·종합 보고서 원문 응답)</summary>
    public required string Steps { get; set; }
    public int LlmElapsedMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class MultimodalResultRecordConfiguration : IEntityTypeConfiguration<MultimodalResultRecord>
{
    public void Configure(EntityTypeBuilder<MultimodalResultRecord> b)
    {
        b.ToTable("multimodal_results");
        b.Property(r => r.ReportSource).HasMaxLength(8);
        b.Property(r => r.ReportSummary).HasColumnType("jsonb");
        b.Property(r => r.Concordance).HasColumnType("jsonb");
        b.Property(r => r.FinalReport).HasColumnType("jsonb");
        b.Property(r => r.Issues).HasColumnType("jsonb");
        b.Property(r => r.Steps).HasColumnType("jsonb");
        b.HasOne<Job>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => r.JobId).IsUnique();
    }
}
