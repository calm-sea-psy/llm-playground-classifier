import { getJson, postForm } from '../../shared/api/http'
import type { JobDto } from '../../shared/api/types'
import type { Population } from '../image/api'

/** 소견서 요약 (LLM JSON 그대로, 키는 snake_case) */
export interface ReportSummary {
  indication: string | null
  key_symptoms: string[]
  findings: string[]
  final_diagnosis: string
  normal: boolean
  mentions: Record<'pneumonia' | 'cardiomegaly' | 'effusion' | 'atelectasis' | 'edema', boolean>
}

/** 소견 하나를 소견서·CNN·VLM 으로 나란히 (없는 값은 API 가 JSON 에서 뺌 ➔ undefined) */
export interface ConcordanceRow {
  key: string
  name: string
  report?: boolean | null
  cnn?: boolean | null
  cnnProbability?: number | null
  cnnLabel?: string | null
  vlm?: boolean | null
  agree?: boolean | null
  /** CNN 이 기준값 바로 위(경계) ➔ 판단 보류, 비교하지 않음 */
  cnnBorderline?: boolean
}

export interface FinalReport {
  summary: string
  key_findings: string[]
  concordance_note: string
  recommendations: string[]
}

export interface MultimodalResult {
  jobId: string
  reportSource: 'text' | 'ocr'
  reportText: string
  reportSummary: ReportSummary | null
  concordance: ConcordanceRow[]
  finalReport: FinalReport | null
  issues: { rule: string; severity: 'Error' | 'Warning'; message: string }[]
  needsReview: boolean
  llmElapsedMs: number
  /** 소견서 요약 때 붙인 용어 정의 (용어집 적용 전 작업은 없음) */
  glossary?: string[] | null
}

export type ReportInput = { text: string } | { file: File }

export function createMultimodalJob(xray: File, report: ReportInput, population: Population) {
  const form = new FormData()
  form.append('xray', xray)
  if ('text' in report) form.append('reportText', report.text)
  else form.append('reportFile', report.file)
  form.append('population', population)
  return postForm<JobDto>('/api/multimodal/jobs', form)
}

export const getMultimodalResult = (jobId: string) => getJson<MultimodalResult>(`/api/multimodal/jobs/${jobId}/result`)

export const reportFileUrl = (jobId: string) => `/api/multimodal/jobs/${jobId}/report-file`
