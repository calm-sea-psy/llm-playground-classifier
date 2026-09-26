import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link, Route, Routes, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  getPack,
  getPackMeta,
  getPacks,
  reloadPacks,
  savePack,
  type Pack,
  type PackDetail,
  type PackFieldJson,
  type PackForbiddenJson,
  type PackMeta,
  type PackTypeJson,
} from '../api'

/**
 * 문서 종류(팩) 관리: 목록 ➔ 상세 ➔ 고치기 · 새 종류 추가.
 * 팩 파일(packs/{종류}/)이 원본이라 평가 도구 · exe 가 같은 정의를 쓴다. 고칠 때는 버전을 올리고 이전 파일은 .history 에 남음
 */
export function PacksRoutes() {
  const [packs, setPacks] = useState<Pack[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const reload = useCallback(
    (fresh = false) =>
      (fresh ? reloadPacks() : getPacks()).then(setPacks, (e) => setError(e instanceof Error ? e.message : String(e))),
    [],
  )
  useEffect(() => {
    reload()
  }, [reload])

  if (error) return <section className="card text-bad">문서 종류를 불러오지 못했습니다: {error}</section>
  if (!packs) return <section className="card muted">불러오는 중…</section>

  const changed = () => reload(true)
  return (
    <Routes>
      <Route index element={<Layout packs={packs} body={<PacksIntro />} />} />
      <Route path="new" element={<Layout packs={packs} body={<EditorLoader onSaved={changed} />} />} />
      <Route path=":id" element={<Layout packs={packs} body={<PackViewLoader />} />} />
      <Route path=":id/edit" element={<Layout packs={packs} body={<EditorLoader onSaved={changed} />} />} />
    </Routes>
  )
}

function Layout({ packs, body }: { packs: Pack[]; body: ReactNode }) {
  const { id } = useParams()
  return (
    <div className="prompts-layout">
      <aside className="card prompt-list">
        <Link to="/text/packs/new" className="small-button pack-new">
          + 새 문서 종류
        </Link>
        <ul>
          {packs.map((p) => (
            <li key={p.id}>
              <Link to={`/text/packs/${p.id}`} className={p.id === id ? 'on' : undefined}>
                <span>
                  <strong>{p.displayName}</strong> <span className="muted small">v{p.version}</span>
                </span>
                <span className="prompt-name muted">{p.id}</span>
                <span className="prompt-badges">
                  <span className="chip">{p.autoClassified ? '자동 분류' : '직접 선택'}</span>
                  <span className="chip">필드 {p.fields.length}</span>
                </span>
              </Link>
            </li>
          ))}
        </ul>
      </aside>
      {body}
    </div>
  )
}

function PacksIntro() {
  return (
    <section className="card prompts-intro">
      <h2>문서 종류</h2>
      <p className="muted small">
        문서 종류 팩은 추출할 필드, 검증 규칙, 수집 금지 항목, 지시문을 한곳에 모은 정의입니다. 왼쪽에서 하나를 고르면
        내용을 보고 고칠 수 있습니다.
      </p>
      <ul className="small">
        <li>
          팩 파일(<code>packs/{'{종류}'}/</code>)이 원본입니다. 문서 처리 · 평가 도구 · 배포용 프로그램이 같은 파일을
          씁니다.
        </li>
        <li>
          고칠 때는 <strong>버전을 올려야</strong> 저장됩니다. 이전 파일은 <code>packs/{'{종류}'}/.history/</code> 에
          남습니다.
        </li>
        <li>저장하기 전에 엔진과 같은 검사(필드 타입, 규칙 이름, 지시문 변수)를 거칩니다.</li>
        <li>
          고친 뒤에는 모델 비교나 문서 처리로 <strong>다시 측정</strong>해서 기준을 넘는지 확인하세요.
        </li>
        <li>
          새 종류는 자동 분류 대상이 아니라서, 문서 처리에서 <strong>문서 종류를 직접 골라야</strong> 합니다.
        </li>
      </ul>
    </section>
  )
}

function useDetail(id: string | undefined) {
  // 불러온 id 와 함께 두어 다른 종류로 옮기면 이전 내용을 보이지 않음
  const [state, setState] = useState<{ id: string; detail?: PackDetail; error?: string } | null>(null)
  useEffect(() => {
    if (!id) return
    getPack(id).then(
      (detail) => setState({ id, detail }),
      (e) => setState({ id, error: e instanceof Error ? e.message : String(e) }),
    )
  }, [id])
  const current = state?.id === id ? state : null
  return { detail: current?.detail ?? null, error: current?.error ?? null }
}

function PackViewLoader() {
  const { id } = useParams()
  const { detail, error } = useDetail(id)
  if (error) return <section className="card text-bad">문서 종류를 불러오지 못했습니다: {error}</section>
  if (!detail) return <section className="card muted">불러오는 중…</section>
  return <PackView detail={detail} />
}

const kindLabel = { fixer: '보정', check: '검사', generic: '공통' } as const

function PackView({ detail }: { detail: PackDetail }) {
  const { type, summary, files, managed, history } = detail
  const [meta, setMeta] = useState<PackMeta | null>(null)
  const [file, setFile] = useState(Object.keys(files)[0] ?? 'prompt.md')
  useEffect(() => {
    getPackMeta().then(setMeta, () => undefined)
  }, [])
  const ruleInfo = (name: string) => meta?.rules.find((r) => r.name === name)

  return (
    <section className="card prompt-editor">
      <header className="prompt-head">
        <div>
          <h2>{type.display_name}</h2>
          <p className="muted small">
            <code>
              {type.id}@{type.version}
            </code>
            {type.description && ` · ${type.description}`}
          </p>
        </div>
        <div className="prompt-actions">
          <Link to={`/text/packs/${type.id}/edit`} className="small-button primary-link">
            고치기
          </Link>
          <Link to={`/text/packs/new?from=${type.id}`} className="small-button">
            복사해서 새 종류
          </Link>
        </div>
      </header>

      <div className="chips">
        <span className="chip">{summary.autoClassified ? '자동 분류 대상' : '문서 처리에서 직접 선택'}</span>
        <span className="chip">{type.generic_checks === false ? '공통 검사 끔' : '공통 검사 켬'}</span>
        {managed && <span className="chip chip-warn">지시문: 프롬프트 관리 사용</span>}
      </div>
      {managed && <ManagedNote id={type.id} />}

      <h3 className="pack-h">필드</h3>
      <FieldTable fields={type.fields} />

      <h3 className="pack-h">검증 규칙</h3>
      {type.rules.length === 0 ? (
        <p className="small muted">없음 {type.generic_checks !== false && '(필드 타입에 따른 공통 검사만)'}</p>
      ) : (
        <ul className="small pack-rules">
          {type.rules.map((r) => (
            <li key={r}>
              <code>{r}</code> <span className="chip">{kindLabel[ruleInfo(r)?.kind ?? 'check']}</span>{' '}
              <span className="muted">{ruleInfo(r)?.description}</span>
            </li>
          ))}
        </ul>
      )}

      <h3 className="pack-h">수집 금지</h3>
      {type.forbidden.length === 0 ? (
        <p className="small muted">없음</p>
      ) : (
        <table className="small">
          <thead>
            <tr>
              <th>항목</th>
              <th>찾는 패턴</th>
            </tr>
          </thead>
          <tbody>
            {type.forbidden.map((f) => (
              <tr key={f.id}>
                <td>{f.label}</td>
                <td>{f.pattern ? <code>{f.pattern}</code> : <span className="muted">지시문으로만 막음</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {type.variables && Object.keys(type.variables).length > 0 && (
        <>
          <h3 className="pack-h">지시문 변수</h3>
          <dl className="kv">
            {Object.entries(type.variables).map(([k, v]) => (
              <div key={k}>
                <dt>
                  <code>{`{{$${k}}}`}</code>
                </dt>
                <dd className="small">{v}</dd>
              </div>
            ))}
          </dl>
        </>
      )}

      <h3 className="pack-h">지시문 파일</h3>
      <div className="tabs small">
        {Object.keys(files).map((f) => (
          <button key={f} className={f === file ? 'on' : ''} onClick={() => setFile(f)}>
            {f}
          </button>
        ))}
      </div>
      <p className="small muted">{fileHelp(file)}</p>
      <pre className="reading-text">{files[file]}</pre>

      {(type.changelog?.length ?? 0) > 0 && (
        <>
          <h3 className="pack-h">변경 기록</h3>
          <ul className="small">
            {type.changelog!.map((c, i) => (
              <li key={i}>
                <strong>{c.version}</strong> <span className="muted">{c.date}</span> — {c.changes}
              </li>
            ))}
          </ul>
        </>
      )}
      {history.length > 0 && (
        <p className="small muted">
          보관된 이전 버전: {history.map((h) => h.version).join(', ')} (<code>packs/{type.id}/.history/</code>)
        </p>
      )}
    </section>
  )
}

function ManagedNote({ id }: { id: string }) {
  return (
    <p className="small muted pack-note">
      이 종류의 지시문은 문서 처리 · 모델 비교에서{' '}
      <Link to={`/prompts/text/extract.${id}`}>프롬프트 관리의 extract.{id}</Link> 적용 버전을 씁니다. 여기의 prompt.md
      는 평가 도구 · 배포용 프로그램이 쓰고, 다음 빌드 때 프롬프트 관리의 기본값이 됩니다.
    </p>
  )
}

function fileHelp(file: string) {
  if (file === 'prompt.md') return '지시문(system). {{$변수}} 는 지시문 변수에서 채웁니다.'
  if (file === 'user.md') return '원문 전달 틀. {{$input_label}} = 원문 종류 안내, {{$ocr_text}} = 원문. 없으면 기본 문장.'
  if (file === 'vlm.user.md')
    return '이미지 폴백 때 전달 틀. + {{$issues}} = 텍스트 추출에서 걸린 문제. 없으면 이미지 폴백을 하지 않습니다.'
  return `모델 계열 ${file.slice('prompt.'.length, -'.md'.length)} 전용 지시문 (없으면 prompt.md).`
}

function FieldTable({ fields }: { fields: PackFieldJson[] }) {
  return (
    <div className="table-wrap">
      <table className="small pack-fields">
        <thead>
          <tr>
            <th>이름</th>
            <th>화면 이름</th>
            <th>타입</th>
            <th>필수</th>
            <th>설명</th>
          </tr>
        </thead>
        <tbody>
          {fields.flatMap((f) => [
            <tr key={f.name}>
              <td>
                <code>{f.name}</code>
              </td>
              <td>{f.label}</td>
              <td>
                {f.type}
                {f.type === 'list' && (
                  <span className="muted">
                    {f.key && ` · 구분 ${f.key}`}
                    {f.ranged && ' · 기간 대조'}
                  </span>
                )}
              </td>
              <td>{f.required ? '예' : ''}</td>
              <td className="muted">{f.description}</td>
            </tr>,
            ...(f.items ?? []).map((it) => (
              <tr key={`${f.name}.${it.name}`} className="pack-item-row">
                <td>
                  <code>
                    <span className="muted">{f.name}.</span>
                    {it.name}
                  </code>
                </td>
                <td>{it.label}</td>
                <td>{it.type}</td>
                <td>{it.required ? '예' : ''}</td>
                <td className="muted">{it.description}</td>
              </tr>
            )),
          ])}
        </tbody>
      </table>
    </div>
  )
}

/* ───────────── 편집 ───────────── */

const NEW_PROMPT = `당신은 문서 전산화 도구의 필드 추출기입니다. 문서 원문 텍스트를 읽고, 주어진 JSON 스키마대로 값을 옮겨 적습니다.

규칙
- 원문에 적힌 값만 옮깁니다. 원문에 없는 값은 null (목록이면 빈 배열) 로 둡니다. 추측하거나 계산해서 채우지 않습니다.
- 이름 · 기관 이름은 원문 표기 그대로 적습니다.
- 날짜는 형식만 바꿉니다: 연월은 YYYY-MM, 날짜는 YYYY-MM-DD.
- 목록은 원문에 있는 모든 항목을 빠짐없이, 원문 순서대로 적습니다.
- 빈칸 표시("-", "없음", "N/A")는 값이 아니라 null 입니다.
`

function newType(): PackTypeJson {
  return {
    id: '',
    version: '0.1.0',
    display_name: '',
    description: '',
    fields: [{ name: '', label: '', type: 'text' }],
    forbidden: [],
    rules: [],
  }
}

function bump(version: string, part: 'major' | 'minor' | 'patch') {
  const [a, b, c] = version.split('.').map((n) => Number.parseInt(n, 10) || 0)
  return part === 'major' ? `${a + 1}.0.0` : part === 'minor' ? `${a}.${b + 1}.0` : `${a}.${b}.${c + 1}`
}

/** 저장용 정리: 기본값인 선택 키는 빼서 기존 type.json 과 같은 모양으로 (화면에 없는 키는 그대로) */
function cleanField(f: PackFieldJson, nested: boolean): PackFieldJson {
  const out: PackFieldJson = { ...f, name: f.name.trim(), label: f.label.trim() }
  if (!out.required) delete out.required
  if (!out.description?.trim()) delete out.description
  else out.description = out.description.trim()
  if (out.type === 'list' && !nested) {
    out.items = (out.items ?? []).map((i) => cleanField(i, true))
    if (!out.key) delete out.key
    if (!out.ranged) delete out.ranged
  } else {
    delete out.items
    delete out.key
    delete out.ranged
  }
  return out
}

function EditorLoader({ onSaved }: { onSaved: () => void }) {
  const { id } = useParams()
  const [params] = useSearchParams()
  const from = params.get('from')
  const { detail, error } = useDetail(id ?? from ?? undefined)
  const [meta, setMeta] = useState<PackMeta | null>(null)
  const [metaError, setMetaError] = useState<string | null>(null)
  useEffect(() => {
    getPackMeta().then(setMeta, (e) => setMetaError(e instanceof Error ? e.message : String(e)))
  }, [])

  if (error || metaError) return <section className="card text-bad">불러오지 못했습니다: {error ?? metaError}</section>
  if (!meta || ((id || from) && !detail)) return <section className="card muted">불러오는 중…</section>

  if (id && detail) return <PackEditor key={`edit-${id}`} meta={meta} base={detail} create={false} onSaved={onSaved} />
  if (from && detail) {
    // 복사: 필드 · 규칙 · 지시문을 가져오고 id · 이름 · 버전 · 변경 기록은 새로
    const { changelog: _changelog, ...rest } = structuredClone(detail.type)
    void _changelog
    const copy: PackDetail = {
      ...detail,
      type: { ...rest, id: '', display_name: `${detail.type.display_name} 복사본`, version: '0.1.0' },
      managed: false,
      history: [],
    }
    return <PackEditor key={`copy-${from}`} meta={meta} base={copy} create onSaved={onSaved} />
  }
  const blank: PackDetail = {
    summary: null as unknown as Pack,
    type: newType(),
    files: { 'prompt.md': NEW_PROMPT },
    managed: false,
    history: [],
  }
  return <PackEditor key="new" meta={meta} base={blank} create onSaved={onSaved} />
}

function PackEditor({
  meta,
  base,
  create,
  onSaved,
}: {
  meta: PackMeta
  base: PackDetail
  create: boolean
  onSaved: () => void
}) {
  const navigate = useNavigate()
  const [type, setType] = useState<PackTypeJson>(() => {
    const t = structuredClone(base.type)
    if (!create) t.version = bump(t.version, 'patch')
    return t
  })
  const [files, setFiles] = useState<Record<string, string>>(() => ({ ...base.files }))
  const [note, setNote] = useState('')
  const [file, setFile] = useState('prompt.md')
  const [busy, setBusy] = useState(false)
  const [errors, setErrors] = useState<string[]>([])
  const [newFamily, setNewFamily] = useState('')

  const set = <K extends keyof PackTypeJson>(key: K, value: PackTypeJson[K]) => setType((t) => ({ ...t, [key]: value }))

  const variables = useMemo(() => Object.entries(type.variables ?? {}), [type.variables])

  const save = async () => {
    setBusy(true)
    setErrors([])
    const clean: PackTypeJson = {
      ...type,
      id: type.id.trim(),
      display_name: type.display_name.trim(),
      fields: type.fields.map((f) => cleanField(f, false)),
      forbidden: type.forbidden.map((f) => (f.pattern?.trim() ? f : { id: f.id, label: f.label })),
    }
    if (!clean.description?.trim()) delete clean.description
    // 파일 변경: 바뀌거나 새로 생긴 파일 + 지운 파일(null)
    const changes: Record<string, string | null> = {}
    for (const [name, content] of Object.entries(files)) if (base.files[name] !== content) changes[name] = content
    for (const name of Object.keys(base.files)) if (!(name in files)) changes[name] = null
    // 복사로 만들 때는 원본 파일도 새 팩에 모두 써야 함
    if (create) for (const [name, content] of Object.entries(files)) changes[name] = content
    try {
      await savePack(clean, changes, note, create)
      onSaved()
      navigate(`/text/packs/${clean.id}`)
    } catch (e) {
      setErrors((e instanceof Error ? e.message : String(e)).split('\n'))
    } finally {
      setBusy(false)
    }
  }

  const optionalFiles = ['user.md', 'vlm.user.md'].filter((f) => !(f in files))

  return (
    <section className="card prompt-editor pack-editor">
      <header className="prompt-head">
        <div>
          <h2>{create ? '새 문서 종류' : `${base.type.display_name} 고치기`}</h2>
          <p className="muted small">
            {create
              ? '필드 · 규칙 · 지시문을 정하고 저장하면 packs 폴더에 새 팩이 생깁니다.'
              : `지금 ${base.type.version}. 저장하면 ${base.type.version} 파일은 .history 에 남습니다.`}
          </p>
        </div>
      </header>
      {base.managed && <ManagedNote id={base.type.id} />}

      <h3 className="pack-h">기본</h3>
      <div className="pack-grid">
        <label>
          <span className="small muted">id (폴더 이름, 영문 소문자 · 숫자 · _)</span>
          <input value={type.id} disabled={!create} onChange={(e) => set('id', e.target.value)} placeholder="business_card" />
        </label>
        <label>
          <span className="small muted">표시 이름</span>
          <input value={type.display_name} onChange={(e) => set('display_name', e.target.value)} placeholder="명함" />
        </label>
        <label>
          <span className="small muted">버전</span>
          <span className="pack-version">
            <input value={type.version} onChange={(e) => set('version', e.target.value)} />
            {!create && (
              <>
                {(['patch', 'minor', 'major'] as const).map((p) => (
                  <button
                    key={p}
                    type="button"
                    className="small-button"
                    onClick={() => set('version', bump(base.type.version, p))}
                  >
                    {bump(base.type.version, p)}
                  </button>
                ))}
              </>
            )}
          </span>
        </label>
        <label className="pack-wide">
          <span className="small muted">설명 (문서 처리의 종류 선택에 표시)</span>
          <input value={type.description ?? ''} onChange={(e) => set('description', e.target.value)} />
        </label>
        <label className="pack-check pack-wide">
          <input
            type="checkbox"
            checked={type.generic_checks !== false}
            onChange={(e) => set('generic_checks', e.target.checked)}
          />
          <span className="small">
            공통 검사 (필드 타입에서 나오는 원문 근거 · 형식 · 필수 검사). 끄면 아래 규칙만 씁니다.
          </span>
        </label>
      </div>

      <h3 className="pack-h">필드</h3>
      <FieldsEditor fields={type.fields} meta={meta} nested={false} onChange={(fields) => set('fields', fields)} />

      <h3 className="pack-h">검증 규칙</h3>
      <ul className="pack-rule-list small">
        {meta.rules.map((r) => (
          <li key={r.name}>
            <label className="pack-check">
              <input
                type="checkbox"
                checked={type.rules.includes(r.name)}
                onChange={(e) =>
                  set('rules', e.target.checked ? [...type.rules, r.name] : type.rules.filter((x) => x !== r.name))
                }
              />
              <span>
                <code>{r.name}</code> <span className="chip">{kindLabel[r.kind]}</span>{' '}
                <span className="muted">{r.description}</span>
              </span>
            </label>
          </li>
        ))}
      </ul>
      <p className="small muted">새 규칙은 코드가 필요합니다 (Digitizer.Engine/Rules/RuleRegistry).</p>

      <h3 className="pack-h">수집 금지</h3>
      <ForbiddenEditor items={type.forbidden} onChange={(forbidden) => set('forbidden', forbidden)} />

      <h3 className="pack-h">지시문 변수</h3>
      <table className="small pack-edit-table">
        <tbody>
          {variables.map(([k, v], i) => (
            <tr key={i}>
              <td>
                <input
                  value={k}
                  placeholder="이름"
                  onChange={(e) =>
                    set('variables', Object.fromEntries(variables.map(([k2, v2], j) => (j === i ? [e.target.value, v2] : [k2, v2]))))
                  }
                />
              </td>
              <td className="pack-grow">
                <input
                  value={v}
                  placeholder="값"
                  onChange={(e) =>
                    set('variables', Object.fromEntries(variables.map(([k2, v2], j) => (j === i ? [k2, e.target.value] : [k2, v2]))))
                  }
                />
              </td>
              <td>
                <button
                  type="button"
                  className="link-button"
                  onClick={() => set('variables', Object.fromEntries(variables.filter((_, j) => j !== i)))}
                >
                  삭제
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div>
        <button
          type="button"
          className="small-button"
          onClick={() => set('variables', { ...(type.variables ?? {}), [`var${variables.length + 1}`]: '' })}
        >
          + 변수
        </button>{' '}
        <span className="small muted">지시문의 {'{{$이름}}'} 자리에 들어갑니다.</span>
      </div>

      <h3 className="pack-h">지시문 파일</h3>
      <div className="tabs small">
        {Object.keys(files).map((f) => (
          <button key={f} className={f === file ? 'on' : ''} onClick={() => setFile(f)}>
            {f}
            {base.files[f] !== files[f] && ' •'}
          </button>
        ))}
      </div>
      <p className="small muted">{fileHelp(file)}</p>
      <textarea
        className="prompt-textarea"
        value={files[file] ?? ''}
        onChange={(e) => setFiles((fs) => ({ ...fs, [file]: e.target.value }))}
      />
      <div className="prompt-actions small">
        {optionalFiles.map((f) => (
          <button
            key={f}
            type="button"
            className="small-button"
            onClick={() => {
              setFiles((fs) => ({
                ...fs,
                [f]: f === 'user.md' ? '{{$input_label}}:\n{{$ocr_text}}\n' : VLM_TEMPLATE,
              }))
              setFile(f)
            }}
          >
            + {f}
          </button>
        ))}
        <input
          className="pack-family"
          value={newFamily}
          placeholder="모델 계열 (예: qwen3-vl)"
          onChange={(e) => setNewFamily(e.target.value.trim())}
        />
        <button
          type="button"
          className="small-button"
          disabled={!newFamily || `prompt.${newFamily}.md` in files}
          onClick={() => {
            const name = `prompt.${newFamily}.md`
            setFiles((fs) => ({ ...fs, [name]: fs['prompt.md'] ?? '' }))
            setFile(name)
            setNewFamily('')
          }}
        >
          + 모델 전용 지시문
        </button>
        {file !== 'prompt.md' && (
          <button
            type="button"
            className="link-button"
            onClick={() => {
              setFiles((fs) => {
                const next = { ...fs }
                delete next[file]
                return next
              })
              setFile('prompt.md')
            }}
          >
            {file} 삭제
          </button>
        )}
      </div>

      <div className="prompt-actions pack-save">
        <input
          className="prompt-note"
          value={note}
          placeholder="변경 메모 (type.json 변경 기록에 남음)"
          onChange={(e) => setNote(e.target.value)}
        />
        <button className="primary" disabled={busy} onClick={save}>
          {busy ? '저장 중…' : create ? '추가' : `${type.version} 으로 저장`}
        </button>
        <Link to={create ? '/text/packs' : `/text/packs/${base.type.id}`} className="small">
          취소
        </Link>
      </div>
      {errors.length > 0 && (
        <ul className="text-bad small pack-errors">
          {errors.map((e, i) => (
            <li key={i}>{e}</li>
          ))}
        </ul>
      )}
    </section>
  )
}

const VLM_TEMPLATE = `문서 이미지와 원문 텍스트를 함께 줍니다. 원문 텍스트에는 오인식이 있을 수 있으니 숫자 · 이름은 이미지를 보고 확인하세요.
{{$issues}}

{{$input_label}}:
{{$ocr_text}}
`

function move<T>(list: T[], i: number, d: number) {
  const j = i + d
  if (j < 0 || j >= list.length) return list
  const next = [...list]
  ;[next[i], next[j]] = [next[j], next[i]]
  return next
}

function FieldsEditor({
  fields,
  meta,
  nested,
  onChange,
}: {
  fields: PackFieldJson[]
  meta: PackMeta
  nested: boolean
  onChange: (fields: PackFieldJson[]) => void
}) {
  const update = (i: number, patch: Partial<PackFieldJson>) => onChange(fields.map((f, j) => (j === i ? { ...f, ...patch } : f)))
  const types = meta.fieldTypes.filter((t) => !nested || t.type !== 'list')

  return (
    <div className={nested ? 'pack-items' : undefined}>
      <div className="table-wrap">
        <table className="small pack-edit-table">
          <thead>
            <tr>
              <th>이름</th>
              <th>화면 이름</th>
              <th>타입</th>
              <th>필수</th>
              <th className="pack-grow">설명 (모델에 전달)</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {fields.map((f, i) => (
              <FieldRow key={i} f={f} i={i} count={fields.length} types={types} meta={meta} nested={nested} update={update}
                remove={() => onChange(fields.filter((_, j) => j !== i))}
                moveBy={(d) => onChange(move(fields, i, d))}
              />
            ))}
          </tbody>
        </table>
      </div>
      <button type="button" className="small-button" onClick={() => onChange([...fields, { name: '', label: '', type: 'text' }])}>
        + {nested ? '항목 필드' : '필드'}
      </button>
    </div>
  )
}

function FieldRow({
  f,
  i,
  count,
  types,
  meta,
  nested,
  update,
  remove,
  moveBy,
}: {
  f: PackFieldJson
  i: number
  count: number
  types: PackMeta['fieldTypes']
  meta: PackMeta
  nested: boolean
  update: (i: number, patch: Partial<PackFieldJson>) => void
  remove: () => void
  moveBy: (d: number) => void
}) {
  const typeHelp = types.find((t) => t.type === f.type)?.description
  return (
    <>
      <tr>
        <td>
          <input className="pack-code" value={f.name} placeholder="name" onChange={(e) => update(i, { name: e.target.value })} />
        </td>
        <td>
          <input value={f.label} placeholder="이름" onChange={(e) => update(i, { label: e.target.value })} />
        </td>
        <td>
          <select
            value={f.type}
            title={typeHelp}
            onChange={(e) =>
              update(
                i,
                e.target.value === 'list' && !f.items?.length
                  ? { type: e.target.value, items: [{ name: '', label: '', type: 'text' }] }
                  : { type: e.target.value },
              )
            }
          >
            {types.map((t) => (
              <option key={t.type} value={t.type} title={t.description}>
                {t.type}
              </option>
            ))}
            {!types.some((t) => t.type === f.type) && <option value={f.type}>{f.type} (모름)</option>}
          </select>
        </td>
        <td className="pack-center">
          <input type="checkbox" checked={!!f.required} onChange={(e) => update(i, { required: e.target.checked })} />
        </td>
        <td className="pack-grow">
          <input value={f.description ?? ''} onChange={(e) => update(i, { description: e.target.value })} />
        </td>
        <td className="row-actions">
          <button type="button" className="link-button" disabled={i === 0} onClick={() => moveBy(-1)} title="위로">
            ↑
          </button>
          <button type="button" className="link-button" disabled={i === count - 1} onClick={() => moveBy(1)} title="아래로">
            ↓
          </button>
          <button type="button" className="link-button" onClick={remove}>
            삭제
          </button>
        </td>
      </tr>
      {f.type === 'list' && !nested && (
        <tr className="pack-item-row">
          <td colSpan={6}>
            <div className="pack-list-opts small">
              <label>
                구분 필드{' '}
                <select value={f.key ?? ''} onChange={(e) => update(i, { key: e.target.value || null })}>
                  <option value="">(없음)</option>
                  {(f.items ?? []).filter((it) => it.name).map((it) => (
                    <option key={it.name} value={it.name}>
                      {it.name}
                    </option>
                  ))}
                </select>
              </label>
              <label className="pack-check">
                <input type="checkbox" checked={!!f.ranged} onChange={(e) => update(i, { ranged: e.target.checked })} />
                기간 대조 (항목에 start · end, 규칙 list_count)
              </label>
            </div>
            <FieldsEditor fields={f.items ?? []} meta={meta} nested onChange={(items) => update(i, { items })} />
          </td>
        </tr>
      )}
    </>
  )
}

function ForbiddenEditor({ items, onChange }: { items: PackForbiddenJson[]; onChange: (items: PackForbiddenJson[]) => void }) {
  const update = (i: number, patch: Partial<PackForbiddenJson>) => onChange(items.map((f, j) => (j === i ? { ...f, ...patch } : f)))
  return (
    <>
      {items.length > 0 && (
        <table className="small pack-edit-table">
          <thead>
            <tr>
              <th>id</th>
              <th>항목</th>
              <th className="pack-grow">찾는 패턴 (정규식, 비우면 지시문으로만 막음)</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {items.map((f, i) => (
              <tr key={i}>
                <td>
                  <input className="pack-code" value={f.id} onChange={(e) => update(i, { id: e.target.value })} />
                </td>
                <td>
                  <input value={f.label} onChange={(e) => update(i, { label: e.target.value })} />
                </td>
                <td className="pack-grow">
                  <input className="pack-code" value={f.pattern ?? ''} onChange={(e) => update(i, { pattern: e.target.value })} />
                </td>
                <td>
                  <button type="button" className="link-button" onClick={() => onChange(items.filter((_, j) => j !== i))}>
                    삭제
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <div>
        <button type="button" className="small-button" onClick={() => onChange([...items, { id: '', label: '' }])}>
          + 수집 금지 항목
        </button>{' '}
        <span className="small muted">패턴이 추출 결과에 있으면 오류, 원문에만 있으면 가림 경고.</span>
      </div>
    </>
  )
}
