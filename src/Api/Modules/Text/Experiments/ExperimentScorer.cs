using System.Text.Json.Nodes;
using Api.Modules.Text.Settings;
using Api.Shared.Data;
using Api.Shared.Jobs;
using Api.Shared.Prompts;
using Microsoft.EntityFrameworkCore;

using Api.Shared.Experiments;

namespace Api.Modules.Text.Experiments;

public sealed record ComboResultDto(
    int Index,
    PipelineSettings Settings,
    string Summary,
    int Total,
    int Completed,
    int Failed,
    int Running,
    /// <summary>검증 통과 / 전체 (실패한 작업은 통과 못 한 것으로 셈)</summary>
    double? PassRate,
    double? FallbackRate,
    /// <summary>KORIE 정답이 있는 문서의 헤더 필드 일치율</summary>
    double? FieldAccuracy,
    double? AmountAccuracy,
    int LabeledDocs,
    double? MedianJobSec,
    double? MedianOcrSec,
    double? MedianLlmSec,
    double? Score,
    int? Rank,
    /// <summary>정답이 있고 검증을 통과한 문서 수 ➔ 그중 합계가 틀린 수 · 필드가 하나라도 틀린 수 (검증 통과가 정답을 보장하지 않는 정도)</summary>
    int PassedLabeled = 0,
    int PassedWrongTotal = 0,
    int PassedWrongAny = 0);

public sealed record DocCellDto(
    Guid JobId,
    string Status,
    bool? Passed,
    bool Fallback,
    double? JobSec,
    int? CorrectFields,
    int? LabeledFields,
    string? Total);

public sealed record DocRowDto(string FileName, IReadOnlyList<DocCellDto?> Cells);

public sealed record ExperimentDetailDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int DocCount,
    string Status,
    int Done,
    int Total,
    JsonNode? Environment,
    bool Labeled,
    IReadOnlyList<ComboResultDto> Combos,
    int? RecommendedIndex,
    IReadOnlyList<DocRowDto> Docs,
    string ScoreFormula,
    // 같은 프롬프트가 실험 도중 다른 버전으로 쓰였으면 목록 (결과가 섞였을 수 있음)
    IReadOnlyList<PromptMix> PromptMixes,
    string? Description = null);

public sealed record ExperimentSummaryDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int DocCount,
    int ComboCount,
    string Status,
    int Done,
    int Total,
    bool Labeled,
    string? Recommended,
    double? BestScore);

/// <summary>
/// 실험 채점: 조합별 검증 통과율·폴백률·처리 시간, KORIE 정답이 있으면 필드 정확도.
/// 점수 = 정답 있음: 0.6×필드 정확도 + 0.3×검증 통과율 + 0.1×속도 / 정답 없음: 0.8×검증 통과율 + 0.2×속도
///       (속도 = min(1, 10초 ÷ 작업 시간 중앙값)). 모든 문서가 끝난 조합 중 1위를 "추천"으로 표시
/// </summary>
public sealed class ExperimentScorer(AppDbContext db, KorieLabels labels)
{
    public const string Formula =
        "정답 라벨이 있으면 0.6×필드 정확도 + 0.3×검증 통과율 + 0.1×속도, 없으면 0.8×검증 통과율 + 0.2×속도 (속도 = min(1, 10초 ÷ 작업 시간 중앙값). "
        + "검증 통과 = 오류(Error)가 없는 것 — 경고(Warning, 예: 품목 합 < 합계)는 통과로 봄. 실패한 작업은 통과 못 한 것으로 셈. "
        + "시간 통계는 조합별 첫 작업(모델 적재 포함)을 뺀 값";

    public async Task<ExperimentDetailDto> ScoreAsync(Experiment experiment, CancellationToken ct)
    {
        var combos = experiment.ComboList();
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.ExperimentId == experiment.Id).ToListAsync(ct);
        var ids = jobs.Select(j => j.Id).ToList();
        var results = await db.Set<ExtractionResultRecord>().AsNoTracking()
            .Where(r => ids.Contains(r.JobId)).ToDictionaryAsync(r => r.JobId, ct);
        var ocrMs = await db.Set<OcrResultRecord>().AsNoTracking()
            .Where(r => ids.Contains(r.JobId)).ToDictionaryAsync(r => r.JobId, r => r.EngineElapsedMs, ct);

