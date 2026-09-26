import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { listJobs } from '../../../shared/api/jobs'
import { isFinished, type JobDto } from '../../../shared/api/types'
import { StatusBadge } from '../../../shared/components/StatusBadge'
import { UploadDropzone } from '../../../shared/components/UploadDropzone'
import { createTextJob, getPacks, getSettings, type Pack, type SettingsDto } from '../api'

// 이미지는 OCR, PDF · DOCX 는 원문을 바로 읽음 (스캔 PDF 는 OCR)
const ACCEPT = '.png,.jpg,.jpeg,.bmp,.tif,.tiff,.webp,.pdf,.docx'
const AUTO = ''
const REFRESH_MS = 3000

/** 업로드 + 최근 작업 목록 */
export function TextHomePage() {
  const navigate = useNavigate()
  const [file, setFile] = useState<File | null>(null)
  const [settings, setSettings] = useState<SettingsDto | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [jobs, setJobs] = useState<JobDto[]>([])
  const [packs, setPacks] = useState<Pack[]>([])
  const [documentType, setDocumentType] = useState(AUTO)

  useEffect(() => {
    getSettings()
      .then(setSettings)
      .catch(() => setSettings(null))
    getPacks()
      .then(setPacks)
      .catch(() => setPacks([]))
  }, [])

  const autoNames = packs.filter((p) => p.autoClassified).map((p) => p.displayName)
  const chosen = packs.find((p) => p.id === documentType)

  // 진행 중인 작업이 있으면 목록을 주기적으로 갱신
  const hasRunning = jobs.some((j) => !isFinished(j.status))
  useEffect(() => {
    const load = () => listJobs('text', 20).then(setJobs).catch(() => {})
    load()
    if (!hasRunning) return
    const timer = setInterval(load, REFRESH_MS)
    return () => clearInterval(timer)
  }, [hasRunning])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!file) return
    setSubmitting(true)
    setError(null)
    try {
      const job = await createTextJob(file, undefined, documentType || undefined)
      navigate(`/text/jobs/${job.jobId}`)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setSubmitting(false)
    }
  }

  return (
    <>
      <form className="card upload-card" onSubmit={submit}>
        <div>
          <h2>문서 업로드</h2>
          <p className="muted small">
            원문(이미지는 OCR, PDF · DOCX 는 그대로) ➔ 문서 분류 ➔ 필드 추출(LLM, 문서 종류 팩) ➔ 규칙 검증 ➔ 필요하면 VLM 폴백
          </p>
        </div>
        <label className="doc-type">
          <span>문서 종류</span>
          <select value={documentType} onChange={(e) => setDocumentType(e.target.value)} disabled={submitting}>
            <option value={AUTO}>자동 분류{autoNames.length ? ` (${autoNames.join(' · ')})` : ''}</option>
            {packs.map((p) => (
              <option key={p.id} value={p.id}>
                {p.displayName}
              </option>
            ))}
          </select>
          <span className="muted small">
            {chosen
              ? `${chosen.displayName} 팩 v${chosen.version} 으로 바로 추출 (분류 생략)${chosen.autoClassified ? '' : ' · 자동 분류 대상이 아니라 직접 골라야 하는 종류'}`
              : 'LLM 이 문서 종류를 고름. 목록에 없는 종류(예: 이력서)는 직접 선택'}
          </span>
        </label>
        <UploadDropzone accept={ACCEPT} file={file} onFile={setFile} disabled={submitting} />
        <div className="upload-actions">
          <span className="small" title={settings?.note ?? undefined}>
            처리 설정: <strong>{settings?.summary ?? '…'}</strong>{' '}
            <Link to="/text" className="muted">
              (모델 비교에서 변경)
            </Link>
          </span>
          <button type="submit" className="primary" disabled={!file || submitting}>
            {submitting ? '업로드 중…' : '처리 시작'}
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
                <th>파일</th>
                <th>상태</th>
                <th className="hide-sm">모델</th>
                <th className="hide-sm">메시지</th>
                <th title="끝난 작업은 완료 시각, 진행 중이면 접수 시각">완료 시각</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((j) => (
                <tr key={j.jobId}>
                  <td>
                    <Link to={`/text/jobs/${j.jobId}`}>{j.fileName}</Link>
                  </td>
                  <td>
                    <StatusBadge status={j.status} />
                  </td>
                  <td className="muted small hide-sm">{j.model ?? '—'}</td>
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
