import { getJson, sendJson } from '../../shared/api/http'

export interface PromptInfo {
  module: string
  name: string
  kind: 'md' | 'json'
  description: string
  /** 모델 전용 변형이면 모델 계열 (예: qwen3-vl) */
  modelFamily?: string | null
  /** 모델 전용 변형을 만들 수 있는 공용 프롬프트 */
  allowsModelVariants: boolean
  hasFile: boolean
  /** 적용 중인 버전 (없으면 파일 기본값) */
  activeVersion?: number | null
  versionCount: number
  updatedAt?: string | null
  variables: string[]
}

export interface PromptCatalog {
  modules: { key: string; displayName: string }[]
  modelFamilies: string[]
  prompts: PromptInfo[]
}

export interface PromptVersion {
  id: number
  version: number
  content: string
  note?: string | null
  active: boolean
  createdAt: string
}

export interface PromptDetail {
  info: PromptInfo
  /** 지금 파이프라인이 쓰는 내용 */
  content: string
  /** 파일 기본값 (UI 에서 추가한 모델 전용 변형은 없음) */
  fileContent?: string | null
  /** 모델 전용 변형이면 공용 프롬프트 이름·내용 */
  baseName?: string | null
  baseContent?: string | null
  versions: PromptVersion[]
}

const base = '/api/prompts'
const path = (module: string, name: string) => `${base}/${module}/${encodeURIComponent(name)}`

export const listPrompts = () => getJson<PromptCatalog>(base)

export const getPrompt = (module: string, name: string) => getJson<PromptDetail>(path(module, name))

export const savePrompt = (module: string, name: string, content: string, note: string, activate: boolean) =>
  sendJson<{ id: number; version: number }>(`${path(module, name)}/versions`, 'POST', { content, note, activate })

export const activateVersion = (module: string, name: string, id: number) =>
  sendJson<void>(`${path(module, name)}/versions/${id}/activate`, 'POST')

export const resetPrompt = (module: string, name: string) => sendJson<void>(`${path(module, name)}/reset`, 'POST')

export const deleteVersion = (module: string, name: string, id: number) =>
  sendJson<void>(`${path(module, name)}/versions/${id}`, 'DELETE')
