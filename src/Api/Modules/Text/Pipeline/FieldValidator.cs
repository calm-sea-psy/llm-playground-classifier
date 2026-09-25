using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Api.Modules.Text.Pipeline;

public enum IssueSeverity
{
    /// <summary>값이 틀렸을 가능성이 높음 ➔ VLM 폴백 대상</summary>
    Error,
    /// <summary>문서 특성상 어긋날 수 있음 (면세 품목, 누락된 품목 줄 등) ➔ 기록만</summary>
    Warning,
}

public sealed record ValidationIssue(string Rule, string? Field, IssueSeverity Severity, string Message);

/// <summary>
/// ValidateFields: LLM 이 아닌 C# 규칙으로 추출 결과를 검증한다.
/// KORIE 확인 결과(1단계): 소계는 공급가액이라 "소계 + 세금 = 합계"가 기준이고, 면세 품목이 섞이면 합계가 더 클 수 있다.
/// </summary>
public static partial class FieldValidator
{
    /// <summary>한 영수증에 1억 원 이상은 오추출로 봄 (0단계 gemma4 가 합계를 5270000000000000 으로 뽑은 사례)</summary>
    private const decimal MaxReceiptAmount = 100_000_000m;

    /// <summary>품목 합이 합계를 이 비율 넘게 초과하면 오류 (할인 줄을 품목에 안 넣은 경우까지는 허용)</summary>
    private const decimal ItemsOverTotalTolerance = 0.10m;

    public static List<ValidationIssue> Validate(string documentType, JsonObject fields) => documentType switch
    {
        DocumentTypes.Receipt => ValidateReceipt(fields),
        DocumentTypes.CommercialInvoice => ValidateInvoice(fields),
        DocumentTypes.InsuranceClaim => ValidateInsuranceClaim(fields),
        _ => [],
    };

    /// <summary>
    /// OCR 이 영수증 수량 1을 7로 자주 읽는다(3·5단계). 금액이 단가와 같으면 수량은 1일 수밖에 없으므로 1로 고친다.
    /// KORIE 3회 실행의 추출 결과 184건에서 "금액 = 단가 & 수량 ≠ 1"의 정답 수량은 전부 1이었다 (오보정 0건).
    /// 고친 값은 경고로 남겨 결과 화면에서 확인할 수 있게 한다.
    /// </summary>
    public static List<ValidationIssue> CorrectQuantities(string documentType, JsonObject fields)
    {
        var fixes = new List<ValidationIssue>();
        if (documentType != DocumentTypes.Receipt)
        {
            return fixes;
        }
        var items = Items(fields);
        for (var i = 0; i < items.Count; i++)
        {
            if (Amount(items[i]["qty"]) is { } q && q != 1
                && Amount(items[i]["unit_price"]) is { } u && Amount(items[i]["amount"]) is { } a && u > 0 && a == u)
            {
                items[i]["qty"] = 1;
                fixes.Add(new("item_qty_corrected", $"items[{i}]", IssueSeverity.Warning,
                    $"품목 {i + 1}: 금액 = 단가({u:N0})라 수량 {q} ➔ 1로 보정 (OCR 이 1을 7로 읽는 경우가 많음)"));
            }
        }
        return fixes;
    }

