import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  getBuildLog,
  getRelease,
  releaseFileUrl,
  startBuild,
  type BuildSnapshot,
  type ReleaseCheck,
  type ReleaseOverview,
} from '../release'

/**
 * 배포 (/text/release): 설치판(문서 전산화 exe)에 들어가는 것이 전부 문서 텍스트 추출(Engine · 팩 · OCR)이라 이 메뉴 아래.
 * 이번 설치판에 들어갈 문서 종류 ➔ 배포 전 점검 ➔ 설치 파일 만들기(이 PC) ➔ 릴리스 노트 미리 보기 ➔ GitHub 배포 명령 (사람이 실행)
 */
const REPO_URL = 'https://github.com/calm-sea-psy/llm-playground-classifier/blob/main/'
const RELEASE_LABEL = { passed: '합격', conditional: '조건부 합격' } as const
const LEVEL = { ok: { chip: 'chip-ok', label: '정상' }, warn: { chip: 'chip-warn', label: '주의' }, bad: { chip: 'chip-bad', label: '문제' } } as const

function time(iso: string | null | undefined) {
  if (!iso) return ''
  const d = new Date(iso)
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
}

export function ReleasePage() {
  const [data, setData] = useState<ReleaseOverview | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    getRelease().then(
      (d) => {
        setData(d)
        setError(null)
      },
      (e: Error) => setError(e.message),
    )
  }, [])
  useEffect(load, [load])

  if (!data) return <section className="card">{error ? <p className="text-bad">{error}</p> : <p className="muted">불러오는 중…</p>}</section>

  const bad = data.checks.filter((c) => c.level === 'bad')
  return (
    <div className="release">
      <section className="card">
        <div className="page-head">
          <h2>배포 · 문서 전산화 {data.version}</h2>
          <button className="small-button" onClick={load}>다시 점검</button>
        </div>
        <p className="muted small">
          평가 도구에서 측정해 합격 표시를 붙인 문서 종류만 설치판(exe)에 들어갑니다. 추출 · 검증 코드와 OCR 설정 · 버전은 측정한 것과 같습니다. 합격 표시는{' '}
          <Link to="/text/packs">문서 종류</Link> 에서, 버전은 <code>src/Digitizer.App/Digitizer.App.csproj</code> 의 <code>&lt;Version&gt;</code> 에서 바꿉니다.
        </p>
        <h3>이번 설치판에 들어가는 문서 종류</h3>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>종류</th>
                <th>팩 버전</th>
                <th>판정</th>
                <th>조건</th>
                <th>측정 보고서</th>
              </tr>
            </thead>
            <tbody>
              {data.packs.map((p) => (
                <tr key={p.id} className={p.included ? undefined : 'muted'}>
                  <td>
                    <Link to={`/text/packs/${p.id}`}>{p.displayName}</Link> <span className="muted small">{p.id}</span>
                  </td>
                  <td>{p.version}</td>
                  <td>
                    {p.release ? (
                      <span className={`chip ${p.release.status === 'passed' ? 'chip-ok' : 'chip-warn'}`}>{RELEASE_LABEL[p.release.status]}</span>
                    ) : (
                      <span className="chip">평가만 · 넣지 않음</span>
                    )}
                    {p.problems.length > 0 && <div className="text-bad small">{p.problems.join('; ')}</div>}
                  </td>
                  <td className="small">{p.release?.conditions?.join(' · ') ?? (p.release ? '-' : '합격 기준으로 측정하지 않음')}</td>
                  <td className="small">
                    {p.release ? (
                      <a href={REPO_URL + p.release.report} target="_blank" rel="noreferrer">보고서</a>
                    ) : (
                      '-'
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section className="card">
        <h3>배포 전 점검</h3>
        {bad.length > 0 && <p className="text-bad small">문제 {bad.length}건: 고친 뒤 설치 파일을 만드세요.</p>}
        <ul className="release-checks">
          {data.checks.map((c) => (
            <CheckRow key={c.name} check={c} />
          ))}
        </ul>
        <h3>설치판 결과 대조 (최근)</h3>
        {data.parity.length === 0 ? (
          <p className="muted small">
            아직 없습니다. <code>eval/digitizer/AppParity</code> 로 설치판 결과를 평가 도구 측정과 비교하세요.
          </p>
        ) : (
          <div className="table-wrap">
            <table className="small">
              <thead>
                <tr>
                  <th>대조</th>
                  <th>시각</th>
                  <th>종류 · 입력</th>
                  <th>건수</th>
                  <th>원문 같음</th>
                  <th>필드 같음</th>
                  <th>검증 같음</th>
                  <th>최종 · 폴백 같음</th>
                  <th>처리 시간 중앙</th>
                </tr>
              </thead>
              <tbody>
                {data.parity.flatMap((run) =>
                  run.rows.map((r, i) => (
                    <tr key={`${run.name}-${r.pack}-${r.input}`}>
                      <td>{i === 0 ? run.name : ''}</td>
                      <td className="muted">{i === 0 ? time(run.at) : ''}</td>
                      <td>
                        {r.pack} · {r.input}
                      </td>
                      <td>{r.count}</td>
                      <td>{r.sameText}/{r.count}</td>
                      <td>{r.sameFields < 0 ? <span className="muted">기준 없음</span> : `${r.sameFields}/${r.count}`}</td>
                      <td>{r.sameErrors < 0 ? <span className="muted">기준 없음</span> : `${r.sameErrors}/${r.count}`}</td>
                      <td>{r.sameFinal === null ? '-' : `${r.sameFinal}/${r.count}`}</td>
                      <td>{r.medianSeconds.toFixed(1)}초</td>
                    </tr>
                  )),
                )}
              </tbody>
            </table>
          </div>
        )}
        <p className="muted small">
          LLM 은 같은 설정으로 다시 돌려도 몇 칸씩 달라집니다 (영수증 30장 기준 평가 도구끼리 7장). 필드가 조금 다른 것은 이 범위 안인지 봅니다.
        </p>
      </section>

      <BuildSection data={data} blocked={bad.length > 0} onDone={load} />

      <section className="card">
        <h3>릴리스 노트 미리 보기</h3>
        {data.releaseNotes ? (
          <pre className="release-notes">{data.releaseNotes}</pre>
        ) : (
          <p className="muted small">설치 파일을 만들면 GitHub Releases 에 올라갈 릴리스 노트가 여기에 나옵니다.</p>
        )}
      </section>

      <section className="card">
        <h3>GitHub 에 배포</h3>
        <p className="muted small">
          공개 저장소에 올리는 일이라 버튼으로 하지 않습니다. 설치 파일을 확인한 뒤 아래 명령을 차례로 실행하면{' '}
          <code>.github/workflows/release-digitizer.yml</code> 이 GitHub 에서 다시 빌드 · 테스트해 Releases 에 올립니다.
        </p>
        <ol className="release-commands">
          {data.tagCommands.map((c) => (
            <li key={c}>
              <code>{c}</code> <CopyButton text={c} />
            </li>
          ))}
        </ol>
      </section>
    </div>
  )
}

function CheckRow({ check }: { check: ReleaseCheck }) {
  const level = LEVEL[check.level]
  return (
    <li>
      <span className={`chip ${level.chip}`}>{level.label}</span>
      <div>
        <strong>{check.name}</strong> <span className="small">{check.detail}</span>
        {check.fix && <div className="muted small">해결: {check.fix}</div>}
      </div>
    </li>
  )
}

function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false)
  return (
    <button
      className="link-button small"
      onClick={() => navigator.clipboard.writeText(text).then(() => {
        setCopied(true)
        window.setTimeout(() => setCopied(false), 1500)
      })}
    >
      {copied ? '복사됨' : '복사'}
    </button>
  )
}

function BuildSection({ data, blocked, onDone }: { data: ReleaseOverview; blocked: boolean; onDone: () => void }) {
  const [build, setBuild] = useState<BuildSnapshot>(data.build)
  const [lines, setLines] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const logRef = useRef<HTMLPreElement>(null)
  const running = build.state === 'running'

  // 돌고 있으면 1초마다 이어서 받음 (이미 받은 줄 다음부터)
  useEffect(() => {
    if (!running) return
    let next = lines.length
    let alive = true
    const timer = window.setInterval(() => {
      getBuildLog(next).then((s) => {
        if (!alive) return
        next = s.next
        if (s.lines.length) setLines((old) => [...old, ...s.lines])
        setBuild(s)
        if (s.state !== 'running') onDone()
      }, () => {})
    }, 1000)
    return () => {
      alive = false
      window.clearInterval(timer)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [running])

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight })
  }, [lines])

  const start = () => {
    setError(null)
    setLines([])
    startBuild().then(setBuild, (e: Error) => setError(e.message))
  }

  const setup = data.setup
  return (
    <section className="card">
      <div className="page-head">
        <h3>설치 파일 만들기</h3>
        <button className="small-button primary-link" onClick={start} disabled={running}>
          {running ? '만드는 중…' : '설치 파일 만들기'}
        </button>
      </div>
      <p className="muted small">
        이 PC 에서 <code>installer\build.ps1</code> (게시 ➔ OCR 배포 파일 ➔ Inno Setup) 와 <code>release-notes.ps1</code> 을 실행합니다. 5분 정도 걸리고,
        한 번에 하나만 돕니다. 모델 · OCR 은 설치 파일에 넣지 않습니다 (설치 도우미가 설치 때 내려받음).
      </p>
      {blocked && !running && <p className="text-warn small">배포 전 점검에 문제가 있습니다. 만들 수는 있지만 배포하기 전에 고치세요.</p>}
      {error && <p className="text-bad small">{error}</p>}
      {build.state !== 'idle' && (
        <p className="small">
          <span className={`chip ${build.state === 'succeeded' ? 'chip-ok' : build.state === 'failed' ? 'chip-bad' : 'chip-warn'}`}>
            {{ running: '만드는 중', succeeded: '완료', failed: '실패', idle: '' }[build.state]}
          </span>{' '}
          시작 {time(build.startedAt)}
          {build.finishedAt && ` · 끝 ${time(build.finishedAt)}`}
        </p>
      )}
      {(running || lines.length > 0) && <pre ref={logRef} className="release-log">{lines.join('\n')}</pre>}
      {setup ? (
        <div className="release-files small">
          <div>
            <strong>{setup.name}</strong> <span className="muted">({(setup.bytes / 1024 / 1024).toFixed(0)}MB · {time(setup.at)})</span>
          </div>
          {setup.sha256 && (
            <div className="muted">
              SHA-256 <code>{setup.sha256}</code>
            </div>
          )}
          <div className="row-actions">
            <a className="small-button" href={releaseFileUrl(setup.name)}>설치 파일 내려받기</a>
            {setup.sha256 && <a className="link-button" href={releaseFileUrl(`${setup.name}.sha256`)}>.sha256</a>}
            {data.releaseNotes && <a className="link-button" href={releaseFileUrl('release-notes.md')}>release-notes.md</a>}
          </div>
        </div>
      ) : (
        <p className="muted small">아직 만든 설치 파일이 없습니다.</p>
      )}
    </section>
  )
}
