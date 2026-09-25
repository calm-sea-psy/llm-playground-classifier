// API 응답 타입 (src/Api 의 DTO 와 대응)

export type JobStatus = 'Queued' | 'OcrRunning' | 'CnnRunning' | 'LlmRunning' | 'Validating' | 'Completed' | 'Failed'

export interface JobDto {
  jobId: string
  jobType: string
  model: string | null
  status: JobStatus
  message: string | null
  error: string | null
  fileName: string
  createdAt: string
  updatedAt: string
  startedAt: string | null
  completedAt: string | null
  /** 처리에 쓴 프롬프트: "모듈/이름" ➔ 버전(null = 파일 기본값)·내용 해시. 기록 도입 전 작업은 없음 */
  prompts?: Record<string, PromptUse> | null
}

export interface PromptUse {
  version: number | null
  hash: string
}

/** 실험 도중 같은 프롬프트가 다른 버전으로 쓰인 경우 (versions 예: "기본값 (ab12cd34)", "v2 (9f00e1aa)") */
export interface PromptMix {
  prompt: string
  versions: string[]
}

export interface FeatureState {
  key: string
  displayName: string
  implemented: boolean
  enabled: boolean
  showInUi: boolean
}

export const isFinished = (status: JobStatus) => status === 'Completed' || status === 'Failed'
