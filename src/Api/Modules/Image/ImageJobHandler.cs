using Api.Shared.Jobs;
using Api.Shared.Llm;

namespace Api.Modules.Image;

/// <summary>Step 2 작업 처리: X-ray 분석(ImageAnalyzer) ➔ 저장</summary>
public sealed class ImageJobHandler(ImageAnalyzer analyzer, ImageSettingsStore store) : IJobHandler
{
    public async Task<string> HandleAsync(Job job, JobReporter reporter, CancellationToken ct)
    {
        var settings = ImageSettings.FromJson(job.Settings) ?? (await store.GetDefaultAsync(ct)).Settings;
        var analysis = await analyzer.AnalyzeAsync(job, settings, reporter, ct);
        await analyzer.SaveAsync(job, analysis, ct);
        return $"판독/저장 완료: {ImageAnalyzer.Describe(analysis)}"
            + (analysis.Report is null ? "" : $", LLM {analysis.Report.ElapsedMs / 1000.0:0.0}초");
    }

    public static ImageSettings Defaults(ImageOptions options, LlmClient llm) =>
        new(options.Model ?? llm.DefaultModel, options.VlmReport, options.VlmSeesCnn, options.Population);
}
