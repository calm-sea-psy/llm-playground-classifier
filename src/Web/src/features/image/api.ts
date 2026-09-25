import { getJson, postForm, sendJson } from '../../shared/api/http'
import type { JobDto, PromptMix } from '../../shared/api/types'

export type Population = 'adult' | 'pediatric'
export type HeatmapRole = 'pneumonia' | 'findings'

export interface CnnFinding {
  label: string
  probability: number
  threshold: number
  positive: boolean
}

export interface CnnResult {
  engine: string
  modelVersion: string
  width: number
  height: number
  findings: CnnFinding[]
  /** Grad-CAM. region = 원본 픽셀 영역 [x0, y0, x1, y1] (PNG 는 /heatmap/{role}) */
  heatmap: { label: string; region: [number, number, number, number] } | null
  elapsedMs: number
}

/** VLM 판독 초안 (LLM JSON 그대로, 키는 snake_case) */
export interface ImageReport {
  findings: string[]
  impression: string
  normal: boolean
  pneumonia_suspected: boolean
  recommendation: string | null
}

export interface ImageIssue {
  rule: string
  severity: 'Error' | 'Warning'
  message: string
}

export interface ImageResult {
  jobId: string
  population: Population
  /** "엔진:소견" (소아 pneumonia:Pneumonia, 성인 xrv:Consolidation) */
  pneumoniaSource: string
  pneumoniaProbability: number | null
  pneumoniaPositive: boolean | null
  pneumonia: CnnResult
  findings: CnnResult
  cnnElapsedMs: number
  model: string | null
  report: ImageReport | null
  reportAttempt: { sawCnn: boolean; parseError: string | null; elapsedMs: number } | null
  llmElapsedMs: number
  issues: ImageIssue[]
  validationPassed: boolean
}


export const getImageResult = (jobId: string) => getJson<ImageResult>(`/api/image/jobs/${jobId}/result`)

export const heatmapUrl = (jobId: string, role: HeatmapRole) => `/api/image/jobs/${jobId}/heatmap/${role}`

export function createImageJob(file: File, population: Population, model?: string) {
  const form = new FormData()
  form.append('file', file)
  form.append('population', population)
  if (model) form.append('model', model)
  return postForm<JobDto>('/api/image/jobs', form)
}

// ---- 판독 기본 설정 · 모델 비교 실험 ----

/** 판독 설정 조합 (API ImageSettings) */
export interface ImageSettings {
  model: string
  vlmReport: boolean
  vlmSeesCnn: boolean
  population: Population
}

export interface ImageSettingsDto {
  settings: ImageSettings
  summary: string
  note: string | null
  updatedAt: string
}

export interface ImageSettingsOptions {
  models: string[]
  populations: Population[]
}

export interface ImageComboResult {
  index: number
  settings: ImageSettings
  summary: string
  total: number
  completed: number
  failed: number
  running: number
  labeledDocs: number
  cnnSensitivity: number | null
  cnnSpecificity: number | null
  cnnBalanced: number | null
  cnnAuc: number | null
  vlmSensitivity: number | null
  vlmSpecificity: number | null
  vlmBalanced: number | null
  /** VLM 판독 초안이 형식대로 나온 비율 */
  vlmSuccessRate: number | null
  vlmNormalAccuracy: number | null
  agreementRate: number | null
  cnnErrors: number
  /** CNN 오답 중 CNN·VLM 불일치(사람 확인)로 걸러진 비율 */
  flagRecall: number | null
  medianJobSec: number | null
  medianCnnMs: number | null
  medianLlmSec: number | null
  score: number | null
  rank: number | null
}

export interface ImageDocCell {
  jobId: string
  status: string
  cnnPositive: boolean | null
  cnnProbability: number | null
  vlmPneumonia: boolean | null
  agree: boolean | null
  jobSec: number | null
}

export interface ImageExperimentDetail {
  id: string
  name: string
  createdAt: string
  docCount: number
  status: 'Running' | 'Completed'
  done: number
  total: number
  environment: { cpu?: string; memoryGb?: number; gpus?: { name: string; memoryTotalMb: number }[]; ollamaVersion?: string } | null
  labeled: boolean
  combos: ImageComboResult[]
  recommendedIndex: number | null
  docs: { fileName: string; truth: { dataset: string; pneumonia: boolean | null; normal: boolean | null } | null; cells: (ImageDocCell | null)[] }[]
  scoreFormula: string
  /** 같은 프롬프트가 실험 도중 다른 버전으로 쓰였으면 목록 */
  promptMixes?: PromptMix[]
}

export interface ImageExperimentSummary {
  id: string
  name: string
  createdAt: string
  docCount: number
  comboCount: number
  status: 'Running' | 'Completed'
  done: number
  total: number
  labeled: boolean
  recommended: string | null
  bestScore: number | null
}

export const getImageSettings = () => getJson<ImageSettingsDto>('/api/image/settings')
export const getImageSettingsOptions = () => getJson<ImageSettingsOptions>('/api/image/settings/options')
export const listImageExperiments = () => getJson<ImageExperimentSummary[]>('/api/image/experiments')
export const getImageExperiment = (id: string) => getJson<ImageExperimentDetail>(`/api/image/experiments/${id}`)
export const applyImageCombo = (id: string, index: number) =>
  sendJson<ImageSettingsDto>(`/api/image/experiments/${id}/combos/${index}/apply`, 'POST')
export const deleteImageExperiment = (id: string) => sendJson<void>(`/api/image/experiments/${id}`, 'DELETE')

export const createImageExperiment = (name: string, files: File[], combos: ImageSettings[]) => {
  const form = new FormData()
  form.append('name', name)
  form.append('combos', JSON.stringify(combos))
  for (const file of files) form.append('files', file)
  return postForm<{ id: string; name: string; jobs: number }>('/api/image/experiments', form)
}

export const sameImageSettings = (a: ImageSettings, b: ImageSettings) =>
  a.model === b.model && a.vlmReport === b.vlmReport && a.vlmSeesCnn === b.vlmSeesCnn && a.population === b.population
