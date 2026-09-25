import { useEffect, useMemo, useState } from 'react'
import { getImageResult, type CnnFinding, type ImageResult } from '../api'
import { findingName, POPULATION_NAMES, sourceName } from '../labels'
import { HeatmapViewer, type HeatmapLayer } from './HeatmapViewer'
import { isBorderline } from '../labels'

const pct = (v: number) => `${(v * 100).toFixed(1)}%`

/** 소견별 확률 막대 + 판정 기준 표시 (기준을 넘은 소견만 강조) */
function FindingBars({ findings }: { findings: CnnFinding[] }) {
  const [showAll, setShowAll] = useState(false)
  const positives = findings.filter((f) => f.positive).length
  const visible = showAll ? findings : findings.slice(0, Math.max(6, positives))
  return (
    <>
      <ul className="finding-bars">
        {visible.map((f) => (
          <li
            key={f.label}
            className={isBorderline(f) ? 'borderline' : f.positive ? 'positive' : undefined}
            title={`${f.label} · 기준 ${f.threshold}${isBorderline(f) ? ' · 경계(기준값 +0.02 이내, 판단 보류)' : ''}`}
          >
            <span className="finding-name">
              {findingName(f.label)} <span className="muted small">{f.label}</span>
            </span>
            <span className="bar">
              <span className="fill" style={{ width: pct(f.probability) }} />
              <span className="threshold" style={{ left: pct(f.threshold) }} />
            </span>
            <span className="tabular small">{f.probability.toFixed(2)}</span>
          </li>
        ))}
      </ul>
      {findings.length > visible.length || showAll ? (
        <button className="link-button small" onClick={() => setShowAll(!showAll)}>
          {showAll ? '접기' : `나머지 ${findings.length - visible.length}개 소견 보기`}
        </button>
      ) : null}
    </>
  )
}

