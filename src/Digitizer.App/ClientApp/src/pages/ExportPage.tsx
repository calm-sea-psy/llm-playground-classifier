import { useCallback, useEffect, useState } from 'react'
import { api, formatTime, type Pack } from '../api.ts'

type Pending = Awaited<ReturnType<typeof api.pendingExports>>
type History = Awaited<ReturnType<typeof api.exports>>

export function ExportPage({ packs }: { packs: Pack[] }) {
  const [pending, setPending] = useState<Pending>([])
  const [history, setHistory] = useState<History>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [message, setMessage] = useState<{ tone: 'ok' | 'bad'; text: string } | null>(null)
  const [exportDays, setExportDays] = useState<number | null>(null)

  const load = useCallback(() => {
    api.pendingExports().then(setPending, (e: Error) => setMessage({ tone: 'bad', text: e.message }))
    api.exports().then(setHistory, () => {})
    api.settings().then((s) => setExportDays(s.settings.exportRetentionDays), () => {})
  }, [])
  useEffect(load, [load])

  const run = async (packId: string, includeExported: boolean) => {
    setBusy(packId)
    setMessage(null)
    try {
      const r = await api.exportPack(packId, includeExported)
      setMessage({ tone: 'ok', text: `${r.fileName} (${r.documentCount}건) 을 만들었습니다` })
      window.location.assign(`/api/exports/${r.id}/file`)
      load()
    } catch (e) {
      setMessage({ tone: 'bad', text: (e as Error).message })
    } finally {
      setBusy(null)
    }
  }

  const packName = (id: string) => packs.find((p) => p.id === id)?.displayName ?? id

  return (
    <section className="narrow">
      <h2>엑셀 내보내기</h2>
      <p className="muted">
        승인한 문서와, 합격 종류(영수증)에서 검증을 통과한 문서만 내보냅니다. 조건부 합격 종류(이력서)는 승인한 문서만. 파일은 문서 폴더의 <b>내보내기</b> 에도 남고,
        {exportDays === null ? '' : exportDays === 0 ? ' 자동으로 지우지 않습니다.' : ` ${exportDays}일이 지나면 지웁니다 (설정의 엑셀 보관 기한).`}
      </p>
      {message && <p className={message.tone === 'ok' ? 'ok-text' : 'error'}>{message.text}</p>}
      <div className="cards">
        {pending.map((p) => {
          const pack = packs.find((x) => x.id === p.packId)
          return (
            <div key={p.packId} className="card">
              <h3>{p.displayName}</h3>
              <p>
                새로 내보낼 문서 <b>{p.notExported}</b>건
                <span className="muted"> · 이미 내보낸 {p.exported}건</span>
              </p>
              {pack?.release?.status === 'conditional' && <p className="sub">조건부 합격: 승인한 문서만</p>}
              <div className="buttons">
                <button className="primary" disabled={busy !== null || p.notExported === 0} onClick={() => run(p.packId, false)}>
                  {busy === p.packId ? '만드는 중…' : '새 문서 내보내기'}
                </button>
                <button disabled={busy !== null || p.notExported + p.exported === 0} onClick={() => run(p.packId, true)}>전체 다시</button>
              </div>
            </div>
          )
        })}
      </div>

      <h3>내보낸 기록</h3>
      {history.length === 0 ? (
        <p className="muted">아직 없습니다.</p>
      ) : (
        <table className="list">
          <thead>
            <tr><th>시각</th><th>종류</th><th>파일</th><th>문서</th></tr>
          </thead>
          <tbody>
            {history.map((h) => (
              <tr key={h.id}>
                <td className="time">{formatTime(h.createdAt)}</td>
                <td>{packName(h.packId)}</td>
                <td>
                  {h.deletedAt ? (
                    <span className="muted">{h.fileName} (보관 기한이 지나 {formatTime(h.deletedAt)} 에 지움)</span>
                  ) : h.exists ? (
                    <a href={`/api/exports/${h.id}/file`}>{h.fileName}</a>
                  ) : (
                    <span className="muted">{h.fileName} (없음: 옮기거나 지움)</span>
                  )}
                </td>
                <td className="num">{h.documentCount}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
