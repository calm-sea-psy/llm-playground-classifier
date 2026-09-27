import { useEffect, useState } from 'react'
import { api, type Pack } from '../api.ts'

// 사용자가 바꿀 수 있는 것: 모델 · 문서 폴더 · 보관 기한. 나머지 처리 설정은 측정한 값 그대로 (설정 파일에서만)
export function SettingsPage({ packs }: { packs: Pack[] }) {
  const [model, setModel] = useState('')
  const [root, setRoot] = useState('')
  const [resolved, setResolved] = useState('')
  const [days, setDays] = useState(90)
  const [models, setModels] = useState<string[] | null>(null)
  const [path, setPath] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ tone: 'ok' | 'bad'; text: string } | null>(null)

  useEffect(() => {
    api.settings().then(
      (s) => {
        setModel(s.settings.model)
        setRoot(s.settings.documentsRoot)
        setResolved(s.resolvedDocumentsRoot)
        setDays(s.settings.retentionDays)
        setModels(s.availableModels)
        setPath(s.settingsPath)
      },
      (e: Error) => setMessage({ tone: 'bad', text: e.message }),
    )
  }, [])

  const save = async () => {
    setBusy(true)
    setMessage(null)
    try {
      const r = await api.saveSettings({ model, documentsRoot: root, retentionDays: days })
      setResolved(r.resolvedDocumentsRoot)
      setMessage({ tone: 'ok', text: '저장했습니다. 다음 문서부터 적용됩니다' })
    } catch (e) {
      setMessage({ tone: 'bad', text: (e as Error).message })
    } finally {
      setBusy(false)
    }
  }

  const unmeasured = packs.filter((p) => !p.measuredModels.includes(model))
  const options = [...new Set([...(models ?? []), model].filter(Boolean))]

  return (
    <section className="narrow">
      <h2>설정</h2>
      <div className="form-grid">
        <label className="field">
          <span className="label">모델</span>
          <select value={model} onChange={(e) => setModel(e.target.value)}>
            {options.map((m) => (
              <option key={m} value={m}>{m}</option>
            ))}
          </select>
          {models === null && <span className="hint warn-text">Ollama 에 연결하지 못해 설치된 모델 목록을 못 읽었습니다</span>}
          {unmeasured.length > 0 && (
            <span className="hint warn-text">
              {unmeasured.map((p) => p.displayName).join(' · ')} 은(는) 이 모델로 측정하지 않았습니다 (측정한 모델:{' '}
              {unmeasured.map((p) => `${p.displayName} ${p.measuredModels.join(', ')}`).join(' / ')}). 결과가 측정 보고서와 다를 수 있습니다
            </span>
          )}
        </label>
        <label className="field">
          <span className="label">문서 폴더</span>
          <input value={root} placeholder={resolved} onChange={(e) => setRoot(e.target.value)} />
          <span className="hint muted">비우면 기본 위치 (지금: {resolved}). 바꾸면 새 위치에 넣기 폴더를 만들고, 이미 처리한 파일은 옮기지 않습니다</span>
        </label>
        <label className="field">
          <span className="label">보관 기한 (일)</span>
          <input type="number" min={1} value={days} onChange={(e) => setDays(Number(e.target.value))} />
          <span className="hint muted">접수일로부터 지나면 원본 · 원문을 지웁니다. 내보낸 문서는 기록만 남습니다</span>
        </label>
      </div>
      <div className="buttons">
        <button className="primary" onClick={save} disabled={busy}>저장</button>
      </div>
      {message && <p className={message.tone === 'ok' ? 'ok-text' : 'error'}>{message.text}</p>}
      <p className="sub">
        설정 파일: <code>{path}</code> (OCR · 폴백 등 나머지 처리 설정은 측정한 값입니다. 바꾸려면 파일을 고치고 프로그램을 다시 시작하세요)
      </p>
    </section>
  )
}
