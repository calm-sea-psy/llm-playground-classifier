using Api.Modules.Text.Ocr;

namespace Api.Modules.Text.Pipeline;

/// <summary>
/// OCR lines ➔ LLM 에 넣을 "읽기 순서 텍스트" (todo 1번 ㅁ)-b).
/// bbox 위쪽 기준으로 정렬한 뒤, 이전 줄의 세로 중앙보다 위에서 시작하면 같은 줄로 묶고 줄 안에서는 x 순으로 잇는다.
/// </summary>
public static class ReadingOrder
{
    public static string Build(OcrResult ocr)
    {
        var boxes = ocr.Lines
            .Where(l => l.Bbox is { Length: > 0 })
            .Select(l => (
                Top: l.Bbox!.Min(p => p[1]),
                Bottom: l.Bbox!.Max(p => p[1]),
                Left: l.Bbox!.Min(p => p[0]),
                l.Text))
            .OrderBy(b => b.Top)
            .ToList();

        var rows = new List<(int Top, int Bottom, List<(int Left, string Text)> Items)>();
        foreach (var box in boxes)
        {
            if (rows.Count > 0 && box.Top < (rows[^1].Top + rows[^1].Bottom) / 2)
            {
                rows[^1].Items.Add((box.Left, box.Text));
            }
            else
            {
                rows.Add((box.Top, box.Bottom, [(box.Left, box.Text)]));
            }
        }

        var lines = rows.Select(r => string.Join(" ", r.Items.OrderBy(i => i.Left).Select(i => i.Text)));
        // 좌표가 없는 엔진(DeepSeek-OCR 등)은 받은 순서 그대로
        var withoutBox = ocr.Lines.Where(l => l.Bbox is not { Length: > 0 }).Select(l => l.Text);
        return string.Join("\n", lines.Concat(withoutBox));
    }
}
