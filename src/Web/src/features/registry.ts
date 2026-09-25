import type { ReactNode } from 'react'
import { imageFeature } from './image'
import { multimodalFeature } from './multimodal'
import { textFeature } from './text'

/**
 * 프론트에 구현된 Step 모듈. 키는 /api/features 의 key 와 같고 경로는 /{key}/*.
 */
export interface FeatureModule {
  key: string
  title: string
  element: ReactNode
}

export const featureModules: FeatureModule[] = [textFeature, imageFeature, multimodalFeature]
