using System.Text.Json.Nodes;
using ClosedXML.Excel;
using Digitizer.App.Api;
using Digitizer.App.Data;
using Digitizer.App.Export;
using Digitizer.App.Processing;
using Digitizer.App.Review;
using Digitizer.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Digitizer.App.Tests;

/// <summary>4차-exe 3단계: 검수 (다시 검사 · 고친 값 · 승인 · 반려) · 조건부 팩 승인 조건 · 엑셀 내보내기 · 수정률 · 요청 차단</summary>
public sealed class ReviewExportTests
{
    private const string ResumeText = "홍길동\n010-1234-5678\nhong@example.com\n서울시 마포구";

    private static JsonObject Resume() => Samples.ResumeFromReceipt().Also(f =>
    {
        f["name"] = "홍길동";
        f["phone"] = "010-1234-5678";
        f["email"] = "hong@example.com";
        f["address"] = null;
    });

    private static ReviewService Review(CoreHarness h) => h.Services.GetRequiredService<ReviewService>();
    private static ExcelExporter Exporter(CoreHarness h) => h.Services.GetRequiredService<ExcelExporter>();

    // 3-3 · 3-4 검수 · 승인

    [Fact]
    public async Task 조건부_팩은_검증을_통과해도_승인하기_전에는_내보낼_수_없다()
    {
        using var h = new CoreHarness(_ => Resume());
        var doc = await h.AcceptAndProcessAsync("resume", "이력서.pdf", ResumeText);
        Assert.Equal(0, doc.Extractions.Single().ErrorCount);
        Assert.Equal(DocumentStatus.NeedsReview, doc.Status);
        Assert.False(ReviewService.Exportable(doc, h.Pack("resume")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Exporter(h).ExportAsync("resume", includeExported: false));

        // 저장만 해도 승인은 아님
        await Review(h).SubmitAsync(doc.Id, new ReviewRequest(Resume(), ReviewAction.Save));
        Assert.Equal(DocumentStatus.NeedsReview, h.Load(doc.Id).Status);

        var (approved, _) = await Review(h).SubmitAsync(doc.Id, new ReviewRequest(Resume(), ReviewAction.Approve));
        Assert.Equal(DocumentStatus.Approved, approved.Status);
        Assert.True(ReviewService.Exportable(h.Load(doc.Id), h.Pack("resume")));
        // 원본은 확인 필요 ➔ 처리됨
        Assert.Equal(Path.Combine(h.Folders.Root, "처리됨", "이력서", "2026-09-27", "이력서.pdf"), h.Load(doc.Id).StoredPath);
    }

    [Fact]
    public async Task 합격_팩은_검증을_통과하면_승인_없이_내보낼_수_있다()
    {
        using var h = new CoreHarness();
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);
        Assert.Equal(DocumentStatus.Processed, doc.Status);
        Assert.True(ReviewService.Exportable(doc, h.Pack("receipt")));
    }

    [Fact]
    public async Task 검증_문제가_남은_승인은_확인_표시가_있어야_한다()
    {
        using var h = new CoreHarness(_ => Samples.Receipt().Also(f => f["total"] = 9999));
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);
        var still = Samples.Receipt().Also(f => f["total"] = 9999);

        var refused = await Assert.ThrowsAsync<ReviewException>(() => Review(h).SubmitAsync(doc.Id, new ReviewRequest(still, ReviewAction.Approve)));
        Assert.NotEmpty(refused.Issues!);
        Assert.Equal(DocumentStatus.NeedsReview, h.Load(doc.Id).Status);

