import { lazy, Suspense, useEffect, useMemo, useState } from 'react'
import { api, ApiError, formatTime, type DocumentDetail, type FieldDef, type Fields, type Issue, type Pack } from '../api.ts'
import { go } from '../route.ts'
import { StatusChip } from './DocumentsPage.tsx'

// 검수: 왼쪽 원본(이미지 · PDF · 원문 텍스트) ↔ 오른쪽 필드. 검증 문제 칸 강조 + 이유, 고친 칸 표시.
// 승인은 여기서만 (자동 승인 없음). 문제가 남았으면 "원본과 대조해 확인함" 을 체크해야 승인됨

// PDF.js(약 2MB)는 PDF 를 열 때만 내려받음
const PdfViewer = lazy(() => import('../PdfViewer.tsx').then((m) => ({ default: m.PdfViewer })))

type Props = { id: number; packs: Pack[]; onChanged: () => void }

export function ReviewPage({ id, packs, onChanged }: Props) {
  const [detail, setDetail] = useState<DocumentDetail | null>(null)
  const [fields, setFields] = useState<Fields | null>(null)
  const [issues, setIssues] = useState<Issue[]>([])
  const [dirty, setDirty] = useState(false)
  const [ack, setAck] = useState(false)
  const [rejecting, setRejecting] = useState(false)
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ tone: 'ok' | 'warn' | 'bad'; text: string } | null>(null)
  const [view, setView] = useState<'file' | 'text'>('file')

  useEffect(() => {
    // 문서가 바뀌면 App 이 key 로 새로 만듦 ➔ 상태 초기화는 필요 없음
    let alive = true
    api.document(id).then(
      (d) => {
        if (!alive) return
        setDetail(d)
        setFields(d.fields ? structuredClone(d.fields) : null)
        setIssues(d.issues)
        setView(d.hasFile && d.fileKind !== 'docx' ? 'file' : 'text')
      },
      (e: Error) => alive && setMessage({ tone: 'bad', text: e.message }),
    )
    return () => {
      alive = false
    }
  }, [id])

  const pack = packs.find((p) => p.id === detail?.document.packId)
  const byPath = useMemo(() => {
    const map = new Map<string, Issue[]>()
    for (const i of issues) {
      const key = i.field ?? ''
      map.set(key, [...(map.get(key) ?? []), i])
    }
    return map
  }, [issues])
  const errors = issues.filter((i) => i.severity === 'Error')
  const general = byPath.get('') ?? []

  if (!detail) return message ? <p className="error">{message.text}</p> : <p className="muted">불러오는 중…</p>
  const doc = detail.document
  const conditional = pack?.release?.status === 'conditional'

  const update = (next: Fields) => {
    setFields(next)
    setDirty(true)
    setAck(false)
  }

  const run = async (action: () => Promise<void>) => {
    setBusy(true)
    setMessage(null)
    try {
      await action()
    } catch (e) {
      if (e instanceof ApiError && e.issues) setIssues(e.issues)
      setMessage({ tone: 'bad', text: (e as Error).message })
    } finally {
      setBusy(false)
    }
  }

  const check = () =>
    run(async () => {
      const r = await api.check(id, fields!)
      setIssues(r.issues)
      setMessage(r.issues.some((i) => i.severity === 'Error') ? { tone: 'warn', text: '다시 검사했습니다. 남은 문제를 확인하세요' } : { tone: 'ok', text: '다시 검사했습니다. 오류 없음' })
    })

  const submit = (action: 'Save' | 'Approve' | 'Reject') =>
    run(async () => {
      const r = await api.review(id, { fields: fields!, action, acknowledgeIssues: ack, note: action === 'Reject' ? note : undefined })
      setIssues(r.issues)
      setDirty(false)
      onChanged()
      if (action === 'Save') {
        setMessage({ tone: 'ok', text: '저장했습니다' })
        return
      }
      // 다음 확인 필요 문서로 (같은 종류, 오래된 것부터)
      const next = (await api.documents({ pack: doc.packId, status: 'NeedsReview' })).filter((d) => d.id !== id).at(-1)
      go(next ? `/doc/${next.id}` : `/?pack=${doc.packId}`)
    })

  return (
    <section className="review">
      <div className="review-head">
        <a href={`#/?pack=${doc.packId}`} className="back" onClick={(e) => { if (dirty && !confirm('저장하지 않은 수정이 있습니다. 나갈까요?')) e.preventDefault() }}>
          ← 목록
        </a>
        <h2>
          #{doc.id} {doc.originalName}
        </h2>
        <StatusChip status={doc.status} />
        <span className="muted">
          {pack?.displayName ?? doc.packId} · 접수 {formatTime(doc.receivedAt)}
          {detail.extraction && ` · ${detail.extraction.model} · ${sourceKindLabel(detail.extraction.sourceKind)}`}
          {detail.extraction?.fallbackUsed && ` · 이미지로 다시 확인 (${detail.extraction.fallbackReason})`}
        </span>
      </div>

      {doc.statusReason && <p className={doc.status === 'Failed' || doc.status === 'Rejected' ? 'notice bad' : 'notice warn'}>{doc.statusReason}</p>}
      {doc.typeWarning && <p className="notice warn">필수 항목이 대부분 비어 있습니다. 이 문서가 <b>{pack?.displayName}</b> 가 맞는지 확인하세요. 아니면 반려하고 맞는 종류 폴더에 다시 넣으세요.</p>}
      {conditional && (
        <p className="notice accent">
          조건부 합격 종류: {pack?.release?.conditions?.join(' · ')}. 원본과 대조해 확인한 뒤 승인하세요.
        </p>
      )}
      {pack && pack.forbidden.length > 0 && <p className="notice muted">수집 금지: {pack.forbidden.join(' · ')} (원문 텍스트에서 가림, 필드에 넣지 마세요)</p>}

      <div className="review-body">
        <div className="pane source">
          <div className="segmented small">
            <button className={view === 'file' ? 'on' : ''} disabled={!detail.hasFile || detail.fileKind === 'docx'} onClick={() => setView('file')}>원본</button>
            <button className={view === 'text' ? 'on' : ''} onClick={() => setView('text')}>원문 텍스트</button>
            {detail.hasFile && <a className="link" href={`/api/documents/${id}/file`} target="_blank" rel="noreferrer">새 창</a>}
          </div>
          {view === 'file' && detail.fileKind === 'image' && <img src={`/api/documents/${id}/file`} alt={doc.originalName} />}
          {view === 'file' && detail.fileKind === 'pdf' && (
            <Suspense fallback={<p className="muted">PDF 보기 준비 중…</p>}>
              <PdfViewer url={`/api/documents/${id}/file`} />
            </Suspense>
          )}
          {view === 'text' && <pre className="source-text">{detail.sourceText ?? '원문이 없습니다 (보관 기한이 지났거나 추출 전 실패)'}</pre>}
          {!detail.hasFile && view === 'file' && <p className="muted">원본 파일이 없습니다</p>}
        </div>

        <div className="pane form">
          {!fields ? (
            <p className="muted">추출 결과가 없습니다.</p>
          ) : (
            <>
              {general.length > 0 && <IssueList issues={general} />}
              {pack?.fields.map((f) => (
                <FieldEditor
                  key={f.name}
                  def={f}
                  path={f.name}
                  value={fields[f.name]}
                  extracted={detail.extractedFields?.[f.name]}
                  byPath={byPath}
                  readOnly={!detail.reviewable}
                  onChange={(v) => update({ ...fields, [f.name]: v })}
                />
              ))}
            </>
          )}

          {detail.reviewable && fields && (
            <div className="actions">
              {message && <p className={message.tone === 'ok' ? 'ok-text' : message.tone === 'warn' ? 'warn-text' : 'error'}>{message.text}</p>}
              {errors.length > 0 && (
                <label className="ack">
                  <input type="checkbox" checked={ack} onChange={(e) => setAck(e.target.checked)} />
                  검증 문제 {errors.length}건을 원본과 대조해 확인했습니다
                </label>
              )}
              <div className="buttons">
                <button onClick={check} disabled={busy}>다시 검사</button>
                <button onClick={() => submit('Save')} disabled={busy || !dirty}>저장</button>
                <button className="primary" onClick={() => submit('Approve')} disabled={busy || (errors.length > 0 && !ack)}>
                  승인{doc.status === 'Approved' ? ' (다시)' : ''}
                </button>
                <button className="danger" onClick={() => setRejecting(!rejecting)} disabled={busy}>반려</button>
              </div>
              {rejecting && (
                <div className="reject">
                  <input placeholder="반려 사유 (예: 다른 종류 문서, 원본이 흐려 읽을 수 없음)" value={note} onChange={(e) => setNote(e.target.value)} />
                  <button className="danger" onClick={() => submit('Reject')} disabled={busy || !note.trim()}>반려 확정</button>
                </div>
              )}
            </div>
          )}
          {!detail.reviewable && <p className="muted">이 문서는 검수할 수 없는 상태입니다.</p>}
        </div>
      </div>
    </section>
  )
}

