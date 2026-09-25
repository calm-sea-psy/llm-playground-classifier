using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Api.Shared.Prompts;

/// <summary>작업 1건이 쓴 프롬프트 1개: 적용 버전(null = 파일 기본값) + 내용 해시(앞 8자리, 파일 기본값이 바뀐 것도 구분)</summary>
public sealed record PromptUse(int? Version, string Hash);

/// <summary>실험 안에서 같은 프롬프트가 서로 다른 내용으로 쓰인 경우 (예: 중간에 v2 적용)</summary>
public sealed record PromptMix(string Prompt, IReadOnlyList<string> Versions);

/// <summary>
/// 작업 범위(scoped): 이 작업을 처리하며 렌더링한 프롬프트를 모은다 ➔ JobWorker 가 끝날 때 jobs.prompts 에 저장.
/// 키 = "{모듈}/{이름}" (모델 전용이면 그 이름, 예: "text/extract.receipt.qwen3-vl")
/// </summary>
public sealed class PromptUsage
{
    private readonly Dictionary<string, PromptUse> used = new();
    private readonly Lock gate = new();

    public void Record(string module, string name, int? version, string content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..8];
        lock (gate)
        {
            used[$"{module}/{name}"] = new PromptUse(version, hash);
        }
    }

    /// <summary>jobs.prompts 에 저장할 JSON (쓴 프롬프트가 없으면 null)</summary>
    public string? ToJson()
    {
        lock (gate)
        {
            return used.Count == 0
                ? null
                : JsonSerializer.Serialize(used.OrderBy(u => u.Key, StringComparer.Ordinal).ToDictionary(), Options);
        }
    }

    public static Dictionary<string, PromptUse> Parse(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<Dictionary<string, PromptUse>>(json, Options) ?? [];

    /// <summary>"v2 (ab12cd34)" 또는 "기본값 (ab12cd34)"</summary>
    public static string Label(PromptUse use) => $"{(use.Version is { } v ? $"v{v}" : "기본값")} ({use.Hash})";

    /// <summary>여러 작업(실험 한 개)에서 같은 프롬프트가 둘 이상의 내용으로 쓰였는지</summary>
    public static List<PromptMix> Mixed(IEnumerable<string?> jobPrompts) =>
        jobPrompts.SelectMany(p => Parse(p))
            .GroupBy(p => p.Key)
            .Select(g => new PromptMix(g.Key, g.Select(x => x.Value).Distinct().Select(Label).Order(StringComparer.Ordinal).ToList()))
            .Where(m => m.Versions.Count > 1)
            .OrderBy(m => m.Prompt, StringComparer.Ordinal)
            .ToList();

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
