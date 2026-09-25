import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, Route, Routes, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { ApiError } from '../../shared/api/http'
import {
  activateVersion,
  deleteVersion,
  getPrompt,
  listPrompts,
  resetPrompt,
  savePrompt,
  type PromptCatalog,
  type PromptDetail,
  type PromptInfo,
} from './api'
import { diffLines } from './diff'

/** 파이프라인 프롬프트 관리: 목록 ➔ 내용 확인·수정(새 버전) ➔ 버전 적용·되돌리기·삭제 */
export function PromptsRoutes() {
  return (
    <Routes>
      <Route index element={<PromptsPage />} />
      <Route path=":module/:name" element={<PromptsPage />} />
    </Routes>
  )
}

function PromptsPage() {
  const { module, name } = useParams()
  const [catalog, setCatalog] = useState<PromptCatalog | null>(null)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(
    () =>
      listPrompts()
        .then(setCatalog)
        .catch((e) => setError(e instanceof Error ? e.message : String(e))),
    [],
  )
  useEffect(() => {
    reload()
  }, [reload])

  if (error) return <section className="card text-bad">프롬프트 목록을 불러오지 못했습니다: {error}</section>
  if (!catalog) return <section className="card muted">불러오는 중…</section>

  return (
    <div className="prompts-layout">
      <PromptList catalog={catalog} selected={module && name ? `${module}/${name}` : null} />
      {module && name ? (
        <PromptEditor key={`${module}/${name}`} module={module} name={name} catalog={catalog} onChanged={reload} />
      ) : (
        <section className="card prompts-intro">
          <h2>프롬프트</h2>
          <p className="muted small">
            파이프라인이 LLM 에 보내는 지시문과 용어집입니다. 왼쪽에서 하나를 고르면 지금 쓰는 내용을 보고 고칠 수
            있습니다.
          </p>
          <ul className="small">
            <li>
              저장하면 새 버전이 쌓이고, 적용한 버전은 API 재시작 없이 <strong>다음 작업부터</strong> 쓰입니다.
            </li>
            <li>파일(src/Api/Modules/…/Prompts)이 기본값이며, 언제든 기본값으로 되돌릴 수 있습니다.</li>
            <li>
              <code>{'{{$변수}}'}</code> 는 파이프라인이 채워 넣는 자리입니다. 빠지거나 모르는 변수가 있으면 저장되지
              않습니다.
            </li>
            <li>지시문(system) 은 모델 전용 버전을 따로 둘 수 있습니다 (예: qwen3-vl 에만 다른 지시).</li>
          </ul>
        </section>
      )}
    </div>
  )
}

function PromptList({ catalog, selected }: { catalog: PromptCatalog; selected: string | null }) {
  const [query, setQuery] = useState('')
  const listRef = useRef<HTMLElement>(null)
  const q = query.trim().toLowerCase()

  // 주소로 바로 들어왔을 때 고른 항목이 목록에서 보이게
  useEffect(() => {
    listRef.current?.querySelector('a.on')?.scrollIntoView({ block: 'nearest' })
  }, [selected])
  const match = (p: PromptInfo) => !q || p.name.includes(q) || p.description.toLowerCase().includes(q)

  return (
    <aside className="card prompt-list" ref={listRef}>
      <input
        type="search"
        className="prompt-search"
        placeholder="이름·설명 검색"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />
      {catalog.modules.map((m) => {
        const items = catalog.prompts.filter((p) => p.module === m.key && match(p))
        if (items.length === 0) return null
        return (
          <section key={m.key}>
            <h4>{m.displayName}</h4>
            <ul>
              {items.map((p) => {
                const key = `${p.module}/${p.name}`
                return (
                  <li key={key}>
                    <Link to={`/prompts/${key}`} className={selected === key ? 'on' : undefined}>
                      <span className="prompt-name">
                        {p.name}
                        {p.kind === 'json' && <span className="muted"> (json)</span>}
                      </span>
                      <span className="prompt-desc small muted">{p.description}</span>
                      <span className="prompt-badges">
                        <StatusChips info={p} />
                      </span>
                    </Link>
                  </li>
                )
              })}
            </ul>
          </section>
        )
      })}
    </aside>
  )
}

