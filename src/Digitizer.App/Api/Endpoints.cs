using System.Text.Json.Nodes;
using Digitizer.App.Data;
using Digitizer.App.Export;
using Digitizer.App.Processing;
using Digitizer.App.Review;
using Digitizer.App.Status;
using Digitizer.Engine;
using Digitizer.Engine.Rules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App.Api;

/// <summary>화면(ClientApp) 용 API. 응답에는 원문 · 필드 값이 들어가므로 LocalOnly 가 이 PC 의 화면에서 온 요청만 통과시킴</summary>
public static class Endpoints
{
    public const int MaxUploadBytes = 50 * 1024 * 1024;

    public static void MapDigitizerApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        api.MapGet("/packs", (PackCatalog c) => c.Packs.Select(p => new
        {
            p.Id,
            p.Version,
            p.DisplayName,
            p.Description,
            p.Release,
            Fields = p.Fields,
            Forbidden = p.Forbidden.Select(f => f.Label),
            MeasuredModels = AppSettings.MeasuredModels.GetValueOrDefault(p.Id, []),
        }));

        // 목록 · 거르기 (종류 · 상태 · 접수일), 확인 필요 건수
        api.MapGet("/documents", async (IDbContextFactory<DigitizerDb> f, string? pack, string? status, DateOnly? from, DateOnly? to, int? take) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var q = db.Documents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(pack)) q = q.Where(d => d.PackId == pack);
            if (!string.IsNullOrEmpty(status))
            {
                var statuses = status.Split(',').Select(s => Enum.TryParse<DocumentStatus>(s, ignoreCase: true, out var v) ? v : (DocumentStatus?)null).OfType<DocumentStatus>().ToList();
                q = q.Where(d => statuses.Contains(d.Status));
            }
            // 날짜는 이 PC 의 날짜 기준 (DateOnly ➔ 그날 0시 현지 시각)
            if (from is { } fd) { var t = LocalStart(fd); q = q.Where(d => d.ReceivedAt >= t); }
            if (to is { } td) { var t = LocalStart(td.AddDays(1)); q = q.Where(d => d.ReceivedAt < t); }
            var docs = await q.OrderByDescending(d => d.Id).Take(Math.Clamp(take ?? 500, 1, 2000)).ToListAsync();
            var ids = docs.Select(d => d.Id).ToList();
            var latest = await db.Extractions.AsNoTracking().Where(x => ids.Contains(x.DocumentId))
                .GroupBy(x => x.DocumentId).Select(g => g.OrderByDescending(x => x.Id).First()).ToListAsync();
            var byDoc = latest.ToDictionary(x => x.DocumentId);
            return docs.Select(d => Summary(d, byDoc.GetValueOrDefault(d.Id)));
        });

        api.MapGet("/documents/counts", async (IDbContextFactory<DigitizerDb> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var rows = await db.Documents.AsNoTracking().Where(d => d.PurgedAt == null)
                .GroupBy(d => new { d.PackId, d.Status }).Select(g => new { g.Key.PackId, g.Key.Status, Count = g.Count() }).ToListAsync();
            return rows.Select(r => new { r.PackId, Status = r.Status.ToString(), r.Count });
        });

        api.MapGet("/documents/{id:long}", async (long id, IDbContextFactory<DigitizerDb> f, PackCatalog catalog) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (doc is null) return Results.NotFound(new { error = $"문서 #{id} 가 없습니다" });
            var x = await db.Extractions.AsNoTracking().Where(e => e.DocumentId == id).OrderByDescending(e => e.Id).FirstOrDefaultAsync();
            var pack = catalog.Get(doc.PackId);
            var corrections = await db.Corrections.AsNoTracking().Where(c => c.DocumentId == id).Select(c => c.FieldPath).ToListAsync();
            return Results.Ok(new
            {
                Document = Summary(doc, x),
                doc.ReviewNote,
                doc.ReviewedAt,
                Reviewable = ReviewService.Reviewable.Contains(doc.Status) && doc.PurgedAt is null && x is not null && pack is not null,
                Exportable = ReviewService.Exportable(doc, pack),
                HasFile = doc.StoredPath is { } p && File.Exists(p),
                FileKind = FileKind(doc.OriginalName),
                Fields = ReviewService.CurrentFields(doc, x),
                ExtractedFields = x?.Fields is null ? null : JsonNode.Parse(x.Fields),
                Issues = ReviewService.ParseIssues(x?.Issues),
                SourceText = x?.SourceText is null || pack is null ? x?.SourceText : ReviewService.Mask(pack, x.SourceText),
                CorrectedPaths = corrections,
                Extraction = x is null ? null : new { x.Engine, x.Model, x.SourceKind, x.Pages, x.OcrConfidence, x.FinalSource, x.FallbackUsed, x.FallbackReason, x.TextMs, x.LlmMs },
            });
        });

        // 원본 보기: 이미지는 방향 맞춘 JPEG (TIF · BMP 도 브라우저에서 보이게), PDF 는 그대로, DOCX 는 내려받기
        api.MapGet("/documents/{id:long}/file", async (long id, IDbContextFactory<DigitizerDb> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
            if (doc?.StoredPath is not { } path || !File.Exists(path)) return Results.NotFound(new { error = "원본 파일이 없습니다 (보관 기한이 지났거나 옮겨짐)" });
            switch (FileKind(doc.OriginalName))
            {
                case "image":
                    await using (var file = File.OpenRead(path))
                        return Results.File(ImageResizer.ToJpeg(file, 2400), "image/jpeg");
                case "pdf":
                    return Results.File(path, "application/pdf", enableRangeProcessing: true);
                default:
                    return Results.File(path, "application/octet-stream", doc.OriginalName);
            }
        });

        api.MapPost("/documents/{id:long}/check", async (long id, JsonObject fields, ReviewService review) =>
            await Guard(async () => Results.Ok(new { Issues = await review.CheckAsync(id, fields) })));

        api.MapPost("/documents/{id:long}/review", async (long id, ReviewRequest request, ReviewService review) =>
            await Guard(async () =>
            {
                var (doc, issues) = await review.SubmitAsync(id, request);
                return Results.Ok(new { Status = doc.Status.ToString(), doc.StatusReason, Issues = issues });
            }));

        // 화면 업로드 = 감시 폴더와 같은 접수 (같은 대기열 · 중복 확인)
        api.MapPost("/upload", async (HttpRequest request, PackCatalog catalog, DocumentIntake intake, AppPaths paths) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "파일을 골라 주세요" });
            var form = await request.ReadFormAsync();
            if (catalog.Get(form["pack"].ToString()) is not { } pack) return Results.BadRequest(new { error = "문서 종류를 골라 주세요" });
            if (form.Files.Count == 0) return Results.BadRequest(new { error = "파일을 골라 주세요" });
            var results = new List<object>();
            foreach (var file in form.Files)
            {
                var name = Path.GetFileName(file.FileName);
                if (file.Length > MaxUploadBytes)
                {
                    results.Add(new { Name = name, Id = (long?)null, Status = "Failed", StatusReason = $"파일이 너무 큽니다 (최대 {MaxUploadBytes / 1024 / 1024}MB)" });
                    continue;
                }
                // 접수는 파일을 옮기므로 임시 폴더에 원래 이름으로 저장한 뒤 넘김
                var temp = Directory.CreateDirectory(Path.Combine(paths.Queue, $"upload-{Guid.NewGuid():N}")).FullName;
                try
                {
                    var path = Path.Combine(temp, name);
                    await using (var target = File.Create(path)) await file.CopyToAsync(target);
                    var doc = await intake.AcceptAsync(path, pack, "upload");
                    results.Add(new { Name = name, Id = (long?)doc.Id, Status = doc.Status.ToString(), doc.StatusReason });
                }
                finally
                {
                    Directory.Delete(temp, recursive: true);
                }
            }
            return Results.Ok(results);
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes * 10L));

        api.MapGet("/exports/pending", async (ExcelExporter exporter) => await exporter.PendingAsync());

        api.MapGet("/exports", async (IDbContextFactory<DigitizerDb> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var exports = await db.Exports.AsNoTracking().OrderByDescending(e => e.Id).Take(100).ToListAsync();
            return exports.Select(e => new { e.Id, e.PackId, FileName = Path.GetFileName(e.FilePath), e.DocumentCount, e.CreatedAt, Exists = File.Exists(e.FilePath) });
        });

        api.MapPost("/exports", async (ExportRequest request, ExcelExporter exporter) => await Guard(async () =>
        {
            var e = await exporter.ExportAsync(request.PackId, request.IncludeExported);
            return Results.Ok(new { e.Id, FileName = Path.GetFileName(e.FilePath), e.DocumentCount });
        }));

        api.MapGet("/exports/{id:long}/file", async (long id, IDbContextFactory<DigitizerDb> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var e = await db.Exports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return e is null || !File.Exists(e.FilePath)
                ? Results.NotFound(new { error = "내보낸 파일이 없습니다 (옮기거나 지웠을 수 있음)" })
                : Results.File(e.FilePath, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Path.GetFileName(e.FilePath));
        });

        // 종류별 수정률 = 검수한 문서에서 사람이 고친 칸 / 비교한 칸 (운영 중 정확도)
        api.MapGet("/stats/corrections", async (IDbContextFactory<DigitizerDb> f, PackCatalog catalog) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var reviewed = await db.Documents.AsNoTracking()
                .Where(d => d.ReviewedAt != null && d.ReviewedSlots != null && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Rejected))
                .Select(d => new { d.Id, d.PackId, d.Status, Slots = d.ReviewedSlots!.Value }).ToListAsync();
            var ids = reviewed.Select(r => r.Id).ToList();
            var corrections = await db.Corrections.AsNoTracking().Where(c => ids.Contains(c.DocumentId)).Select(c => new { c.DocumentId, c.FieldPath }).ToListAsync();
            var packOf = reviewed.ToDictionary(r => r.Id, r => r.PackId);
            return catalog.Packs.Select(p =>
            {
                var mine = reviewed.Where(r => r.PackId == p.Id && r.Status == DocumentStatus.Approved).ToList();
                var mineIds = mine.Select(r => r.Id).ToHashSet();
                var fixes = corrections.Where(c => mineIds.Contains(c.DocumentId)).ToList();
                var slots = mine.Sum(r => r.Slots);
                return new
                {
                    PackId = p.Id,
                    p.DisplayName,
                    Approved = mine.Count,
                    Rejected = reviewed.Count(r => r.PackId == p.Id && r.Status == DocumentStatus.Rejected),
                    DocumentsCorrected = fixes.Select(c => c.DocumentId).Distinct().Count(),
                    Slots = slots,
                    Corrected = fixes.Count,
                    Rate = slots == 0 ? (double?)null : (double)fixes.Count / slots,
                    // 어느 칸을 자주 고치는지 (목록 순번은 빼고 필드 이름으로 묶음)
                    TopFields = fixes.GroupBy(c => System.Text.RegularExpressions.Regex.Replace(c.FieldPath, @"\[\d+\]", "[]"))
                        .Select(g => new { Field = g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).Take(5),
                };
            });
        });

        api.MapGet("/settings", async (SettingsFile settings, SystemCheck check) => new
        {
            Settings = settings.Current,
            ResolvedDocumentsRoot = settings.Current.ResolvedDocumentsRoot,
            AvailableModels = await check.OllamaModelsAsync(),
            SettingsPath = settings.Path,
        });

        api.MapPut("/settings", (SettingsChange change, SettingsFile settings, PackCatalog catalog, FileRouter router) =>
        {
            var next = settings.Current with
            {
                DocumentsRoot = change.DocumentsRoot?.Trim() ?? settings.Current.DocumentsRoot,
                RetentionDays = change.RetentionDays ?? settings.Current.RetentionDays,
                Model = change.Model?.Trim() ?? settings.Current.Model,
            };
            if (next.DocumentsRoot.Length > 0 && !Path.IsPathFullyQualified(next.DocumentsRoot))
                return Results.BadRequest(new { error = "문서 폴더는 전체 경로로 적어 주세요 (예: D:\\문서 전산화)" });
            try
            {
                settings.Save(next);
                router.Folders.Ensure(catalog.Packs);  // 새 위치에 넣기 폴더 만들기
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Ok(new { Settings = settings.Current, ResolvedDocumentsRoot = settings.Current.ResolvedDocumentsRoot });
        });

        api.MapGet("/status", async (SystemCheck check) => new { Checks = await check.RunAsync() });
    }

    public sealed record ExportRequest(string PackId, bool IncludeExported = false);

    /// <summary>설정 화면에서 바꿀 수 있는 것 (나머지 처리 설정은 측정한 값 그대로, 파일에서만)</summary>
    public sealed record SettingsChange(string? DocumentsRoot, int? RetentionDays, string? Model);

    private static DateTimeOffset LocalStart(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));

    public static string FileKind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "pdf",
        ".docx" => "docx",
        var e when DocumentIntake.ImageExtensions.Contains(e) => "image",
        _ => "other",
    };

    private static object Summary(DocumentRecord d, ExtractionRecord? x) => new
    {
        d.Id,
        d.PackId,
        d.PackVersion,
        d.OriginalName,
        Status = d.Status.ToString(),
        d.StatusReason,
        d.TypeWarning,
        d.Source,
        d.ReceivedAt,
        d.ProcessedAt,
        d.ReviewedAt,
        d.PurgedAt,
        Exported = d.LastExportId is not null,
        ErrorCount = x?.ErrorCount,
        WarningCount = x?.WarningCount,
        FallbackUsed = x?.FallbackUsed,
    };

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ReviewException ex)
        {
            return Results.BadRequest(new { error = ex.Message, issues = ex.Issues });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
