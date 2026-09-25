import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ExperimentDescription, RankMargin } from '../../../shared/components/ExperimentNotes'
import { PromptMixWarning } from '../../../shared/components/PromptsUsed'
import {
  applyImageCombo,
  getImageExperiment,
  getImageSettings,
  sameImageSettings,
  type ImageDocCell,
  type ImageExperimentDetail,
  type ImageSettingsDto,
} from '../api'
import { Disclaimer } from '../components/Disclaimer'

const POLL_MS = 3000
const pct = (v: number | null) => (v === null ? '—' : `${(v * 100).toFixed(1)}%`)
const sec = (v: number | null) => (v === null ? '—' : `${v.toFixed(1)}초`)

/** X-ray 실험 1건: 조합별 폐렴 판단 정확도(CNN·VLM)·일치율·시간·순위 ➔ [기본으로 적용], 영상별 결과 */
export function ImageExperimentPage() {
  const { experimentId = '' } = useParams()
  return <Detail key={experimentId} id={experimentId} />
}

function Cell({ cell, truth }: { cell: ImageDocCell | null; truth: boolean | null | undefined }) {
  if (!cell) return <>—</>
  if (cell.status !== 'Completed') {
    return <span className={cell.status === 'Failed' ? 'text-bad' : 'muted'}>{cell.status === 'Failed' ? '실패' : '대기·처리 중'}</span>
  }
  // API 가 null 값을 JSON 에서 빼므로(undefined) == null 로 비교
  const mark = (v: boolean | null | undefined) =>
    v == null ? '—' : truth == null ? (v ? '폐렴' : '음성') : v === truth ? (v ? '폐렴 ✓' : '음성 ✓') : v ? '폐렴 ✗' : '음성 ✗'
  const tone = (v: boolean | null | undefined) => (v == null || truth == null ? '' : v === truth ? 'ok-mark' : 'bad-mark')
  return (
    <Link to={`/image/jobs/${cell.jobId}`} className="small">
      CNN <span className={tone(cell.cnnPositive)}>{mark(cell.cnnPositive)}</span>
      {cell.cnnProbability != null && <span className="muted"> {cell.cnnProbability.toFixed(2)}</span>} · VLM{' '}
      <span className={tone(cell.vlmPneumonia)}>{mark(cell.vlmPneumonia)}</span>
      {cell.agree === false && <span className="chip chip-bad">불일치</span>}
      {cell.vlmPneumonia == null && <span className="chip chip-warn">판독 실패</span>}
    </Link>
  )
}

