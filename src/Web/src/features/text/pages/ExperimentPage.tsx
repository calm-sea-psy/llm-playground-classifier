import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { jobFileUrl } from '../../../shared/api/jobs'
import { ExperimentDescription, RankMargin } from '../../../shared/components/ExperimentNotes'
import { PromptMixWarning } from '../../../shared/components/PromptsUsed'
import {
  applyCombo,
  getExperiment,
  getResult,
  getSettings,
  sameSettings,
  type ExperimentDetail,
  type ExtractionResult,
  type SettingsDto,
} from '../api'
import { CompareTable } from '../components/CompareTable'

const POLL_MS = 3000
const pct = (v: number | null) => (v === null ? '—' : `${(v * 100).toFixed(1)}%`)
const sec = (v: number | null) => (v === null ? '—' : `${v.toFixed(1)}초`)

/** 실험 1건: 조합별 지표·순위(추천) ➔ [기본으로 적용], 문서별 결과, 선택한 문서의 조합별 필드 비교 */
export function ExperimentPage() {
  const { experimentId = '' } = useParams()
  return <ExperimentDetailView key={experimentId} id={experimentId} />
}

function ExperimentDetailView({ id }: { id: string }) {
  const [detail, setDetail] = useState<ExperimentDetail | null>(null)
  const [current, setCurrent] = useState<SettingsDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)

  const load = useCallback(
    () =>
      Promise.all([getExperiment(id), getSettings()])
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
        <Link to="/text" className="back">
          ← 모델 비교
        </Link>
        <h2>{detail.name}</h2>
        {running ? <span className="badge badge-run">진행 중</span> : <span className="badge badge-ok">완료</span>}
        <span className="muted small">
          {new Date(detail.createdAt).toLocaleString('ko-KR')} · 문서 {detail.docCount}장 × 조합 {detail.combos.length}개
        </span>
      </div>
      <ExperimentDescription module="text" id={detail.id} description={detail.description} onSaved={load} />

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
        <div className="table-scroll">
          <table className="jobs combo-table">
            <thead>
              <tr>
                <th>순위</th>
                <th>조합</th>
                <th className="num">완료</th>
                {detail.labeled && <th className="num">필드 정확도</th>}
                {detail.labeled && <th className="num">금액 정확도</th>}
                <th className="num">검증 통과</th>
                {detail.labeled && (
                  <th className="num" title="검증을 통과한 문서 중 합계가 정답과 다른 문서 (검증 통과가 정답을 보장하지 않는 정도)">
                    통과했지만 틀림
                  </th>
                )}
                <th className="num">폴백</th>
                <th className="num">작업 시간</th>
                <th className="num hide-sm">OCR / LLM</th>
                <th className="num">점수</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {detail.combos.map((c) => {
                const isDefault = !!current && sameSettings(current.settings, c.settings)
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
                    {detail.labeled && (
                      <td className="num tabular" title={`정답 있는 문서 ${c.labeledDocs}장`}>
                        {pct(c.fieldAccuracy)}
                      </td>
                    )}
                    {detail.labeled && <td className="num tabular">{pct(c.amountAccuracy)}</td>}
                    <td className="num tabular">{pct(c.passRate)}</td>
                    {detail.labeled && (
                      <td
                        className="num tabular"
                        title={`정답 있는 문서 중 검증 통과 ${c.passedLabeled}건 ➔ 합계 틀림 ${c.passedWrongTotal}건 · 필드가 하나라도 틀림 ${c.passedWrongAny}건 (상호·시각 등 정의가 모호한 필드 포함)`}
                      >
                        {c.passedLabeled ? (
                          <span className={c.passedWrongTotal > 0 ? 'text-warn' : undefined}>
                            합계 {c.passedWrongTotal}/{c.passedLabeled}
                          </span>
                        ) : (
                          '—'
                        )}
                      </td>
                    )}
                    <td className="num tabular">{pct(c.fallbackRate)}</td>
                    <td className="num tabular">{sec(c.medianJobSec)}</td>
                    <td className="num tabular small hide-sm">
                      {sec(c.medianOcrSec)} / {sec(c.medianLlmSec)}
                    </td>
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
                          onClick={async () => setCurrent(await applyCombo(detail.id, c.index))}
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
        <RankMargin
          combos={detail.combos}
          sample={detail.labeled ? `정답 있는 문서 ${Math.max(...detail.combos.map((c) => c.labeledDocs))}장` : `문서 ${detail.docCount}장 (정답 없음)`}
        />
        <p className="muted small">
          점수: {detail.scoreFormula}. 모든 문서가 끝난 조합만 순위를 매깁니다.
        </p>
        <PromptMixWarning mixes={detail.promptMixes} />
      </section>

      <section className="card">
        <h3>문서별 결과</h3>
        <p className="muted small">행을 누르면 아래에 조합별 추출 결과를 나란히 보여 주고 그 위치로 이동합니다.</p>
        <div className="table-scroll">
          <table className="jobs doc-matrix">
            <thead>
              <tr>
                <th>문서</th>
                {detail.combos.map((c) => (
                  <th key={c.index}>조합 {c.index + 1}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {detail.docs.map((d) => (
                <tr
                  key={d.fileName}
                  className={selected === d.fileName ? 'selected' : undefined}
                  onClick={() => setSelected(selected === d.fileName ? null : d.fileName)}
                >
                  <td className="small">{d.fileName}</td>
                  {d.cells.map((cell, i) => (
                    <td key={i} className="small tabular">
                      {!cell ? (
                        '—'
                      ) : cell.status !== 'Completed' ? (
                        <span className={cell.status === 'Failed' ? 'text-bad' : 'muted'}>{cell.status === 'Failed' ? '실패' : '대기·처리 중'}</span>
                      ) : (
                        <>
                          <span className={cell.passed ? 'ok-mark' : 'bad-mark'}>{cell.passed ? '통과' : '검증 실패'}</span>
                          {cell.labeledFields ? ` · 필드 ${cell.correctFields}/${cell.labeledFields}` : ''}
                          {cell.fallback && ' · 폴백'} · {sec(cell.jobSec)}
                        </>
                      )}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {selected && <DocCompare detail={detail} fileName={selected} />}
    </>
  )
}

/** 선택한 문서를 조합별로 나란히 (원본 이미지 + 필드 비교) */
function DocCompare({ detail, fileName }: { detail: ExperimentDetail; fileName: string }) {
  const row = detail.docs.find((d) => d.fileName === fileName)
  const [results, setResults] = useState<(ExtractionResult | null)[]>([])
  const key = row?.cells.map((c) => `${c?.jobId}:${c?.status}`).join()

  useEffect(() => {
    if (!row) return
    let cancelled = false
    Promise.all(row.cells.map((c) => (c?.status === 'Completed' ? getResult(c.jobId).catch(() => null) : Promise.resolve(null)))).then(
      (r) => !cancelled && setResults(r),
    )
    return () => {
      cancelled = true
    }
    // 셀 상태가 바뀔 때만 다시 불러옴
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])

  // 표가 길어 행을 누른 뒤 한참 내려야 보였음 ➔ 고른 문서가 바뀌면 이 패널로 이동
  const panel = useRef<HTMLElement>(null)
  useEffect(() => {
    panel.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  }, [fileName])

  const firstJob = row?.cells.find(Boolean)?.jobId
  if (!row) return null
  return (
    <section className="card doc-compare" ref={panel}>
      <header className="panel-head">
        <h3>{fileName}</h3>
        <span className="small">
          {row.cells.map((c, i) =>
            c ? (
              <Link key={i} to={`/text/jobs/${c.jobId}`} className="job-link">
                조합 {i + 1} 상세
              </Link>
            ) : null,
          )}
        </span>
      </header>
      <div className="compare-body">
        {firstJob && <img className="compare-image" src={jobFileUrl(firstJob)} alt={fileName} />}
        <CompareTable columns={detail.combos.map((c, i) => ({ title: `조합 ${c.index + 1}`, result: results[i] ?? null }))} />
      </div>
    </section>
  )
}
