import { getJson } from './http'

/** 기기 사양 (GET /api/system/info, 모델 비교 페이지 상단·실험 기록) */
export interface SystemInfo {
  os: string
  cpu: string
  logicalCores: number
  memoryGb: number
  gpus: { name: string; memoryTotalMb: number; memoryUsedMb: number; driver: string; temperatureC: number | null; utilizationPercent: number | null }[]
  runtime: string
  ollamaVersion: string | null
  ollamaLoaded: { name: string; vramBytes: number }[]
  llmModels: string[]
  ocrEngines: Record<string, { loaded: boolean; model_version: string | null }> | null
}

export const getSystemInfo = () => getJson<SystemInfo>('/api/system/info')