    /// <summary>
    /// 근거 확인(텍스트 추출 결과만): 영수증의 날짜·시각·합계·사업자번호 숫자가 OCR 텍스트에 실제로 있는지.
    /// OCR 이 그 줄을 깨뜨리면 LLM 이 그럴듯한 값을 지어냄 (IMG00090: 인쇄 23-02-26 20:32 ➔ 추출 2024-01-01 00:00, 형식은 맞아 통과했음).
    /// B_korie 텍스트 결과에서 이 규칙에 걸린 값은 날짜 2/2·시각 7/7·합계 4/4 모두 실제로 틀림 ➔ Error (VLM 폴백으로 이미지에서 다시 확인).
    /// VLM 결과는 이미지를 직접 보므로 적용하지 않는다
    /// </summary>
    public static List<ValidationIssue> CheckGrounded(string documentType, JsonObject fields, string ocrText)
    {
        var issues = new List<ValidationIssue>();
        if (documentType != DocumentTypes.Receipt)
        {
            return issues;
        }
        // 줄마다 숫자만 남김 (+ 다음 줄과 이은 것: 값이 줄바꿈으로 나뉜 경우)
        var lines = ocrText.Split('\n').Select(l => NonDigitRegex().Replace(l, "")).Where(l => l.Length > 0).ToList();
        var haystack = lines.Concat(lines.Zip(lines.Skip(1), (a, b) => a + b)).ToList();
        bool Found(IEnumerable<string> candidates) => candidates.Any(c => haystack.Any(h => h.Contains(c, StringComparison.Ordinal)));
        void Check(string field, string label, IEnumerable<string>? candidates, string value)
        {
            if (candidates is not null && !Found(candidates))
            {
                issues.Add(new("grounded", field, IssueSeverity.Error, $"{label} {value} 이(가) OCR 텍스트에 없습니다 (지어낸 값 의심)"));
            }
        }

        if (Text(fields["date"]) is { } date && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            // 2023-02-26 ➔ 20230226 · 230226 · 2023226 · 23226 (영수증은 23-02-26, 2023.2.26 등으로 인쇄)
            Check("date", "날짜", [$"{d:yyyyMMdd}", $"{d:yyMMdd}", $"{d.Year}{d.Month}{d.Day}", $"{d:yy}{d.Month}{d.Day}"], date);
        }
        if (Text(fields["time"]) is { } time && TimeRegex().IsMatch(time))
        {
            Check("time", "시각", [time[..2] + time[3..5]], time);
        }
        if (Amount(fields["total"]) is { } total && total > 0 && total == decimal.Truncate(total))
        {
            Check("total", "합계", [((long)total).ToString(CultureInfo.InvariantCulture)], $"{total:N0}");
        }
        if (Text(fields["business_no"]) is { } businessNo && NonDigitRegex().Replace(businessNo, "") is { Length: 10 } digits)
        {
            Check("business_no", "사업자등록번호", [digits], businessNo);
        }
        return issues;
    }

    private static List<ValidationIssue> ValidateReceipt(JsonObject f)
    {
        var issues = new List<ValidationIssue>();
        var total = Amount(f["total"]);
        var subtotal = Amount(f["subtotal"]);
        var tax = Amount(f["tax"]);
        var grossTotal = Amount(f["gross_total"]);

        if (total is null)
        {
            issues.Add(new("total_required", "total", IssueSeverity.Error, "합계(total)가 없습니다"));
        }
        foreach (var (name, value) in new[] { ("total", total), ("subtotal", subtotal), ("tax", tax), ("gross_total", grossTotal) })
        {
            if (value is { } v && Math.Abs(v) >= MaxReceiptAmount)
            {
                issues.Add(new("amount_range", name, IssueSeverity.Error, $"{name} 금액이 비정상적으로 큽니다: {v:N0}"));
            }
        }

        var items = Items(f);
        issues.AddRange(CheckItemAmounts(items, tolerance: 0m, IssueSeverity.Error));
        var itemAmounts = items.Select(i => Amount(i["amount"])).ToList();
        if (total is { } t && itemAmounts.Count > 0 && itemAmounts.All(a => a is not null))
        {
            var sum = itemAmounts.Sum(a => a!.Value);
            // 합이 합계보다 크게 넘치면 금액을 지어냈거나(OCR 이 수량 1을 7로 읽고 LLM 이 7×단가로 계산) 줄이 중복된 것.
            // 모자라면 품목 줄 누락·별도 할인일 수 있어 경고만
            if (sum > t * (1 + ItemsOverTotalTolerance))
            {
                issues.Add(new("items_sum", "items", IssueSeverity.Error,
                    $"품목 금액 합 {sum:N0} 이 합계 {t:N0} 보다 {ItemsOverTotalTolerance:P0} 넘게 큽니다 (금액 계산·중복 의심)"));
            }
            else if (sum != t)
            {
                issues.Add(new("items_sum", "items", IssueSeverity.Warning,
                    $"품목 금액 합 {sum:N0} ≠ 합계 {t:N0} (누락된 품목 줄이나 별도 할인일 수 있음)"));
            }
        }

        if (subtotal is { } s && tax is { } x && total is { } tt)
        {
            if (s + x > tt)
            {
                issues.Add(new("subtotal_tax", "subtotal", IssueSeverity.Error,
                    $"소계 {s:N0} + 세금 {x:N0} = {s + x:N0} 이 합계 {tt:N0} 보다 큽니다"));
            }
            else if (s + x < tt)
            {
                issues.Add(new("subtotal_tax", "subtotal", IssueSeverity.Warning,
                    $"소계 {s:N0} + 세금 {x:N0} = {s + x:N0} < 합계 {tt:N0} (면세 품목이 있으면 정상)"));
            }
        }

        // 할인 전 합계 ➔ 할인 ➔ 결제 금액. 영수증마다 표기가 달라(할인을 품목 줄에 안 넣기도 함) 경고만
        if (grossTotal is { } g && total is { } paid)
        {
            var discounts = itemAmounts.Where(a => a is < 0).Sum(a => a!.Value);
            if (g < paid)
            {
                issues.Add(new("gross_total", "gross_total", IssueSeverity.Warning,
                    $"할인 전 합계 {g:N0} 이 결제 금액 {paid:N0} 보다 작습니다"));
            }
            else if (discounts < 0 && g + discounts != paid)
            {
                issues.Add(new("gross_total", "gross_total", IssueSeverity.Warning,
                    $"할인 전 합계 {g:N0} + 할인 {discounts:N0} = {g + discounts:N0} ≠ 결제 금액 {paid:N0}"));
            }
        }

        CheckDate(issues, f, "date", required: false);
        if (Text(f["time"]) is { } time && !TimeRegex().IsMatch(time))
        {
            issues.Add(new("time_format", "time", IssueSeverity.Error, $"시간 형식이 HH:MM(:SS) 가 아닙니다: {time}"));
        }
        if (Text(f["business_no"]) is { } businessNo && !IsValidBusinessNo(businessNo))
        {
            issues.Add(new("business_no", "business_no", IssueSeverity.Error, $"사업자등록번호 체크섬이 맞지 않습니다: {businessNo}"));
        }
        return issues;
    }

