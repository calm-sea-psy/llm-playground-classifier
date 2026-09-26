using Digitizer.Engine.Rules;
using System.Diagnostics;
using System.Text.Json;
using Api.Modules.Text.Ocr;
using Api.Modules.Text.Pipeline;
using Api.Modules.Text.Settings;
using Api.Shared.Data;
using Api.Shared.Jobs;
using Api.Shared.Llm;
using Api.Shared.Storage;
using Digitizer.Engine;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text;

/// <summary>
/// Step 1 작업 처리: 원문 (이미지 OCR · PDF · DOCX) ➔ ClassifyDocument (문서 종류를 지정하면 생략) ➔ ExtractFields (팩 + Engine)
/// ➔ ValidateFields ➔ (VLM 폴백 ➔ 재검증, 원본이 이미지일 때만) ➔ SaveResult.
/// 추출 ~ 최종 선택은 Engine DocumentProcessor (exe 와 같은 흐름), 여기는 원문 · 분류 · 진행 표시 · 저장만
/// </summary>
public sealed class TextJobHandler(
    IOcrEngine ocr,
    TextPipeline pipeline,
    LlmClient llm,
    AppDbContext db,
    UploadStorage storage,
    IOptions<PipelineOptions> pipelineOptions,
    SettingsStore settingsStore,
    PackStore packs,
    IHttpClientFactory httpFactory,
    TimeProvider clock)
    : IJobHandler
{
    /// <summary>Engine 이 스캔 PDF 를 OCR 할 때 쓰는 HttpClient 이름 (TextModule 에서 등록)</summary>
    public const string EngineOcrClient = "digitizer-engine-ocr";

    public async Task<string> HandleAsync(Job job, JobReporter reporter, CancellationToken ct)
    {
        // 작업에 저장된 설정 조합 (모델 비교 실험), 없으면 DB 의 문서 처리 기본 설정
        var settings = PipelineSettings.FromJson(job.Settings) ?? (await settingsStore.GetDefaultAsync(ct)).Settings;
        // PDF · DOCX 는 이미지가 없어 VLM 폴백 · 이미지 분류를 하지 않음
        var isDocument = JobFactory.IsDocument(job.FileName);
        DocumentText document;
        double? ocrConfidence;
        // 종류를 알기 전에는 기본 줄 순서(top), 팩이 정해지면 그 팩의 방식으로 다시 만듦 (OCR 원문만)
        Func<string, string>? reorder = null;
        if (isDocument)
        {
            await reporter.SetStatusAsync(job, JobStatus.OcrRunning, "원문 추출 중 (PDF 텍스트 층 · DOCX, 스캔 PDF 는 OCR)", ct);
            var extractor = new TextExtractor(new OcrClient(httpFactory.CreateClient(EngineOcrClient), settings.OcrEngine));
            var source = await extractor.ExtractAsync(storage.PathOf(job.Id, job.StoredFileName), ct);
            await storage.WriteTextAsync(job.Id, "source.txt", source.Text, ct);
            document = new DocumentText(source.Text, Structured: false, FieldExtractor.InputLabel(source.Source));
            ocrConfidence = source.OcrConfidence;
            reorder = mode => source.WithReadingOrder(mode).Text;
        }
        else
        {
            var ocrResult = await RunOcrAsync(job, settings, reporter, ct);
            // 경로 B(문서 파싱 엔진)는 표 구조가 보존된 Markdown, 경로 A 는 줄 좌표로 만든 읽기 순서 텍스트
            var structured = !string.IsNullOrWhiteSpace(ocrResult.Markdown);
            document = new DocumentText(structured ? ocrResult.Markdown! : ocrResult.ReadingText(), structured);
            ocrConfidence = ocrResult.AvgConfidence;
            if (!structured) reorder = ocrResult.ReadingText;
        }
        var readingText = document.Text;
        var model = settings.Model;
        var options = pipelineOptions.Value;
        LlmImage? vlmImage = null;
        LlmImage VlmImage() => vlmImage ??= LoadVlmImage(job, options.VlmMaxImageSide);

        // 1) ClassifyDocument (문서 종류를 지정했으면 생략, OCR 텍스트가 없으면 이미지로 분류)
        Classification classification;
        if (settings.DocumentType is { } chosen)
        {
            classification = new Classification(chosen, 0, "사용자 지정", null);
        }
        else
        {
            await reporter.SetStatusAsync(job, JobStatus.LlmRunning, $"LLM 구조화 중: 문서 분류 ({model})", ct);
            classification = await pipeline.ClassifyDocumentAsync(
                model, document, readingText.Length == 0 && !isDocument ? VlmImage() : null, ct);
        }
        var documentType = classification.DocumentType;
        // 추출 가능 = 문서 종류 팩이 있음 (이력서처럼 자동 분류 대상이 아닌 종류도 지정하면 처리)
        if (packs.Get(documentType) is not { } pack)
        {
            await SaveResultAsync(job, model, classification, readingText, final: null, attempts: [], fallbackReason: null, ct);
            return $"완료: 지원하지 않는 문서 종류 ({documentType})";
        }

        if (reorder is not null && pack.ReadingOrder != ReadingOrder.Top)
        {
            document = document with { Text = reorder(pack.ReadingOrder) };
            readingText = document.Text;
        }

        // 2) ExtractFields ➔ 3) ValidateFields ➔ 4) (검증 실패 or 신뢰도 미달 ➔ VLM 폴백 ➔ 재검증) ➔ 최종 선택
        var processor = pipeline.CreateProcessor((stage, message, token) => reporter.SetStatusAsync(job,
            stage is ProcessStage.Validated or ProcessStage.Revalidated ? JobStatus.Validating : JobStatus.LlmRunning, message, token));
        var processed = await processor.ProcessAsync(pack,
            new ProcessInput(document, ocrConfidence, isDocument ? null : () => ToImageInput(VlmImage())),
            new ProcessOptions(model, settings.VlmFallback, settings.FallbackConfidence), ct);
        var attempts = processed.Attempts.ToList();
        var fallbackReason = processed.FallbackReason;

        // 원문이 비었고 이미지도 없어(PDF · DOCX) 폴백할 수 없으면 추출 없이 끝냄
        if (processed.Final is not { } final)
        {
            await SaveResultAsync(job, model, classification, readingText, final: null, attempts: [], fallbackReason: null, ct);
            return $"완료: 원문 텍스트가 없어 필드를 추출하지 못했습니다 ({pack.DisplayName})";
        }

        // 5) SaveResult
        await SaveResultAsync(job, model, classification, readingText, final, attempts, fallbackReason, ct);

        var llmMs = classification.ElapsedMs + attempts.Sum(a => a.ElapsedMs);
        return $"검증/저장 완료: {pack.DisplayName}, "
            + (final.ErrorCount == 0 ? "검증 통과" : $"검증 실패(오류 {final.ErrorCount}건)")
            + (fallbackReason is null ? "" : $", VLM 폴백{(final.Source == ExtractionAttempt.Vlm ? " 결과 사용" : " 후 텍스트 결과 유지")}")
            + $", LLM {llmMs / 1000.0:0.0}초";
    }

    private async Task<OcrResult> RunOcrAsync(Job job, PipelineSettings settings, JobReporter reporter, CancellationToken ct)
    {
        await reporter.SetStatusAsync(job, JobStatus.OcrRunning, $"OCR 추출 중 ({settings.OcrEngine})", ct);
        long pixels;
        await using (var file = storage.OpenRead(job.Id, job.StoredFileName))
        {
            pixels = ImageResizer.PixelCount(file);
        }
        if (llm.ShouldUnloadBeforeOcr(pixels, settings.UnloadBeforeOcr))
        {
            await reporter.SetStatusAsync(job, JobStatus.OcrRunning, $"OCR 추출 중 ({settings.OcrEngine}, 큰 이미지라 LLM 을 잠시 내림)", ct);
            await llm.UnloadAllAsync(ct);
        }

        var watch = Stopwatch.StartNew();
        OcrResult result;
        await using (var file = storage.OpenRead(job.Id, job.StoredFileName))
        {
            result = await ocr.RecognizeAsync(file, job.FileName, settings.OcrEngine, ct);
        }
        var requestMs = (int)watch.ElapsedMilliseconds;

        var json = JsonSerializer.Serialize(result, OcrJson.Options);
        await storage.WriteTextAsync(job.Id, "ocr.json", json, ct);
        db.Set<OcrResultRecord>().Add(new OcrResultRecord
        {
            JobId = job.Id,
            Engine = result.Engine,
            ModelVersion = result.ModelVersion,
            Page = result.Page,
            Width = result.Width,
            Height = result.Height,
            LineCount = result.Lines.Count,
            AvgConfidence = result.AvgConfidence,
            EngineElapsedMs = result.ElapsedMs,
            RequestElapsedMs = requestMs,
            Result = json,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static ImageInput ToImageInput(LlmImage image) => new(image.Data, image.MimeType);

    private LlmImage LoadVlmImage(Job job, int maxSide)
    {
        using var file = storage.OpenRead(job.Id, job.StoredFileName);
        return new LlmImage(ImageResizer.ToJpeg(file, maxSide), "image/jpeg");
    }

    private async Task SaveResultAsync(
        Job job,
        string model,
        Classification classification,
        string readingText,
        ExtractionAttempt? final,
        List<ExtractionAttempt> attempts,
        string? fallbackReason,
        CancellationToken ct)
    {
        var record = new ExtractionResultRecord
        {
            JobId = job.Id,
            Model = model,
            DocumentType = classification.DocumentType,
            ClassifyMs = classification.ElapsedMs,
            FinalSource = final?.Source,
            FallbackUsed = attempts.Any(a => a.Source == ExtractionAttempt.Vlm),
            FallbackReason = fallbackReason,
            ValidationPassed = final is null ? null : final.ErrorCount == 0,
            ErrorCount = final?.ErrorCount ?? 0,
            WarningCount = final?.Issues.Count(i => i.Severity == IssueSeverity.Warning) ?? 0,
            Fields = final?.Fields?.ToJsonString(PipelineJson.Options),
            Issues = JsonSerializer.Serialize(final?.Issues ?? [], PipelineJson.Options),
            Attempts = JsonSerializer.Serialize(attempts, PipelineJson.Options),
            ReadingText = readingText,
            LlmElapsedMs = classification.ElapsedMs + attempts.Sum(a => a.ElapsedMs),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Set<ExtractionResultRecord>().Add(record);
        await db.SaveChangesAsync(ct);

        var file = new
        {
            model,
            classification,
            documentType = classification.DocumentType,
            finalSource = record.FinalSource,
            fallbackReason,
            validationPassed = record.ValidationPassed,
            fields = final?.Fields,
            issues = final?.Issues,
            attempts,
            readingText,
        };
        await storage.WriteTextAsync(job.Id, "llm.json", JsonSerializer.Serialize(file, PipelineJson.Options), ct);
    }
}