function StatusChips({ info }: { info: PromptInfo }) {
  return (
    <>
      {info.activeVersion != null ? (
        <span className="chip chip-warn">v{info.activeVersion} 적용 중</span>
      ) : info.hasFile ? (
        <span className="chip">기본값</span>
      ) : (
        <span className="chip">미적용 (공용 사용)</span>
      )}
      {info.modelFamily && <span className="chip">{info.modelFamily} 전용</span>}
      {!info.hasFile && <span className="chip">UI 에서 추가</span>}
    </>
  )
}

type Tab = 'edit' | 'diff' | 'history'

/** 프롬프트 상세. 아직 없는 모델 전용 프롬프트(from = 공용 이름)면 공용 프롬프트 내용으로 시작 */
async function fetchDetail(module: string, name: string, from: string | null) {
  try {
    return { detail: await getPrompt(module, name), isNew: false }
  } catch (e) {
    if (!(e instanceof ApiError && e.status === 404 && from)) throw e
    const base = await getPrompt(module, from)
    const detail: PromptDetail = { ...base, versions: [], fileContent: null, baseName: from, baseContent: base.content }
    return { detail, isNew: true }
  }
}

function PromptEditor({
  module,
  name,
  catalog,
  onChanged,
}: {
  module: string
  name: string
  catalog: PromptCatalog
  onChanged: () => void
}) {
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const from = params.get('from') // 새 모델 전용 프롬프트를 만들 때 원본(공용) 이름
  const [detail, setDetail] = useState<PromptDetail | null>(null)
  const [isNew, setIsNew] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [draft, setDraft] = useState('')
  const [note, setNote] = useState('')
  const [tab, setTab] = useState<Tab>('edit')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [viewing, setViewing] = useState<number | null>(null)

  const load = useCallback(
    () =>
      fetchDetail(module, name, from).then(
        (r) => {
          setDetail(r.detail)
          setDraft(r.detail.content)
          setIsNew(r.isNew)
        },
        (e) => setError(e instanceof Error ? e.message : String(e)),
      ),
    [module, name, from],
  )

  useEffect(() => {
    load()
  }, [load])

  if (error) return <section className="card text-bad">프롬프트를 불러오지 못했습니다: {error}</section>
  if (!detail) return <section className="card muted">불러오는 중…</section>

  const info = detail.info
  const dirty = draft !== detail.content
  const family = isNew ? name.slice((from ?? '').length + 1) : info.modelFamily
  // 비교 기준: 파일 기본값, 모델 전용이면 공용 프롬프트
  const reference = detail.fileContent ?? detail.baseContent ?? ''
  const referenceLabel = detail.fileContent != null ? '파일 기본값' : `공용 프롬프트 ${detail.baseName}`
  const viewed = detail.versions.find((v) => v.id === viewing)

  const run = async (action: () => Promise<unknown>, done: string, leaveTo?: string) => {
    setBusy(true)
    setMessage(null)
    try {
      await action()
      if (leaveTo) {
        // UI 에서 추가한 프롬프트의 마지막 버전을 지우면 프롬프트 자체가 없어짐 ➔ 공용 프롬프트로
        onChanged()
        navigate(leaveTo, { replace: true })
        return
      }
      setMessage({ ok: true, text: done })
      setNote('')
      if (isNew) navigate(`/prompts/${module}/${name}`, { replace: true })
      await load()
      onChanged()
    } catch (e) {
      setMessage({
        ok: false,
        text: e instanceof Error ? e.message : String(e),
      })
    } finally {
      setBusy(false)
    }
  }

  const save = (activate: boolean) =>
    run(
      () => savePrompt(module, name, draft, note, activate),
      activate ? '새 버전을 저장하고 적용했습니다. 다음 작업부터 쓰입니다.' : '새 버전을 저장했습니다 (적용 안 함).',
    )

  const canAddVariant = info.allowsModelVariants && !isNew
  const existingVariants = new Set(
    catalog.prompts.filter((p) => p.module === module && p.name.startsWith(`${name}.`)).map((p) => p.name),
  )

  return (
    <section className="card prompt-editor">
      <header className="prompt-head">
        <div>
          <h2>
            <code>{name}</code>
          </h2>
          <p className="muted small">{isNew ? `${from} 의 ${family} 전용 버전 (새로 만듦)` : info.description}</p>
        </div>
        <div className="prompt-badges">{!isNew && <StatusChips info={info} />}</div>
      </header>

      {info.kind === 'md' && (
        <p className="small muted">
          파이프라인이 채우는 변수:{' '}
          {info.variables.length === 0
            ? '없음'
            : info.variables.map((v) => <code key={v} className="var-chip">{`{{$${v}}}`}</code>)}
        </p>
      )}
      {info.kind === 'json' && (
        <p className="small muted">
          용어집 형식: <code>{'{"entries": [{"pattern": "정규식", "text": "정의"}]}'}</code> — 소견서에 pattern 이
          나오면(대소문자 무시) text 를 요약 지시문 끝에 붙입니다.
        </p>
      )}

      <div className="tabs small">
        <button className={tab === 'edit' ? 'on' : ''} onClick={() => setTab('edit')}>
          편집{dirty && ' •'}
        </button>
        <button className={tab === 'diff' ? 'on' : ''} onClick={() => setTab('diff')}>
          {referenceLabel}과 비교
        </button>
        <button className={tab === 'history' ? 'on' : ''} onClick={() => setTab('history')} disabled={isNew}>
          버전 이력 {detail.versions.length}
        </button>
      </div>

      {tab === 'edit' && (
        <>
          <textarea
            className="prompt-textarea"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            spellCheck={false}
            disabled={busy}
          />
          <div className="prompt-actions">
            <input
              className="prompt-note"
              placeholder="수정 메모 (예: 폐부종 부정문 규칙 강조)"
              value={note}
              maxLength={500}
              onChange={(e) => setNote(e.target.value)}
              disabled={busy}
            />
            <button onClick={() => setDraft(detail.content)} disabled={busy || !dirty}>
              편집 취소
            </button>
            <button onClick={() => save(false)} disabled={busy || (!dirty && !isNew)}>
              저장만
            </button>
            <button className="primary" onClick={() => save(true)} disabled={busy || (!dirty && !isNew)}>
              저장하고 적용
            </button>
          </div>
          <p className="muted small">
            {isNew
              ? `적용하면 ${family} 모델로 처리할 때 공용 프롬프트 대신 이 내용을 씁니다.`
              : info.activeVersion != null
                ? `지금 v${info.activeVersion} 을(를) 쓰는 중입니다.`
                : info.hasFile
                  ? '지금 파일 기본값을 쓰는 중입니다.'
                  : '적용된 버전이 없어 공용 프롬프트를 쓰는 중입니다.'}
          </p>
        </>
      )}

      {tab === 'diff' && (
        <DiffView
          before={reference}
          after={draft}
          beforeLabel={referenceLabel}
          afterLabel={dirty ? '편집 중' : '지금 쓰는 내용'}
        />
      )}

      {tab === 'history' && (
        <>
          <div className="table-scroll">
            <table className="jobs prompt-versions">
              <thead>
                <tr>
                  <th>버전</th>
                  <th>메모</th>
                  <th>저장 시각</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {detail.versions.map((v) => (
                  <tr key={v.id} className={v.active ? 'active' : undefined}>
                    <td>
                      v{v.version} {v.active && <span className="chip chip-ok">적용 중</span>}
                    </td>
                    <td className="small">{v.note ?? <span className="muted">—</span>}</td>
                    <td className="muted small tabular">{new Date(v.createdAt).toLocaleString('ko-KR')}</td>
                    <td className="row-actions small">
                      <button className="link-button" onClick={() => setViewing(viewing === v.id ? null : v.id)}>
                        {viewing === v.id ? '닫기' : '보기'}
                      </button>
                      <button className="link-button" onClick={() => setDraft(v.content)} title="편집 칸으로 불러오기">
                        불러오기
                      </button>
                      {!v.active && (
                        <>
                          <button
                            className="link-button"
                            disabled={busy}
                            onClick={() =>
                              run(() => activateVersion(module, name, v.id), `v${v.version} 을(를) 적용했습니다.`)
                            }
                          >
                            적용
                          </button>
                          <button
                            className="link-button text-bad"
                            disabled={busy}
                            onClick={() =>
                              confirm(`v${v.version} 을(를) 삭제할까요? 되돌릴 수 없습니다.`) &&
                              run(
                                () => deleteVersion(module, name, v.id),
                                `v${v.version} 을(를) 삭제했습니다.`,
                                !info.hasFile && detail.versions.length === 1 && detail.baseName
                                  ? `/prompts/${module}/${detail.baseName}`
                                  : undefined,
                              )
                            }
                          >
                            삭제
                          </button>
                        </>
                      )}
                    </td>
                  </tr>
                ))}
                <tr className={info.activeVersion == null ? 'active' : undefined}>
                  <td>
                    {info.hasFile ? '파일 기본값' : '공용 프롬프트 사용'}{' '}
                    {info.activeVersion == null && <span className="chip chip-ok">적용 중</span>}
                  </td>
                  <td className="small muted">
                    {info.hasFile ? '저장소의 원본 파일' : `${detail.baseName} 을(를) 그대로 씀`}
                  </td>
                  <td />
                  <td className="row-actions small">
                    {info.activeVersion != null && (
                      <button
                        className="link-button"
                        disabled={busy}
                        onClick={() =>
                          run(
                            () => resetPrompt(module, name),
                            info.hasFile ? '파일 기본값으로 되돌렸습니다.' : '공용 프롬프트를 쓰도록 되돌렸습니다.',
                          )
                        }
                      >
                        {info.hasFile ? '기본값으로 되돌리기' : '적용 해제'}
                      </button>
                    )}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          {viewed && (
            <DiffView
              before={reference}
              after={viewed.content}
              beforeLabel={referenceLabel}
              afterLabel={`v${viewed.version}`}
            />
          )}
        </>
      )}

      {message && <p className={message.ok ? 'text-ok small' : 'text-bad small'}>{message.text}</p>}

      {canAddVariant && (
        <VariantForm module={module} name={name} families={catalog.modelFamilies} existing={existingVariants} />
      )}
    </section>
  )
}

