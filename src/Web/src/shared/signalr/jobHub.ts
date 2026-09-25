import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { JobDto } from '../api/types'

// 앱 전체에서 SignalR 연결 1개를 공유하고, 작업별 구독(SubscribeJob)을 참조 수로 관리한다.
type Handler = (job: JobDto) => void

const handlers = new Map<string, Set<Handler>>()
let connection: HubConnection | null = null
let starting: Promise<void> | null = null

function getConnection(): HubConnection {
  if (connection) return connection
  connection = new HubConnectionBuilder()
    .withUrl('/hubs/jobs')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
  connection.on('JobUpdated', (job: JobDto) => handlers.get(job.jobId)?.forEach((h) => h(job)))
  // 재연결되면 서버 쪽 그룹이 사라졌으므로 다시 구독
  connection.onreconnected(() => {
    for (const jobId of handlers.keys()) connection!.invoke('SubscribeJob', jobId).catch(console.warn)
  })
  return connection
}

/** 연결될 때까지 대기. 재연결 중이면 onreconnected 가 구독을 복구하므로 false */
async function ensureConnected(): Promise<boolean> {
  const c = getConnection()
  if (c.state === HubConnectionState.Disconnected) {
    starting ??= c.start().finally(() => (starting = null))
  }
  if (starting) await starting
  return c.state === HubConnectionState.Connected
}

/**
 * 작업 진행 알림 구독. 반환된 함수로 해제.
 * onError: 연결 자체가 실패했을 때 (호출 측에서 폴링으로 대체)
 */
export function subscribeJob(jobId: string, handler: Handler, onError?: (e: unknown) => void): () => void {
  let set = handlers.get(jobId)
  if (!set) {
    set = new Set()
    handlers.set(jobId, set)
    ensureConnected()
      .then((connected) => {
        if (connected && handlers.has(jobId)) return getConnection().invoke('SubscribeJob', jobId)
      })
      .catch((e) => onError?.(e))
  }
  set.add(handler)

  return () => {
    set.delete(handler)
    if (set.size > 0) return
    handlers.delete(jobId)
    if (connection?.state === HubConnectionState.Connected) {
      connection.invoke('UnsubscribeJob', jobId).catch(() => {})
    }
  }
}
