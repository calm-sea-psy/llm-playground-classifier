import { useRef, useState, type DragEvent } from 'react'

interface Props {
  accept: string
  files: File[]
  onFiles: (files: File[]) => void
  disabled?: boolean
  max: number
  /** 안내 문구 (기본: 문서 실험용) */
  label?: string
  hint?: string
}

/** 여러 파일 선택 (클릭·드래그 앤 드롭). 같은 이름은 한 번만 */
export function MultiDropzone({
  accept,
  files,
  onFiles,
  disabled,
  max,
  label = '실험할 문서 이미지를 끌어다 놓거나 클릭해서 선택 (여러 장)',
  hint = 'KORIE 영수증이면 정답 라벨로 필드 정확도까지 계산',
}: Props) {
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)

  const add = (list: FileList | null) => {
    if (!list || disabled) return
    const byName = new Map(files.map((f) => [f.name, f]))
    for (const f of Array.from(list)) byName.set(f.name, f)
    onFiles([...byName.values()].slice(0, max))
  }

  const onDrop = (e: DragEvent) => {
    e.preventDefault()
    setDragging(false)
    add(e.dataTransfer.files)
  }

  const totalMb = files.reduce((sum, f) => sum + f.size, 0) / 1024 / 1024

  return (
    <div>
      <div
        className={`dropzone ${dragging ? 'is-dragging' : ''} ${files.length ? 'has-file' : ''}`}
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
        onClick={() => !disabled && input.current?.click()}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && input.current?.click()}
      >
        <input
          ref={input}
          type="file"
          accept={accept}
          multiple
          hidden
          onChange={(e) => {
            add(e.target.files)
            e.target.value = ''
          }}
        />
        {files.length ? (
          <>
            <strong>
              문서 {files.length}장 · {totalMb.toFixed(1)} MB
            </strong>
            <span className="muted">클릭하거나 끌어다 놓아 더 추가 (최대 {max}장)</span>
          </>
        ) : (
          <>
            <strong>{label}</strong>
            <span className="muted">
              {hint} · 최대 {max}장
            </span>
          </>
        )}
      </div>
      {files.length > 0 && (
        <div className="file-chips">
          {files.map((f) => (
            <span key={f.name} className="chip">
              {f.name}
              <button type="button" aria-label={`${f.name} 빼기`} onClick={() => onFiles(files.filter((x) => x !== f))}>
                ×
              </button>
            </span>
          ))}
          <button type="button" className="link-button small" onClick={() => onFiles([])}>
            모두 빼기
          </button>
        </div>
      )}
    </div>
  )
}
