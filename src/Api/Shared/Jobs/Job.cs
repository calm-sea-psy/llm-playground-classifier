using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Shared.Jobs;

public enum JobStatus
{
    Queued,
    OcrRunning,
    /// <summary>Step 2: CNN 분류 (폐렴 판정·소견)</summary>
    CnnRunning,
    LlmRunning,
    Validating,
    Completed,
    Failed,
}

public sealed class Job
{
    public Guid Id { get; set; }
    /// <summary>처리할 모듈 키 (IPipelineModule.Key, 예: "text")</summary>
    public required string JobType { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    /// <summary>마지막 진행 메시지 (SignalR 로 보낸 것과 같은 문구)</summary>
    public string? StatusMessage { get; set; }
    public string? Error { get; set; }
    /// <summary>LLM 모델 태그 (생성 시 지정, 없으면 Llm:DefaultModel)</summary>
    public string? Model { get; set; }
    /// <summary>이 작업에 쓴 설정 조합 스냅샷 JSON (모듈이 해석, 없으면 모듈 기본 설정)</summary>
    public string? Settings { get; set; }
    /// <summary>모델 비교 실험에서 만든 작업이면 실험 ID 와 조합 번호</summary>
    public Guid? ExperimentId { get; set; }
    public int? ComboIndex { get; set; }
    /// <summary>처리에 쓴 프롬프트 JSON: {"모듈/이름": {"version": 2 | null(파일 기본값), "hash": "내용 해시 8자리"}}</summary>
    public string? Prompts { get; set; }

    /// <summary>사용자가 올린 원래 파일명</summary>
    public required string FileName { get; set; }
    /// <summary>data/uploads/{jobId}/ 아래 저장된 파일명</summary>
    public required string StoredFileName { get; set; }
    public string? ContentType { get; set; }
    public long FileSize { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsFinished => Status is JobStatus.Completed or JobStatus.Failed;
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("jobs");
        b.Property(j => j.JobType).HasMaxLength(32);
        b.Property(j => j.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(j => j.FileName).HasMaxLength(260);
        b.Property(j => j.StoredFileName).HasMaxLength(64);
        b.Property(j => j.ContentType).HasMaxLength(128);
        b.Property(j => j.Model).HasMaxLength(64);
        b.Property(j => j.Settings).HasColumnType("jsonb");
        b.Property(j => j.Prompts).HasColumnType("jsonb");
        b.HasIndex(j => j.ExperimentId);
        b.Ignore(j => j.IsFinished);
        b.HasIndex(j => j.CreatedAt);
        b.HasIndex(j => j.Status);
    }
}

public sealed record JobDto(
    Guid JobId,
    string JobType,
    string? Model,
    string Status,
    string? Message,
    string? Error,
    string FileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    JsonNode? Settings,
    Guid? ExperimentId,
    JsonNode? Prompts)
{
    public static JobDto From(Job j) => new(
        j.Id, j.JobType, j.Model, j.Status.ToString(), j.StatusMessage, j.Error, j.FileName,
        j.CreatedAt, j.UpdatedAt, j.StartedAt, j.CompletedAt,
        j.Settings is null ? null : JsonNode.Parse(j.Settings), j.ExperimentId,
        j.Prompts is null ? null : JsonNode.Parse(j.Prompts));
}