/** 완료된 작업: 판정 요약 ➔ X-ray + 히트맵 | 폐렴 신호·소견 확률 ➔ VLM 판독 초안 + 교차 검증 */
export function ImageResultView({ jobId }: { jobId: string }) {
  const [result, setResult] = useState<ImageResult | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getImageResult(jobId)
      .then(setResult)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [jobId])

  const layers = useMemo<HeatmapLayer[]>(() => {
    if (!result) return []
    const out: HeatmapLayer[] = []
    if (result.pneumonia.heatmap) {
      const label = result.pneumonia.heatmap.label
      const positive = result.pneumonia.findings.find((f) => f.label === label)?.positive ?? false
      out.push({ role: 'pneumonia', title: '폐렴 신호', label, positive, region: result.pneumonia.heatmap.region })
    }
    if (result.findings.heatmap) {
      const label = result.findings.heatmap.label
      const positive = result.findings.findings.find((f) => f.label === label)?.positive ?? false
      out.push({ role: 'findings', title: '가장 강한 소견', label, positive, region: result.findings.heatmap.region })
    }
    return out
  }, [result])

  if (error) return <section className="card text-bad">결과를 불러오지 못했습니다: {error}</section>
  if (!result) return <section className="card muted">결과 불러오는 중…</section>

  const errors = result.issues.filter((i) => i.severity === 'Error')
  const warnings = result.issues.filter((i) => i.severity === 'Warning')
  const report = result.report
  const signal = result.pneumoniaProbability

  return (
    <>
      <section className={`card verdict ${result.validationPassed ? 'verdict-ok' : 'verdict-bad'}`}>
        <strong>{result.validationPassed ? 'CNN·VLM 판단 일치' : '사람 확인 필요: CNN 과 VLM 의 판단이 다릅니다'}</strong>
        <span className="small">
          {POPULATION_NAMES[result.population]} · 폐렴{' '}
          {result.pneumoniaPositive == null ? '판단 없음' : result.pneumoniaPositive ? '양성' : '음성'}
          {signal != null && ` (${signal.toFixed(3)})`} · 양성 소견{' '}
          {result.findings.findings.filter((f) => f.positive).length}개 · CNN {result.cnnElapsedMs}ms
          {result.model && ` · VLM ${result.model} ${(result.llmElapsedMs / 1000).toFixed(1)}초`}
        </span>
      </section>

      <div className="image-result-grid">
        <section className="card panel">
          <header className="panel-head">
            <h3>X-ray · Grad-CAM</h3>
          </header>
          <HeatmapViewer jobId={jobId} width={result.findings.width} height={result.findings.height} layers={layers} />
        </section>

        <div className="stack">
          <section className="card panel">
            <header className="panel-head">
              <h3>폐렴 신호</h3>
              <span className="muted small">{sourceName(result.pneumoniaSource)}</span>
            </header>
            {(() => {
              const [, label] = result.pneumoniaSource.split(':')
              const f = result.pneumonia.findings.find((x) => x.label === label)
              return f ? (
                <FindingBars findings={[f]} />
              ) : (
                <p className="muted small">폐렴 신호가 없습니다</p>
              )
            })()}
            <p className="muted small">
              {result.population === 'pediatric'
                ? '소아: Kaggle 소아 흉부 X-ray 로 파인튜닝한 모델 (소아 AUC 0.98). 성인 영상에는 맞지 않습니다.'
                : '성인: xrv 의 경화(consolidation) 출력으로 판단 (성인 IU 에서 폐렴성 음영 AUC 0.87). 소아 영상이면 대상을 소아로 올리세요.'}
            </p>
          </section>

          <section className="card panel">
            <header className="panel-head">
              <h3>소견 18종</h3>
              <span className="muted small">{result.findings.modelVersion}</span>
            </header>
            <FindingBars findings={result.findings.findings} />
            <p className="muted small">막대 = 확률, 세로선 = 판정 기준. 기준을 넘은 소견이 강조됩니다.</p>
          </section>
        </div>
      </div>

      <div className="image-result-grid">
        <section className="card panel">
          <header className="panel-head">
            <h3>VLM 판독 초안</h3>
            {result.reportAttempt && (
              <span className="muted small">
                {result.reportAttempt.sawCnn ? 'CNN 결과를 참고해 작성' : 'CNN 결과를 보지 않고 독립 판독'}
              </span>
            )}
          </header>
          {!result.reportAttempt ? (
            <p className="muted small">VLM 판독을 끈 작업입니다 (CNN 결과만).</p>
          ) : !report ? (
            <p className="text-bad small">판독 초안 형식 오류: {result.reportAttempt.parseError}</p>
          ) : (
            <div className="report">
              <p className="impression">{report.impression}</p>
              <div className="chips">
                <span className={`chip ${report.normal ? 'chip-ok' : 'chip-warn'}`}>{report.normal ? '정상' : '이상 소견'}</span>
                <span className={`chip ${report.pneumonia_suspected ? 'chip-bad' : ''}`}>
                  {report.pneumonia_suspected ? '폐렴 의심' : '폐렴 의심 아님'}
                </span>
              </div>
              <ul>
                {report.findings.map((line) => (
                  <li key={line}>{line}</li>
                ))}
              </ul>
              {report.recommendation && <p className="small">권고: {report.recommendation}</p>}
            </div>
          )}
        </section>

        <section className="card panel">
          <header className="panel-head">
            <h3>교차 검증</h3>
            <span className="muted small">CNN 과 VLM 의 판단 비교</span>
          </header>
          {result.issues.length === 0 ? (
            <p className="ok-mark small">문제 없음</p>
          ) : (
            <ul className="issues">
              {[...errors, ...warnings].map((i) => (
                <li key={i.rule + i.message} className={i.severity === 'Error' ? 'has-error' : 'has-warning'}>
                  <span className={`chip ${i.severity === 'Error' ? 'chip-bad' : 'chip-warn'}`}>
                    {i.severity === 'Error' ? '불일치' : '참고'}
                  </span>{' '}
                  {i.message}
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </>
  )
}
