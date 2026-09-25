import type { FeatureModule } from '../registry'
import { MultimodalRoutes } from './MultimodalRoutes'

/** Step 3 통합: 흉부 X-ray + 소견서 ➔ 영상 분석 + 소견서 요약 ➔ 일치 비교 ➔ 환자 종합 보고서 */
export const multimodalFeature: FeatureModule = {
  key: 'multimodal',
  title: 'X-ray + 소견서 통합',
  element: <MultimodalRoutes />,
}
