import { useCallback, useEffect, useState } from 'react'
import { api, type Pack } from './api.ts'
import { DocumentsPage } from './pages/DocumentsPage.tsx'
import { ExportPage } from './pages/ExportPage.tsx'
import { ReviewPage } from './pages/ReviewPage.tsx'
import { SettingsPage } from './pages/SettingsPage.tsx'
import { StatsPage } from './pages/StatsPage.tsx'
import { StatusPage } from './pages/StatusPage.tsx'
import { UploadPage } from './pages/UploadPage.tsx'
import { parseRoute } from './route.ts'

const tabs = [
  { path: '/', label: '문서' },
  { path: '/upload', label: '올리기' },
  { path: '/export', label: '내보내기' },
  { path: '/stats', label: '수정률' },
  { path: '/settings', label: '설정' },
  { path: '/status', label: '상태 점검' },
]

export default function App() {
  const [route, setRoute] = useState(parseRoute)
  const [packs, setPacks] = useState<Pack[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reviewCount, setReviewCount] = useState(0)
  const [queue, setQueue] = useState<{ paused: boolean; queued: number } | null>(null)

  useEffect(() => {
    const onHash = () => setRoute(parseRoute())
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  useEffect(() => {
    api.packs().then(setPacks, (e: Error) => setError(e.message))
  }, [])

  // 확인 필요 건수 배지 · 일시 정지 상태 (10초마다, 알림 영역 메뉴에서 바꿀 수도 있음)
  const refreshCount = useCallback(() => {
    api.queue().then(setQueue, () => {})
    api.counts().then(
      (rows) => setReviewCount(rows.filter((r) => r.status === 'NeedsReview').reduce((s, r) => s + r.count, 0)),
      () => {},
    )
  }, [])
  useEffect(() => {
    refreshCount()
    const t = window.setInterval(refreshCount, 10000)
    return () => window.clearInterval(t)
  }, [refreshCount, route])

  const docMatch = route.path.match(/^\/doc\/(\d+)$/)
  const active = docMatch ? '/' : route.path

  let page
  if (!packs) page = error ? <p className="error">프로그램에 연결하지 못했습니다: {error}</p> : <p className="muted">불러오는 중…</p>
  else if (docMatch) page = <ReviewPage key={docMatch[1]} id={Number(docMatch[1])} packs={packs} onChanged={refreshCount} />
  else if (route.path === '/upload') page = <UploadPage packs={packs} />
  else if (route.path === '/export') page = <ExportPage packs={packs} />
  else if (route.path === '/stats') page = <StatsPage />
  else if (route.path === '/settings') page = <SettingsPage packs={packs} />
  else if (route.path === '/status') page = <StatusPage />
  else page = <DocumentsPage packs={packs} query={route.query} />

  return (
    <div className="app">
      <header className="top">
        <a className="brand" href="#/">문서 전산화</a>
        <nav>
          {tabs.map((t) => (
            <a key={t.path} href={`#${t.path}`} className={active === t.path ? 'tab active' : 'tab'}>
              {t.label}
              {t.path === '/' && reviewCount > 0 && <span className="count" title="확인 필요">{reviewCount}</span>}
            </a>
          ))}
        </nav>
      </header>
      {queue?.paused && (
        <div className="paused">
          처리를 일시 정지했습니다{queue.queued > 0 ? ` (대기 ${queue.queued}건)` : ''}. 새 문서는 접수만 됩니다.
          <button onClick={() => api.setPaused(false).then(refreshCount, () => {})}>다시 시작</button>
        </div>
      )}
      <main>{page}</main>
    </div>
  )
}