        var comboResults = new List<ComboResultDto>();
        var cells = new Dictionary<(string File, int Combo), DocCellDto>();
        var anyLabeled = false;
        for (var i = 0; i < combos.Count; i++)
        {
            var comboJobs = jobs.Where(j => j.ComboIndex == i).ToList();
            int passed = 0, fallback = 0, correct = 0, labeled = 0, amountCorrect = 0, amountLabeled = 0, labeledDocs = 0;
            int passedLabeled = 0, passedWrongTotal = 0, passedWrongAny = 0;
            var jobSec = new List<double>();
            var ocrSec = new List<double>();
            var llmSec = new List<double>();
            // 조합의 첫 작업은 모델 적재(조합이 바뀌면 LLM 을 새로 올림, 약 4초+)가 섞이므로 시간 통계에서 뺌 (작업이 2건 이상일 때)
            var warmup = comboJobs.Count(j => j.Status == JobStatus.Completed) >= 2
                ? comboJobs.Where(j => j is { Status: JobStatus.Completed, StartedAt: not null }).MinBy(j => j.StartedAt)?.Id
                : null;
            foreach (var job in comboJobs)
            {
                results.TryGetValue(job.Id, out var r);
                var fields = r?.Fields is null ? null : JsonNode.Parse(r.Fields) as JsonObject;
                int? docCorrect = null, docLabeled = null;
                if (job.Status == JobStatus.Completed && labels.For(job.FileName) is { } truth && r?.DocumentType == "receipt")
                {
                    docCorrect = 0;
                    docLabeled = 0;
                    foreach (var field in KorieLabels.Fields.Where(truth.ContainsKey))
                    {
                        var ok = KorieLabels.IsCorrect(field, truth[field], fields?[field]?.ToString());
                        docCorrect += ok ? 1 : 0;
                        docLabeled++;
                        if (KorieLabels.AmountFields.Contains(field))
                        {
                            amountCorrect += ok ? 1 : 0;
                            amountLabeled++;
                        }
                    }
                    correct += docCorrect.Value;
                    labeled += docLabeled.Value;
                    labeledDocs++;
                    if (r.ValidationPassed == true)
                    {
                        passedLabeled++;
                        passedWrongAny += docCorrect < docLabeled ? 1 : 0;
                        passedWrongTotal += truth.TryGetValue("total", out var t)
                            && !KorieLabels.IsCorrect("total", t, fields?["total"]?.ToString()) ? 1 : 0;
                    }
                }
                double? seconds = job is { StartedAt: { } s, CompletedAt: { } c } ? (c - s).TotalSeconds : null;
                if (job.Status == JobStatus.Completed && r is not null)
                {
                    passed += r.ValidationPassed == true ? 1 : 0;
                    fallback += r.FallbackUsed ? 1 : 0;
                    if (job.Id != warmup)
                    {
                        if (seconds is { } sec) jobSec.Add(sec);
                        llmSec.Add(r.LlmElapsedMs / 1000.0);
                        if (ocrMs.TryGetValue(job.Id, out var ms)) ocrSec.Add(ms / 1000.0);
                    }
                }
                cells[(job.FileName, i)] = new DocCellDto(
                    job.Id, job.Status.ToString(), r?.ValidationPassed, r?.FallbackUsed ?? false,
                    seconds is null ? null : Math.Round(seconds.Value, 1), docCorrect, docLabeled,
                    fields?["total"]?.ToString() ?? fields?["total_amount"]?.ToString());
            }

            var completed = comboJobs.Count(j => j.Status == JobStatus.Completed);
            var failed = comboJobs.Count(j => j.Status == JobStatus.Failed);
            var finished = completed + failed;
            double? passRate = finished > 0 ? (double)passed / finished : null;
            double? accuracy = labeled > 0 ? (double)correct / labeled : null;
            double? median = Median(jobSec);
            double? score = passRate is null ? null
                : accuracy is { } acc ? 0.6 * acc + 0.3 * passRate.Value + 0.1 * Speed(median)
                : 0.8 * passRate.Value + 0.2 * Speed(median);
            anyLabeled |= labeled > 0;
            comboResults.Add(new ComboResultDto(
                i, combos[i], combos[i].Summary, comboJobs.Count, completed, failed, comboJobs.Count - finished,
                passRate, completed > 0 ? (double)fallback / completed : null, accuracy,
                amountLabeled > 0 ? (double)amountCorrect / amountLabeled : null, labeledDocs,
                median, Median(ocrSec), Median(llmSec), score, null, passedLabeled, passedWrongTotal, passedWrongAny));
        }

        // 순위: 모든 문서가 끝난 조합만 (진행 중인 조합은 점수가 바뀔 수 있음)
        var ranked = comboResults.Where(c => c.Running == 0 && c.Score is not null).OrderByDescending(c => c.Score).ToList();
        comboResults = comboResults.Select(c => c with { Rank = ranked.IndexOf(c) is var idx and >= 0 ? idx + 1 : null }).ToList();
        int? recommended = ranked.Count > 0 && ranked.Count == combos.Count ? ranked[0].Index : null;

        var files = jobs.OrderBy(j => j.CreatedAt).Select(j => j.FileName).Distinct().ToList();
        var docs = files.Select(f => new DocRowDto(f,
            Enumerable.Range(0, combos.Count).Select(i => cells.GetValueOrDefault((f, i))).ToList())).ToList();
        var done = jobs.Count(j => j.IsFinished);

        return new ExperimentDetailDto(
            experiment.Id, experiment.Name, experiment.CreatedAt, experiment.DocCount,
            done == jobs.Count ? "Completed" : "Running", done, jobs.Count,
            experiment.Environment is null ? null : JsonNode.Parse(experiment.Environment),
            anyLabeled, comboResults, recommended, docs, Formula, PromptUsage.Mixed(jobs.Select(j => j.Prompts)), experiment.Description);
    }

    public static ExperimentSummaryDto Summarize(ExperimentDetailDto d) => new(
        d.Id, d.Name, d.CreatedAt, d.DocCount, d.Combos.Count, d.Status, d.Done, d.Total, d.Labeled,
        d.RecommendedIndex is { } i ? d.Combos[i].Summary : null,
        d.RecommendedIndex is { } j ? d.Combos[j].Score : null);

    private static double Speed(double? medianSec) => medianSec is > 0 ? Math.Min(1, 10 / medianSec.Value) : 0;

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return Math.Round(sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2, 1);
    }
}
