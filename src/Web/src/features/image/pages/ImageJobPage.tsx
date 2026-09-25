import { Link, useParams } from 'react-router-dom'
import { JobProgress } from '../../../shared/components/JobProgress'
import { StatusBadge } from '../../../shared/components/StatusBadge'
import { useJob } from '../../../shared/hooks/useJob'
import { Disclaimer } from '../components/Disclaimer'
import { ImageResultView } from '../components/ImageResultView'
import { imageProgress } from '../progress'

/** 작업 1건: 진행 상황(SignalR) ➔ 완료되면 판독 결과. 다른 작업으로 이동하면 key 로 상태를 새로 시작 */
export function ImageJobPage() {
  const { jobId = '' } = useParams()
  return <JobDetail key={jobId} jobId={jobId} />
}

function JobDetail({ jobId }: { jobId: string }) {
  const { job, timeline, error, polling } = useJob(jobId)

  if (error) {
    return (
      <section className="card">
        <p className="text-bad">작업을 불러오지 못했습니다: {error}</p>
        <Link to="/image/process">목록으로</Link>
      </section>
    )
  }
  if (!job) return <section className="card muted">불러오는 중…</section>

  return (
    <>
      <div className="page-head">
        <Link to="/image/process" className="back">
          ← 판독
        </Link>
        <h2>{job.fileName}</h2>
        <StatusBadge status={job.status} />
        {job.model && <span className="muted small">{job.model}</span>}
        {polling && <span className="muted small">(실시간 연결 실패, 3초마다 조회 중)</span>}
      </div>
      <Disclaimer />

      <JobProgress job={job} timeline={timeline} model={imageProgress} />

      {job.status === 'Failed' && job.error && <section className="card text-bad">실패 사유: {job.error}</section>}
      {job.status === 'Completed' && <ImageResultView jobId={job.jobId} />}
    </>
  )
}
