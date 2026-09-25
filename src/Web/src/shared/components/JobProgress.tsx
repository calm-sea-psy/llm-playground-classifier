import { useEffect, useState } from 'react'
import { isFinished, type JobDto } from '../api/types'
import type { TimelineEntry } from '../hooks/useJob'
import { PromptsUsed } from './PromptsUsed'

/** 모듈별 진행 단계 정의: 단계 이름 + (상태, 메시지) ➔ 현재 단계·진행률. 실패는 step -1 */
export interface ProgressModel {
  steps: readonly string[]
  progressOf: (job: JobDto) => { step: number; percent: number }
}

function elapsedSeconds(from: string, to: string) {
  return Math.max(0, (new Date(to).getTime() - new Date(from).getTime()) / 1000)
}

export function JobProgress({ job, timeline, model }: { job: JobDto; timeline: TimelineEntry[]; model: ProgressModel }) {
  const { steps: STEPS, progressOf } = model
  const finished = isFinished(job.status)
  const { step, percent } = progressOf(job)
  const failed = job.status === 'Failed'
  // 실패하면 마지막으로 도달한 단계까지 채움
  const reached = failed
    ? Math.max(0, ...timeline.map((e) => progressOf({ ...job, status: e.status, message: e.message }).step))
    : step

  const [now, setNow] = useState(() => new Date().toISOString())
  useEffect(() => {
    if (finished) return
    const timer = setInterval(() => setNow(new Date().toISOString()), 250)
    return () => clearInterval(timer)
  }, [finished])
  const elapsed = elapsedSeconds(job.createdAt, finished ? (job.completedAt ?? job.updatedAt) : now)

  return (
    <section className="card progress-card" aria-live="polite">
      <div className="progress-head">
        <strong className={failed ? 'text-bad' : undefined}>{job.message ?? '대기 중'}</strong>
        <span className="muted tabular">{elapsed.toFixed(1)}초</span>
      </div>

      <div
        className={`progress-bar ${failed ? 'is-failed' : finished ? 'is-done' : 'is-running'}`}
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
      >
        <div style={{ width: `${percent}%` }} />
      </div>

      <ol className="steps">
        {STEPS.map((label, i) => (
          <li
            key={label}
            className={i < reached || (i === reached && finished && !failed) ? 'done' : i === reached ? (failed ? 'failed' : 'current') : ''}
          >
            {label}
          </li>
        ))}
      </ol>

      {timeline.length > 0 && (
        <ol className="timeline">
          {timeline.map((e) => (
            <li key={`${e.at}-${e.message}`} className={e.status === 'Failed' ? 'text-bad' : undefined}>
              <span className="muted tabular">+{elapsedSeconds(job.createdAt, e.at).toFixed(1)}s</span>
              <span>{e.message}</span>
            </li>
          ))}
        </ol>
      )}

      {finished && <PromptsUsed prompts={job.prompts} />}
    </section>
  )
}
