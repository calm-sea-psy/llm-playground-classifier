import type { ImageSettings, ImageSettingsOptions, Population } from '../api'
import { POPULATION_NAMES } from '../labels'

interface Props {
  index: number
  value: ImageSettings
  options: ImageSettingsOptions
  onChange: (value: ImageSettings) => void
  onRemove?: () => void
  isDefault: boolean
  disabled?: boolean
}

/** 이미지 실험 조합 하나: VLM 모델 · CNN 모델(이진 판단 출처) · VLM 판독 켜기 · VLM 에 CNN 결과 보여 주기 */
export function ImageComboEditor({ index, value, options, onChange, onRemove, isDefault, disabled }: Props) {
  const set = <K extends keyof ImageSettings>(key: K, v: ImageSettings[K]) => onChange({ ...value, [key]: v })

  return (
    <fieldset className="combo" disabled={disabled}>
      <legend>
        조합 {index + 1}
        {isDefault && <span className="chip chip-ok">현재 기본</span>}
      </legend>
      <label>
        <span>VLM 모델</span>
        <select value={value.model} onChange={(e) => set('model', e.target.value)}>
          {options.models.map((m) => (
            <option key={m}>{m}</option>
          ))}
        </select>
      </label>
      <label>
        <span title="이진 판단 출처: 파인튜닝 모델 = 테스트 데이터로 파인튜닝, 사전학습 모델 = xrv 경화 출력">CNN 모델</span>
        <select value={value.population} onChange={(e) => set('population', e.target.value as Population)}>
          {options.populations.map((p) => (
            <option key={p} value={p}>
              {POPULATION_NAMES[p]}
            </option>
          ))}
        </select>
      </label>
      <label className="check">
        <input type="checkbox" checked={value.vlmReport} onChange={(e) => set('vlmReport', e.target.checked)} />
        <span>VLM 판독 초안 작성</span>
      </label>
      <label className="check">
        <input
          type="checkbox"
          checked={value.vlmSeesCnn}
          disabled={!value.vlmReport}
          onChange={(e) => set('vlmSeesCnn', e.target.checked)}
        />
        <span title="끄면 VLM 이 CNN 결과를 모르고 판독해 두 판단을 독립적으로 비교">VLM 에 CNN 결과 보여 주기</span>
      </label>
      {onRemove && (
        <button type="button" className="link-button small combo-remove" onClick={onRemove}>
          조합 빼기
        </button>
      )}
    </fieldset>
  )
}
