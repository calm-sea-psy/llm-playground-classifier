using System.Text.Json.Nodes;
using Api.Modules.Text.Pipeline;

namespace Api.Tests;

/// <summary>LLM 추출 결과를 검증하는 C# 규칙 (영수증·상업송장·보험 청구서)</summary>
public class FieldValidatorTests
{
    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static List<ValidationIssue> Receipt(string json) => FieldValidator.Validate(DocumentTypes.Receipt, Json(json));

    private static IEnumerable<string> Errors(List<ValidationIssue> issues) =>
        issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Rule);

    [Fact]
    public void 정상_영수증은_오류가_없다()
    {
        var issues = Receipt("""
            {
              "total": 11000, "subtotal": 10000, "tax": 1000, "date": "2024-05-01", "time": "13:05:22",
              "business_no": "124-81-00998",
              "items": [ { "name": "A", "qty": 2, "unit_price": 3000, "amount": 6000 },
                         { "name": "B", "qty": 1, "unit_price": 5000, "amount": 5000 } ]
            }
            """);
        Assert.Empty(issues);
    }

    [Fact]
    public void 합계가_없으면_오류()
    {
        Assert.Contains("total_required", Errors(Receipt("""{ "items": [] }""")));
    }

    [Fact]
    public void 수량x단가가_금액과_다르면_오류()
    {
        var issues = Receipt("""{ "total": 7000, "items": [ { "qty": 7, "unit_price": 1000, "amount": 1000 } ] }""");
        Assert.Contains("item_amount", Errors(issues));
    }

    [Fact]
    public void 할인_줄은_수량x단가_규칙에서_제외()
    {
        var issues = Receipt("""{ "total": 9000, "items": [ { "qty": 1, "unit_price": 10000, "amount": 10000 }, { "qty": 1, "unit_price": 0, "amount": -1000 } ] }""");
        Assert.DoesNotContain("item_amount", Errors(issues));
        Assert.DoesNotContain("items_sum", issues.Select(i => i.Rule));
    }

    [Fact]
    public void 품목_합이_합계를_10퍼센트_넘게_초과하면_오류_모자라면_경고()
    {
        var over = Receipt("""{ "total": 10000, "items": [ { "amount": 7000 }, { "amount": 7000 } ] }""");
        Assert.Contains("items_sum", Errors(over));

        var under = Receipt("""{ "total": 10000, "items": [ { "amount": 7000 } ] }""");
        var issue = Assert.Single(under, i => i.Rule == "items_sum");
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void 소계_더하기_세금이_합계보다_크면_오류_작으면_면세로_보고_경고()
    {
        Assert.Contains("subtotal_tax", Errors(Receipt("""{ "total": 10000, "subtotal": 10000, "tax": 1000 }""")));

        var taxFree = Receipt("""{ "total": 12000, "subtotal": 10000, "tax": 1000 }""");
        Assert.Equal(IssueSeverity.Warning, Assert.Single(taxFree, i => i.Rule == "subtotal_tax").Severity);
    }

    [Fact]
    public void 비정상적으로_큰_금액은_오류()
    {
        // 0단계에서 gemma4 가 합계를 5,270조 원으로 뽑은 사례
        Assert.Contains("amount_range", Errors(Receipt("""{ "total": 5270000000000000 }""")));
    }

    [Theory]
    [InlineData("2024-13-01")]
    [InlineData("24-05-01")]
    [InlineData("1999-12-31")]
    public void 날짜_형식이나_범위가_틀리면_오류(string date)
    {
        Assert.Contains("date_format", Errors(Receipt($$"""{ "total": 1000, "date": "{{date}}" }""")));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("1305")]
    public void 시간_형식이_틀리면_오류(string time)
    {
        Assert.Contains("time_format", Errors(Receipt($$"""{ "total": 1000, "time": "{{time}}" }""")));
    }

    [Theory]
    [InlineData("124-81-00998", true)]
    [InlineData("1248100998", true)]
    [InlineData("124-81-00999", false)]
    [InlineData("124-81-009", false)]
    public void 사업자등록번호_체크섬(string value, bool valid)
    {
        Assert.Equal(valid, FieldValidator.IsValidBusinessNo(value));
    }

    [Fact]
    public void 할인_전_합계가_결제_금액보다_작으면_경고()
    {
        var issues = Receipt("""{ "total": 10000, "gross_total": 9000 }""");
        Assert.Equal(IssueSeverity.Warning, Assert.Single(issues, i => i.Rule == "gross_total").Severity);
    }

    [Fact]
    public void 금액이_단가와_같으면_수량을_1로_보정()
    {
        // PaddleOCR 이 수량 1을 7로 읽는 경우 (금액 = 단가면 수량은 1일 수밖에 없음)
        var fields = Json("""{ "items": [ { "qty": 7, "unit_price": 9980, "amount": 9980 }, { "qty": 2, "unit_price": 1000, "amount": 2000 } ] }""");
        var fixes = FieldValidator.CorrectQuantities(DocumentTypes.Receipt, fields);

        var fix = Assert.Single(fixes);
        Assert.Equal("items[0]", fix.Field);
        Assert.Equal(1m, FieldValidator.Amount(fields["items"]![0]!["qty"]));
        Assert.Equal(2m, FieldValidator.Amount(fields["items"]![1]!["qty"]));
    }

    [Fact]
    public void 수량_보정은_영수증에만()
    {
        var fields = Json("""{ "items": [ { "qty": 7, "unit_price": 10, "amount": 10 } ] }""");
        Assert.Empty(FieldValidator.CorrectQuantities(DocumentTypes.CommercialInvoice, fields));
    }

    [Theory]
    [InlineData("12,850원", 12850)]
    [InlineData("₩ 1,000", 1000)]
    [InlineData("$54.83", 54.83)]
    public void 금액_문자열을_숫자로(string text, decimal expected)
    {
        Assert.Equal(expected, FieldValidator.Amount(JsonValue.Create(text)));
    }

    [Fact]
    public void 송장_통화_코드와_합성_데이터용_산술_경고()
    {
        var issues = FieldValidator.Validate(DocumentTypes.CommercialInvoice, Json("""
            { "total_amount": 100, "currency": "XYZ", "items": [ { "qty": 2, "unit_price": 5.64, "amount": 54.83 } ] }
            """));
        Assert.Contains("currency", Errors(issues));
        // AI Hub 상업송장은 합성 데이터라 산술 규칙은 경고로만
        Assert.All(issues.Where(i => i.Rule is "item_amount" or "items_sum"), i => Assert.Equal(IssueSeverity.Warning, i.Severity));
    }

    [Fact]
    public void 보험_청구서는_피보험자가_필수이고_형식은_경고()
    {
        var issues = FieldValidator.Validate(DocumentTypes.InsuranceClaim, Json("""{ "resident_no": "12345", "email": "abc" }"""));
        Assert.Contains("insured_name_required", Errors(issues));
        Assert.Contains(issues, i => i.Rule == "resident_no_format" && i.Severity == IssueSeverity.Warning);
        Assert.Contains(issues, i => i.Rule == "email_format" && i.Severity == IssueSeverity.Warning);
    }
}