        var (approved, issues) = await Review(h).SubmitAsync(doc.Id, new ReviewRequest(still, ReviewAction.Approve, AcknowledgeIssues: true));
        Assert.Equal(DocumentStatus.Approved, approved.Status);
        Assert.Contains("확인하고 승인", approved.StatusReason);
        Assert.Contains(issues, i => i.Severity == Digitizer.Engine.Rules.IssueSeverity.Error);
    }

    [Fact]
    public async Task 고친_값은_다시_검사하고_고친_칸만_corrections_에_남는다()
    {
        using var h = new CoreHarness(_ => Samples.Receipt().Also(f => f["total"] = 9999));
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);

        // 화면은 숫자를 글자로 보냄 ("3,000") ➔ 팩 타입대로 정수로
        var fixedFields = Samples.Receipt().Also(f => f["total"] = "3,000");
        Assert.DoesNotContain(await Review(h).CheckAsync(doc.Id, fixedFields), i => i.Severity == Digitizer.Engine.Rules.IssueSeverity.Error);

        var (approved, issues) = await Review(h).SubmitAsync(doc.Id, new ReviewRequest(fixedFields, ReviewAction.Approve));
        Assert.Equal(DocumentStatus.Approved, approved.Status);
        var saved = h.Load(doc.Id);
        Assert.Equal(3000, JsonNode.Parse(saved.ReviewedFields!)!["total"]!.GetValue<long>());
        var c = Assert.Single(saved.Corrections);
        Assert.Equal(("total", "9999", "3000"), (c.FieldPath, c.ExtractedValue, c.CorrectedValue));
        Assert.Equal(14, saved.ReviewedSlots);  // 맨 위 10칸 + 품목 1건 × 4칸

        // 다시 저장하면 corrections 는 마지막 검수 기준으로 교체 (쌓이지 않음)
        await Review(h).SubmitAsync(doc.Id, new ReviewRequest(fixedFields.Also(f => f["payment_method"] = "카드"), ReviewAction.Save));
        Assert.Equal(["payment_method", "total"], h.Load(doc.Id).Corrections.Select(x => x.FieldPath).Order());
    }

    [Fact]
    public async Task 사람이_고친_칸의_원문_근거_오류는_경고로_낮추고_안_고친_칸은_그대로()
    {
        // OCR 이 주소를 잘못 읽음 ➔ 모델은 OCR 대로, 사람은 원본을 보고 고침 (고친 값은 OCR 원문에 없음)
        const string text = "홍길동\n010-1234-5678\nhong@example.com\n서울시 마포구 월드컵로 1O";
        // 모델이 이메일을 지어냄 (원문에 없음) ➔ 처리 때 오류
        JsonObject Extracted() => Resume().Also(f => { f["address"] = "서울시 마포구 월드컵로 1O"; f["email"] = "kim@example.com"; });
        using var h = new CoreHarness(_ => Extracted());
        var doc = await h.AcceptAndProcessAsync("resume", "이력서.pdf", text);
        Assert.Contains(ReviewService.ParseIssues(doc.Extractions.Single().Issues), x => x.Field == "email" && x.Rule == "grounded");

        // 주소만 사람이 고침: 주소는 경고로, 손대지 않은 이메일은 오류 그대로 (승인하려면 확인 표시 필요)
        var issues = await Review(h).CheckAsync(doc.Id, Extracted().Also(f => f["address"] = "서울시 마포구 월드컵로 10"));
        var address = Assert.Single(issues, x => x.Field == "address");
        Assert.Equal((ReviewService.HumanGrounded, Digitizer.Engine.Rules.IssueSeverity.Warning), (address.Rule, address.Severity));
        var email = Assert.Single(issues, x => x.Field == "email");
        Assert.Equal(("grounded", Digitizer.Engine.Rules.IssueSeverity.Error), (email.Rule, email.Severity));
    }

    [Fact]
    public async Task 목록_항목을_추가하거나_지우면_그_항목의_칸이_모두_고친_칸이다()
    {
        using var h = new CoreHarness();
        var doc = await h.AcceptAndProcessAsync("receipt", "영수증.pdf", Samples.ReceiptText);
        var added = Samples.Receipt();
        ((JsonArray)added["items"]!).Add(new JsonObject { ["name"] = "라면", ["qty"] = 1, ["unit_price"] = 4000, ["amount"] = 4000 });

        await Review(h).SubmitAsync(doc.Id, new ReviewRequest(added, ReviewAction.Save));

        Assert.Equal(["items[1].amount", "items[1].name", "items[1].qty", "items[1].unit_price"],
            h.Load(doc.Id).Corrections.Select(c => c.FieldPath).Order());
    }

    [Fact]
    public async Task 반려는_사유가_필요하고_원본을_실패_폴더로_옮긴다()
    {
        using var h = new CoreHarness(_ => Samples.ResumeFromReceipt());
        var doc = await h.AcceptAndProcessAsync("resume", "영수증인데.pdf", Samples.ReceiptText);

        await Assert.ThrowsAsync<ReviewException>(() => Review(h).SubmitAsync(doc.Id, new ReviewRequest(Samples.ResumeFromReceipt(), ReviewAction.Reject)));
        var (rejected, _) = await Review(h).SubmitAsync(doc.Id, new ReviewRequest(Samples.ResumeFromReceipt(), ReviewAction.Reject, Note: "영수증임"));

        Assert.Equal(DocumentStatus.Rejected, rejected.Status);
        var saved = h.Load(doc.Id);
        Assert.Equal(Path.Combine(h.Folders.Failed, "영수증인데.pdf"), saved.StoredPath);
        Assert.Contains("반려: 영수증임", File.ReadAllText(saved.StoredPath + FileRouter.ReasonSuffix));
        Assert.False(ReviewService.Exportable(saved, h.Pack("resume")));
    }

    [Fact]
    public async Task 실패_대기_중인_문서는_검수할_수_없다()
    {
        using var h = new CoreHarness();
        var failed = await h.AcceptAndProcessAsync("receipt", "a.pdf", "!fail");
        var queued = await h.Intake.AcceptAsync(h.Drop("receipt", "b.pdf", Samples.ReceiptText), h.Pack("receipt"), "watch");

        await Assert.ThrowsAsync<ReviewException>(() => Review(h).SubmitAsync(failed.Id, new ReviewRequest(Samples.Receipt(), ReviewAction.Approve)));
        await Assert.ThrowsAsync<ReviewException>(() => Review(h).SubmitAsync(queued.Id, new ReviewRequest(Samples.Receipt(), ReviewAction.Approve)));
    }

    [Fact]
    public void 원문의_수집_금지_값은_검수_화면에서_가린다()
    {
        using var h = new CoreHarness();
        var masked = ReviewService.Mask(h.Pack("resume"), "홍길동 주민등록번호 900101-1234567 서울");
        Assert.DoesNotContain("1234567", masked);
        Assert.Contains("홍길동", masked);
        Assert.Contains("●", masked);
    }

    // 3-6 엑셀

    [Fact]
    public async Task 엑셀은_팩_시트_구성대로_검수값으로_만들고_내보낸_건은_다시_넣지_않는다()
    {
        using var h = new CoreHarness(_ => Samples.Receipt().Also(f => f["total"] = 9999));
        var reviewed = await h.AcceptAndProcessAsync("receipt", "고침.pdf", Samples.ReceiptText);
        await Review(h).SubmitAsync(reviewed.Id, new ReviewRequest(Samples.Receipt(), ReviewAction.Approve));
        var pending = await h.AcceptAndProcessAsync("receipt", "확인 안 함.pdf", Samples.ReceiptText + "\n2");  // 검증 문제 ➔ 확인 필요
        Assert.Equal(DocumentStatus.NeedsReview, pending.Status);

        var export = await Exporter(h).ExportAsync("receipt", includeExported: false);

        Assert.Equal(1, export.DocumentCount);
        Assert.StartsWith(Path.Combine(h.Folders.Root, "내보내기", "영수증_"), export.FilePath);
        using (var book = new XLWorkbook(export.FilePath))
        {
            Assert.Equal(["영수증", "품목"], book.Worksheets.Select(w => w.Name));
            var main = book.Worksheet("영수증");
            Assert.Equal("문서 번호", main.Cell(1, 1).GetString());
            var totalCol = main.Row(1).CellsUsed().First(c => c.GetString() == "결제 금액").Address.ColumnNumber;
            Assert.Equal(3000, main.Cell(2, totalCol).GetDouble());  // 추출값 9999 가 아니라 검수값
            Assert.Equal("123-45-67891", main.Cell(2, main.Row(1).CellsUsed().First(c => c.GetString() == "사업자등록번호").Address.ColumnNumber).GetString());
            Assert.True(main.Cell(3, 1).IsEmpty());
            var items = book.Worksheet("품목");
            Assert.Equal((reviewed.Id, "김밥"), ((long)items.Cell(2, 1).GetDouble(), items.Cell(2, 4).GetString()));
        }

        Assert.NotNull(h.Load(reviewed.Id).LastExportId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Exporter(h).ExportAsync("receipt", includeExported: false));
        Assert.Equal(1, (await Exporter(h).ExportAsync("receipt", includeExported: true)).DocumentCount);
        var p = Assert.Single(await Exporter(h).PendingAsync(), x => x.PackId == "receipt");
        Assert.Equal((0, 1), (p.NotExported, p.Exported));
    }

    [Fact]
    public void 이력서_엑셀은_인적사항과_목록마다_시트()
    {
        using var h = new CoreHarness();
        Assert.Equal(["인적사항", "학력", "경력", "자격증", "어학"], ExcelExporter.Sheets(h.Pack("resume")).Select(s => s.Name));
    }

    [Fact]
    public void 엑셀_시트_정의가_틀리면_팩_검사에서_걸린다()
    {
        using var h = new CoreHarness();
        var broken = h.Pack("resume") with
        {
            Excel = new ExcelDef([new ExcelSheet("x", Fields: ["education"]), new ExcelSheet("y", List: "name"), new ExcelSheet("z")]),
        };
        var errors = PackCheck.Check(broken);
        Assert.Contains(errors, e => e.Contains("education") && e.Contains("맨 위 필드"));
        Assert.Contains(errors, e => e.Contains("name") && e.Contains("목록"));
        Assert.Contains(errors, e => e.Contains("하나만"));
        Assert.Empty(PackCheck.Check(h.Pack("resume")));
    }

    // 요청 차단 (LocalOnly)

    [Theory]
    [InlineData("127.0.0.1:5310", "GET", false, 200)]
    [InlineData("localhost:5310", "POST", true, 200)]
    [InlineData("127.0.0.1:5310", "POST", false, 403)]   // 다른 사이트의 폼 전송 (헤더를 붙일 수 없음)
    [InlineData("evil.example:5310", "GET", false, 403)]  // DNS 리바인딩
    public async Task 이_PC_화면에서_온_요청만_받는다(string host, string method, bool header, int expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Method = method;
        context.Request.Path = "/api/upload";
        if (header) context.Request.Headers[LocalOnly.Header] = "1";
        context.Response.Body = new MemoryStream();
        var reached = false;

        await new LocalOnly(_ => { reached = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.Equal(expected == 200, reached);
        Assert.Equal(expected, context.Response.StatusCode);
    }
}
