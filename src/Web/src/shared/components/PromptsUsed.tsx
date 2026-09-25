import { Link } from 'react-router-dom'
import type { PromptMix, PromptUse } from '../api/types'

const label = (u: PromptUse) => (u.version != null ? `v${u.version}` : '기본값')

/** 작업이 쓴 프롬프트 목록 (프롬프트 관리 화면으로 링크). 기록이 없으면(기록 도입 전 작업) 아무것도 안 그림 */
export function PromptsUsed({ prompts }: { prompts?: Record<string, PromptUse> | null }) {
  if (!prompts) return null
  // DB(jsonb)가 키 순서를 바꾸므로 이름순으로
  const entries = Object.entries(prompts).sort(([a], [b]) => a.localeCompare(b))
  if (entries.length === 0) return null
  const edited = entries.filter(([, u]) => u.version != null).length
  return (
    <details className="prompts-used small">
      <summary>
        사용한 프롬프트 {entries.length}개
        {edited > 0 ? <span className="chip chip-warn">수정 버전 {edited}개</span> : <span className="muted"> · 모두 기본값</span>}
      </summary>
      <ul>
        {entries.map(([key, u]) => (
          <li key={key}>
            <Link to={`/prompts/${key}`}>
              <code>{key}</code>
            </Link>{' '}
            <span className={u.version != null ? 'chip chip-warn' : 'chip'}>{label(u)}</span>{' '}
            <span className="muted tabular" title="내용 해시 (같으면 같은 내용)">
              {u.hash}
            </span>
          </li>
        ))}
      </ul>
    </details>
  )
}

/** 실험 도중 같은 프롬프트가 다른 버전으로 쓰였을 때 경고 */
export function PromptMixWarning({ mixes }: { mixes?: PromptMix[] | null }) {
  if (!mixes || mixes.length === 0) return null
  return (
    <div className="prompt-mix small">
      <strong>프롬프트가 실험 도중 바뀌었습니다.</strong> 작업마다 다른 버전으로 처리되어 결과를 그대로 비교하기 어렵습니다.
      <ul>
        {mixes.map((m) => (
          <li key={m.prompt}>
            <Link to={`/prompts/${m.prompt}`}>
              <code>{m.prompt}</code>
            </Link>
            : {m.versions.join(', ')}
          </li>
        ))}
      </ul>
    </div>
  )
}
