using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Digitizer.Engine;

/// <summary>추출된 원문. Source: pdf-text · docx · ocr. OcrPages = OCR 줄 (쪽마다, 읽기 순서 방식을 바꿔 다시 만들 때)</summary>
public sealed record SourceText(string Text, string Source, int Pages, long ElapsedMs, double? OcrConfidence = null,
    IReadOnlyList<IReadOnlyList<OcrBox>>? OcrPages = null)
{
    /// <summary>OCR 원문이면 다른 읽기 순서 방식으로 다시 만든 텍스트 (종류를 분류한 뒤 팩의 방식을 알게 될 때)</summary>
    public SourceText WithReadingOrder(string mode) => OcrPages is null
        ? this
        : this with { Text = string.Join("\n\n", OcrPages.Select(p => ReadingOrder.Build(p, mode))) };
}

/// <summary>
/// 원문 텍스트 추출. PDF 는 텍스트 층을 먼저 쓰고, 텍스트가 거의 없으면(스캔 PDF) 페이지 이미지를 OCR.
/// DOCX 는 문단 · 표를 문서 순서대로 (표는 칸을 " | " 로 이음). 이미지는 OCR (줄 순서는 readingOrder 방식 = 팩의 reading_order)
/// </summary>
public sealed class TextExtractor(OcrClient ocr, string readingOrder = ReadingOrder.Top)
{
    private const int MinTextLayerChars = 30;

    public async Task<SourceText> ExtractAsync(string path, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".pdf":
            {
                using var pdf = PdfDocument.Open(path);
                var pages = pdf.GetPages().ToList();
                var text = string.Join("\n\n", pages.Select(p => ContentOrderTextExtractor.GetText(p)));
                if (text.Count(c => !char.IsWhiteSpace(c)) >= MinTextLayerChars)
                    return new SourceText(Normalize(text), "pdf-text", pages.Count, sw.ElapsedMilliseconds);

                // 스캔 PDF: 페이지마다 들어 있는 이미지를 OCR
                var images = pages.SelectMany(p => p.GetImages()).Select(i => i.TryGetPng(out var png) ? png : i.RawBytes.ToArray()).ToList();
                return await OcrImages(images, sw, ct);
            }
            case ".docx":
                return new SourceText(Normalize(Docx(path)), "docx", 1, sw.ElapsedMilliseconds);
            case ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp":
                return await OcrImages([await File.ReadAllBytesAsync(path, ct)], sw, ct);
            default:
                throw new NotSupportedException($"지원하지 않는 형식입니다: {ext} (PDF · DOCX · 이미지)");
        }
    }

    /// <summary>여러 쪽 스캔 (scan_1.png, scan_2.png …) 을 한 문서로</summary>
    public async Task<SourceText> ExtractPagesAsync(IEnumerable<string> imagePaths, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var images = new List<byte[]>();
        foreach (var p in imagePaths) images.Add(await File.ReadAllBytesAsync(p, ct));
        return await OcrImages(images, sw, ct);
    }

    private async Task<SourceText> OcrImages(List<byte[]> images, Stopwatch sw, CancellationToken ct)
    {
        var pages = new List<IReadOnlyList<OcrBox>>();
        var confidences = new List<double>();
        foreach (var image in images)
        {
            var result = await ocr.RecognizeAsync(image, ct);
            pages.Add(result.Boxes);
            if (result.AvgConfidence is { } c) confidences.Add(c);
        }
        return new SourceText(string.Join("\n\n", pages.Select(p => ReadingOrder.Build(p, readingOrder))), "ocr", images.Count,
            sw.ElapsedMilliseconds, confidences.Count > 0 ? confidences.Average() : null, pages);
    }

    private static string Docx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var sb = new StringBuilder();
        foreach (var element in doc.MainDocumentPart!.Document!.Body!.ChildElements)
        {
            switch (element)
            {
                case Paragraph p:
                    sb.AppendLine(p.InnerText);
                    break;
                case Table t:
                    foreach (var row in t.Elements<TableRow>())
                        sb.AppendLine(string.Join(" | ", row.Elements<TableCell>().Select(c => c.InnerText.Trim())));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}

/// <summary>OCR 서비스 (Python FastAPI, POST /ocr) 호출. 줄은 ReadingOrder 로 읽기 순서 텍스트로</summary>
public sealed class OcrClient(HttpClient http, string? engine = null)
{
    public sealed record OcrLine([property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("confidence")] double? Confidence,
        [property: JsonPropertyName("bbox")] List<List<int>>? Bbox);

    private sealed record OcrResponse([property: JsonPropertyName("lines")] List<OcrLine> Lines,
        [property: JsonPropertyName("avg_confidence")] double? AvgConfidence);

    /// <param name="Text">기본 방식(top) 읽기 순서 텍스트</param>
    public sealed record OcrText(string Text, double? AvgConfidence, int LineCount, IReadOnlyList<OcrBox> Boxes);

    public async Task<OcrText> RecognizeAsync(byte[] image, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(image), "file", "page.png" } };
        var url = engine is null ? "ocr" : $"ocr?engine={Uri.EscapeDataString(engine)}";
        using var response = await http.PostAsync(url, form, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OCR 오류 {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        var result = await response.Content.ReadFromJsonAsync<OcrResponse>(ct) ?? throw new InvalidDataException("OCR 응답이 비었습니다");
        var boxes = result.Lines.Select(l => new OcrBox(l.Text, l.Bbox?.Select(p => p.ToArray()).ToArray())).ToList();
        return new OcrText(ReadingOrder.Build(boxes), result.AvgConfidence, result.Lines.Count, boxes);
    }
}
