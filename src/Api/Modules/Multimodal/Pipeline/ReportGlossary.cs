using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Shared.Prompts;

namespace Api.Modules.Multimodal.Pipeline;

/// <summary>
/// 소견서 요약용 용어집 (2차-6, 가장 단순한 RAG): 소견서에 나온 용어의 정의만 키워드(정규식)로 찾아 system 프롬프트에 붙인다.
/// 내용은 PromptStore 의 "multimodal/summarize.glossary" (UI 에서 적용한 버전, 없으면 Prompts/summarize.glossary.json).
/// eval/summary_variants.py 도 같은 파일을 씀
/// </summary>
public sealed class ReportGlossary(PromptStore store, PromptUsage usage)
{
    public const string Name = "summarize.glossary";

    /// <summary>작업마다 새로 만들어지므로(scoped) 해석 결과는 정적으로 공유</summary>
    private static (string Content, List<(Regex Pattern, string Text)> Entries)? parsed;

    /// <summary>소견서에 키워드가 있는 정의 (용어집 순서 유지)</summary>
    public IReadOnlyList<string> Retrieve(string report) =>
        Entries().Where(e => e.Pattern.IsMatch(report)).Select(e => e.Text).ToList();

    /// <summary>system 프롬프트 뒤에 붙일 블록 (찾은 정의가 없으면 빈 문자열)</summary>
    public static string Block(IReadOnlyList<string> hits) =>
        hits.Count == 0 ? "" : "\n\n참고 용어 정의 (소견서에 나온 용어만 검색해 붙임)\n" + string.Join("\n", hits.Select(h => $"- {h}"));

    /// <summary>용어집 내용이 바뀌었을 때만 다시 해석</summary>
    private List<(Regex Pattern, string Text)> Entries()
    {
        var (content, version) = store.Resolve(MultimodalModule.ModuleKey, Name, PromptStore.Json);
        if (content is null)
        {
            throw new FileNotFoundException($"용어집 '{Name}.json' 이 없습니다");
        }
        usage.Record(MultimodalModule.ModuleKey, Name, version, content);
        var current = parsed;
        if (current is { } c && ReferenceEquals(c.Content, content))
        {
            return c.Entries;
        }
        using var doc = JsonDocument.Parse(content);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => (new Regex(e.GetProperty("pattern").GetString()!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                e.GetProperty("text").GetString()!))
            .ToList();
        parsed = (content, entries);
        return entries;
    }
}
