import type { Fields, ValidationIssue } from '../api'
import { AMOUNT_KEYS, bySchemaOrder, label } from '../labels'

function formatValue(key: string, value: unknown): string {
  if (value === null || value === undefined || value === '') return '—'
  if (typeof value === 'number') return AMOUNT_KEYS.has(key) ? value.toLocaleString('ko-KR') : String(value)
  if (Array.isArray(value)) return value.length ? value.join(', ') : '—'
  return String(value)
}

/** issue.field: "total", "items", "items[3]" ➔ 해당 칸 강조 */
function issueFor(issues: ValidationIssue[], field: string) {
  const matched = issues.filter((i) => i.field === field)
  return matched.find((i) => i.severity === 'Error') ?? matched[0]
}

export function FieldsView({ fields, issues }: { fields: Fields; issues: ValidationIssue[] }) {
  const entries = Object.entries(fields).sort(([a], [b]) => bySchemaOrder(a, b))
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
              <dt>{label(key)}</dt>
              <dd className={AMOUNT_KEYS.has(key) ? 'tabular num' : undefined}>{formatValue(key, value)}</dd>
            </div>
          )
        })}
      </dl>

      {tables.map(([key, rows]) => {
        const columns = Object.keys(rows[0] ?? {}).sort(bySchemaOrder)
        const tableIssue = issueFor(issues, key)
        return (
          <div key={key} className="table-wrap">
            <div className={`table-title ${tableIssue ? `has-${tableIssue.severity.toLowerCase()}` : ''}`} title={tableIssue?.message}>
              {label(key)} <span className="muted">{rows.length}개</span>
            </div>
            <table>
              <thead>
                <tr>
                  <th>#</th>
                  {columns.map((c) => (
                    <th key={c} className={AMOUNT_KEYS.has(c) || c === 'qty' ? 'num' : undefined}>
                      {label(c)}
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
                        <td key={c} className={AMOUNT_KEYS.has(c) || c === 'qty' ? 'num tabular' : undefined}>
                          {formatValue(c, row[c])}
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
