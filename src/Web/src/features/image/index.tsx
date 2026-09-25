import type { FeatureModule } from '../registry'
import { ImageRoutes } from './ImageRoutes'

/** Step 2 이미지: 흉부 X-ray ➔ CNN(폐렴 신호·18개 소견, Grad-CAM) ➔ VLM 판독 초안 ➔ 교차 검증 */
export const imageFeature: FeatureModule = {
  key: 'image',
  title: '흉부 X-ray 판독',
  element: <ImageRoutes />,
}
