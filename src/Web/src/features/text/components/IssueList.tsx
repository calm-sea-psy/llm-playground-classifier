import type { ValidationIssue } from '../api'

export function IssueList({ issues }: { issues: ValidationIssue[] }) {
  if (issues.length === 0) return <p className="muted small">검증 문제 없음</p>
  const sorted = [...issues].sort((a, b) => (a.severity === b.severity ? 0 : a.severity === 'Error' ? -1 : 1))
  return (
    <ul className="issues">
      {sorted.map((issue, i) => (
        <li key={i} className={issue.severity === 'Error' ? 'is-error' : 'is-warning'}>
          <span className="issue-tag">{issue.severity === 'Error' ? '오류' : '경고'}</span>
          <span>{issue.message}</span>
        </li>
      ))}
    </ul>
  )
}