    private static List<ValidationIssue> ValidateInvoice(JsonObject f)
    {
        var issues = new List<ValidationIssue>();
        var total = Amount(f["total_amount"]);
        if (total is null)
        {
            issues.Add(new("total_required", "total_amount", IssueSeverity.Error, "합계(total_amount)가 없습니다"));
        }

        // AI Hub 상업송장은 숫자를 무작위로 채운 합성 데이터라 수량×단가·품목 합이 원래 맞지 않음 (예: 2 × $5.64 ≠ $54.83)
        // ➔ 산술 규칙은 경고로만 기록. 실제 송장을 다룰 때는 Error 로 올릴 것
        var items = Items(f);
        issues.AddRange(CheckItemAmounts(items, tolerance: 0.01m, IssueSeverity.Warning));
        var itemAmounts = items.Select(i => Amount(i["amount"])).ToList();
        if (total is { } t && itemAmounts.Count > 0 && itemAmounts.All(a => a is not null))
        {
            var sum = itemAmounts.Sum(a => a!.Value);
            if (Math.Abs(sum - t) > 0.01m)
            {
                issues.Add(new("items_sum", "items", IssueSeverity.Warning, $"품목 금액 합 {sum:N2} ≠ 합계 {t:N2}"));
            }
        }

        CheckDate(issues, f, "invoice_date", required: false);
        if (Text(f["currency"]) is { } currency && !CurrencyCodes.Contains(currency))
        {
            issues.Add(new("currency", "currency", IssueSeverity.Error, $"ISO 4217 통화 코드가 아닙니다: {currency}"));
        }
        return issues;
    }

