import { useState } from 'react'
import { api, statusLabel, type DocumentStatus, type Pack } from '../api.ts'

// 화면 업로드 = 감시 폴더와 같은 접수 (같은 대기열 · 중복 확인)
export function UploadPage({ packs }: { packs: Pack[] }) {
  const [pack, setPack] = useState(packs[0]?.id ?? '')
  const [files, setFiles] = useState<File[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [results, setResults] = useState<{ name: string; id: number | null; status: DocumentStatus; statusReason: string | null }[]>([])
  const [drag, setDrag] = useState(false)

  const upload = async () => {
    setBusy(true)
    setError(null)
    try {
      const form = new FormData()
      form.append('pack', pack)
      for (const f of files) form.append('files', f)
      setResults(await api.upload(form))
      setFiles([])
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="narrow">
      <h2>올리기</h2>
      <p className="muted">문서 폴더의 <b>넣기\&lt;종류&gt;</b> 에 넣는 것과 같습니다. PDF · DOCX · 이미지(PNG · JPG · TIF · BMP), 파일당 50MB까지.</p>
      <div className="segmented" role="group" aria-label="문서 종류">
        {packs.map((p) => (
          <button key={p.id} className={pack === p.id ? 'on' : ''} onClick={() => setPack(p.id)}>
            {p.displayName}
          </button>
        ))}
      </div>
      <label
        className={`drop${drag ? ' over' : ''}`}
        onDragOver={(e) => { e.preventDefault(); setDrag(true) }}
        onDragLeave={() => setDrag(false)}
        onDrop={(e) => { e.preventDefault(); setDrag(false); setFiles([...files, ...Array.from(e.dataTransfer.files)]) }}
      >
        <input type="file" multiple accept=".pdf,.docx,.png,.jpg,.jpeg,.tif,.tiff,.bmp" onChange={(e) => setFiles([...files, ...Array.from(e.target.files ?? [])])} />
        {files.length === 0 ? '여기로 끌어 놓거나 눌러서 고르세요' : `${files.length}개 선택: ${files.map((f) => f.name).join(', ')}`}
      </label>
      <div className="buttons">
        <button className="primary" onClick={upload} disabled={busy || files.length === 0 || !pack}>
          {busy ? '올리는 중…' : `${packs.find((p) => p.id === pack)?.displayName ?? ''}(으)로 접수`}
        </button>
        {files.length > 0 && <button onClick={() => setFiles([])} disabled={busy}>선택 지우기</button>}
      </div>
      {error && <p className="error">{error}</p>}
      {results.length > 0 && (
        <ul className="results">
          {results.map((r, i) => (
            <li key={i}>
              {r.id ? <a href={`#/doc/${r.id}`}>#{r.id} {r.name}</a> : r.name} — {statusLabel[r.status] ?? r.status}
              {r.statusReason && <span className="sub"> ({r.statusReason})</span>}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
