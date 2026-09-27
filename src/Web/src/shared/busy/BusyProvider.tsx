import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { BusyContext, type BusyApi, type BusyUpdate } from './busyContext'

interface BusyState {
  title: string
  detail: string | null
  lines: string[]
  startedAt: number
}

/**
 * 처리 중 레이어 팝업: 저장 · 적용 · 시작 · 설치 파일 만들기처럼 서버에 무언가를 하는 동안 화면 전체를 어둡게 덮고 가운데에 진행 상황을 보여 줌.
 * 뒤 화면은 inert(누르기 · 키 입력 · 포커스 안 됨)라 다른 요청이 끼어들지 못하고, 창을 닫거나 새로 고치려 하면 확인을 물음.
 * 끝나면(성공 · 실패 모두) 해제. 동시에 둘이 걸리면(안 생기게 막지만) 마지막 것을 보여 주고 모두 끝나야 해제
 */
export function BusyProvider({ children }: { children: ReactNode }) {
  const [stack, setStack] = useState<(BusyState & { id: number })[]>([])
  const nextId = useRef(0)
  const current = stack.at(-1) ?? null

  const run = useCallback<BusyApi['run']>(async (title, work) => {
    const id = ++nextId.current
    setStack((s) => [...s, { id, title, detail: null, lines: [], startedAt: Date.now() }])
    const update: BusyUpdate = (change) =>
      setStack((s) =>
        s.map((b) =>
          b.id !== id
            ? b
            : {
                ...b,
                title: change.title ?? b.title,
                detail: change.detail === undefined ? b.detail : change.detail,
                lines: change.lines ? [...b.lines, ...change.lines].slice(-200) : b.lines,
              },
        ),
      )
    try {
      return await work(update)
    } finally {
      setStack((s) => s.filter((b) => b.id !== id))
    }
  }, [])

  // 처리 중에 창을 닫거나 새로 고치면 확인 (요청은 서버에서 계속되지만 결과를 못 봄)
  const active = current !== null
  useEffect(() => {
    if (!active) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [active])

  return (
    <BusyContext.Provider value={{ run, active }}>
      <div inert={active} className="busy-host">
        {children}
      </div>
      {current && <BusyOverlay state={current} />}
    </BusyContext.Provider>
  )
}

function BusyOverlay({ state }: { state: BusyState }) {
  const [now, setNow] = useState(() => Date.now())
  const logRef = useRef<HTMLPreElement>(null)
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer)
  }, [])
  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight })
  }, [state.lines])

  const seconds = Math.max(0, Math.floor((now - state.startedAt) / 1000))
  const elapsed = seconds < 60 ? `${seconds}초` : `${Math.floor(seconds / 60)}분 ${seconds % 60}초`
  return (
    <div className="busy-backdrop" role="alertdialog" aria-modal="true" aria-busy="true" aria-labelledby="busy-title" aria-describedby="busy-detail">
      <div className="busy-card">
        <div className="busy-head">
          <span className="busy-spinner" aria-hidden="true" />
          <strong id="busy-title">{state.title}</strong>
        </div>
        <p id="busy-detail" className="small">
          {state.detail ?? '처리하고 있습니다'} <span className="muted">· {elapsed}</span>
        </p>
        {state.lines.length > 0 && (
          <pre ref={logRef} className="busy-log">
            {state.lines.slice(-12).join('\n')}
          </pre>
        )}
        <p className="muted small">끝나면 자동으로 닫힙니다. 그동안 다른 조작은 막아 둡니다.</p>
      </div>
    </div>
  )
}