    private static List<ValidationIssue> ValidateInsuranceClaim(JsonObject f)
    {
        // 합성 데이터라 날짜·번호가 실제 규칙을 따르지 않으므로 형식만 느슨하게 확인
        var issues = new List<ValidationIssue>();
        if (Text(f["insured_name"]) is null)
        {
            issues.Add(new("insured_name_required", "insured_name", IssueSeverity.Error, "피보험자 성명이 없습니다"));
        }
        CheckPattern(issues, f, "resident_no", ResidentNoRegex(), "주민번호 형식(000000-0000000)이 아닙니다");
        CheckPattern(issues, f, "contact_phone", PhoneRegex(), "전화번호 형식이 아닙니다");
        CheckPattern(issues, f, "email", EmailRegex(), "이메일 형식이 아닙니다");
        CheckPattern(issues, f, "account_no", AccountNoRegex(), "계좌번호 형식(숫자·하이픈 10~16자리)이 아닙니다");
        return issues;
    }

    private static IEnumerable<ValidationIssue> CheckItemAmounts(List<JsonObject> items, decimal tolerance, IssueSeverity severity)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var qty = Amount(items[i]["qty"]);
            var unitPrice = Amount(items[i]["unit_price"]);
            var amount = Amount(items[i]["amount"]);
            // 할인·에누리 줄(금액 음수, 단가 0)은 수량×단가 규칙 대상이 아님
            if (qty is { } q && unitPrice is { } u && amount is { } a && q > 0 && u > 0 && a >= 0
                && Math.Abs(q * u - a) > tolerance)
            {
                yield return new("item_amount", $"items[{i}]", severity,
                    $"품목 {i + 1}: 수량 {q} × 단가 {u:N} = {q * u:N} ≠ 금액 {a:N}");
            }
        }
    }

    private static void CheckDate(List<ValidationIssue> issues, JsonObject f, string field, bool required)
    {
        var value = Text(f[field]);
        if (value is null)
        {
            if (required)
            {
                issues.Add(new("date_required", field, IssueSeverity.Error, $"{field} 가 없습니다"));
            }
            return;
        }
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date.Year < 2000 || date > DateOnly.FromDateTime(DateTime.Today).AddYears(1))
        {
            issues.Add(new("date_format", field, IssueSeverity.Error, $"날짜가 YYYY-MM-DD 형식의 유효한 날짜가 아닙니다: {value}"));
        }
    }

    private static void CheckPattern(List<ValidationIssue> issues, JsonObject f, string field, Regex regex, string message)
    {
        if (Text(f[field]) is { } value && !regex.IsMatch(value))
        {
            issues.Add(new($"{field}_format", field, IssueSeverity.Warning, $"{message}: {value}"));
        }
    }

    /// <summary>사업자등록번호 10자리 체크섬 (가중치 1,3,7,1,3,7,1,3,5)</summary>
    public static bool IsValidBusinessNo(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length != 10)
        {
            return false;
        }
        int[] weights = [1, 3, 7, 1, 3, 7, 1, 3, 5];
        var sum = weights.Select((w, i) => w * digits[i]).Sum() + digits[8] * 5 / 10;
        return (10 - sum % 10) % 10 == digits[9];
    }

    /// <summary>숫자 또는 "12,850원" 같은 문자열을 decimal 로 (AmountsAsString 스키마 대응)</summary>
    public static decimal? Amount(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<decimal>(out var d))
        {
            return d;
        }
        // 코드에서 넣은 숫자(예: 수량 보정의 int 1)는 TryGetValue<decimal> 이 false ➔ JSON 표현으로 읽음
        if (value.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return n;
        }
        if (value.TryGetValue<string>(out var s))
        {
            var cleaned = AmountNoiseRegex().Replace(s, "");
            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        return null;
    }

    public static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static List<JsonObject> Items(JsonObject f) =>
        f["items"] is JsonArray array ? array.OfType<JsonObject>().ToList() : [];

    private static readonly HashSet<string> CurrencyCodes = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c => { try { return new RegionInfo(c.Name).ISOCurrencySymbol; } catch (ArgumentException) { return null; } })
        .OfType<string>()
        .ToHashSet();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigitRegex();

    [GeneratedRegex(@"[\s,원₩$]")]
    private static partial Regex AmountNoiseRegex();

    [GeneratedRegex(@"^\d{6}-?\d{7}$")]
    private static partial Regex ResidentNoRegex();

    [GeneratedRegex(@"^[\d\-\s()]{9,15}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^(?=(?:\D*\d){10,16}\D*$)[\d\-\s]+$")]
    private static partial Regex AccountNoRegex();
}
