import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { JobProgress } from '../../../shared/components/JobProgress'
import { StatusBadge } from '../../../shared/components/StatusBadge'
import { useJob } from '../../../shared/hooks/useJob'
import { Disclaimer } from '../../image/components/Disclaimer'
import { ImageResultView } from '../../image/components/ImageResultView'
import { getMultimodalResult, reportFileUrl, type ConcordanceRow, type MultimodalResult } from '../api'
import { multimodalProgress } from '../progress'

/** 작업 1건: 진행(SignalR) ➔ 종합 보고서 · 일치 비교 · 소견서 요약 ➔ X-ray 상세(Step 2 화면 재사용) */
export function MultimodalJobPage() {
  const { jobId = '' } = useParams()
  return <JobDetail key={jobId} jobId={jobId} />
}

function JobDetail({ jobId }: { jobId: string }) {
  const { job, timeline, error, polling } = useJob(jobId)

  if (error) {
    return (
      <section className="card">
        <p className="text-bad">작업을 불러오지 못했습니다: {error}</p>
        <Link to="/multimodal">목록으로</Link>
      </section>
    )
  }
  if (!job) return <section className="card muted">불러오는 중…</section>

  return (
    <>
      <div className="page-head">
        <Link to="/multimodal" className="back">
          ← 통합
        </Link>
        <h2>{job.fileName}</h2>
        <StatusBadge status={job.status} />
        {job.model && <span className="muted small">{job.model}</span>}
        {polling && <span className="muted small">(실시간 연결 실패, 3초마다 조회 중)</span>}
      </div>
      <Disclaimer />

      <JobProgress job={job} timeline={timeline} model={multimodalProgress} />

      {job.status === 'Failed' && job.error && <section className="card text-bad">실패 사유: {job.error}</section>}
      {job.status === 'Completed' && <MultimodalResultView jobId={job.jobId} />}
    </>
  )
}

const yesNo = (v: boolean | null | undefined, yes = '있음', no = '없음') =>
  v == null ? <span className="muted">—</span> : v ? <strong>{yes}</strong> : <span>{no}</span>

