using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Shared.Data;
using Api.Shared.Experiments;
using Api.Shared.Jobs;
using Api.Shared.Prompts;
using Microsoft.EntityFrameworkCore;

namespace Api.Modules.Image.Experiments;

public sealed record ImageComboResultDto(
    int Index,
    ImageSettings Settings,
    string Summary,
    int Total,
    int Completed,
    int Failed,
    int Running,
    int LabeledDocs,
    double? CnnSensitivity,
    double? CnnSpecificity,
    double? CnnBalanced,
    double? CnnAuc,
    double? VlmSensitivity,
    double? VlmSpecificity,
    double? VlmBalanced,
    double? VlmSuccessRate,
    double? VlmNormalAccuracy,
    double? AgreementRate,
    int CnnErrors,
    double? FlagRecall,
    double? MedianJobSec,
    double? MedianCnnMs,
    double? MedianLlmSec,
    double? Score,
    int? Rank);

public sealed record ImageDocCellDto(
    Guid JobId,
    string Status,
    bool? CnnPositive,
    double? CnnProbability,
    bool? VlmPneumonia,
    bool? Agree,
    double? JobSec);

public sealed record ImageDocRowDto(string FileName, ImageTruth? Truth, List<ImageDocCellDto?> Cells);

public sealed record ImageExperimentDetailDto(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int DocCount,
    string Status,
    int Done,
    int Total,
    JsonNode? Environment,
    bool Labeled,
    List<ImageComboResultDto> Combos,
    int? RecommendedIndex,
    List<ImageDocRowDto> Docs,
    string ScoreFormula,
    // 같은 프롬프트가 실험 도중 다른 버전으로 쓰였으면 목록 (결과가 섞였을 수 있음)
    IReadOnlyList<PromptMix> PromptMixes);