function VariantForm({
  module,
  name,
  families,
  existing,
}: {
  module: string
  name: string
  families: string[]
  existing: Set<string>
}) {
  const navigate = useNavigate()
  const available = families.filter((f) => !existing.has(`${name}.${f}`))
  const [family, setFamily] = useState(available[0] ?? '')
  if (available.length === 0) return null
  return (
    <div className="variant-form small">
      <span className="muted">모델 전용 버전 만들기:</span>
      <select value={family} onChange={(e) => setFamily(e.target.value)}>
        {available.map((f) => (
          <option key={f} value={f}>
            {f}
          </option>
        ))}
      </select>
      <button onClick={() => navigate(`/prompts/${module}/${name}.${family}?from=${encodeURIComponent(name)}`)}>
        만들기
      </button>
    </div>
  )
}

function DiffView({
  before,
  after,
  beforeLabel,
  afterLabel,
}: {
  before: string
  after: string
  beforeLabel: string
  afterLabel: string
}) {
  const lines = useMemo(() => diffLines(before, after), [before, after])
  const changed = lines.filter((l) => l.kind !== 'same').length
  return (
    <div className="prompt-diff">
      <p className="small muted">
        <span className="diff-del">− {beforeLabel}</span> <span className="diff-add">+ {afterLabel}</span>
        {changed === 0 && ' — 차이 없음'}
      </p>
      <pre>
        {lines.map((l, i) => (
          <div key={i} className={`diff-${l.kind}`}>
            <span className="diff-mark">{l.kind === 'add' ? '+' : l.kind === 'del' ? '−' : ' '}</span>
            {l.text || ' '}
          </div>
        ))}
      </pre>
    </div>
  )
}
