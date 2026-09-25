import type { FeatureModule } from '../registry'
import { TextRoutes } from './TextRoutes'

/** Step 1 텍스트: 문서 업로드 ➔ OCR ➔ LLM 구조화 ➔ 검증 ➔ JSON */
export const textFeature: FeatureModule = {
  key: 'text',
  title: '문서 텍스트 추출',
  element: <TextRoutes />,
}
