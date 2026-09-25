import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { listJobs } from '../../../shared/api/jobs'
import { isFinished, type JobDto } from '../../../shared/api/types'
import { StatusBadge } from '../../../shared/components/StatusBadge'
import { UploadDropzone } from '../../../shared/components/UploadDropzone'
import { getImageSettings, type Population } from '../../image/api'
import { Disclaimer } from '../../image/components/Disclaimer'
import { POPULATION_NAMES } from '../../image/labels'
import { createMultimodalJob } from '../api'

const ACCEPT = '.png,.jpg,.jpeg,.bmp,.tif,.tiff,.webp'
const REFRESH_MS = 3000

/** X-ray + 소견서(텍스트 붙여넣기 또는 이미지) 업로드 + 최근 작업 */
export function MultimodalHomePage() {
  const navigate = useNavigate()
  const [xray, setXray] = useState<File | null>(null)
  const [mode, setMode] = useState<'text' | 'image'>('text')
  const [reportText, setReportText] = useState('')
  const [reportFile, setReportFile] = useState<File | null>(null)
  const [population, setPopulation] = useState<Population>('adult')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [jobs, setJobs] = useState<JobDto[]>([])
  const [model, setModel] = useState<string | null>(null)

  useEffect(() => {
    getImageSettings()
      .then((s) => setModel(s.settings.model))
      .catch(() => {})
  }, [])

  const hasRunning = jobs.some((j) => !isFinished(j.status))
  useEffect(() => {
    const load = () => listJobs('multimodal', 20).then(setJobs).catch(() => {})
    load()
    if (!hasRunning) return
    const timer = setInterval(load, REFRESH_MS)
    return () => clearInterval(timer)
  }, [hasRunning])

  const reportReady = mode === 'text' ? reportText.trim().length > 0 : reportFile !== null

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!xray || !reportReady) return
    setSubmitting(true)
    setError(null)
    try {
      const report = mode === 'text' ? { text: reportText } : { file: reportFile! }
      const job = await createMultimodalJob(xray, report, population)
      navigate(`/multimodal/jobs/${job.jobId}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setSubmitting(false)
    }
  }

  return (
    <>
      <Disclaimer />
      <form className="card upload-card" onSubmit={submit}>
        <div>
          <h2>X-ray + 소견서 통합</h2>
          <p className="muted small">
            흉부 X-ray 분석(CNN·VLM) 과 소견서 요약(LLM) 을 소견별로 맞춰 보고, 두 결과를 묶어 환자 종합 보고서를 만듭니다.
            소견서와 자동 분석이 어긋나면 표시합니다.
          </p>
        </div>

        <div className="multimodal-inputs">
          <div>
            <h4>흉부 X-ray</h4>
            <UploadDropzone
              accept={ACCEPT}
              file={xray}
              onFile={setXray}
              disabled={submitting}
              label="흉부 X-ray 를 끌어다 놓거나 클릭해서 선택"
            />
          </div>
          <div>
            <h4>소견서</h4>
            <div className="tabs small">
              <button type="button" className={mode === 'text' ? 'on' : ''} onClick={() => setMode('text')}>
                텍스트 붙여넣기
              </button>
              <button type="button" className={mode === 'image' ? 'on' : ''} onClick={() => setMode('image')}>
                소견서 이미지 (OCR)
              </button>
            </div>
            {mode === 'text' ? (
              <textarea
                className="report-input"
                value={reportText}
                onChange={(e) => setReportText(e.target.value)}
                placeholder={'예) INDICATION: ...\nFINDINGS: ...\nIMPRESSION: ...'}
                disabled={submitting}
                maxLength={20000}
              />
            ) : (
              <UploadDropzone
                accept={ACCEPT}
                file={reportFile}
                onFile={setReportFile}
                disabled={submitting}
                label="소견서 이미지를 끌어다 놓거나 클릭해서 선택"
              />
            )}
          </div>
        </div>

        <fieldset className="population" disabled={submitting}>
          <legend className="small">대상</legend>
          {(['adult', 'pediatric'] as const).map((p) => (
            <label key={p} className="small">
              <input type="radio" name="population" value={p} checked={population === p} onChange={() => setPopulation(p)} />{' '}
              {POPULATION_NAMES[p]}
            </label>
          ))}
        </fieldset>

        <div className="upload-actions">
          <span className="muted small">
            영상 분석 설정은 <Link to="/image">X-ray 판독 기본 설정</Link>을 따릅니다
            {model && (
              <>
                {' '}· LLM <strong>{model}</strong> (VLM 판독 · 소견서 요약 · 종합 보고서 공통)
              </>
            )}
          </span>
          <button type="submit" className="primary" disabled={!xray || !reportReady || submitting}>
            {submitting ? '업로드 중…' : '종합 보고서 만들기'}
          </button>
        </div>
        {error && <p className="text-bad small">{error}</p>}
      </form>

      <section className="card">
        <h3>최근 작업</h3>
        {jobs.length === 0 ? (
          <p className="muted small">아직 작업이 없습니다.</p>
        ) : (
          <div className="table-scroll">
            <table className="jobs">
              <thead>
                <tr>
                  <th>X-ray</th>
                  <th>상태</th>
                  <th className="hide-sm">메시지</th>
                  <th title="끝난 작업은 완료 시각, 진행 중이면 접수 시각">완료 시각</th>
                </tr>
              </thead>
              <tbody>
                {jobs.map((j) => (
                  <tr key={j.jobId}>
                    <td>
                      <Link to={`/multimodal/jobs/${j.jobId}`}>{j.fileName}</Link>
                    </td>
                    <td>
                      <StatusBadge status={j.status} />
                    </td>
                    <td className="small ellipsis hide-sm" title={j.message ?? undefined}>
                      {j.message}
                    </td>
                    <td className="muted small tabular">
                      {j.completedAt ? new Date(j.completedAt).toLocaleString('ko-KR') : `접수 ${new Date(j.createdAt).toLocaleString('ko-KR')}`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </>
  )
}
