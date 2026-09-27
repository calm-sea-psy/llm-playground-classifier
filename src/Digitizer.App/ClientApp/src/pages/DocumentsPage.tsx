import { useEffect, useState } from 'react'
import { api, formatTime, statusLabel, statusTone, type DocumentStatus, type DocumentSummary, type Pack } from '../api.ts'
import { go } from '../route.ts'

const statusFilters: { value: string; label: string }[] = [
  { value: '', label: '전체' },
  { value: 'NeedsReview', label: '확인 필요' },
  { value: 'Processed', label: '검증 통과' },
  { value: 'Approved', label: '승인' },
  { value: 'Rejected', label: '반려' },
  { value: 'Failed,Duplicate', label: '실패 · 중복' },
  { value: 'Queued,Processing', label: '대기 · 처리 중' },
]

export function DocumentsPage({ packs, query }: { packs: Pack[]; query: URLSearchParams }) {
  const filter = {
    pack: query.get('pack') ?? '',
    status: query.get('status') ?? '',
    from: query.get('from') ?? '',
    to: query.get('to') ?? '',
  }
  const [docs, setDocs] = useState<DocumentSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const key = JSON.stringify(filter)

  useEffect(() => {
    let alive = true
    let timer = 0
    const load = () =>
      api.documents(filter).then(
        (rows) => {
          if (!alive) return
          setDocs(rows)
          setError(null)
          // 처리 중인 건이 있으면 자주, 없으면 가끔 (감시 폴더로 새 문서가 들어옴)
          timer = window.setTimeout(load, rows.some((d) => d.status === 'Queued' || d.status === 'Processing') ? 3000 : 15000)
        },
        (e: Error) => alive && setError(e.message),
      )
    load()
    return () => {
      alive = false
      window.clearTimeout(timer)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])

  const set = (name: string, value: string) => {
    const next = new URLSearchParams({ ...filter, [name]: value })
    for (const [k, v] of [...next.entries()]) if (!v) next.delete(k)
    go(`/?${next}`)
  }
  const packName = (id: string) => packs.find((p) => p.id === id)?.displayName ?? id

  return (
    <section>
      <div className="filters">
        <label>
          종류
          <select value={filter.pack} onChange={(e) => set('pack', e.target.value)}>
            <option value="">전체</option>
            {packs.map((p) => (
              <option key={p.id} value={p.id}>{p.displayName}</option>
            ))}
          </select>
        </label>
        <div className="segmented" role="group" aria-label="상태">
          {statusFilters.map((s) => (
            <button key={s.value} className={filter.status === s.value ? 'on' : ''} onClick={() => set('status', s.value)}>
              {s.label}
            </button>
          ))}
        </div>
        <label>
          접수일
          <input type="date" value={filter.from} onChange={(e) => set('from', e.target.value)} />
          ~
          <input type="date" value={filter.to} onChange={(e) => set('to', e.target.value)} />
        </label>
      </div>

      {error && <p className="error">{error}</p>}
      {docs === null ? (
        <p className="muted">불러오는 중…</p>
      ) : docs.length === 0 ? (
        <div className="empty">
          <p>문서가 없습니다.</p>
          <p className="muted">문서 폴더의 <b>넣기\&lt;종류&gt;</b> 에 파일을 넣거나 <a href="#/upload">올리기</a>에서 고르세요.</p>
        </div>
      ) : (
        <table className="list">
          <thead>
            <tr>
              <th>번호</th>
              <th>종류</th>
              <th>파일</th>
              <th>상태</th>
              <th>검증</th>
              <th>접수</th>
            </tr>
          </thead>
          <tbody>
            {docs.map((d) => (
              <tr key={d.id} onClick={() => go(`/doc/${d.id}`)} className="clickable">
                <td className="num">{d.id}</td>
                <td className="kind">{packName(d.packId)}</td>
                <td className="name">
                  {d.originalName}
                  {d.typeWarning && <span className="chip warn" title="필수 항목이 대부분 비어 있음">종류 확인</span>}
                  {d.exported && <span className="chip muted">내보냄</span>}
                  {d.statusReason && <div className="sub">{d.statusReason}</div>}
                </td>
                <td><StatusChip status={d.status} /></td>
                <td>
                  {d.errorCount === null ? '' : d.errorCount > 0 ? <span className="bad-text">문제 {d.errorCount}</span> : '통과'}
                  {d.fallbackUsed && <span className="sub"> · 이미지 재확인</span>}
                </td>
                <td className="time">{formatTime(d.receivedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}

export function StatusChip({ status }: { status: DocumentStatus }) {
  return <span className={`chip ${statusTone[status]}`}>{statusLabel[status]}</span>
}
