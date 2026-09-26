import type { FeatureModule } from '../registry'
import { ImageRoutes } from './ImageRoutes'

/** Step 2 이미지: 이미지 ➔ CNN(이진 판단 · 다중 항목 18개, Grad-CAM) ➔ VLM 판독 초안 ➔ 교차 검증 */
export const imageFeature: FeatureModule = {
  key: 'image',
  title: 'CNN + VLM 판독',
  element: <ImageRoutes />,
}
