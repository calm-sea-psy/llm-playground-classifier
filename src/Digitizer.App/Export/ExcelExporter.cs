using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Digitizer.App.Data;
using Digitizer.App.Processing;
using Digitizer.App.Review;
using Digitizer.Engine;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Export;

/// <summary>
/// 엑셀 내보내기: 팩 type.json 의 excel 시트 구성대로 (없으면 맨 위 필드 1장 + 목록마다 1장). 문서마다 검수값(없으면 추출값).
/// 대상 = ReviewService.Exportable (승인 + 합격 팩의 검증 통과), 기본은 아직 안 내보낸 건만. 파일은 문서 폴더 내보내기\
/// </summary>
public sealed class ExcelExporter(IDbContextFactory<DigitizerDb> dbFactory, PackCatalog catalog, FileRouter router, TimeProvider clock)
{
    public const string DocumentIdHeader = "문서 번호";
    public const string FileNameHeader = "파일 이름";

    public sealed record Pending(string PackId, string DisplayName, int NotExported, int Exported);

    /// <summary>종류별 내보낼 수 있는 건수 (안 내보낸 것 · 내보낸 것)</summary>
    public async Task<List<Pending>> PendingAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var docs = await db.Documents.AsNoTracking()
            .Where(d => d.PurgedAt == null && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Processed))
            .ToListAsync(ct);
        return catalog.Packs.Select(p =>
        {
            var mine = docs.Where(d => d.PackId == p.Id && ReviewService.Exportable(d, p)).ToList();
            return new Pending(p.Id, p.DisplayName, mine.Count(d => d.LastExportId is null), mine.Count(d => d.LastExportId is not null));
        }).ToList();
    }

    /// <param name="includeExported">이미 내보낸 건도 다시 넣음 (파일을 잃어버렸을 때)</param>
    public async Task<ExportRecord> ExportAsync(string packId, bool includeExported, CancellationToken ct = default)
    {
        var pack = catalog.Get(packId) ?? throw new KeyNotFoundException($"문서 종류가 없습니다: {packId}");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var candidates = await db.Documents
            .Where(d => d.PackId == packId && d.PurgedAt == null && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Processed)
                && (includeExported || d.LastExportId == null))
            .Include(d => d.Extractions)
            .OrderBy(d => d.Id)
            .ToListAsync(ct);
        var docs = candidates.Where(d => ReviewService.Exportable(d, pack)).ToList();
        if (docs.Count == 0) throw new InvalidOperationException($"{pack.DisplayName}: 내보낼 문서가 없습니다 (승인했거나 검증을 통과한 건만)");

        var now = clock.GetLocalNow();
        Directory.CreateDirectory(router.Folders.Exports);
        var path = FileRouter.FreeName(router.Folders.Exports, $"{UserFolders.FolderName(pack)}_{now:yyyyMMdd_HHmmss}.xlsx");
        var rows = docs.Select(d => (d, ReviewService.CurrentFields(d, d.Extractions.OrderByDescending(x => x.Id).FirstOrDefault()))).ToList();
        Write(pack, rows, path);

        var export = new ExportRecord { PackId = pack.Id, FilePath = path, DocumentCount = docs.Count, CreatedAt = clock.GetUtcNow() };
        db.Exports.Add(export);
        await db.SaveChangesAsync(ct);
        foreach (var d in docs) d.LastExportId = export.Id;
        await db.SaveChangesAsync(ct);
        return export;
    }

    public static IReadOnlyList<ExcelSheet> Sheets(DocumentType pack) => pack.Excel?.Sheets is { Count: > 0 } sheets
        ? sheets
        : [new ExcelSheet(pack.DisplayName, Fields: pack.Fields.Where(f => f.Type != "list").Select(f => f.Name).ToList()),
           .. pack.Fields.Where(f => f.Type == "list").Select(f => new ExcelSheet(f.Label, List: f.Name))];

    public static void Write(DocumentType pack, IReadOnlyList<(DocumentRecord Doc, JsonObject? Fields)> rows, string path)
    {
        using var book = new XLWorkbook();
        foreach (var sheet in Sheets(pack))
        {
            var ws = book.AddWorksheet(SheetName(sheet.Name));
            if (sheet.List is { } listName)
            {
                var list = pack.Fields.First(f => f.Name == listName);
                var items = list.Items ?? [];
                Header(ws, [DocumentIdHeader, FileNameHeader, "순번", .. items.Select(i => i.Label)]);
                var r = 2;
                foreach (var (doc, fields) in rows)
                {
                    var n = 0;
                    foreach (var item in (fields?[listName] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        ws.Cell(r, 1).Value = doc.Id;
                        ws.Cell(r, 2).Value = doc.OriginalName;
                        ws.Cell(r, 3).Value = ++n;
                        for (var c = 0; c < items.Count; c++) Set(ws.Cell(r, c + 4), items[c], item[items[c].Name]);
                        r++;
                    }
                }
            }
            else
            {
                var defs = (sheet.Fields ?? []).Select(n => pack.Fields.First(f => f.Name == n)).ToList();
                Header(ws, [DocumentIdHeader, FileNameHeader, .. defs.Select(f => f.Label)]);
                var r = 2;
                foreach (var (doc, fields) in rows)
                {
                    ws.Cell(r, 1).Value = doc.Id;
                    ws.Cell(r, 2).Value = doc.OriginalName;
                    for (var c = 0; c < defs.Count; c++) Set(ws.Cell(r, c + 3), defs[c], fields?[defs[c].Name]);
                    r++;
                }
            }
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents(1, Math.Min(ws.LastRowUsed()?.RowNumber() ?? 1, 200), 8, 60);
        }
        book.SaveAs(path);
    }

    private static void Header(IXLWorksheet ws, IReadOnlyList<string> headers)
    {
        for (var i = 0; i < headers.Count; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEFE");
    }

    /// <summary>금액 · 숫자는 숫자 칸 (합계 계산 가능), 문자열 목록은 ", " 로 이음, 나머지는 글자 그대로 (전화번호 앞 0 유지)</summary>
    private static void Set(IXLCell cell, FieldDef def, JsonNode? node)
    {
        if (node is null) return;
        if (def.Type is "amount" or "number" && node is JsonValue v && v.TryGetValue<double>(out var d))
        {
            cell.Value = d;
            if (def.Type == "amount") cell.Style.NumberFormat.Format = "#,##0";
            return;
        }
        cell.Value = node switch
        {
            JsonArray a => string.Join(", ", a.Select(FieldPaths.Value).OfType<string>()),
            _ => FieldPaths.Value(node) ?? "",
        };
        cell.Style.NumberFormat.Format = "@";
    }

    /// <summary>엑셀 시트 이름 규칙: 31자, : \ / ? * [ ] 불가</summary>
    private static string SheetName(string name)
    {
        var clean = string.Concat(name.Where(c => ":\\/?*[]".IndexOf(c) < 0)).Trim();
        if (clean.Length == 0) clean = "Sheet";
        return clean.Length > 31 ? clean[..31] : clean;
    }
}
