import { useEffect, useState } from 'react'
import { getSystemInfo, type SystemInfo } from '../api/system'

const REFRESH_MS = 10_000
const gb = (mb: number) => (mb / 1024).toFixed(1)

/** 기기 사양 (모델 비교 페이지 상단). VRAM 사용량·로드된 모델은 10초마다 갱신 */
export function SystemSpecs() {
  const [info, setInfo] = useState<SystemInfo | null>(null)
  const [error, setError] = useState(false)

  useEffect(() => {
    let cancelled = false
    const load = () =>
      getSystemInfo()
        .then((i) => !cancelled && (setInfo(i), setError(false)))
        .catch(() => !cancelled && setError(true))
    load()
    const timer = setInterval(load, REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  if (error && !info) return <section className="card muted small">기기 정보를 불러오지 못했습니다</section>
  if (!info) return <section className="card muted small">기기 정보 불러오는 중…</section>

  const ocr = Object.entries(info.ocrEngines ?? {})
  return (
    <section className="card specs">
      <div className="specs-grid">
        {info.gpus.map((g) => {
          const used = Math.round((g.memoryUsedMb / g.memoryTotalMb) * 100)
          return (
            <div key={g.name} className="spec spec-wide">
              <span className="spec-label">GPU</span>
              <strong>{g.name}</strong>
              <div className="vram">
                <div className="vram-bar" role="progressbar" aria-valuenow={used} aria-valuemin={0} aria-valuemax={100}>
                  <div style={{ width: `${used}%` }} className={used > 90 ? 'hot' : undefined} />
                </div>
                <span className="small tabular">
                  VRAM {gb(g.memoryUsedMb)} / {gb(g.memoryTotalMb)} GB
                </span>
              </div>
              <span className="muted small">
                드라이버 {g.driver}
                {g.temperatureC !== null && ` · ${g.temperatureC}°C`}
                {g.utilizationPercent !== null && ` · 사용률 ${g.utilizationPercent}%`}
              </span>
            </div>
          )
        })}
        <div className="spec">
          <span className="spec-label">CPU</span>
          <strong>{info.cpu}</strong>
          <span className="muted small">
            논리 코어 {info.logicalCores}개 · RAM {info.memoryGb} GB
          </span>
        </div>
        <div className="spec">
          <span className="spec-label">LLM (Ollama {info.ollamaVersion ?? '응답 없음'})</span>
          <strong>{info.llmModels.join(', ')}</strong>
          <span className="muted small">
            GPU 적재:{' '}
            {info.ollamaLoaded.length
              ? info.ollamaLoaded.map((m) => `${m.name} (${(m.vramBytes / 1024 ** 3).toFixed(1)} GB)`).join(', ')
              : '없음'}
          </span>
        </div>
        <div className="spec">
          <span className="spec-label">OCR</span>
          <strong>{ocr.length ? ocr.map(([name]) => name).join(', ') : '응답 없음'}</strong>
          <span className="muted small">
            {ocr.map(([name, e]) => `${name} ${e.loaded ? '로드됨' : '요청 시 로드'}`).join(' · ')}
          </span>
        </div>
        <div className="spec">
          <span className="spec-label">OS · 런타임</span>
          <strong>{info.os}</strong>
          <span className="muted small">{info.runtime}</span>
        </div>
      </div>
    </section>
  )
}
