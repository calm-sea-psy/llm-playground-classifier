import type { Fields, Pack, PackField, ValidationIssue } from '../api'
import { AMOUNT_KEYS, bySchemaOrder, label } from '../labels'

/** 칸 이름 · 순서 · 숫자 형식: 팩이 있으면 팩 정의, 없으면(예전 결과) labels.ts */
interface Spec {
  label: (key: string) => string
  order: (a: string, b: string) => number
  isAmount: (key: string) => boolean
}

function specOf(fields: PackField[] | undefined): Spec {
  if (!fields) return { label, order: bySchemaOrder, isAmount: (k) => AMOUNT_KEYS.has(k) }
  const byName = new Map(fields.map((f, i) => [f.name, { f, i }]))
  return {
    label: (k) => byName.get(k)?.f.label ?? label(k),
    order: (a, b) => (byName.get(a)?.i ?? 999) - (byName.get(b)?.i ?? 999),
    isAmount: (k) => byName.get(k)?.f.type === 'amount' || (byName.get(k)?.f.type === 'number' && AMOUNT_KEYS.has(k)),
  }
}

function formatValue(value: unknown, amount: boolean): string {
  if (value === null || value === undefined || value === '') return '—'
  if (typeof value === 'number') return amount ? value.toLocaleString('ko-KR') : String(value)
  if (Array.isArray(value)) return value.length ? value.map((v) => v ?? '—').join(', ') : '—'
  return String(value)
}

/** issue.field: "total", "items", "items[3]" ➔ 해당 칸 강조 */
function issueFor(issues: ValidationIssue[], field: string) {
  const matched = issues.filter((i) => i.field === field)
  return matched.find((i) => i.severity === 'Error') ?? matched[0]
}

export function FieldsView({ fields, issues, pack }: { fields: Fields; issues: ValidationIssue[]; pack?: Pack | null }) {
  const top = specOf(pack?.fields)
  const entries = Object.entries(fields).sort(([a], [b]) => top.order(a, b))
  const scalars = entries.filter(([, v]) => !(Array.isArray(v) && v.some((x) => typeof x === 'object' && x !== null)))
  const tables = entries.filter(([, v]) => Array.isArray(v) && v.some((x) => typeof x === 'object' && x !== null)) as [
    string,
    Record<string, unknown>[],
  ][]

  return (
    <div className="fields">
      <dl className="kv">
        {scalars.map(([key, value]) => {
          const issue = issueFor(issues, key)
          return (
            <div key={key} className={issue ? `has-${issue.severity.toLowerCase()}` : undefined} title={issue?.message}>
              <dt>{top.label(key)}</dt>
              <dd className={top.isAmount(key) ? 'tabular num' : undefined}>{formatValue(value, top.isAmount(key))}</dd>
            </div>
          )
        })}
      </dl>

      {tables.map(([key, rows]) => {
        // 목록마다 열 정의가 다름 (이력서: 경력 · 학력 · 자격증 …)
        const col = specOf(pack?.fields.find((f) => f.name === key)?.items ?? undefined)
        const columns = [...new Set(rows.flatMap((r) => Object.keys(r)))].sort(col.order)
        const tableIssue = issueFor(issues, key)
        return (
          <div key={key} className="table-wrap">
            <div className={`table-title ${tableIssue ? `has-${tableIssue.severity.toLowerCase()}` : ''}`} title={tableIssue?.message}>
              {top.label(key)} <span className="muted">{rows.length}개</span>
            </div>
            <table>
              <thead>
                <tr>
                  <th>#</th>
                  {columns.map((c) => (
                    <th key={c} className={col.isAmount(c) || c === 'qty' ? 'num' : undefined}>
                      {col.label(c)}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {rows.map((row, i) => {
                  const issue = issueFor(issues, `${key}[${i}]`)
                  return (
                    <tr key={i} className={issue ? `has-${issue.severity.toLowerCase()}` : undefined} title={issue?.message}>
                      <td className="muted tabular">{i + 1}</td>
                      {columns.map((c) => (
                        <td key={c} className={col.isAmount(c) || c === 'qty' ? 'num tabular' : undefined}>
                          {formatValue(row[c], col.isAmount(c))}
                        </td>
                      ))}
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )
      })}
    </div>
  )
}
