using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Digitizer.App.Data;
using Digitizer.Engine;
using Digitizer.Engine.Rules;
using EngineLlmOptions = Digitizer.Engine.LlmOptions;

namespace Digitizer.App.Processing;

/// <summary>원문 추출 (테스트는 가짜로 바꿔 끼움)</summary>
public interface ISourceReader
{
    Task<SourceText> ReadAsync(string path, DocumentType pack, CancellationToken ct);
}

/// <summary>
/// Engine TextExtractor + OCR 서비스. 평가 도구 문서 처리와 같게: 이미지는 큰 것만 OCR 전에 LLM 을 내리고(UnloadBeforeOcr),
/// OCR 줄 순서는 팩의 reading_order (영수증 top · 이력서 center)
/// </summary>
public sealed class OcrSourceReader(SettingsFile settings, IHttpClientFactory http) : ISourceReader
{
    public const string OcrClient = "digitizer-ocr";
    public const string OllamaClient = "digitizer-ollama";

    public async Task<SourceText> ReadAsync(string path, DocumentType pack, CancellationToken ct)
    {
        var s = settings.Current;
        if (DocumentIntake.ImageExtensions.Contains(Path.GetExtension(path)) && s.UnloadBeforeOcr != UnloadPolicy.Never)
        {
            long pixels;
            await using (var file = File.OpenRead(path)) pixels = ImageResizer.PixelCount(file);
            if (OllamaMemory.ShouldUnloadBeforeOcr(pixels, s.UnloadBeforeOcr, s.UnloadAboveMegapixels))
                await OllamaMemory.UnloadAllAsync(http.CreateClient(OllamaClient), ct);
        }
        var extractor = new TextExtractor(new Engine.OcrClient(http.CreateClient(OcrClient), s.OcrEngine), pack.ReadingOrder);
        return await extractor.ExtractAsync(path, ct);
    }
}

/// <summary>처리 결과에 따라 옮길 곳</summary>
public enum Outcome
{
    Processed,
    NeedsReview,
    Failed,
}

/// <summary>
/// 문서 1건 처리: 원문 추출 ➔ Engine DocumentProcessor (추출 · 검증 · VLM 폴백 · 최종 선택, 문장은 팩 파일 그대로) ➔ 결과 판정.
/// 평가 도구와 같은 흐름 · 설정 (측정한 코드 = 배포하는 코드). 저장 · 파일 이동은 ProcessingQueue
/// </summary>
public sealed class DocumentRunner(SettingsFile settings, ISourceReader reader, Func<AppSettings, string, IFieldExtractor> extractorFor)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record Result(Outcome Outcome, string? Reason, bool TypeWarning, ExtractionRecord? Extraction);

    /// <summary>실제 Ollama 로 추출하는 기본 구성</summary>
    public static Func<AppSettings, string, IFieldExtractor> OllamaExtractors(IHttpClientFactory http) => (s, model) =>
        new FieldExtractor(http.CreateClient(OcrSourceReader.OllamaClient),
            new EngineLlmOptions(model, s.ContextLength, s.KeepAlive, s.CpuOnly, s.MaxOutputTokens));

    public async Task<Result> RunAsync(DocumentRecord doc, DocumentType pack, DateTimeOffset now, CancellationToken ct)
    {
        var s = settings.Current;
        var path = doc.StoredPath ?? throw new InvalidOperationException("원본 파일 위치가 없습니다");
        SourceText source;
        try
        {
            source = await reader.ReadAsync(path, pack, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Result(Outcome.Failed, $"원문 추출 실패: {Describe(ex)}", false, null);
        }

        var document = new DocumentText(source.Text, Structured: false, FieldExtractor.InputLabel(source.Source));
        // VLM 폴백은 원본이 이미지일 때만 (평가 도구와 같음: PDF · DOCX 는 이미지 없음)
        Func<ImageInput>? image = DocumentIntake.ImageExtensions.Contains(Path.GetExtension(path))
            ? () =>
            {
                using var file = File.OpenRead(path);
                return new ImageInput(ImageResizer.ToJpeg(file, s.VlmMaxImageSide), "image/jpeg");
            }
            : null;

        ProcessResult processed;
        try
        {
            var processor = new DocumentProcessor(model => extractorFor(s, model), PackPromptSource.Instance);
            processed = await processor.ProcessAsync(pack, new ProcessInput(document, source.OcrConfidence, image),
                new ProcessOptions(s.Model, s.VlmFallback, s.FallbackConfidence), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new Result(Outcome.Failed, $"LLM 추출 실패 ({s.Model}): {Describe(ex)}", false, null);
        }

        var final = processed.Final;
        var extraction = new ExtractionRecord
        {
            DocumentId = doc.Id,
            Engine = $"{pack.Id}@{pack.Version}",
            Model = s.Model,
            SourceKind = source.Source,
            Pages = source.Pages,
            SourceText = source.Text,
            OcrConfidence = source.OcrConfidence,
            Fields = final?.Fields?.ToJsonString(Json),
            Issues = JsonSerializer.Serialize(final?.Issues ?? [], Json),
            Attempts = JsonSerializer.Serialize(processed.Attempts, Json),
            FinalSource = final?.Source,
            FallbackUsed = processed.Attempts.Any(a => a.Source == ExtractionAttempt.Vlm),
            FallbackReason = processed.FallbackReason,
            ErrorCount = final?.ErrorCount ?? 0,
            WarningCount = final?.Issues.Count(i => i.Severity == IssueSeverity.Warning) ?? 0,
            TextMs = source.ElapsedMs,
            LlmMs = processed.Attempts.Sum(a => a.ElapsedMs),
            CreatedAt = now,
        };

        if (final is null)
            return new Result(Outcome.Failed, "원문 텍스트가 없어 필드를 추출하지 못했습니다 (빈 문서 · 이미지만 있는 PDF 인지 확인)", false, extraction);
        if (final.Fields is null)
            return new Result(Outcome.NeedsReview, $"추출 결과를 읽지 못했습니다: {final.ParseError}", false, extraction);

        var typeWarning = TypeMismatch.Suspect(pack, final.Fields);
        var reasons = new List<string>();
        if (typeWarning) reasons.Add(TypeMismatch.Message);
        if (final.ErrorCount > 0) reasons.Add($"검증 문제 {final.ErrorCount}건");
        // 조건부 합격 팩(이력서): 검증을 통과해도 사람이 모두 검수 (docs/pack_reports.md 의 조건)
        if (pack.Release?.Status == PackRelease.Conditional) reasons.Add("조건부 합격 종류라 모든 문서를 검수합니다");
        return reasons.Count == 0
            ? new Result(Outcome.Processed, null, false, extraction)
            : new Result(Outcome.NeedsReview, string.Join(" · ", reasons), typeWarning, extraction);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => $"서비스에 연결하지 못했습니다 ({ex.Message})",
        TaskCanceledException => "시간 초과",
        _ => ex.Message,
    };
}