public sealed record ImageExperimentSummaryDto(
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
/// 이미지 실험 채점 (조회할 때 계산). 정답은 파일 이름으로 (ImageLabels: Kaggle 폴더 규칙 · IU 소견서).
/// 일치율은 점수에 넣지 않음: VLM 이 CNN 을 따라 쓰면 일치율은 오르지만 검증은 약해지기 때문 ➔ 대신 "CNN 오답을 불일치로 잡아낸 비율"을 따로 표시
/// </summary>
public sealed class ImageExperimentScorer(AppDbContext db, ImageLabels labels)
{
    public const string Formula =
        "정답이 있으면 0.5×CNN 폐렴 균형 정확도 + 0.4×VLM 폐렴 균형 정확도×VLM 판독 성공률 + 0.1×속도 (VLM 판독을 끈 조합은 0.9×CNN + 0.1×속도). "
        + "균형 정확도 = (민감도 + 특이도) ÷ 2, 속도 = min(1, 5초 ÷ 작업 시간 중앙값). VLM 지표·일치율은 판독에 성공한 영상 기준. "
        + "정답이 없는 영상만 있으면 순위를 매기지 않음";

    public async Task<ImageExperimentDetailDto> ScoreAsync(Experiment experiment, CancellationToken ct)
    {
        var combos = experiment.ImageCombos();
        var jobs = await db.Jobs.AsNoTracking().Where(j => j.ExperimentId == experiment.Id).ToListAsync(ct);
        var ids = jobs.Select(j => j.Id).ToList();
        var results = await db.Set<ImageResultRecord>().AsNoTracking()
            .Where(r => ids.Contains(r.JobId)).ToDictionaryAsync(r => r.JobId, ct);

        var comboResults = new List<ImageComboResultDto>();
        var cells = new Dictionary<(string File, int Combo), ImageDocCellDto>();
        var anyLabeled = false;
        for (var i = 0; i < combos.Count; i++)
        {
            var comboJobs = jobs.Where(j => j.ComboIndex == i).ToList();
            var cnn = new Confusion();
            var vlm = new Confusion();
            int agreeCount = 0, agreeTotal = 0, normalOk = 0, normalTotal = 0, cnnErrors = 0, flagged = 0, labeledDocs = 0;
            int vlmAttempts = 0, vlmOk = 0;
            var auc = new List<(double P, bool Y)>();
            var jobSec = new List<double>();
            var cnnMs = new List<double>();
            var llmSec = new List<double>();
            // 조합의 첫 작업은 LLM 모델 적재가 섞이므로 시간 통계에서 뺌 (Text 실험과 같은 규칙)
            var warmup = comboJobs.Count(j => j.Status == JobStatus.Completed) >= 2
                ? comboJobs.Where(j => j is { Status: JobStatus.Completed, StartedAt: not null }).MinBy(j => j.StartedAt)?.Id
                : null;

            foreach (var job in comboJobs)
            {
                results.TryGetValue(job.Id, out var r);
                var truth = labels.For(job.FileName);
                var report = r?.Report is null ? null : JsonNode.Parse(r.Report);
                bool? vlmPneumonia = report?["pneumonia_suspected"]?.GetValue<bool>();
                bool? vlmNormal = report?["normal"]?.GetValue<bool>();
                // 일치 여부는 VLM 이 실제로 판단을 냈을 때만 (판독 형식 오류는 "불일치"가 아니라 "판독 실패")
                bool? agree = r?.PneumoniaPositive is { } cnnP && vlmPneumonia is { } vlmP ? cnnP == vlmP : null;
                double? seconds = job is { StartedAt: { } s, CompletedAt: { } c } ? (c - s).TotalSeconds : null;

                if (job.Status == JobStatus.Completed && r is not null)
                {
                    if (r.Model is not null)
                    {
                        vlmAttempts++;
                        vlmOk += report is not null ? 1 : 0;
                    }
                    if (agree is { } a)
                    {
                        agreeCount += a ? 1 : 0;
                        agreeTotal++;
                    }
                    if (truth?.Pneumonia is { } y && r.PneumoniaPositive is { } cp)
                    {
                        labeledDocs++;
                        cnn.Add(cp, y);
                        if (r.PneumoniaProbability is { } prob)
                        {
                            auc.Add((prob, y));
                        }
                        if (vlmPneumonia is { } vp)
                        {
                            vlm.Add(vp, y);
                        }
                        if (cp != y)
                        {
                            cnnErrors++;
                            flagged += agree == false ? 1 : 0; // CNN 이 틀렸고 VLM 이 다른 판단을 내 사람 확인에 걸림
                        }
                    }
                    if (truth?.Normal is { } n && vlmNormal is { } vn)
                    {
                        normalOk += vn == n ? 1 : 0;
                        normalTotal++;
                    }
                    if (job.Id != warmup)
                    {
                        if (seconds is { } sec) jobSec.Add(sec);
                        cnnMs.Add(r.CnnElapsedMs);
                        if (r.Model is not null) llmSec.Add(r.LlmElapsedMs / 1000.0);
                    }
                }
                cells[(job.FileName, i)] = new ImageDocCellDto(job.Id, job.Status.ToString(), r?.PneumoniaPositive,
                    r?.PneumoniaProbability is { } pp ? Math.Round(pp, 3) : null, vlmPneumonia, agree,
                    seconds is null ? null : Math.Round(seconds.Value, 1));
            }

            var completed = comboJobs.Count(j => j.Status == JobStatus.Completed);
            var failed = comboJobs.Count(j => j.Status == JobStatus.Failed);
            var median = Median(jobSec);
            double? vlmSuccess = vlmAttempts > 0 ? (double)vlmOk / vlmAttempts : null;
            double? score = null;
            if (cnn.Balanced is { } cb)
            {
                score = vlmAttempts == 0
                    ? 0.9 * cb + 0.1 * Speed(median)
                    : 0.5 * cb + 0.4 * (vlm.Balanced ?? 0) * (vlmSuccess ?? 0) + 0.1 * Speed(median);
            }
            anyLabeled |= labeledDocs > 0;
            comboResults.Add(new ImageComboResultDto(
                i, combos[i], combos[i].Summary, comboJobs.Count, completed, failed, comboJobs.Count - completed - failed,
                labeledDocs, cnn.Sensitivity, cnn.Specificity, cnn.Balanced, Auc(auc),
                vlm.Sensitivity, vlm.Specificity, vlm.Balanced, vlmSuccess,
                normalTotal > 0 ? (double)normalOk / normalTotal : null,
                agreeTotal > 0 ? (double)agreeCount / agreeTotal : null,
                cnnErrors, cnnErrors > 0 ? (double)flagged / cnnErrors : null,
                median, Median(cnnMs), Median(llmSec), score, null));
        }

        // 순위: 모든 문서가 끝난 조합만
        var ranked = comboResults.Where(c => c.Running == 0 && c.Score is not null).OrderByDescending(c => c.Score).ToList();
        comboResults = comboResults.Select(c => c with { Rank = ranked.IndexOf(c) is var idx and >= 0 ? idx + 1 : null }).ToList();
        int? recommended = ranked.Count > 0 && ranked.Count == combos.Count ? ranked[0].Index : null;

        var files = jobs.OrderBy(j => j.CreatedAt).Select(j => j.FileName).Distinct().ToList();
        var docs = files.Select(f => new ImageDocRowDto(f, labels.For(f),
            Enumerable.Range(0, combos.Count).Select(i => cells.GetValueOrDefault((f, i))).ToList())).ToList();
        var done = jobs.Count(j => j.IsFinished);
        return new ImageExperimentDetailDto(
            experiment.Id, experiment.Name, experiment.CreatedAt, experiment.DocCount,
            done == jobs.Count ? "Completed" : "Running", done, jobs.Count,
            experiment.Environment is null ? null : JsonNode.Parse(experiment.Environment),
            anyLabeled, comboResults, recommended, docs, Formula, PromptUsage.Mixed(jobs.Select(j => j.Prompts)));
    }

    public static ImageExperimentSummaryDto Summarize(ImageExperimentDetailDto d) => new(
        d.Id, d.Name, d.CreatedAt, d.DocCount, d.Combos.Count, d.Status, d.Done, d.Total, d.Labeled,
        d.RecommendedIndex is { } i ? d.Combos[i].Summary : null,
        d.RecommendedIndex is { } j ? d.Combos[j].Score : null);

    private static double Speed(double? medianSec) => medianSec is > 0 ? Math.Min(1, 5 / medianSec.Value) : 0;

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return Math.Round(sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2, 1);
    }

    /// <summary>AUC = 양성이 음성보다 높은 확률을 받을 확률 (Mann-Whitney, 동점 0.5)</summary>
    private static double? Auc(List<(double P, bool Y)> xs)
    {
        var pos = xs.Where(x => x.Y).Select(x => x.P).ToList();
        var neg = xs.Where(x => !x.Y).Select(x => x.P).ToList();
        if (pos.Count == 0 || neg.Count == 0) return null;
        double wins = 0;
        foreach (var p in pos)
        {
            foreach (var n in neg)
            {
                wins += p > n ? 1 : p == n ? 0.5 : 0;
            }
        }
        return wins / (pos.Count * neg.Count);
    }

    private sealed class Confusion
    {
        private int tp, fn, tn, fp;

        public void Add(bool predicted, bool actual)
        {
            if (actual) { if (predicted) tp++; else fn++; }
            else { if (predicted) fp++; else tn++; }
        }

        public double? Sensitivity => tp + fn > 0 ? (double)tp / (tp + fn) : null;
        public double? Specificity => tn + fp > 0 ? (double)tn / (tn + fp) : null;
        public double? Balanced => Sensitivity is { } s && Specificity is { } p ? (s + p) / 2
            : Sensitivity ?? Specificity;
    }
}

public static class ImageExperiments
{
    /// <summary>Image 실험의 조합 목록 (ImageSettings)</summary>
    public static List<ImageSettings> ImageCombos(this Experiment experiment) =>
        JsonSerializer.Deserialize<List<ImageSettings>>(experiment.Combos, ImageSettings.Json) ?? [];
}
