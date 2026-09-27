import { useCallback, useEffect, useState } from 'react'
import { api, type CheckItem } from '../api.ts'

const levelLabel = { ok: '정상', warn: '주의', bad: '문제' } as const

export function StatusPage() {
  const [checks, setChecks] = useState<CheckItem[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(true)

  const fetchChecks = useCallback(() => {
    api.status().then(
      (r) => {
        setChecks(r.checks)
        setError(null)
        setBusy(false)
      },
      (e: Error) => {
        setError(e.message)
        setBusy(false)
      },
    )
  }, [])
  useEffect(fetchChecks, [fetchChecks])
  const reload = () => {
    setBusy(true)
    fetchChecks()
  }

  return (
    <section className="narrow">
      <h2>상태 점검</h2>
      <div className="buttons">
        <button onClick={reload} disabled={busy}>{busy ? '확인 중…' : '다시 확인'}</button>
      </div>
      {error && <p className="error">{error}</p>}
      {checks && (
        <ul className="checks">
          {checks.map((c) => (
            <li key={c.name} className={c.level}>
              <span className={`chip ${c.level === 'bad' ? 'bad' : c.level === 'warn' ? 'warn' : 'ok'}`}>{levelLabel[c.level]}</span>
              <div>
                <b>{c.name}</b> <span>{c.detail}</span>
                {c.fix && <div className="sub">해결: {c.fix}</div>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