function ConcordanceTable({ rows }: { rows: ConcordanceRow[] }) {
  return (
    <div className="table-scroll">
      <table className="jobs concordance">
        <thead>
          <tr>
            <th>소견</th>
            <th>소견서</th>
            <th>CNN</th>
            <th>VLM</th>
            <th>소견서 vs CNN</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key} className={r.agree === false ? 'disagree' : undefined}>
              <td>{r.name}</td>
              <td>{yesNo(r.report)}</td>
              <td>
                {yesNo(r.cnn, '양성', '음성')}
                {r.cnnProbability != null && <span className="muted small"> {r.cnnProbability.toFixed(2)}</span>}
              </td>
              <td>{r.key === 'pneumonia' ? yesNo(r.vlm, '의심', '의심 아님') : <span className="muted small">—</span>}</td>
              <td>
                {r.agree == null ? (
                  <span className="muted">—</span>
                ) : r.agree ? (
                  <span className="ok-mark">일치</span>
                ) : (
                  <span className="chip chip-bad">불일치</span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function MultimodalResultView({ jobId }: { jobId: string }) {
  const [result, setResult] = useState<MultimodalResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [showText, setShowText] = useState(false)

  useEffect(() => {
    getMultimodalResult(jobId)
      .then(setResult)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [jobId])

  if (error) return <section className="card text-bad">결과를 불러오지 못했습니다: {error}</section>
  if (!result) return <section className="card muted">결과 불러오는 중…</section>

  const final = result.finalReport
  const summary = result.reportSummary
  const disagree = result.concordance.filter((r) => r.agree === false).length

  return (
    <>
      <section className={`card verdict ${result.needsReview ? 'verdict-bad' : 'verdict-ok'}`}>
        <strong>{result.needsReview ? '사람 확인 필요' : '영상 분석과 소견서가 크게 어긋나지 않습니다'}</strong>
        <span className="small">
          소견서 vs CNN 불일치 {disagree}개 · 소견서 {result.reportSource === 'ocr' ? '이미지(OCR)' : '텍스트'} · LLM{' '}
          {(result.llmElapsedMs / 1000).toFixed(1)}초
        </span>
      </section>

      <section className="card panel">
        <header className="panel-head">
          <h3>환자 종합 보고서</h3>
          <span className="muted small">소견서 요약 + CNN + VLM 판독만으로 작성 (새 소견 추가 안 함)</span>
        </header>
        {!final ? (
          <p className="text-bad small">종합 보고서 형식 오류</p>
        ) : (
          <div className="report">
            <p className="impression">{final.summary}</p>
            <h4>핵심 소견</h4>
            <ul>
              {final.key_findings.map((f) => (
                <li key={f}>{f}</li>
              ))}
            </ul>
            <h4>소견서와 자동 분석의 일치</h4>
            <p className={disagree > 0 ? 'text-warn' : undefined}>{final.concordance_note}</p>
            {final.recommendations.length > 0 && (
              <>
                <h4>권고</h4>
                <ul>
                  {final.recommendations.map((r) => (
                    <li key={r}>{r}</li>
                  ))}
                </ul>
              </>
            )}
          </div>
        )}
      </section>

      <div className="image-result-grid">
        <section className="card panel">
          <header className="panel-head">
            <h3>소견별 일치 비교</h3>
          </header>
          <ConcordanceTable rows={result.concordance} />
          {result.issues.length > 0 && (
            <ul className="issues">
              {result.issues.map((i) => (
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

        <section className="card panel">
          <header className="panel-head">
            <h3>소견서 요약</h3>
            <button className="link-button small" onClick={() => setShowText(!showText)}>
              {showText ? '요약 보기' : result.reportSource === 'ocr' ? '원본·OCR 텍스트 보기' : '원문 보기'}
            </button>
          </header>
          {showText ? (
            <>
              {result.reportSource === 'ocr' && <img className="report-image" src={reportFileUrl(jobId)} alt="소견서" />}
              <pre className="report-text">{result.reportText}</pre>
            </>
          ) : !summary ? (
            <p className="text-bad small">소견서 요약 형식 오류</p>
          ) : (
            <dl className="report-summary small">
              <dt>최종 진단</dt>
              <dd>
                <strong>{summary.final_diagnosis}</strong> {summary.normal && <span className="chip chip-ok">정상</span>}
              </dd>
              {summary.indication && (
                <>
                  <dt>검사 사유</dt>
                  <dd>{summary.indication}</dd>
                </>
              )}
              {summary.key_symptoms.length > 0 && (
                <>
                  <dt>핵심 증상</dt>
                  <dd>{summary.key_symptoms.join(', ')}</dd>
                </>
              )}
              <dt>소견</dt>
              <dd>
                <ul>
                  {summary.findings.map((f) => (
                    <li key={f}>{f}</li>
                  ))}
                </ul>
              </dd>
            </dl>
          )}
          {!showText && result.glossary != null && (
            <details className="glossary small">
              <summary>참고한 용어 정의 {result.glossary.length}개</summary>
              {result.glossary.length === 0 ? (
                <p className="muted">소견서에 용어집의 용어가 없어 붙이지 않았습니다.</p>
              ) : (
                <ul>
                  {result.glossary.map((g) => (
                    <li key={g}>{g}</li>
                  ))}
                </ul>
              )}
              <p className="muted">소견서에 나온 용어의 정의만 용어집에서 찾아 요약 프롬프트에 붙였습니다.</p>
            </details>
          )}
        </section>
      </div>

      <h3 className="section-title">X-ray 분석 상세</h3>
      <ImageResultView jobId={jobId} />
    </>
  )
}
