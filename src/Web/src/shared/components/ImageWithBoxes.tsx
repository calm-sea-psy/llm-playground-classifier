export interface Box {
  points: number[][]
  tone?: 'low' | 'normal'
}

interface Props {
  src: string
  width: number
  height: number
  boxes: Box[]
  active: number | null
  onActive: (index: number | null) => void
  showBoxes: boolean
}

/** 원본 이미지 위에 OCR bbox 를 겹쳐 그림. bbox 좌표는 OCR 결과의 width/height 기준 픽셀 */
export function ImageWithBoxes({ src, width, height, boxes, active, onActive, showBoxes }: Props) {
  return (
    <div className="image-stage" style={{ aspectRatio: `${width} / ${height}` }}>
      <img src={src} alt="업로드한 문서" />
      {showBoxes && (
        <svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" onMouseLeave={() => onActive(null)}>
          {boxes.map((box, i) => (
            <polygon
              key={i}
              points={box.points.map((p) => p.join(',')).join(' ')}
              className={`${box.tone === 'low' ? 'low' : ''} ${i === active ? 'active' : ''}`}
              onMouseEnter={() => onActive(i)}
            />
          ))}
        </svg>
      )}
    </div>
  )
}
