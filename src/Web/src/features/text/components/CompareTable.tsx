import type { ExtractionResult, Fields, ValidationIssue } from '../api'
import { AMOUNT_KEYS, DOCUMENT_TYPES, bySchemaOrder, label } from '../labels'
import { IssueList } from './IssueList'

export interface CompareColumn {
  title: string
  result: ExtractionResult | null
}

function display(key: string, value: unknown): string {
  if (value === null || value === undefined || value === '') return '—'
  if (typeof value === 'number') return AMOUNT_KEYS.has(key) ? value.toLocaleString('ko-KR') : String(value)
  if (Array.isArray(value)) return value.join(', ')
  return String(value)
}

const issueOf = (r: ExtractionResult | null, field: string) =>
  r?.issues.find((i) => i.field === field && i.severity === 'Error') ?? r?.issues.find((i) => i.field === field)

type Row = { key: string; values: string[]; issues: (ValidationIssue | undefined)[] }

/** 스칼라 필드 + 품목(행 단위)을 한 표로 펼침. 열 = 조합 */
function rows(results: (ExtractionResult | null)[]): Row[] {
  const fields: Fields[] = results.map((r) => r?.fields ?? {})
  const keys = [...new Set(fields.flatMap(Object.keys))].filter((k) => k !== 'items').sort(bySchemaOrder)
  const out: Row[] = keys.map((key) => ({
    key: label(key),
    values: fields.map((f) => display(key, f[key])),
    issues: results.map((r) => issueOf(r, key)),
  }))
  const items = fields.map((f) => (f.items as Record<string, unknown>[] | undefined) ?? [])
  const item = (it: Record<string, unknown> | undefined) =>
    it
      ? `${display('name', it.name ?? it.description)} · ${display('qty', it.qty)} × ${display('unit_price', it.unit_price)} = ${display('amount', it.amount)}`
      : '—'
  for (let i = 0; i < Math.max(0, ...items.map((x) => x.length)); i++) {
    out.push({
      key: `품목 ${i + 1}`,
      values: items.map((list) => item(list[i])),
      issues: results.map((r) => issueOf(r, `items[${i}]`)),
    })
  }
  return out
}

const seconds = (ms?: number) => (ms === undefined ? '—' : `${(ms / 1000).toFixed(1)}초`)

/** 같은 문서를 여러 조합으로 처리한 추출 결과를 필드별로 나란히 (값이 다른 행 강조) */
export function CompareTable({ columns }: { columns: CompareColumn[] }) {
  const results = columns.map((c) => c.result)
  const table = rows(results)
  const ready = results.filter(Boolean).length
  const differs = (r: Row) => ready > 1 && new Set(r.values).size > 1
  const diffCount = table.filter(differs).length

  const summary = (r: ExtractionResult | null) =>
    !r ? (
      <span className="muted small">대기 중</span>
    ) : (
      <div className="chips">
        <span className="chip">{DOCUMENT_TYPES[r.documentType] ?? r.documentType}</span>
        {r.validationPassed ? (
          <span className="chip chip-ok">검증 통과</span>
        ) : (
          <span className="chip chip-bad">검증 실패 · 오류 {r.errorCount}</span>
        )}
        {r.fallbackUsed && <span className="chip chip-warn">폴백 · {r.finalSource === 'vlm' ? 'VLM 결과' : '텍스트 결과'}</span>}
        <span className="chip">LLM {seconds(r.llmElapsedMs)}</span>
      </div>
    )

  return (
    <div className="compare-table">
      <table>
        <thead>
          <tr>
            <th>필드</th>
            {columns.map((c) => (
              <th key={c.title}>{c.title}</th>
            ))}
          </tr>
          <tr className="compare-summary">
            <td className="muted small">{ready > 1 ? `다른 값 ${diffCount}개` : ''}</td>
            {columns.map((c) => (
              <td key={c.title}>{summary(c.result)}</td>
            ))}
          </tr>
        </thead>
        <tbody>
          {table.map((r) => (
            <tr key={r.key}>
              <th scope="row" className="small">
                {r.key}
              </th>
              {r.values.map((v, i) => {
                const issue = r.issues[i]
                const cls = [issue ? `has-${issue.severity.toLowerCase()}` : '', differs(r) ? 'differs' : ''].join(' ')
                return (
                  <td key={i} className={cls} title={issue?.message}>
                    {v}
                  </td>
                )
              })}
            </tr>
          ))}
        </tbody>
      </table>

      <div className="compare-issues">
        {columns.map((c) => (
          <div key={c.title}>
            <h4>{c.title} 검증</h4>
            {c.result ? <IssueList issues={c.result.issues} /> : <p className="muted small">대기 중</p>}
          </div>
        ))}
      </div>
    </div>
  )
}
