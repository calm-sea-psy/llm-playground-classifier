import { useEffect, useState } from 'react'
import { api } from '../api.ts'

type Row = Awaited<ReturnType<typeof api.corrections>>[number]

// 운영 중 정확도: 승인한 문서에서 사람이 고친 칸의 비율 (측정 보고서의 필드 정확도와 비교해 볼 값)
export function StatsPage() {
  const [rows, setRows] = useState<Row[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    api.corrections().then(setRows, (e: Error) => setError(e.message))
  }, [])

  return (
    <section className="narrow">
      <h2>종류별 수정률</h2>
      <p className="muted">
        승인한 문서에서 검수 때 고친 칸 ÷ 비교한 칸. 낮을수록 추출이 정확합니다. 검수 없이 내보낸 검증 통과 건(합격 종류)과 보관 기한이 지나 기록을 지운 문서는 빠집니다.
      </p>
      {error && <p className="error">{error}</p>}
      {rows && (
        <div className="cards">
          {rows.map((r) => (
            <div key={r.packId} className="card">
              <h3>{r.displayName}</h3>
              {r.approved === 0 ? (
                <p className="muted">아직 승인한 문서가 없습니다</p>
              ) : (
                <>
                  <p className="big">{r.rate === null ? '-' : `${(r.rate * 100).toFixed(1)}%`}</p>
                  <p className="sub">
                    고친 칸 {r.corrected} / {r.slots} · 승인 {r.approved}건 중 {r.documentsCorrected}건을 고침 · 반려 {r.rejected}건
                  </p>
                  {r.topFields.length > 0 && (
                    <>
                      <p className="sub">자주 고친 칸</p>
                      <ul className="compact">
                        {r.topFields.map((f) => (
                          <li key={f.field}>
                            <code>{f.field}</code> {f.count}회
                          </li>
                        ))}
                      </ul>
                    </>
                  )}
                </>
              )}
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
