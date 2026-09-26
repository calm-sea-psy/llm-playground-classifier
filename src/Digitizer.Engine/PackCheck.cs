using System.Text.RegularExpressions;
using Digitizer.Engine.Rules;

namespace Digitizer.Engine;

/// <summary>
/// 팩 정의 검사 (팩 관리 화면에서 저장하기 전 · 테스트). 엔진이 읽을 수는 있어도 추출 · 검증이 어긋나는 정의를 막는다
/// (모르는 필드 타입은 스키마에서 문자열로 처리돼 검증이 조용히 빠짐, 이름 중복은 JSON 스키마가 깨짐)
/// </summary>
public static partial class PackCheck
{
    /// <summary>필드 타입과 화면 설명. FieldExtractor.Schema · Validator 가 아는 타입만</summary>
    public static readonly IReadOnlyDictionary<string, string> FieldTypes = new Dictionary<string, string>
    {
        ["text"] = "짧은 문자열. 원문에 있어야 함 (공백 무시)",
        ["longtext"] = "긴 설명. 원문 근거는 핵심 단어로만 확인",
        ["phone"] = "전화번호. 숫자만 비교",
        ["email"] = "이메일",
        ["date"] = "날짜 YYYY-MM-DD",
        ["month"] = "연월 YYYY-MM",
        ["month_or_present"] = "연월 또는 present (재직 중 · 현재)",
        ["time"] = "시각 HH:MM 또는 HH:MM:SS",
        ["amount"] = "금액 (원 단위 정수)",
        ["number"] = "숫자 (소수 가능)",
        ["list"] = "여러 건 (항목 필드를 따로 정의)",
        ["string_list"] = "문자열 여러 개",
        ["text_list"] = "문자열 여러 개 (칸마다 빈 값 가능)",
    };

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    public static partial Regex IdRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRegex();

    /// <summary>문제 목록 (비면 통과)</summary>
    public static List<string> Check(DocumentType type)
    {
        var errors = new List<string>();
        if (!IdRegex().IsMatch(type.Id ?? "")) errors.Add($"id '{type.Id}' 는 영문 소문자로 시작하고 소문자 · 숫자 · _ 만 쓸 수 있습니다");
        if (!VersionRegex().IsMatch(type.Version ?? "")) errors.Add($"version '{type.Version}' 은 1.0.0 형식이어야 합니다");
        if (string.IsNullOrWhiteSpace(type.DisplayName)) errors.Add("표시 이름이 비었습니다");
        if (string.IsNullOrWhiteSpace(type.Prompt)) errors.Add("지시문(prompt.md)이 비었습니다");
        if (type.Fields is not { Count: > 0 }) errors.Add("필드가 하나도 없습니다");
        else CheckFields(type.Fields, "", errors, nested: false);

        if (type.Rules is null) errors.Add("rules 가 없습니다 (규칙이 없으면 빈 목록 [])");
        if (type.Forbidden is null) errors.Add("forbidden 이 없습니다 (수집 금지 항목이 없으면 빈 목록 [])");
        foreach (var rule in type.Rules ?? [])
            if (!RuleRegistry.IsKnown(rule)) errors.Add($"알 수 없는 규칙 {rule}");
        foreach (var dup in (type.Rules ?? []).GroupBy(r => r).Where(g => g.Count() > 1))
            errors.Add($"규칙 {dup.Key} 가 두 번 있습니다");

        foreach (var fb in type.Forbidden ?? [])
        {
            if (string.IsNullOrWhiteSpace(fb.Id) || string.IsNullOrWhiteSpace(fb.Label)) errors.Add("수집 금지 항목의 id · 이름이 비었습니다");
            if (fb.Pattern is { Length: > 0 } p)
            {
                try { _ = new Regex(p); }
                catch (ArgumentException ex) { errors.Add($"수집 금지 {fb.Label}: 패턴 오류 ({ex.Message})"); }
            }
        }
        foreach (var dup in (type.Forbidden ?? []).GroupBy(f => f.Id).Where(g => g.Count() > 1))
            errors.Add($"수집 금지 id {dup.Key} 가 두 번 있습니다");

        // {{$변수}} 는 variables 로 채우므로 없는 변수는 빈 문자열이 됨 ➔ 지시문에 모르는 변수가 있으면 오류
        foreach (var (name, text) in new[] { ("prompt.md", type.Prompt) }.Concat(type.ModelPrompts.Select(p => ($"prompt.{p.Key}.md", p.Value))))
            foreach (var v in Variables(text).Where(v => !type.Variables.ContainsKey(v)))
                errors.Add($"{name}: 변수 {{{{${v}}}}} 의 값이 variables 에 없습니다");
        foreach (var (name, text, allowed) in new[]
        {
            ("user.md", type.UserTemplate, new[] { "input_label", "ocr_text" }),
            ("vlm.user.md", type.VlmUserTemplate, new[] { "input_label", "ocr_text", "issues" }),
        })
        {
            if (text is null) continue;
            foreach (var v in Variables(text).Where(v => !allowed.Contains(v)))
                errors.Add($"{name}: 모르는 변수 {{{{${v}}}}} (쓸 수 있는 변수: {string.Join(", ", allowed)})");
            if (!Variables(text).Contains("ocr_text")) errors.Add($"{name}: 원문 자리 {{{{$ocr_text}}}} 가 없습니다");
        }
        return errors;
    }

    private static void CheckFields(List<FieldDef> fields, string prefix, List<string> errors, bool nested)
    {
        foreach (var f in fields)
        {
            var path = prefix + (string.IsNullOrEmpty(f.Name) ? "(이름 없음)" : f.Name);
            if (!IdRegex().IsMatch(f.Name ?? "")) errors.Add($"필드 이름 {path} 는 영문 소문자로 시작하고 소문자 · 숫자 · _ 만 쓸 수 있습니다");
            if (string.IsNullOrWhiteSpace(f.Label)) errors.Add($"필드 {path} 의 화면 이름이 비었습니다");
            if (!FieldTypes.ContainsKey(f.Type ?? "")) { errors.Add($"필드 {path}: 모르는 타입 {f.Type}"); continue; }
            if (f.Type == "list")
            {
                if (nested) { errors.Add($"필드 {path}: 목록 안에 목록은 쓸 수 없습니다"); continue; }
                if (f.Items is not { Count: > 0 }) { errors.Add($"목록 {path} 에 항목 필드가 없습니다"); continue; }
                CheckFields(f.Items, path + ".", errors, nested: true);
                if (f.Key is not null && f.Items.All(i => i.Name != f.Key)) errors.Add($"목록 {path}: 구분 필드 {f.Key} 가 항목에 없습니다");
                if (f.Ranged && (f.Items.All(i => i.Name != "start") || f.Items.All(i => i.Name != "end")))
                    errors.Add($"목록 {path}: 기간 대조(ranged)는 항목에 start · end 가 있어야 합니다");
            }
            else if (f.Items is { Count: > 0 }) errors.Add($"필드 {path}: 목록이 아닌데 항목 필드가 있습니다");
        }
        foreach (var dup in fields.GroupBy(f => f.Name).Where(g => g.Count() > 1))
            errors.Add($"필드 이름 {prefix}{dup.Key} 가 두 번 있습니다");
    }

    public static IEnumerable<string> Variables(string template) =>
        VariableRegex().Matches(template).Select(m => m.Groups[1].Value).Distinct();

    [GeneratedRegex(@"\{\{\s*\$(\w+)\s*\}\}")]
    private static partial Regex VariableRegex();
}
