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

/** documentType: 문서 종류 팩 id. 없으면 LLM 이 분류 (영수증 · 상업송장 · 보험 청구서만) */
export const createTextJob = (file: File, model?: string, documentType?: string) => {
  const form = new FormData()
  form.append('file', file)
  if (model) form.append('model', model)
  if (documentType) form.append('documentType', documentType)
  return postForm<JobDto>('/api/text/jobs', form)
}

/** 문서 종류 팩의 필드 (packs/{종류}/type.json). type: text · date · month · amount · number · list … */
export interface PackField {
  name: string
  label: string
  type: string
  required: boolean
  key: string | null
  items: PackField[] | null
  description: string | null
}

export interface Pack {
  id: string
  version: string
  displayName: string
  description: string | null
  /** LLM 분류로 찾는 종류 (아니면 문서 처리에서 골라야 함) */
  autoClassified: boolean
  fields: PackField[]
  rules: string[]
  genericChecks: boolean
  forbidden: string[]
}

let packsCache: Promise<Pack[]> | null = null
/** 팩 목록 (화면 전체에서 한 번만 불러옴) */
export const getPacks = () => (packsCache ??= getJson<Pack[]>('/api/text/packs').catch((e) => ((packsCache = null), Promise.reject(e))))

/** type.json 원본의 필드 (snake_case 그대로, 화면에 없는 키도 보존) */
export interface PackFieldJson {
  name: string
  label: string
  type: string
  required?: boolean
  key?: string | null
  ranged?: boolean
  items?: PackFieldJson[] | null
  description?: string | null
  [extra: string]: unknown
}

export interface PackForbiddenJson {
  id: string
  label: string
  pattern?: string | null
}

/** packs/{종류}/type.json 원본 (excel · notes · changelog 등 화면에서 안 고치는 키도 그대로 저장) */
export interface PackTypeJson {
  id: string
  version: string
  display_name: string
  description?: string | null
  generic_checks?: boolean
  variables?: Record<string, string>
  fields: PackFieldJson[]
  forbidden: PackForbiddenJson[]
  rules: string[]
  changelog?: { version: string; date: string; changes: string }[]
  [extra: string]: unknown
}

export interface PackDetail {
  summary: Pack
  type: PackTypeJson
  /** 프롬프트 파일 (prompt.md · prompt.{모델 계열}.md · user.md · vlm.user.md) */
  files: Record<string, string>
  /** 프롬프트 관리에 등록된 종류: 문서 처리는 지시문을 프롬프트 관리의 적용 버전으로 씀 */
  managed: boolean
  /** 고치기 전 버전 보관 (packs/{종류}/.history) */
  history: { version: string; savedAt: string }[]
}

export interface PackMeta {
  fieldTypes: { type: string; description: string }[]
  rules: { name: string; kind: 'fixer' | 'check' | 'generic'; description: string | null }[]
}

export const getPack = (id: string) => getJson<PackDetail>(`/api/text/packs/${id}`)
export const getPackMeta = () => getJson<PackMeta>('/api/text/packs/meta')

/** 저장 (create = 새 종류). files 의 값이 null 이면 그 파일 삭제. 실패하면 문제가 한 줄에 하나씩 든 메시지 */
export async function savePack(type: PackTypeJson, files: Record<string, string | null>, note: string, create: boolean) {
  const saved = await sendJson<Pack>(create ? '/api/text/packs' : `/api/text/packs/${type.id}`, create ? 'POST' : 'PUT', {
    type,
    files,
    note,
  })
  packsCache = null
  return saved
}

/** 팩을 고친 뒤 목록 다시 읽기 */
export const reloadPacks = () => ((packsCache = null), getPacks())

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
  /** 정답 있고 검증 통과한 문서 수 ➔ 그중 합계 틀림 · 필드 하나라도 틀림 */
  passedLabeled: number
  passedWrongTotal: number
  passedWrongAny: number
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
  /** 실험 설명 (문서를 어떻게 골랐는지 등) */
  description?: string | null
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

export const createExperiment = (name: string, files: File[], combos: PipelineSettings[], description = '') => {
  const form = new FormData()
  form.append('name', name)
  form.append('description', description)
  form.append('combos', JSON.stringify(combos))
  for (const file of files) form.append('files', file)
  return postForm<{ id: string; name: string; jobs: number }>('/api/text/experiments', form)
}

/** 설정 한 줄 요약 (API Summary 와 같은 형식) */
export const summarize = (s: PipelineSettings) =>
  `${s.model} · ${s.ocrEngine === 'ppstructure' ? '경로 B' : '경로 A'} · LLM 내리기 ${s.unloadBeforeOcr} · 폴백 ${
    s.vlmFallback ? `<${s.fallbackConfidence.toFixed(2)}` : '끔'
  }`

export const sameSettings = (a: PipelineSettings, b: PipelineSettings) =>
  a.model === b.model &&
  a.ocrEngine === b.ocrEngine &&
  a.unloadBeforeOcr === b.unloadBeforeOcr &&
  a.fallbackConfidence === b.fallbackConfidence &&
  a.vlmFallback === b.vlmFallback
