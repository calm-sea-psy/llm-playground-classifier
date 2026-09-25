import { createContext, useContext } from 'react'
import type { FeatureState } from '../api/types'

export interface FeatureContextValue {
  loading: boolean
  error: string | null
  /** 서버 설정 전체 (Enabled/ShowInUi) */
  features: FeatureState[]
}

export const FeatureContext = createContext<FeatureContextValue>({ loading: true, error: null, features: [] })

export const useFeatures = () => useContext(FeatureContext)
