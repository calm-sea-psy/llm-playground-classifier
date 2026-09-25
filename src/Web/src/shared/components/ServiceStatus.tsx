import { useEffect, useState } from 'react'

interface TargetStatus {
  name: string
  state: 'Unknown' | 'Starting' | 'Up' | 'Warning' | 'Down'
  reason: string | null
  gaveUp: boolean
}

const POLL_MS = 5000
const LABEL: Record<TargetStatus['state'], string> = {
  Up: '정상',
  Warning: '주의',
  Down: '중단',
  Starting: '시작 중',
  Unknown: '확인 중',
}

/** 헤더 오른쪽 서비스 상태 (모니터링 서버 /status). 모니터가 꺼져 있으면 표시하지 않음 */
export function ServiceStatus() {
  const [targets, setTargets] = useState<TargetStatus[] | null>(null)

  useEffect(() => {
    let cancelled = false
    const load = () =>
      fetch('/monitor/status')
        .then((r) => (r.ok ? r.json() : Promise.reject(r.status)))
        .then((d: { targets: TargetStatus[] }) => !cancelled && setTargets(d.targets))
        .catch(() => !cancelled && setTargets(null))
    load()
    const timer = setInterval(load, POLL_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  if (!targets || targets.length === 0) return null
  const bad = targets.filter((t) => t.state !== 'Up')

  return (
    <a className="service-status" href="http://127.0.0.1:5100/" target="_blank" rel="noreferrer"
       title={bad.length ? bad.map((t) => `${t.name}: ${LABEL[t.state]}${t.reason ? ` · ${t.reason}` : ''}`).join('\n') : '모든 서비스 정상'}>
      {targets.map((t) => (
        <span key={t.name} className={`svc svc-${t.state}`}>
          <i />
          {t.name}
        </span>
      ))}
    </a>
  )
}
