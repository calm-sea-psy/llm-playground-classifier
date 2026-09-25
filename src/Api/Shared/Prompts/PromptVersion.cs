using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Shared.Prompts;

/// <summary>
/// prompt_versions 테이블: UI 에서 고친 프롬프트의 버전. 파일(Prompts/*.md·*.json)이 기본값이고,
/// (모듈, 이름)마다 Active 인 버전이 최대 하나 있으면 파일 대신 그 내용을 쓴다. 없으면 파일 기본값
/// </summary>
public sealed class PromptVersion
{
    public long Id { get; set; }
    public required string Module { get; set; }
    public required string Name { get; set; }
    /// <summary>(모듈, 이름) 안에서 1부터 증가</summary>
    public int Version { get; set; }
    public required string Content { get; set; }
    /// <summary>수정 이유 (예: "폐부종 부정문 규칙 강조")</summary>
    public string? Note { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class PromptVersionConfiguration : IEntityTypeConfiguration<PromptVersion>
{
    public void Configure(EntityTypeBuilder<PromptVersion> b)
    {
        b.ToTable("prompt_versions");
        b.Property(p => p.Module).HasMaxLength(32);
        b.Property(p => p.Name).HasMaxLength(128);
        b.Property(p => p.Note).HasMaxLength(500);
        b.HasIndex(p => new { p.Module, p.Name, p.Version }).IsUnique();
        // (모듈, 이름)마다 적용 중인 버전은 하나
        b.HasIndex(p => new { p.Module, p.Name }).IsUnique().HasFilter("active");
    }
}
