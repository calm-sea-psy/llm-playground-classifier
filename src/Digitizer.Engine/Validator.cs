using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Digitizer.Engine.Rules;

namespace Digitizer.Engine;

/// <summary>
/// 공통 검증 (문서 종류 정의의 필드 타입에서 자동). 종류별 코드 없음. 문제 타입은 Rules.ValidationIssue 하나로 (Field = "career[2].start" 같은 경로, Error 가 있으면 사람 확인)
/// - grounded: 값이 원문에 있는지 (형식이 맞는 값을 지어내는 것을 잡음, 영수증 FieldValidator.CheckGrounded 와 같은 방식)
/// - format: 전화 · 이메일 · 날짜 형식
/// - date_order: 목록 항목의 시작 ≤ 끝, 시작 = 끝이면 한쪽 날짜를 복사한 것으로 의심
/// - list_count: 원문의 기간 표기 수 &gt; 추출된 기간 항목 수 ➔ 빠뜨린 항목 의심 (코드 대조는 지어낸 값만 잡고 빠뜨린 값은 못 잡으므로)
/// - forbidden: 수집 금지 값이 추출 결과에 있으면 오류, 원문에만 있으면 가림 경고
/// </summary>
public static partial class Validator
{
    /// <param name="fromImage">이미지를 직접 본 결과(VLM 폴백)면 원문 근거 확인을 건너뜀</param>
    public static List<ValidationIssue> Validate(DocumentType type, JsonObject fields, string source, bool fromImage = false)
    {
        var issues = new List<ValidationIssue>();
        // 1) 보정 규칙 (값을 고치고 경고) ➔ 2) 공통 검사 ➔ 3) 등록된 규칙. 기존 영수증 파이프라인 순서와 같음
        foreach (var rule in type.Rules.Where(RuleRegistry.Fixers.ContainsKey))
            issues.AddRange(RuleRegistry.Fixers[rule](fields));
        if (type.GenericChecks) issues.AddRange(Generic(type, fields, source, fromImage));
        foreach (var rule in type.Rules.Where(RuleRegistry.Checks.ContainsKey))
            issues.AddRange(RuleRegistry.Checks[rule](fields, source, fromImage));

        var output = fields.ToJsonString();
        foreach (var fb in type.Forbidden.Where(f => f.Pattern is not null))
        {
            var re = new Regex(fb.Pattern!);
            if (re.IsMatch(output)) issues.Add(new("forbidden", "", IssueSeverity.Error, $"수집 금지 값이 추출 결과에 있습니다: {fb.Label}"));
            else if (re.IsMatch(source)) issues.Add(new("forbidden_in_source", "", IssueSeverity.Warning, $"원문에 {fb.Label} 가 있습니다 (가림 필요)"));
        }
        return issues;
    }

