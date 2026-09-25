import { getJson } from './http'
import type { JobDto } from './types'

export const getJob = (jobId: string, signal?: AbortSignal) => getJson<JobDto>(`/api/jobs/${jobId}`, signal)

export const listJobs = (type: string, take = 20, signal?: AbortSignal) =>
  getJson<JobDto[]>(`/api/jobs?type=${encodeURIComponent(type)}&take=${take}`, signal)

/** 업로드한 원본 파일 (결과 화면 이미지) */
export const jobFileUrl = (jobId: string) => `/api/jobs/${jobId}/file`
