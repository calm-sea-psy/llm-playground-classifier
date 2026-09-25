import type { PipelineSettings, SettingsOptions, UnloadPolicy } from '../api'

const OCR_LABEL: Record<string, string> = {
  paddleocr: '경로 A · PP-OCR 줄 텍스트',
  ppstructure: '경로 B · PP-StructureV3 표 구조',
}
const UNLOAD_LABEL: Record<UnloadPolicy, string> = {
  Never: '유지 (내리지 않음)',
  Always: '항상 내림',
  LargeImages: '큰 이미지(5MP 초과)만 내림',
}

interface Props {
  index: number
  value: PipelineSettings
  options: SettingsOptions
  onChange: (value: PipelineSettings) => void
  onRemove?: () => void
  isDefault: boolean
  disabled?: boolean
}

/** 실험 조합 한 줄: 모델·OCR 경로·LLM 내리기·폴백·금액 스키마 */
export function ComboEditor({ index, value, options, onChange, onRemove, isDefault, disabled }: Props) {
  const set = <K extends keyof PipelineSettings>(key: K, v: PipelineSettings[K]) => onChange({ ...value, [key]: v })

  return (
    <fieldset className="combo" disabled={disabled}>
      <legend>
        조합 {index + 1}
        {isDefault && <span className="chip chip-ok">현재 기본</span>}
      </legend>
      <label>
        <span>LLM 모델</span>
        <select value={value.model} onChange={(e) => set('model', e.target.value)}>
          {options.models.map((m) => (
            <option key={m}>{m}</option>
          ))}
        </select>
      </label>
      <label>
        <span>OCR 경로</span>
        <select value={value.ocrEngine} onChange={(e) => set('ocrEngine', e.target.value)}>
          {options.ocrEngines.map((o) => (
            <option key={o} value={o}>
              {OCR_LABEL[o] ?? o}
            </option>
          ))}
        </select>
      </label>
      <label>
        <span title="OCR 과 LLM 이 GPU 메모리를 나눠 쓸 때의 대책">OCR 전 LLM 내리기</span>
        <select value={value.unloadBeforeOcr} onChange={(e) => set('unloadBeforeOcr', e.target.value as UnloadPolicy)}>
          {options.unloadPolicies.map((p) => (
            <option key={p} value={p}>
              {UNLOAD_LABEL[p]}
            </option>
          ))}
        </select>
      </label>
      <label className="check">
        <input type="checkbox" checked={value.vlmFallback} onChange={(e) => set('vlmFallback', e.target.checked)} />
        <span>VLM 폴백 (검증 오류·저신뢰 시 이미지로 재추출)</span>
      </label>
      <label>
        <span title="OCR 평균 신뢰도가 이보다 낮으면 폴백">폴백 신뢰도 기준</span>
        <input
          type="number"
          min={0}
          max={1}
          step={0.05}
          value={value.fallbackConfidence}
          disabled={!value.vlmFallback}
          onChange={(e) => set('fallbackConfidence', Number(e.target.value))}
        />
      </label>
      <label className="check">
        <input type="checkbox" checked={value.amountsAsString} onChange={(e) => set('amountsAsString', e.target.checked)} />
        <span>금액을 문자열로 받기</span>
      </label>
      {onRemove && (
        <button type="button" className="link-button small combo-remove" onClick={onRemove}>
          조합 빼기
        </button>
      )}
    </fieldset>
  )
}
