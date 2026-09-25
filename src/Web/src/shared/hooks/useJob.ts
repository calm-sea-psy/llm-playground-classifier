import { useCallback, useEffect, useState } from 'react'
import { getJob } from '../api/jobs'
import { isFinished, type JobDto, type JobStatus } from '../api/types'
import { subscribeJob } from '../signalr/jobHub'

export interface TimelineEntry {
  status: JobStatus
  message: string
  at: string
}

const POLL_MS = 3000

/**
 * 작업 상태 = GET /api/jobs/{id} (현재 상태) + SignalR JobUpdated (이후 변화).
 * 구독을 먼저 걸고 GET 을 하므로 그 사이의 변화도 놓치지 않는다. SignalR 연결에 실패하면 폴링으로 대체.
 * 다른 작업으로 바뀔 때는 호출 측 컴포넌트에 key={jobId} 를 줘서 상태를 새로 시작한다.
 */
export function useJob(jobId: string) {
  const [job, setJob] = useState<JobDto | null>(null)
  const [timeline, setTimeline] = useState<TimelineEntry[]>([])
  const [error, setError] = useState<string | null>(null)
  const [polling, setPolling] = useState(false)

  const apply = useCallback((next: JobDto) => {
    // 늦게 도착한 오래된 상태가 최신 상태를 덮어쓰지 않도록
    setJob((prev) => (prev && prev.updatedAt > next.updatedAt ? prev : next))
    const message = next.message
    if (!message) return
    setTimeline((prev) =>
      prev.some((e) => e.at === next.updatedAt && e.message === message)
        ? prev
        : [...prev, { status: next.status, message, at: next.updatedAt }].sort((a, b) => a.at.localeCompare(b.at)),
    )
  }, [])

  useEffect(() => {
    const controller = new AbortController()

    const unsubscribe = subscribeJob(jobId, apply, () => setPolling(true))
    getJob(jobId, controller.signal)
      .then(apply)
      .catch((e) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e))
      })

    return () => {
      controller.abort()
      unsubscribe()
    }
  }, [jobId, apply])

  // SignalR 을 못 쓸 때만: 끝날 때까지 주기적으로 조회
  const finished = job ? isFinished(job.status) : false
  useEffect(() => {
    if (!polling || finished) return
    const timer = setInterval(() => getJob(jobId).then(apply).catch(() => {}), POLL_MS)
    return () => clearInterval(timer)
  }, [jobId, polling, finished, apply])

  return { job, timeline, error, polling }
}
