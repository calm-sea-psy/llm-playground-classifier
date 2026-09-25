import type { JobStatus } from '../api/types'

const LABELS: Record<JobStatus, string> = {
  Queued: '대기',
  OcrRunning: 'OCR',
  CnnRunning: 'CNN',
  LlmRunning: 'LLM',
  Validating: '검증',
  Completed: '완료',
  Failed: '실패',
}

export function StatusBadge({ status }: { status: JobStatus }) {
  const tone = status === 'Completed' ? 'ok' : status === 'Failed' ? 'bad' : 'run'
  return <span className={`badge badge-${tone}`}>{LABELS[status]}</span>
}
