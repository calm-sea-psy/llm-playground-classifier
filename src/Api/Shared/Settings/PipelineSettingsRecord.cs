using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Shared.Settings;

/// <summary>
/// pipeline_settings 테이블: 모듈별 기본 설정 (한 모듈 한 행). Id = "default"(Text 문서 처리) | "image"(흉부 X-ray 판독).
/// 설정 JSON 형식은 모듈이 해석한다
/// </summary>
public sealed class PipelineSettingsRecord
{
    public const string DefaultId = "default";
    public const string ImageId = "image";
    public required string Id { get; set; }
    public required string Settings { get; set; }
    /// <summary>이 설정의 근거 (예: "실험 '영수증 30장' 추천 1위")</summary>
    public string? Note { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class PipelineSettingsRecordConfiguration : IEntityTypeConfiguration<PipelineSettingsRecord>
{
    public void Configure(EntityTypeBuilder<PipelineSettingsRecord> b)
    {
        b.ToTable("pipeline_settings");
        b.Property(r => r.Id).HasMaxLength(32);
        b.Property(r => r.Settings).HasColumnType("jsonb");
    }
}
