using Api.Modules;

namespace Api.Shared.Features;

public sealed class FeatureToggle
{
    public bool Enabled { get; set; }
    public bool ShowInUi { get; set; }
}

public sealed record FeatureState(string Key, string DisplayName, bool Implemented, bool Enabled, bool ShowInUi);

/// <summary>Features 설정 + 구현된 모듈 목록 ➔ 실제로 켤 모듈과 UI 노출 여부를 결정한다.</summary>
public sealed class FeatureRegistry
{
    public required IReadOnlyList<FeatureState> Features { get; init; }
    public required IReadOnlyList<IPipelineModule> EnabledModules { get; init; }

    public static FeatureRegistry Build(IConfiguration configuration, IReadOnlyList<IPipelineModule> catalog)
    {
        var toggles = new Dictionary<string, FeatureToggle>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in configuration.GetSection("Features").GetChildren())
        {
            toggles[section.Key] = section.Get<FeatureToggle>() ?? new FeatureToggle();
        }
        var modules = catalog.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

        // 설정에서 켠 모듈 + 그 모듈이 의존하는 모듈 (UI 노출과 무관하게 로드)
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Enable(string key, string requiredBy)
        {
            if (!modules.ContainsKey(key))
            {
                throw new InvalidOperationException($"Features:{requiredBy} 에 필요한 '{key}' 모듈이 아직 구현되지 않았습니다");
            }
            if (!enabled.Add(key))
            {
                return;
            }
            foreach (var dependency in modules[key].DependsOn)
            {
                Enable(dependency, key);
            }
        }
        foreach (var (key, toggle) in toggles)
        {
            if (toggle.Enabled)
            {
                Enable(key, key);
            }
        }

        // 메뉴 순서: 구현된 모듈(catalog 순) ➔ 설정에만 있는 미구현 모듈
        var keys = catalog.Select(m => m.Key).Union(toggles.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(k => k.ToLowerInvariant())
            .Distinct();
        var features = keys.Select(key =>
        {
            var implemented = modules.TryGetValue(key, out var module);
            var toggle = toggles.GetValueOrDefault(key) ?? new FeatureToggle();
            return new FeatureState(
                key,
                module?.DisplayName ?? key,
                implemented,
                Enabled: enabled.Contains(key),
                // 설정에서 직접 켠 모듈만 노출 (의존성으로 켜진 모듈은 숨김)
                ShowInUi: implemented && toggle.Enabled && toggle.ShowInUi);
        }).ToList();

        return new FeatureRegistry
        {
            Features = features,
            EnabledModules = catalog.Where(m => enabled.Contains(m.Key)).ToList(),
        };
    }
}
