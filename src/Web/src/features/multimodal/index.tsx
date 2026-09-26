import type { FeatureModule } from '../registry'
import { MultimodalRoutes } from './MultimodalRoutes'

/** Step 3 통합: 이미지 + 텍스트 문서 ➔ 이미지 분석 + 문서 요약 ➔ 일치 비교 ➔ 종합 보고서 */
export const multimodalFeature: FeatureModule = {
  key: 'multimodal',
  title: 'CNN + VLM + 텍스트 추출',
  element: <MultimodalRoutes />,
}
