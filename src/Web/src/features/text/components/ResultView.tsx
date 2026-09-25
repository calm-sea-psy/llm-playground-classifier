import { useEffect, useMemo, useState } from 'react'
import { jobFileUrl } from '../../../shared/api/jobs'
import { ImageWithBoxes, type Box } from '../../../shared/components/ImageWithBoxes'
import { getOcr, getResult, type ExtractionResult, type OcrResult } from '../api'
import { DOCUMENT_TYPES } from '../labels'
import { FieldsView } from './FieldsView'
import { IssueList } from './IssueList'

const LOW_CONFIDENCE = 0.8

const seconds = (ms: number) => `${(ms / 1000).toFixed(1)}초`

/** 원본 이미지(+bbox) / OCR 텍스트 / 추출 결과를 나란히 표시 */
export function ResultView({ jobId }: { jobId: string }) {
  const [ocr, setOcr] = useState<OcrResult | null>(null)
  const [result, setResult] = useState<ExtractionResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [active, setActive] = useState<number | null>(null)
  const [showBoxes, setShowBoxes] = useState(true)
  const [textTab, setTextTab] = useState<'lines' | 'reading'>('lines')
  const [showJson, setShowJson] = useState(false)

  useEffect(() => {
    let cancelled = false
    Promise.all([getOcr(jobId), getResult(jobId).catch(() => null)])
      .then(([o, r]) => {
        if (cancelled) return
        setOcr(o)
        setResult(r)
      })
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)))
    return () => {
      cancelled = true
    }
  }, [jobId])

  const boxes = useMemo<Box[]>(
    () =>
      (ocr?.lines ?? []).map((l) => ({
        points: l.bbox ?? [],
        tone: l.confidence !== null && l.confidence < LOW_CONFIDENCE ? 'low' : 'normal',
      })),
    [ocr],
  )

  if (error) return <section className="card text-bad">결과를 불러오지 못했습니다: {error}</section>
  if (!ocr) return <section className="card muted">결과를 불러오는 중…</section>

  const lowCount = ocr.lines.filter((l) => l.confidence !== null && l.confidence < LOW_CONFIDENCE).length

  return (
    <div className="result-grid">
      <section className="card panel">
        <header className="panel-head">
          <h3>원본</h3>
          <label className="toggle small">
            <input type="checkbox" checked={showBoxes} onChange={(e) => setShowBoxes(e.target.checked)} /> OCR 영역
          </label>
        </header>
        <ImageWithBoxes
          src={jobFileUrl(jobId)}
          width={ocr.width}
          height={ocr.height}
          boxes={boxes}
          active={active}
          onActive={setActive}
          showBoxes={showBoxes}
        />
      </section>

      <section className="card panel">
        <header className="panel-head">
          <h3>OCR</h3>
          <div className="tabs small">
            <button className={textTab === 'lines' ? 'on' : ''} onClick={() => setTextTab('lines')}>
              줄 {ocr.lines.length}
            </button>
            <button className={textTab === 'reading' ? 'on' : ''} onClick={() => setTextTab('reading')} disabled={!result}>
              LLM 입력 텍스트
            </button>
          </div>
        </header>
        <p className="meta small muted">
          {ocr.engine} · 평균 신뢰도 {ocr.avg_confidence?.toFixed(3) ?? '—'} · 신뢰도 {LOW_CONFIDENCE} 미만 {lowCount}줄 ·{' '}
          {seconds(ocr.elapsed_ms)}
        </p>
        {textTab === 'lines' ? (
          <ol className="ocr-lines" onMouseLeave={() => setActive(null)}>
            {ocr.lines.map((line, i) => (
              <li
                key={i}
                className={`${i === active ? 'active' : ''} ${boxes[i]?.tone === 'low' ? 'low' : ''}`}
                onMouseEnter={() => setActive(i)}
              >
                <span className="ocr-text">{line.text}</span>
                <span className="muted tabular">{line.confidence?.toFixed(2) ?? ''}</span>
              </li>
            ))}
          </ol>
        ) : (
          <pre className="reading-text">{result?.readingText}</pre>
        )}
      </section>

      <section className="card panel">
        <header className="panel-head">
          <h3>추출 결과</h3>
          {result?.fields && (
            <label className="toggle small">
              <input type="checkbox" checked={showJson} onChange={(e) => setShowJson(e.target.checked)} /> JSON
            </label>
          )}
        </header>
        {!result ? (
          <p className="muted">LLM 결과가 없습니다.</p>
        ) : (
          <>
            <div className="chips">
              <span className="chip">{DOCUMENT_TYPES[result.documentType] ?? result.documentType}</span>
              {result.validationPassed === true && <span className="chip chip-ok">검증 통과</span>}
              {result.validationPassed === false && <span className="chip chip-bad">검증 실패 · 오류 {result.errorCount}</span>}
              {result.fallbackUsed && (
                <span className="chip chip-warn" title={result.fallbackReason ?? undefined}>
                  VLM 폴백 · {result.finalSource === 'vlm' ? 'VLM 결과 사용' : '텍스트 결과 유지'}
                </span>
              )}
              <span className="chip muted">
                {result.model} · LLM {seconds(result.llmElapsedMs)}
              </span>
            </div>

            {result.fields ? (
              showJson ? (
                <pre className="json">{JSON.stringify(result.fields, null, 2)}</pre>
              ) : (
                <FieldsView fields={result.fields} issues={result.issues} />
              )
            ) : (
              <p className="muted">필드 추출 대상이 아닌 문서입니다.</p>
            )}

            <h4>검증</h4>
            <IssueList issues={result.issues} />

            {result.attempts.length > 0 && (
              <details className="attempts">
                <summary>
                  시도 기록 {result.attempts.length}건 · 분류 {seconds(result.classifyMs)}
                </summary>
                {result.attempts.map((a, i) => (
                  <div key={i} className="attempt">
                    <div className="small">
                      <strong>{a.source === 'vlm' ? 'VLM 폴백 (이미지 + OCR)' : 'OCR 텍스트'}</strong>{' '}
                      <span className="muted">
                        {seconds(a.elapsedMs)} · 토큰 {a.promptTokens ?? '?'}/{a.completionTokens ?? '?'} ·{' '}
                        {a.schemaValid ? `오류 ${a.errorCount}` : `스키마 위반: ${a.parseError}`}
                        {result.finalSource === a.source && ' · 최종 사용'}
                      </span>
                    </div>
                    <IssueList issues={a.issues} />
                  </div>
                ))}
              </details>
            )}
          </>
        )}
      </section>
    </div>
  )
}
