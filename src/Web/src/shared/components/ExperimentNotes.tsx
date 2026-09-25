import { useState } from 'react'
import { sendJson } from '../api/http'

/** 실험 설명: 문서를 어떻게 골랐는지 등 결과를 읽을 때 필요한 설계. 끝난 실험에도 고칠 수 있음 */
export function ExperimentDescription({
  module,
  id,
  description,
  onSaved,
}: {
  module: 'text' | 'image'
  id: string
  description?: string | null
  onSaved: () => void
}) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(description ?? '')
  const [error, setError] = useState<string | null>(null)

  const save = async () => {
    try {
      await sendJson<void>(`/api/${module}/experiments/${id}/description`, 'PUT', { description: draft })
      setEditing(false)
      setError(null)
      onSaved()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  if (editing) {
    return (
      <div className="experiment-description editing">
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          maxLength={2000}
          rows={3}
          placeholder="예: 검증은 통과했지만 OCR 신뢰도가 0.9 미만인 문서만 골라 폴백 기준을 비교 (그래서 0.9 조합은 폴백 100%)"
        />
        <div className="row-actions small">
          <button className="link-button" onClick={() => setEditing(false)}>
            취소
          </button>
          <button className="small-button primary" onClick={save}>
            저장
          </button>
        </div>
        {error && <p className="text-bad small">{error}</p>}
      </div>
    )
  }
  return (
    <div className="experiment-description small">
      {description ? <p>{description}</p> : <p className="muted">실험 설명이 없습니다. 문서를 어떻게 골랐는지 적어 두면 결과를 읽기 쉽습니다.</p>}
      <button
        className="link-button"
        onClick={() => {
          setDraft(description ?? '')
          setEditing(true)
        }}
      >
        {description ? '설명 고치기' : '설명 쓰기'}
      </button>
    </div>
  )
}

/** 1위와 2위의 점수 차와 표본 수. 차이가 작으면 표본에 따라 순위가 바뀔 수 있다고 알림 */
export function RankMargin({
  combos,
  sample,
}: {
  combos: { rank?: number | null; score?: number | null }[]
  sample: string
}) {
  const ranked = combos
    .filter((c) => c.rank != null && c.score != null)
    .sort((a, b) => (a.rank ?? 0) - (b.rank ?? 0))
  if (ranked.length < 2) return null
  const gap = (ranked[0].score ?? 0) - (ranked[1].score ?? 0)
  const close = gap < 0.02
  return (
    <p className={`rank-margin small ${close ? 'is-close' : ''}`}>
      1위와 2위 점수 차 <strong>{gap.toFixed(3)}</strong> · {sample}
      {close && ' — 차이가 작아 문서를 바꾸면 순위가 뒤집힐 수 있습니다. 점수보다 아래 지표와 문서별 결과를 함께 보세요.'}
    </p>
  )
}
