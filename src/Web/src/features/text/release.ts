import { getJson, sendJson } from '../../shared/api/http'
import type { PackRelease } from './api'

/** 배포 화면 (/api/text/release): 설치판(exe)에 들어갈 문서 종류 · 점검 · 설치 파일 만들기 · 릴리스 노트 */
export interface ReleasePack {
  id: string
  version: string
  displayName: string
  release: PackRelease | null
  included: boolean
  problems: string[]
  promptOverrides: { name: string; version: number }[]
}

export interface ReleaseCheck {
  name: string
  level: 'ok' | 'warn' | 'bad'
  detail: string
  fix: string | null
}

export interface ParityRow {
  pack: string
  input: string
  count: number
  sameText: number
  /** -1 = 추출 기준이 없어 대조하지 않음 (원문 · 시간만) */
  sameFields: number
  sameErrors: number
  sameFinal: number | null
  medianSeconds: number
}

export interface BuildSnapshot {
  state: 'idle' | 'running' | 'succeeded' | 'failed'
  startedAt: string | null
  finishedAt: string | null
  tag: string | null
  next: number
  lines: string[]
}

export interface ReleaseOverview {
  version: string
  tag: string
  packs: ReleasePack[]
  checks: ReleaseCheck[]
  parity: { name: string; at: string; rows: ParityRow[] }[]
  setup: { name: string; bytes: number; at: string; sha256: string | null } | null
  releaseNotes: string | null
  tagCommands: string[]
  build: BuildSnapshot
}

export const getRelease = () => getJson<ReleaseOverview>('/api/text/release')
export const startBuild = () => sendJson<BuildSnapshot>('/api/text/release/build', 'POST')
export const getBuildLog = (from: number) => getJson<BuildSnapshot>(`/api/text/release/build?from=${from}`)
export const releaseFileUrl = (name: string) => `/api/text/release/files/${encodeURIComponent(name)}`
