import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { listJobs } from '../../../shared/api/jobs'
import { isFinished, type JobDto } from '../../../shared/api/types'
import { StatusBadge } from '../../../shared/components/StatusBadge'
import { UploadDropzone } from '../../../shared/components/UploadDropzone'
import { createImageJob, getImageSettings, type ImageSettingsDto } from '../api'
import { AppliedPipeline } from '../components/AppliedPipeline'
import { Disclaimer } from '../components/Disclaimer'

const ACCEPT = '.png,.jpg,.jpeg,.bmp,.tif,.tiff,.webp'
const REFRESH_MS = 3000

/** 이미지 업로드 + 최근 작업 */
export function ImageHomePage() {
  const navigate = useNavigate()
  const [settings, setSettings] = useState<ImageSettingsDto | null>(null)
  const [file, setFile] = useState<File | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [jobs, setJobs] = useState<JobDto[]>([])

  useEffect(() => {
    getImageSettings()
      .then(setSettings)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [])

  // 진행 중인 작업이 있으면 목록을 주기적으로 갱신
  const hasRunning = jobs.some((j) => !isFinished(j.status))
  useEffect(() => {
    const load = () => listJobs('image', 20).then(setJobs).catch(() => {})
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
      const job = await createImageJob(file)
      navigate(`/image/jobs/${job.jobId}`)
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
          <h2>이미지 업로드</h2>
          <p className="muted small">CNN 이진 판단 + 다중 항목 18개(Grad-CAM) ➔ VLM 판독 초안 ➔ 두 판단 교차 검증</p>
        </div>
        <UploadDropzone
          accept={ACCEPT}
          file={file}
          onFile={setFile}
          disabled={submitting}
          label="이미지를 끌어다 놓거나 클릭해서 선택"
        />
        <AppliedPipeline settings={settings} />
        <div className="upload-actions">
          <span />
          <button type="submit" className="primary" disabled={!file || submitting}>
            {submitting ? '업로드 중…' : '판독 시작'}
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
                  <th className="hide-sm">메시지</th>
                  <th title="끝난 작업은 완료 시각, 진행 중이면 접수 시각">완료 시각</th>
                </tr>
              </thead>
              <tbody>
                {jobs.map((j) => (
                  <tr key={j.jobId}>
                    <td>
                      <Link to={`/image/jobs/${j.jobId}`}>{j.fileName}</Link>
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
