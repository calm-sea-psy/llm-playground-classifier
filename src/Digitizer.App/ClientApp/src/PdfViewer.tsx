import { useEffect, useRef, useState } from 'react'
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy, type RenderTask } from 'pdfjs-dist'
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url'

// 브라우저 PDF 뷰어 대신 PDF.js 로 캔버스에 그림: 브라우저가 PDF 를 내려받기로 처리하거나 뷰어가 없어도 보이고,
// 나중에 원본 위에 수집 금지 값을 가릴 때도 같은 캔버스를 씀
GlobalWorkerOptions.workerSrc = workerUrl

const zoomSteps = [0.5, 0.75, 1, 1.25, 1.5, 2, 3]

export function PdfViewer({ url }: { url: string }) {
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [page, setPage] = useState(1)
  const [zoom, setZoom] = useState(1)  // 1 = 칸 너비에 맞춤
  const [width, setWidth] = useState(0)
  const box = useRef<HTMLDivElement>(null)
  const canvas = useRef<HTMLCanvasElement>(null)

  useEffect(() => {
    const task = getDocument({
      url,
      cMapUrl: '/pdfjs/cmaps/',
      cMapPacked: true,
      standardFontDataUrl: '/pdfjs/standard_fonts/',
      wasmUrl: '/pdfjs/wasm/',
    })
    task.promise.then(setPdf, (e: Error) => setError(`PDF 를 열지 못했습니다: ${e.message}`))
    return () => {
      void task.destroy()
    }
  }, [url])

  // 칸 너비가 바뀌면 다시 그림 (창 크기 · 모바일)
  useEffect(() => {
    if (!box.current) return
    const observer = new ResizeObserver(([entry]) => setWidth(Math.floor(entry.contentRect.width)))
    observer.observe(box.current)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    if (!pdf || !canvas.current || width === 0) return
    let render: RenderTask | null = null
    let cancelled = false
    pdf.getPage(page).then((p) => {
      if (cancelled || !canvas.current) return
      const fit = width / p.getViewport({ scale: 1 }).width
      const viewport = p.getViewport({ scale: fit * zoom })
      const ratio = window.devicePixelRatio || 1
      const c = canvas.current
      c.width = Math.floor(viewport.width * ratio)
      c.height = Math.floor(viewport.height * ratio)
      c.style.width = `${Math.floor(viewport.width)}px`
      c.style.height = `${Math.floor(viewport.height)}px`
      render = p.render({ canvas: c, viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0] })
      render.promise.catch((e: Error) => {
        if (e.name !== 'RenderingCancelledException') setError(`쪽을 그리지 못했습니다: ${e.message}`)
      })
    })
    return () => {
      cancelled = true
      render?.cancel()
    }
  }, [pdf, page, zoom, width])

  const pages = pdf?.numPages ?? 0
  const zoomIndex = zoomSteps.indexOf(zoom)

  return (
    <div className="pdf">
      <div className="pdf-bar">
        <button className="link" disabled={page <= 1} onClick={() => setPage(page - 1)} aria-label="이전 쪽">◀</button>
        <span>{pages ? `${page} / ${pages}쪽` : '여는 중…'}</span>
        <button className="link" disabled={page >= pages} onClick={() => setPage(page + 1)} aria-label="다음 쪽">▶</button>
        <span className="spacer" />
        <button className="link" disabled={zoomIndex <= 0} onClick={() => setZoom(zoomSteps[zoomIndex - 1])} aria-label="축소">−</button>
        <button className="link" onClick={() => setZoom(1)} title="칸 너비에 맞춤">{Math.round(zoom * 100)}%</button>
        <button className="link" disabled={zoomIndex >= zoomSteps.length - 1} onClick={() => setZoom(zoomSteps[zoomIndex + 1])} aria-label="확대">+</button>
      </div>
      <div ref={box} className="pdf-page">
        {error ? <p className="error">{error}</p> : <canvas ref={canvas} data-page={page} data-pages={pages} />}
      </div>
    </div>
  )
}
