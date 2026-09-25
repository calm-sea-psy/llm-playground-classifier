using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Shared.Experiments;

/// <summary>
/// experiments 테이블: 모델 비교 실험 = 문서 N장 × 설정 조합 M개. 작업(Job)은 jobs.experiment_id·combo_index 로 연결되고,
/// 지표(정확도·검증 통과율·시간)는 조회할 때 작업 결과에서 계산한다 (채점 규칙을 고쳐도 다시 돌릴 필요 없음).
/// 모듈 공통: 조합 JSON 의 형식과 채점은 모듈(JobType)이 해석한다 (Text = PipelineSettings, Image = ImageSettings)
/// </summary>
public sealed class Experiment
{
    public Guid Id { get; set; }
    /// <summary>실험한 모듈 키 (IPipelineModule.Key, 예: "text", "image")</summary>
    public required string JobType { get; set; }
    public required string Name { get; set; }
    /// <summary>실험 설명 (문서를 어떻게 골랐는지 등 결과를 읽을 때 필요한 설계). 예: "신뢰도 0.9 미만 문서만 골라 폴백 100%"</summary>
    public string? Description { get; set; }
    /// <summary>모듈의 설정 조합 목록 JSON</summary>
    public required string Combos { get; set; }
    public int DocCount { get; set; }
    /// <summary>실험 당시 기기·버전 정보 (나중에 결과를 볼 때 어떤 환경이었는지)</summary>
    public string? Environment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ExperimentConfiguration : IEntityTypeConfiguration<Experiment>
{
    public void Configure(EntityTypeBuilder<Experiment> b)
    {
        b.ToTable("experiments");
        b.Property(e => e.JobType).HasMaxLength(32).HasDefaultValue("text");
        b.Property(e => e.Name).HasMaxLength(200);
        b.Property(e => e.Description).HasMaxLength(2000);
        b.Property(e => e.Combos).HasColumnType("jsonb");
        b.Property(e => e.Environment).HasColumnType("jsonb");
        b.HasIndex(e => new { e.JobType, e.CreatedAt });
    }
}
