using System.Text.Json;
using System.Text.Json.Serialization;
using Api.Modules.Text.Ocr;
using Api.Modules.Text.Pipeline;
using Api.Shared.Data;
using Api.Shared.Settings;
using Api.Shared.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text.Settings;

/// <summary>
/// 문서 처리 한 번에 쓰는 설정 조합. 작업(Job)마다 스냅샷으로 저장되어, 같은 서버에서 여러 조합을 동시에 실험할 수 있다.
/// 모델 비교 페이지의 "조합" = 이 레코드 하나
/// </summary>
public sealed record PipelineSettings(
    /// <summary>LLM 모델 태그</summary>
    string Model,
    /// <summary>paddleocr(경로 A: 줄 텍스트) | ppstructure(경로 B: 레이아웃·표 Markdown)</summary>
    string OcrEngine,
    /// <summary>OCR 전에 LLM 을 GPU 에서 내릴지 (6단계 GPU 경합 대책)</summary>
    UnloadPolicy UnloadBeforeOcr,
    /// <summary>OCR 평균 신뢰도가 이보다 낮으면 VLM 폴백</summary>
    double FallbackConfidence,
    /// <summary>검증 오류·저신뢰 시 이미지로 다시 추출할지</summary>
    /// <remarks>예전 스냅숏의 amountsAsString · extraction 은 4차 통합에서 없앤 옵션이라 읽을 때 무시됨</remarks>
    bool VlmFallback,
    /// <summary>문서 종류 팩 id (예: resume). null 이면 LLM 이 분류 (영수증 · 상업송장 · 보험 청구서만 자동 분류)</summary>
    string? DocumentType = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PipelineSettings? FromJson(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<PipelineSettings>(json, Json);

    /// <summary>화면 표시용 한 줄 요약 (저장 JSON 에는 넣지 않음)</summary>
    [JsonIgnore]
    public string Summary =>
        $"{Model} · {(OcrEngine == "ppstructure" ? "경로 B" : "경로 A")} · LLM 내리기 {UnloadBeforeOcr}"
        + $" · 폴백 {(VlmFallback ? $"<{FallbackConfidence:0.00}" : "끔")}"
        + (DocumentType is null ? "" : $" · 문서 종류 {DocumentType}");
}

public sealed record SettingsDto(PipelineSettings Settings, string Summary, string? Note, DateTimeOffset UpdatedAt);

/// <summary>선택 가능한 값 (모델 비교 페이지의 조합 편집기)</summary>
public sealed record SettingsOptionsDto(
    IReadOnlyList<string> Models,
    IReadOnlyList<string> OcrEngines,
    IReadOnlyList<string> UnloadPolicies);

public sealed class SettingsStore(
    AppDbContext db,
    LlmClient llm,
    IOptions<LlmOptions> llmOptions,
    IOptions<OcrOptions> ocrOptions,
    IOptions<Pipeline.PipelineOptions> pipelineOptions,
    PackStore packs,
    TimeProvider clock)
{
    public SettingsOptionsDto Options() => new(llm.Models, Engines, Enum.GetNames<UnloadPolicy>());

    private List<string> Engines => ocrOptions.Value.Engines.Count > 0 ? ocrOptions.Value.Engines : [ocrOptions.Value.Engine];

    /// <summary>DB 의 기본 설정. 처음이면 appsettings(5·6단계 배치 평가로 정한 조합)로 만든다</summary>
    public async Task<SettingsDto> GetDefaultAsync(CancellationToken ct)
    {
        var record = await db.Set<PipelineSettingsRecord>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == PipelineSettingsRecord.DefaultId, ct);
        if (record is null)
        {
            record = new PipelineSettingsRecord
            {
                Id = PipelineSettingsRecord.DefaultId,
                Settings = FromAppSettings().ToJson(),
                Note = "5·6단계 배치 평가 결과 (KORIE 150장: gemma4:12b 선정, 경로 A, 큰 이미지만 LLM 내리기) — docs/model_selection.md",
                UpdatedAt = clock.GetUtcNow(),
            };
            db.Add(record);
            await db.SaveChangesAsync(ct);
        }
        var settings = PipelineSettings.FromJson(record.Settings)!;
        return new SettingsDto(settings, settings.Summary, record.Note, record.UpdatedAt);
    }

    public async Task<SettingsDto> SetDefaultAsync(PipelineSettings settings, string? note, CancellationToken ct)
    {
        await GetDefaultAsync(ct); // 없으면 먼저 생성
        var record = await db.Set<PipelineSettingsRecord>().FirstAsync(r => r.Id == PipelineSettingsRecord.DefaultId, ct);
        record.Settings = settings.ToJson();
        record.Note = note;
        record.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new SettingsDto(settings, settings.Summary, note, record.UpdatedAt);
    }

    /// <summary>잘못된 값이면 사유, 정상이면 null</summary>
    public string? Validate(PipelineSettings s)
    {
        if (!llm.IsKnownModel(s.Model))
        {
            return $"알 수 없는 모델: {s.Model} (사용 가능: {string.Join(", ", llm.Models)})";
        }
        if (!Engines.Contains(s.OcrEngine))
        {
            return $"알 수 없는 OCR 엔진: {s.OcrEngine} (사용 가능: {string.Join(", ", Engines)})";
        }
        if (s.FallbackConfidence is < 0 or > 1)
        {
            return "폴백 신뢰도 기준은 0~1 사이여야 합니다";
        }
        if (s.DocumentType is { } type && packs.Get(type) is null)
        {
            return $"문서 종류 팩이 없습니다: {type} (packs/{type})";
        }
        return null;
    }

    private PipelineSettings FromAppSettings() => new(
        llm.DefaultModel,
        ocrOptions.Value.Engine,
        llmOptions.Value.UnloadBeforeOcr,
        pipelineOptions.Value.FallbackConfidence,
        VlmFallback: true);
}