    private static List<ValidationIssue> Generic(DocumentType type, JsonObject fields, string source, bool fromImage)
    {
        var issues = new List<ValidationIssue>();
        var hay = fromImage ? null : new Haystack(source);

        foreach (var f in type.Fields)
        {
            var node = fields[f.Name];
            if (f.Type == "list")
            {
                var items = node as JsonArray ?? [];
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is not JsonObject item) continue;
                    foreach (var sub in f.Items ?? [])
                        CheckValue(sub, item[sub.Name], $"{f.Name}[{i}].{sub.Name}", hay, issues);
                    if (type.Rules.Contains("date_order") && Text(item["start"]) is { } s && Text(item["end"]) is { } e && e != "present"
                        && string.CompareOrdinal(s, e) > 0)
                        issues.Add(new("date_order", $"{f.Name}[{i}]", IssueSeverity.Error, $"{f.Label} 시작 {s} 이 끝 {e} 보다 늦습니다"));
                    // v0.1: 졸업 연월만 있는데 입학에도 같은 값을 복사 (값은 원문에 있어 근거 확인으로는 못 잡음)
                    if (type.Rules.Contains("date_order") && Text(item["start"]) is { } s2 && s2 == Text(item["end"]))
                        issues.Add(new("date_same", $"{f.Name}[{i}]", IssueSeverity.Error, $"{f.Label} 시작과 끝이 같습니다 ({s2}): 한쪽 날짜를 복사했을 수 있음"));
                }
            }
            else if (f.Type == "string_list")
            {
                var items = node as JsonArray ?? [];
                for (var i = 0; i < items.Count; i++)
                    CheckValue(f with { Type = "text" }, items[i], $"{f.Name}[{i}]", hay, issues);
            }
            else
            {
                CheckValue(f, node, f.Name, hay, issues);
            }
        }

        if (type.Rules.Contains("list_count"))
        {
            var ranges = RangeRegex().Matches(source).Count;
            var extracted = type.Fields.Where(f => f.Type == "list" && f.Ranged)
                .Sum(f => (fields[f.Name] as JsonArray ?? []).Count(i => Text(i?["start"]) is not null && Text(i?["end"]) is not null));
            if (ranges > extracted)
                issues.Add(new("list_count", "", IssueSeverity.Error, $"원문의 기간 표기 {ranges}개 > 추출된 기간 항목 {extracted}개 (빠뜨린 항목 의심)"));
        }
        return issues;
    }

    private static void CheckValue(FieldDef f, JsonNode? node, string path, Haystack? hay, List<ValidationIssue> issues)
    {
        if (f.Type is "amount" or "number")
        {
            // 숫자 칸: 형식은 스키마가 강제, 원문 근거는 금액만 (수량은 OCR 이 1을 7로 읽는 등 보정 규칙이 따로 있음)
            if (f.Type == "amount" && hay is not null && FieldValidator.Amount(node) is { } amount && amount != 0
                && !hay.HasDigits(decimal.Truncate(Math.Abs(amount)).ToString(CultureInfo.InvariantCulture)))
                issues.Add(Ungrounded(f, path, amount.ToString(CultureInfo.InvariantCulture)));
            return;
        }
        var value = Text(node);
        if (value is null)
        {
            if (f.Required) issues.Add(new("required", path, IssueSeverity.Error, $"{f.Label} 이 없습니다"));
            return;
        }
        switch (f.Type)
        {
            case "time":
                if (!TimeRegex().IsMatch(value)) issues.Add(new("format", path, IssueSeverity.Error, $"시각 형식(HH:MM)이 아닙니다: {value}"));
                else if (hay is not null && !hay.HasDigits(value[..2] + value[3..5])) issues.Add(Ungrounded(f, path, value));
                break;
            case "phone":
                var digits = Digits(value);
                if (digits.Length is < 9 or > 11 || digits[0] != '0') issues.Add(new("format", path, IssueSeverity.Error, $"전화번호 형식이 아닙니다: {value}"));
                else if (hay is not null && !hay.HasDigits(digits)) issues.Add(Ungrounded(f, path, value));
                break;
            case "email":
                if (!EmailRegex().IsMatch(value)) issues.Add(new("format", path, IssueSeverity.Error, $"이메일 형식이 아닙니다: {value}"));
                else if (hay is not null && !hay.HasText(value)) issues.Add(Ungrounded(f, path, value));
                break;
            case "date":
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    issues.Add(new("format", path, IssueSeverity.Error, $"날짜 형식(YYYY-MM-DD)이 아닙니다: {value}"));
                else if (hay is not null && !hay.HasAnyDigits([$"{d:yyyyMMdd}", $"{d.Year}{d.Month}{d.Day}", $"{d:yyMMdd}"])) issues.Add(Ungrounded(f, path, value));
                break;
            case "month" or "month_or_present":
                if (value == "present")
                {
                    if (f.Type != "month_or_present") issues.Add(new("format", path, IssueSeverity.Error, $"{f.Label} 은 연월이어야 합니다"));
                    else if (hay is not null && !PresentRegex().IsMatch(hay.Source)) issues.Add(Ungrounded(f, path, "재직 중/현재"));
                }
                else if (!MonthRegex().IsMatch(value))
                    issues.Add(new("format", path, IssueSeverity.Error, $"연월 형식(YYYY-MM)이 아닙니다: {value}"));
                else if (hay is not null && !hay.HasAnyDigits([value[..4] + value[5..7], value[..4] + int.Parse(value[5..7])])) issues.Add(Ungrounded(f, path, value));
                break;
            case "longtext":
                var words = WordRegex().Matches(value).Select(m => m.Value).Where(w => w.Length >= 2).ToList();
                if (hay is not null && words.Count > 0 && words.Count(hay.HasText) * 2 < words.Count) issues.Add(Ungrounded(f, path, value));
                break;
            default:
                if (hay is not null && !hay.HasText(value)) issues.Add(Ungrounded(f, path, value));
                break;
        }
    }

    private static ValidationIssue Ungrounded(FieldDef f, string path, string value) =>
        new("grounded", path, IssueSeverity.Error, $"{f.Label} '{value}' 이(가) 원문에 없습니다 (지어낸 값 의심)");

    public static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    /// <summary>원문 대조용: 공백 · 기호를 뺀 문자열, 줄마다 숫자만 (+ 다음 줄과 이은 것: 값이 줄바꿈으로 나뉜 경우)</summary>
    private sealed class Haystack
    {
        public string Source { get; }
        private readonly string _compact;
        private readonly List<string> _digitLines;

        public Haystack(string source)
        {
            Source = source;
            _compact = Compact(source);
            var lines = source.Split('\n').Select(Digits).Where(l => l.Length > 0).ToList();
            _digitLines = [.. lines, .. lines.Zip(lines.Skip(1), (a, b) => a + b)];
        }

        public bool HasText(string value) => Compact(value) is { Length: > 0 } c && _compact.Contains(c, StringComparison.OrdinalIgnoreCase);
        public bool HasDigits(string digits) => _digitLines.Any(l => l.Contains(digits, StringComparison.Ordinal));
        public bool HasAnyDigits(IEnumerable<string> candidates) => candidates.Any(HasDigits);
        private static string Compact(string s) => NonWordRegex().Replace(s, "");
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailRegex();
    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")] private static partial Regex MonthRegex();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?$")] private static partial Regex TimeRegex();
    [GeneratedRegex(@"현재|재직\s*중|재직중|present", RegexOptions.IgnoreCase)] private static partial Regex PresentRegex();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex WordRegex();
    [GeneratedRegex(@"[^\p{L}\p{N}@.]")] private static partial Regex NonWordRegex();

    // 기간 표기: 2019.03 ~ 2022.08 · 2019년 3월 ~ 재직 중 · 2019-03 ~ 현재
    [GeneratedRegex(@"(19|20)\d{2}\s*[.\-/년]\s*\d{1,2}\s*월?\s*[~\-–]\s*(?:(19|20)\d{2}\s*[.\-/년]\s*\d{1,2}\s*월?|현재|재직\s*중)")]
    private static partial Regex RangeRegex();
}
