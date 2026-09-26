import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { listJobs } from '../../../shared/api/jobs'
import { isFinished, type JobDto } from '../../../shared/api/types'
import { StatusBadge } from '../../../shared/components/StatusBadge'
import { UploadDropzone } from '../../../shared/components/UploadDropzone'
import { getImageSettings, type ImageSettingsDto } from '../../image/api'
import { AppliedPipeline } from '../../image/components/AppliedPipeline'
import { Disclaimer } from '../../image/components/Disclaimer'
import { createMultimodalJob } from '../api'

const ACCEPT = '.png,.jpg,.jpeg,.bmp,.tif,.tiff,.webp'
const REFRESH_MS = 3000

/** 이미지 + 텍스트 문서 이미지(OCR) 업로드 + 최근 작업. 이미지 분석 설정은 CNN + VLM 판독 기본 설정 그대로 (표시만) */
export function MultimodalHomePage() {
  const navigate = useNavigate()
  const [xray, setXray] = useState<File | null>(null)
  const [reportFile, setReportFile] = useState<File | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [jobs, setJobs] = useState<JobDto[]>([])
  const [settings, setSettings] = useState<ImageSettingsDto | null>(null)

  useEffect(() => {
    getImageSettings()
      .then(setSettings)
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

  const model = settings?.settings.model

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!xray || !reportFile) return
    setSubmitting(true)
    setError(null)
    try {
      const job = await createMultimodalJob(xray, reportFile)
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
          <h2>CNN + VLM + 텍스트 추출</h2>
          <p className="muted small">
            이미지 분석(CNN · VLM)과 텍스트 문서 요약(LLM)을 항목별로 맞춰 보고, 두 결과를 묶어 종합 보고서를 만듭니다.
            문서와 자동 분석이 어긋나면 표시합니다.
          </p>
        </div>

        <div className="multimodal-inputs">
          <div>
            <h4>이미지</h4>
            <UploadDropzone
              accept={ACCEPT}
              file={xray}
              onFile={setXray}
              disabled={submitting}
              label="이미지를 끌어다 놓거나 클릭해서 선택"
            />
          </div>
          <div>
            <h4>텍스트 문서 (OCR)</h4>
            <UploadDropzone
              accept={ACCEPT}
              file={reportFile}
              onFile={setReportFile}
              disabled={submitting}
              label="문서 이미지를 끌어다 놓거나 클릭해서 선택"
            />
          </div>
        </div>

        <AppliedPipeline
          settings={settings}
          extra={[
            { label: '텍스트 문서', value: '문서 이미지 ➔ OCR ➔ 읽기 순서 텍스트 ➔ LLM 요약' },
            ...(model ? [{ label: 'LLM', value: `${model} (VLM 판독 · 문서 요약 · 종합 보고서 공통)` }] : []),
          ]}
        />

        <div className="upload-actions">
          <span />
          <button type="submit" className="primary" disabled={!xray || !reportFile || submitting}>
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
                  <th>이미지</th>
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