function sourceKindLabel(kind: string) {
  return kind === 'ocr' ? 'OCR' : kind === 'pdf-text' ? 'PDF 텍스트' : kind === 'docx' ? 'DOCX' : kind
}

function IssueList({ issues }: { issues: Issue[] }) {
  return (
    <ul className="issues">
      {issues.map((i, n) => (
        <li key={n} className={i.severity === 'Error' ? 'bad-text' : 'warn-text'}>{i.message}</li>
      ))}
    </ul>
  )
}

type EditorProps = {
  def: FieldDef
  path: string
  value: unknown
  extracted: unknown
  byPath: Map<string, Issue[]>
  readOnly: boolean
  onChange: (value: unknown) => void
}

function same(a: unknown, b: unknown) {
  const norm = (v: unknown) => (v === '' || v === undefined || (Array.isArray(v) && v.length === 0) ? null : typeof v === 'number' ? String(v) : v)
  return JSON.stringify(norm(a)) === JSON.stringify(norm(b))
}

function FieldEditor({ def, path, value, extracted, byPath, readOnly, onChange }: EditorProps) {
  const own = byPath.get(path) ?? []
  const bad = own.some((i) => i.severity === 'Error')
  const changed = def.type !== 'list' && !same(value, extracted)
  const cls = `field${bad ? ' has-error' : own.length ? ' has-warning' : ''}${changed ? ' changed' : ''}`

  if (def.type === 'list') {
    const items = Array.isArray(value) ? (value as Fields[]) : []
    const extractedItems = Array.isArray(extracted) ? (extracted as Fields[]) : []
    const blank = () => Object.fromEntries((def.items ?? []).map((s) => [s.name, null]))
    return (
      <fieldset className={cls}>
        <legend>
          {def.label} <span className="muted">{items.length}건</span>
        </legend>
        <IssueList issues={own} />
        {items.map((item, i) => {
          const itemPath = `${path}[${i}]`
          const itemIssues = byPath.get(itemPath) ?? []
          return (
            <div key={i} className={`item${itemIssues.some((x) => x.severity === 'Error') ? ' has-error' : ''}`}>
              <div className="item-head">
                <span>{i + 1}</span>
                {!readOnly && (
                  <button className="link danger-text" onClick={() => onChange(items.filter((_, n) => n !== i))}>삭제</button>
                )}
              </div>
              <IssueList issues={itemIssues} />
              <div className="item-grid">
                {(def.items ?? []).map((sub) => (
                  <FieldEditor
                    key={sub.name}
                    def={sub}
                    path={`${itemPath}.${sub.name}`}
                    value={item?.[sub.name]}
                    extracted={extractedItems[i]?.[sub.name]}
                    byPath={byPath}
                    readOnly={readOnly}
                    onChange={(v) => onChange(items.map((it, n) => (n === i ? { ...it, [sub.name]: v } : it)))}
                  />
                ))}
              </div>
            </div>
          )
        })}
        {!readOnly && <button className="link" onClick={() => onChange([...items, blank()])}>+ {def.label} 추가</button>}
      </fieldset>
    )
  }

  const text = def.type === 'string_list' || def.type === 'text_list'
    ? (Array.isArray(value) ? value : []).map((v) => v ?? '').join('\n')
    : value === null || value === undefined ? '' : String(value)
  const set = (t: string) =>
    onChange(def.type === 'string_list' || def.type === 'text_list' ? t.split('\n').map((s) => s.trim()).filter(Boolean) : t === '' ? null : t)
  const multiline = def.type === 'longtext' || def.type === 'string_list' || def.type === 'text_list'

  return (
    <label className={cls}>
      <span className="label">
        {def.label}
        {def.required && <span className="req" title="필수">*</span>}
        {changed && <span className="chip accent" title={`추출값: ${extracted === null || extracted === undefined ? '(없음)' : JSON.stringify(extracted)}`}>고침</span>}
        {(def.type === 'string_list' || def.type === 'text_list') && <span className="muted"> (한 줄에 하나)</span>}
      </span>
      {multiline ? (
        <textarea value={text} rows={Math.min(8, Math.max(2, text.split('\n').length))} readOnly={readOnly} onChange={(e) => set(e.target.value)} />
      ) : (
        <input value={text} readOnly={readOnly} placeholder={placeholder(def.type)} inputMode={def.type === 'amount' || def.type === 'number' ? 'decimal' : undefined} onChange={(e) => set(e.target.value)} />
      )}
      {own.map((i, n) => (
        <span key={n} className={i.severity === 'Error' ? 'hint bad-text' : 'hint warn-text'}>{i.message}</span>
      ))}
    </label>
  )
}

function placeholder(type: string) {
  switch (type) {
    case 'date': return 'YYYY-MM-DD'
    case 'month': return 'YYYY-MM'
    case 'month_or_present': return 'YYYY-MM 또는 present'
    case 'time': return 'HH:MM'
    case 'amount': return '원 단위 숫자'
    default: return ''
  }
}