function Detail({ id }: { id: string }) {
  const [detail, setDetail] = useState<ImageExperimentDetail | null>(null)
  const [current, setCurrent] = useState<ImageSettingsDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    () =>
      Promise.all([getImageExperiment(id), getImageSettings()])
        .then(([d, s]) => {
          setDetail(d)
          setCurrent(s)
        })
        .catch((e) => setError(e instanceof Error ? e.message : String(e))),
    [id],
  )

  useEffect(() => {
    load()
  }, [load])

  const running = detail?.status === 'Running'
  useEffect(() => {
    if (!running) return
    const timer = setInterval(load, POLL_MS)
    return () => clearInterval(timer)
  }, [running, load])

  if (error) return <section className="card text-bad">실험을 불러오지 못했습니다: {error}</section>
  if (!detail) return <section className="card muted">불러오는 중…</section>

  const percent = detail.total ? Math.round((detail.done / detail.total) * 100) : 0
  const env = detail.environment

  return (
    <>
      <div className="page-head">
        <Link to="/image" className="back">
          ← 모델 비교
        </Link>
        <h2>{detail.name}</h2>
        {running ? <span className="badge badge-run">진행 중</span> : <span className="badge badge-ok">완료</span>}
        <span className="muted small">
          {new Date(detail.createdAt).toLocaleString('ko-KR')} · 영상 {detail.docCount}장 × 조합 {detail.combos.length}개
        </span>
      </div>
      <Disclaimer />
      <ExperimentDescription module="image" id={detail.id} description={detail.description} onSaved={load} />

      <section className="card progress-card">
        <div className="progress-head">
          <strong>
            {detail.done} / {detail.total} 작업 완료
          </strong>
          <span className="muted small">{percent}%</span>
        </div>
        <div className={`progress-bar ${running ? 'is-running' : 'is-done'}`}>
          <div style={{ width: `${percent}%` }} />
        </div>
        {env && (
          <span className="muted small">
            실험 환경: {env.gpus?.map((g) => `${g.name} ${(g.memoryTotalMb / 1024).toFixed(0)}GB`).join(', ')} · {env.cpu} · RAM{' '}
            {env.memoryGb} GB · Ollama {env.ollamaVersion ?? '?'}
          </span>
        )}
      </section>

      <section className="card">
        <h3>조합별 결과</h3>
        {!detail.labeled && (
          <p className="text-warn small">
            정답을 찾을 수 있는 영상이 없어 정확도·순위를 계산하지 않았습니다 (Kaggle·IU 파일 이름을 그대로 올리세요).
          </p>
        )}
        <div className="table-scroll">
          <table className="jobs combo-table">
            <thead>
              <tr>
                <th>순위</th>
                <th>조합</th>
                <th className="num">완료</th>
                <th className="num" title="CNN 폐렴 판단: 민감도 / 특이도">CNN 민감·특이</th>
                <th className="num">CNN AUC</th>
                <th className="num" title="VLM 판독 초안이 형식대로 나온 비율">VLM 성공</th>
                <th className="num" title="VLM 폐렴 의심 판단: 민감도 / 특이도 (판독에 성공한 영상 기준)">VLM 민감·특이</th>
                <th className="num" title="CNN·VLM 폐렴 판단이 같은 비율 (점수에는 넣지 않음)">일치율</th>
                <th className="num" title="CNN 이 틀린 영상 중 CNN·VLM 불일치로 사람 확인에 걸린 비율">CNN 오답 적발</th>
                <th className="num">작업 시간</th>
                <th className="num">점수</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {detail.combos.map((c) => {
                const isDefault = !!current && sameImageSettings(current.settings, c.settings)
                const isBest = detail.recommendedIndex === c.index
                return (
                  <tr key={c.index} className={isBest ? 'best' : undefined}>
                    <td className="tabular">
                      {c.rank ?? '—'}
                      {isBest && <span className="chip chip-ok">추천</span>}
                    </td>
                    <td className="small">
                      <strong>조합 {c.index + 1}</strong> {c.summary}
                    </td>
                    <td className="num tabular small">
                      {c.completed}/{c.total}
                      {c.failed > 0 && <span className="text-bad"> (실패 {c.failed})</span>}
                    </td>
                    <td className="num tabular small">
                      {pct(c.cnnSensitivity)} / {pct(c.cnnSpecificity)}
                    </td>
                    <td className="num tabular small">{c.cnnAuc == null ? '—' : c.cnnAuc.toFixed(3)}</td>
                    <td className={`num tabular small ${c.vlmSuccessRate != null && c.vlmSuccessRate < 0.9 ? 'text-bad' : ''}`}>
                      {pct(c.vlmSuccessRate)}
                    </td>
                    <td
                      className={`num tabular small ${c.vlmJudged != null && c.vlmJudged < 10 ? 'muted' : ''}`}
                      title={`판독에 성공하고 정답이 있는 영상 ${c.vlmJudged ?? '?'}장 기준`}
                    >
                      {pct(c.vlmSensitivity)} / {pct(c.vlmSpecificity)}
                      {c.vlmJudged != null && c.vlmJudged < c.labeledDocs && <span className="small"> (n={c.vlmJudged})</span>}
                    </td>
                    <td className="num tabular small">{pct(c.agreementRate)}</td>
                    <td className="num tabular small" title={`CNN 오답 ${c.cnnErrors}건`}>
                      {c.cnnErrors === 0 ? '오답 없음' : pct(c.flagRecall)}
                    </td>
                    <td className="num tabular small">{sec(c.medianJobSec)}</td>
                    <td className="num tabular">
                      <strong>{c.score === null ? '—' : c.score.toFixed(3)}</strong>
                    </td>
                    <td>
                      {isDefault ? (
                        <span className="chip chip-ok">현재 기본</span>
                      ) : (
                        <button
                          className={isBest ? 'primary small-button' : 'small-button'}
                          disabled={c.completed === 0}
                          onClick={async () => setCurrent(await applyImageCombo(detail.id, c.index))}
                        >
                          기본으로 적용
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
        <RankMargin combos={detail.combos} sample={`영상 ${detail.docCount}장`} />
        <p className="muted small">
          점수: {detail.scoreFormula}. 모든 영상이 끝난 조합만 순위를 매깁니다. 시간 통계는 조합별 첫 작업(모델 적재 포함)을 뺀 값입니다.
        </p>
        <PromptMixWarning mixes={detail.promptMixes} />
      </section>

      <section className="card">
        <h3>영상별 결과</h3>
        <p className="muted small">✓ = 정답과 같음, ✗ = 틀림. 칸을 누르면 그 작업의 판독 화면으로 이동합니다.</p>
        <div className="table-scroll">
          <table className="jobs doc-matrix">
            <thead>
              <tr>
                <th>영상</th>
                <th>정답</th>
                {detail.combos.map((c) => (
                  <th key={c.index}>조합 {c.index + 1}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {detail.docs.map((d) => (
                <tr key={d.fileName}>
                  <td className="small">{d.fileName}</td>
                  <td className="small">
                    {d.truth?.pneumonia == null ? (
                      <span className="muted">—</span>
                    ) : (
                      <>
                        {d.truth.pneumonia ? '폐렴' : d.truth.normal ? '정상' : '폐렴 아님'}
                        <span className="muted"> ({d.truth.dataset})</span>
                      </>
                    )}
                  </td>
                  {d.cells.map((cell, i) => (
                    <td key={i}>
                      <Cell cell={cell} truth={d.truth?.pneumonia} />
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </>
  )
}
