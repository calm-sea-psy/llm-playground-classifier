using System.Text.Json;

namespace Api.Modules.Text.Ocr;

/// <summary>OCR 표준 응답 계약 (OcrService contract.py 와 1:1, JSON 은 snake_case)</summary>
public sealed record OcrResult(
    string Engine,
    string ModelVersion,
    int Page,
    int Width,
    int Height,
    IReadOnlyList<OcrLine> Lines,
    /// <summary>줄이 없으면 0, 엔진이 신뢰도를 주지 않으면 null</summary>
    double? AvgConfidence,
    int ElapsedMs,
    /// <summary>문서 파싱 엔진(ppstructure)만: 레이아웃 블록 (읽기 순서)</summary>
    IReadOnlyList<LayoutBlock>? Blocks = null,
    /// <summary>문서 파싱 엔진만: 읽기 순서로 복원한 Markdown (표는 HTML). 있으면 LLM 입력으로 우선 사용</summary>
    string? Markdown = null);

public sealed record LayoutBlock(string Label, int[] Bbox, string Content);

/// <summary>Bbox: 좌상단부터 시계 방향 4점 [[x, y], ...], 원본 이미지 픽셀 좌표</summary>
public sealed record OcrLine(string Text, double? Confidence, int[][]? Bbox);

public static class OcrJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
