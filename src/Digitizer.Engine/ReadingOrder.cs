namespace Digitizer.Engine;

/// <summary>OCR 줄 하나 (bbox = 꼭짓점 [x, y] 목록, 좌표가 없는 엔진은 null)</summary>
public sealed record OcrBox(string Text, int[][]? Bbox);

/// <summary>
/// OCR 줄 ➔ LLM 에 넣을 "읽기 순서 텍스트". 방식은 팩(type.json reading_order)이 정함 ➔ 측정한 방식 그대로 배포:
/// - top (기본): bbox 위쪽 기준 정렬, 이전 줄의 세로 중앙보다 위에서 시작하면 같은 줄, 줄 안은 x 순 · 공백 1칸. 영수증 KORIE 150장 측정
/// - center: 세로 중심이 줄 높이의 절반 안이면 같은 행, 행 안은 x 순 · 공백 2칸 (표의 칸이 한 행으로 모임). 이력서 0단계 측정
/// 4차-exe 1단계: 하나로 합쳤더니 이력서 표 양식 스캔에서 옆 칸("신장/체중")이 주소에 끼어 검증을 통과 ➔ 팩별 선택으로
/// </summary>
public static class ReadingOrder
{
    public const string Top = "top";
    public const string Center = "center";
    public static readonly IReadOnlyList<string> Modes = [Top, Center];

    public static string Build(IEnumerable<OcrBox> ocrLines, string mode = Top) => mode switch
    {
        Top => BuildTop(ocrLines),
        Center => BuildCenter(ocrLines),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, $"읽기 순서 방식은 {string.Join(" · ", Modes)} 중 하나"),
    };

    private static string BuildTop(IEnumerable<OcrBox> ocrLines)
    {
        var all = ocrLines.ToList();
        var boxes = all
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
        var withoutBox = all.Where(l => l.Bbox is not { Length: > 0 }).Select(l => l.Text);
        return string.Join("\n", lines.Concat(withoutBox));
    }

    /// <summary>이전 Engine OcrClient.ToText 그대로 (git b761369)</summary>
    private static string BuildCenter(IEnumerable<OcrBox> ocrLines)
    {
        var lines = ocrLines.ToList();
        var boxes = lines.Where(l => l.Bbox is { Length: > 0 }).Select(l =>
        {
            var ys = l.Bbox!.Select(p => p[1]).ToList();
            var xs = l.Bbox!.Select(p => p[0]).ToList();
            return (l.Text, Top: ys.Min(), Bottom: ys.Max(), Left: xs.Min());
        }).OrderBy(b => (b.Top + b.Bottom) / 2.0).ToList();

        var rows = new List<List<(string Text, int Top, int Bottom, int Left)>>();
        foreach (var b in boxes)
        {
            var center = (b.Top + b.Bottom) / 2.0;
            var row = rows.LastOrDefault();
            if (row is not null)
            {
                var rowCenter = row.Average(r => (r.Top + r.Bottom) / 2.0);
                var rowHeight = row.Average(r => r.Bottom - r.Top);
                if (Math.Abs(center - rowCenter) <= rowHeight / 2)
                {
                    row.Add(b);
                    continue;
                }
            }
            rows.Add([b]);
        }
        var noBox = lines.Where(l => l.Bbox is not { Length: > 0 }).Select(l => l.Text);
        return string.Join("\n", rows.Select(r => string.Join("  ", r.OrderBy(b => b.Left).Select(b => b.Text))).Concat(noBox));
    }
}
