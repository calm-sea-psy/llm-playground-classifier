import { useEffect, useState, type ReactNode } from 'react'
import { getJson } from '../api/http'
import type { FeatureState } from '../api/types'
import { FeatureContext, type FeatureContextValue } from './featureContext'

/** 앱 시작 시 GET /api/features 로 Step 노출 설정을 받아 메뉴·라우트 구성에 사용 (todo 7번) */
export function FeatureProvider({ children }: { children: ReactNode }) {
  const [value, setValue] = useState<FeatureContextValue>({ loading: true, error: null, features: [] })

  useEffect(() => {
    const controller = new AbortController()
    getJson<FeatureState[]>('/api/features', controller.signal)
      .then((features) => setValue({ loading: false, error: null, features }))
      .catch((e) => {
        if (!controller.signal.aborted) {
          setValue({ loading: false, error: e instanceof Error ? e.message : String(e), features: [] })
        }
      })
    return () => controller.abort()
  }, [])

  return <FeatureContext.Provider value={value}>{children}</FeatureContext.Provider>
}
