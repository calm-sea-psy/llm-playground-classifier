import { getJson, postForm, sendJson } from '../../shared/api/http'
import type { JobDto, PromptMix } from '../../shared/api/types'

export interface OcrLine {
  text: string
  confidence: number | null
  bbox: number[][] | null
}

/** OcrService 표준 응답 (snake_case 그대로) */
export interface OcrResult {
  engine: string
  model_version: string
  page: number
  width: number
  height: number
  lines: OcrLine[]
  avg_confidence: number | null
  elapsed_ms: number
}

export type Severity = 'Error' | 'Warning'

export interface ValidationIssue {
  rule: string
  field: string | null
  severity: Severity
  message: string
}

export type Fields = Record<string, unknown>

export interface ExtractionAttempt {
  source: 'text' | 'vlm'
  model: string
  elapsedMs: number
  promptTokens: number | null
  completionTokens: number | null
  schemaValid: boolean
  parseError: string | null
  fields: Fields | null
  issues: ValidationIssue[]
  raw: string
  errorCount: number
}

export interface ExtractionResult {
  jobId: string
  model: string
  documentType: string
  finalSource: 'text' | 'vlm' | null
  fallbackUsed: boolean
  fallbackReason: string | null
  validationPassed: boolean | null
  errorCount: number
  warningCount: number
  fields: Fields | null
  issues: ValidationIssue[]
  attempts: ExtractionAttempt[]
  readingText: string
  classifyMs: number
  llmElapsedMs: number
}

export interface ModelList {
  defaultModel: string
  models: string[]
}

export const createTextJob = (file: File, model?: string) => {
  const form = new FormData()
  form.append('file', file)
  if (model) form.append('model', model)
  return postForm<JobDto>('/api/text/jobs', form)
}

export const getModels = () => getJson<ModelList>('/api/text/models')
export const getOcr = (jobId: string) => getJson<OcrResult>(`/api/text/jobs/${jobId}/ocr`)
export const getResult = (jobId: string) => getJson<ExtractionResult>(`/api/text/jobs/${jobId}/result`)

// ---- 설정 조합 · 모델 비교 실험 ----

export type UnloadPolicy = 'Never' | 'Always' | 'LargeImages'

/** 문서 처리 설정 조합 (API PipelineSettings) */
export interface PipelineSettings {
  model: string
  ocrEngine: string
  unloadBeforeOcr: UnloadPolicy
  fallbackConfidence: number
  vlmFallback: boolean
  amountsAsString: boolean
}

export interface SettingsDto {
  settings: PipelineSettings
  summary: string
  note: string | null
  updatedAt: string
}

export interface SettingsOptions {
  models: string[]
  ocrEngines: string[]
  unloadPolicies: UnloadPolicy[]
}

export interface ComboResult {
  index: number
  settings: PipelineSettings
  summary: string
  total: number
  completed: number
  failed: number
  running: number
  passRate: number | null
  fallbackRate: number | null
  fieldAccuracy: number | null
  amountAccuracy: number | null
  labeledDocs: number
  medianJobSec: number | null
  medianOcrSec: number | null
  medianLlmSec: number | null
  score: number | null
  rank: number | null
}

export interface DocCell {
  jobId: string
  status: string
  passed: boolean | null
  fallback: boolean
  jobSec: number | null
  correctFields: number | null
  labeledFields: number | null
  total: string | null
}

export interface ExperimentDetail {
  id: string
  name: string
  createdAt: string
  docCount: number
  status: 'Running' | 'Completed'
  done: number
  total: number
  environment: {
    cpu?: string
    memoryGb?: number
    gpus?: { name: string; memoryTotalMb: number; driver: string }[]
    ollamaVersion?: string
  } | null
  labeled: boolean
  combos: ComboResult[]
  recommendedIndex: number | null
  docs: { fileName: string; cells: (DocCell | null)[] }[]
  scoreFormula: string
  /** 같은 프롬프트가 실험 도중 다른 버전으로 쓰였으면 목록 */
  promptMixes?: PromptMix[]
}

export interface ExperimentSummary {
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



export const getSettings = () => getJson<SettingsDto>('/api/text/settings')
export const getSettingsOptions = () => getJson<SettingsOptions>('/api/text/settings/options')
export const listExperiments = () => getJson<ExperimentSummary[]>('/api/text/experiments')
export const getExperiment = (id: string) => getJson<ExperimentDetail>(`/api/text/experiments/${id}`)
export const applyCombo = (id: string, index: number) =>
  sendJson<SettingsDto>(`/api/text/experiments/${id}/combos/${index}/apply`, 'POST')
export const deleteExperiment = (id: string) => sendJson<void>(`/api/text/experiments/${id}`, 'DELETE')

export const createExperiment = (name: string, files: File[], combos: PipelineSettings[]) => {
  const form = new FormData()
  form.append('name', name)
  form.append('combos', JSON.stringify(combos))
  for (const file of files) form.append('files', file)
  return postForm<{ id: string; name: string; jobs: number }>('/api/text/experiments', form)
}

/** 설정 한 줄 요약 (API Summary 와 같은 형식) */
export const summarize = (s: PipelineSettings) =>
  `${s.model} · ${s.ocrEngine === 'ppstructure' ? '경로 B' : '경로 A'} · LLM 내리기 ${s.unloadBeforeOcr} · 폴백 ${
    s.vlmFallback ? `<${s.fallbackConfidence.toFixed(2)}` : '끔'
  }${s.amountsAsString ? ' · 금액 문자열' : ''}`

export const sameSettings = (a: PipelineSettings, b: PipelineSettings) =>
  a.model === b.model &&
  a.ocrEngine === b.ocrEngine &&
  a.unloadBeforeOcr === b.unloadBeforeOcr &&
  a.fallbackConfidence === b.fallbackConfidence &&
  a.vlmFallback === b.vlmFallback &&
  a.amountsAsString === b.amountsAsString
