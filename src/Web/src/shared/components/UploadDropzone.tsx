import { useRef, useState, type DragEvent } from 'react'

interface Props {
  accept: string
  file: File | null
  onFile: (file: File) => void
  disabled?: boolean
}

/** 클릭 또는 드래그 앤 드롭으로 파일 1개 선택 */
export function UploadDropzone({ accept, file, onFile, disabled }: Props) {
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)

  const onDrop = (e: DragEvent) => {
    e.preventDefault()
    setDragging(false)
    const dropped = e.dataTransfer.files[0]
    if (dropped && !disabled) onFile(dropped)
  }

  return (
    <div
      className={`dropzone ${dragging ? 'is-dragging' : ''} ${file ? 'has-file' : ''}`}
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
        hidden
        onChange={(e) => {
          const selected = e.target.files?.[0]
          if (selected) onFile(selected)
          e.target.value = ''
        }}
      />
      {file ? (
        <>
          <strong>{file.name}</strong>
          <span className="muted">{(file.size / 1024 / 1024).toFixed(2)} MB · 다른 파일을 고르려면 클릭</span>
        </>
      ) : (
        <>
          <strong>문서 이미지를 끌어다 놓거나 클릭해서 선택</strong>
          <span className="muted">PNG, JPG, BMP, TIFF, WEBP · 20MB 이하</span>
        </>
      )}
    </div>
  )
}
