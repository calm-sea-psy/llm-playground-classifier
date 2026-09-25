using System.Text.Json;
using Api.Modules.Text.Settings;
using Api.Shared.Experiments;

namespace Api.Modules.Text.Experiments;

public static class TextExperiments
{
    /// <summary>Text 실험의 조합 목록 (PipelineSettings)</summary>
    public static List<PipelineSettings> ComboList(this Experiment experiment) =>
        JsonSerializer.Deserialize<List<PipelineSettings>>(experiment.Combos, PipelineSettings.Json) ?? [];
}
