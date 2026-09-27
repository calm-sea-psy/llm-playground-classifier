import { createContext, useContext } from 'react'
import { getJob } from '../api/jobs'
import { isFinished, type JobDto, type JobStatus } from '../api/types'
import { subscribeJob } from '../signalr/jobHub'

/** 진행 상황 바꾸기: 제목 · 한 줄 설명 · 로그 줄 추가 (설치 파일 만들기) */
export type BusyUpdate = (change: { title?: string; detail?: string | null; lines?: string[] }) => void

export interface BusyApi {
  /** 처리 중 레이어를 띄우고 work 가 끝나면(실패해도) 닫음. work 의 결과 · 예외는 그대로 돌려줌 */
  run: <T>(title: string, work: (update: BusyUpdate) => Promise<T>) => Promise<T>
  /** 레이어가 떠 있는지 */
  active: boolean
}

export const BusyContext = createContext<BusyApi | null>(null)

export function useBusy(): BusyApi {
  const busy = useContext(BusyContext)
  if (!busy) throw new Error('BusyProvider 안에서만 쓸 수 있습니다')
  return busy
}

const STATUS_TEXT: Record<JobStatus, string> = {
  Queued: '대기열에서 순서를 기다리는 중',
  OcrRunning: 'OCR 중',
  CnnRunning: 'CNN 판독 중',
  LlmRunning: 'LLM 처리 중',
  Validating: '검증 중',
  Completed: '완료',
  Failed: '실패',
}

/**
 * 작업이 끝날 때까지 기다림 (처리 시작 · 종합 보고서 · 판독). 진행 문장은 onProgress 로 (SignalR, 연결이 안 되면 3초마다 조회).
 * 끝난 작업 상태를 돌려줌 (실패도 돌려줌 ➔ 작업 화면이 실패 사유를 보여 줌)
 */
export function waitForJob(jobId: string, onProgress: (text: string) => void): Promise<JobDto> {
  return new Promise((resolve, reject) => {
    let done = false
    let timer = 0
    const seen = (job: JobDto) => {
      if (done) return
      onProgress(job.message ?? STATUS_TEXT[job.status])
      if (isFinished(job.status)) {
        done = true
        window.clearInterval(timer)
        unsubscribe()
        resolve(job)
      }
    }
    const poll = () => getJob(jobId).then(seen, () => {})
    // SignalR 이 알림을 놓쳐도 끝나게 10초마다 확인, 연결 자체가 안 되면 3초마다
    timer = window.setInterval(poll, 10000)
    const unsubscribe = subscribeJob(jobId, seen, () => {
      window.clearInterval(timer)
      timer = window.setInterval(poll, 3000)
    })
    // 구독 뒤 현재 상태 (그 사이에 끝났을 수도 있음)
    getJob(jobId).then(seen, (e) => {
      if (!done) {
        done = true
        window.clearInterval(timer)
        unsubscribe()
        reject(e)
      }
    })
  })
}
