import { Link } from 'react-router-dom'
import type { ImageSettingsDto } from '../api'
import { POPULATION_NAMES } from '../labels'

const CNN_MODEL_NOTE = {
  adult: 'xrv 경화 출력 (테스트 데이터: IU 성인)',
  pediatric: '테스트 데이터로 파인튜닝 (Kaggle 소아)',
} as const

/**
 * 판독 · 통합 화면에 적용되는 설정 (고르지 않고 표시만). 모델 비교에서 [기본으로 적용]한 조합이 그대로 쓰여
 * 실험 ➔ 기본 설정 ➔ 처리가 한 파이프라인으로 이어진다. extra = 화면별로 덧붙일 줄 (예: 텍스트 문서 ➔ OCR)
 */
export function AppliedPipeline({ settings, extra }: { settings: ImageSettingsDto | null; extra?: { label: string; value: string }[] }) {
  if (!settings) return <p className="muted small">적용 설정 불러오는 중…</p>
  const s = settings.settings
  const rows = [
    { label: 'CNN 모델 (이진 판단)', value: `${POPULATION_NAMES[s.population]} — ${CNN_MODEL_NOTE[s.population]}` },
    {
      label: 'VLM 판독',
      value: !s.vlmReport ? '끔 (CNN 결과만)' : `${s.model} · ${s.vlmSeesCnn ? 'CNN 결과를 참고해 작성' : 'CNN 결과를 보지 않고 독립 판독'}`,
    },
    ...(extra ?? []),
  ]
  return (
    <section className="applied-pipeline small">
      <header>
        <strong>적용 중인 파이프라인</strong>{' '}
        <Link to="/image" className="muted">
          (CNN + VLM 판독 모델 비교에서 정함)
        </Link>
      </header>
      <dl>
        {rows.map((r) => (
          <div key={r.label}>
            <dt>{r.label}</dt>
            <dd>{r.value}</dd>
          </div>
        ))}
      </dl>
      {settings.note && <p className="muted">근거: {settings.note}</p>}
    </section>
  )
}
