import { useEffect, useState } from 'react'
import { jobFileUrl } from '../../../shared/api/jobs'
import { heatmapUrl, type HeatmapRole } from '../api'
import { findingName } from '../labels'

export interface HeatmapLayer {
  role: HeatmapRole
  title: string
  label: string
  /** 이 소견이 양성인지 (음성 소견의 히트맵은 의미가 약함) */
  positive: boolean
  region: [number, number, number, number]
}

/** 흑백 Grad-CAM(밝을수록 영향 큼) ➔ jet 색 + 투명도. 약한 영역(15% 미만)은 투명하게 두어 원본이 보이도록 */
function colorize(src: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const img = new Image()
    img.onload = () => {
      const canvas = document.createElement('canvas')
      canvas.width = img.width
      canvas.height = img.height
      const ctx = canvas.getContext('2d')!
      ctx.drawImage(img, 0, 0)
      const data = ctx.getImageData(0, 0, img.width, img.height)
      const px = data.data
      const clamp = (v: number) => Math.max(0, Math.min(1, v))
      for (let i = 0; i < px.length; i += 4) {
        const v = px[i] / 255
        px[i] = clamp(1.5 - Math.abs(4 * v - 3)) * 255
        px[i + 1] = clamp(1.5 - Math.abs(4 * v - 2)) * 255
        px[i + 2] = clamp(1.5 - Math.abs(4 * v - 1)) * 255
        px[i + 3] = v < 0.15 ? 0 : v * 0.85 * 255
      }
      ctx.putImageData(data, 0, 0)
      resolve(canvas.toDataURL())
    }
    img.onerror = () => reject(new Error('히트맵을 불러오지 못했습니다'))
    img.src = src
  })
}

/** 원본 X-ray 위에 Grad-CAM 을 겹쳐 보여줌. region 은 원본 픽셀 좌표라 % 로 바꿔 위치를 맞춤 */
export function HeatmapViewer({
  jobId,
  width,
  height,
  layers,
}: {
  jobId: string
  width: number
  height: number
  layers: HeatmapLayer[]
}) {
  const [role, setRole] = useState<HeatmapRole | 'off'>(layers[0]?.role ?? 'off')
  const [opacity, setOpacity] = useState(0.8)
  const [overlays, setOverlays] = useState<Partial<Record<HeatmapRole, string>>>({})
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    Promise.all(layers.map(async (l) => [l.role, await colorize(heatmapUrl(jobId, l.role))] as const))
      .then((pairs) => !cancelled && setOverlays(Object.fromEntries(pairs)))
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)))
    return () => {
      cancelled = true
    }
  }, [jobId, layers])

  const layer = layers.find((l) => l.role === role)
  const [x0, y0, x1, y1] = layer?.region ?? [0, 0, 0, 0]

  return (
    <div>
      <div className="heatmap-tools small">
        <div className="tabs">
          {layers.map((l) => (
            <button key={l.role} className={role === l.role ? 'on' : ''} onClick={() => setRole(l.role)}>
              {l.title}: {findingName(l.label)}
            </button>
          ))}
          <button className={role === 'off' ? 'on' : ''} onClick={() => setRole('off')}>
            원본만
          </button>
        </div>
        {role !== 'off' && (
          <label className="muted">
            진하기{' '}
            <input type="range" min={0.2} max={1} step={0.1} value={opacity} onChange={(e) => setOpacity(Number(e.target.value))} />
          </label>
        )}
      </div>
      <div className="heatmap-wrap">
        <img src={jobFileUrl(jobId)} alt="업로드한 X-ray" />
        {layer && overlays[layer.role] && (
          <img
            className="heatmap-overlay"
            src={overlays[layer.role]}
            alt=""
            style={{
              left: `${(x0 / width) * 100}%`,
              top: `${(y0 / height) * 100}%`,
              width: `${((x1 - x0) / width) * 100}%`,
              height: `${((y1 - y0) / height) * 100}%`,
              opacity,
            }}
          />
        )}
      </div>
      {layer && !layer.positive && (
        <p className="text-warn small">
          {findingName(layer.label)} 은(는) 음성(기준 미만)입니다. Grad-CAM 은 항상 가장 강한 곳을 표시하므로, 음성 소견의 히트맵이
          가리키는 영역은 의미가 약합니다.
        </p>
      )}
      <p className="muted small">
        {error ??
          '색이 진할수록(빨강) 모델이 그 소견을 판단할 때 크게 본 영역입니다 (Grad-CAM). 모델은 가운데 정사각형 영역만 봅니다.'}
      </p>
    </div>
  )
}
