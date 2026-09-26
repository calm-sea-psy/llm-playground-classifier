using Api.Shared.Data;
using Api.Shared.Llm;
using Api.Shared.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Modules.Image;

public sealed record ImageSettingsDto(ImageSettings Settings, string Summary, string? Note, DateTimeOffset UpdatedAt);

/// <summary>선택 가능한 값 (모델 비교 페이지의 조합 편집기·업로드 화면)</summary>
public sealed record ImageSettingsOptionsDto(IReadOnlyList<string> Models, IReadOnlyList<string> Populations);

/// <summary>흉부 X-ray 판독 기본 설정 (pipeline_settings 의 "image" 행). 모델 비교 실험의 [기본으로 적용]으로 바뀐다</summary>
public sealed class ImageSettingsStore(AppDbContext db, LlmClient llm, IOptions<ImageOptions> options, TimeProvider clock)
{
    public ImageSettingsOptionsDto Options() => new(llm.Models, Populations.All);

    /// <summary>DB 의 기본 설정. 처음이면 appsettings Image 섹션(2차-1b·2차-2 결정)으로 만든다</summary>
    public async Task<ImageSettingsDto> GetDefaultAsync(CancellationToken ct)
    {
        var record = await db.Set<PipelineSettingsRecord>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == PipelineSettingsRecord.ImageId, ct);
        if (record is null)
        {
            var o = options.Value;
            record = new PipelineSettingsRecord
            {
                Id = PipelineSettingsRecord.ImageId,
                Settings = new ImageSettings(o.Model ?? llm.DefaultModel, o.VlmReport, o.VlmSeesCnn, o.Population).ToJson(),
                Note = "초기 설정: VLM 은 CNN 결과를 보지 않고 독립 판독(2차-2), 대상 기본 성인(2차-1b) — docs/architecture.md",
                UpdatedAt = clock.GetUtcNow(),
            };
            db.Add(record);
            await db.SaveChangesAsync(ct);
        }
        var settings = ImageSettings.FromJson(record.Settings)!;
        return new ImageSettingsDto(settings, settings.Summary, record.Note, record.UpdatedAt);
    }

    public async Task<ImageSettingsDto> SetDefaultAsync(ImageSettings settings, string? note, CancellationToken ct)
    {
        await GetDefaultAsync(ct); // 없으면 먼저 생성
        var record = await db.Set<PipelineSettingsRecord>().FirstAsync(r => r.Id == PipelineSettingsRecord.ImageId, ct);
        record.Settings = settings.ToJson();
        record.Note = note;
        record.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new ImageSettingsDto(settings, settings.Summary, note, record.UpdatedAt);
    }

    /// <summary>잘못된 값이면 사유, 정상이면 null</summary>
    public string? Validate(ImageSettings s)
    {
        if (!llm.IsKnownModel(s.Model))
        {
            return $"알 수 없는 모델: {s.Model} (사용 가능: {string.Join(", ", llm.Models)})";
        }
        if (!Populations.All.Contains(s.Population))
        {
            return $"알 수 없는 대상: {s.Population} (사용 가능: {string.Join(", ", Populations.All)})";
        }
        return null;
    }
}

public static class ImageSettingsText
{
    /// <summary>화면 표시용 한 줄 요약</summary>
    public static string Describe(ImageSettings s) =>
        $"{s.Model} · {(s.Population == Populations.Pediatric ? "파인튜닝 모델" : "사전학습 모델")}"
        + $" · VLM {(!s.VlmReport ? "끔" : s.VlmSeesCnn ? "CNN 참고" : "독립 판독")}";
}
