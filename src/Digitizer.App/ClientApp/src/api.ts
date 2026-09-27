// App API (/api). 쓰기 요청에는 X-Digitizer 헤더를 붙임 (다른 사이트가 몰래 보내는 요청을 서버가 거부하는 기준, LocalOnly)

export type FieldDef = {
  name: string
  label: string
  type: string
  required?: boolean
  items?: FieldDef[] | null
  description?: string | null
}

export type Pack = {
  id: string
  version: string
  displayName: string
  description?: string | null
  release: { status: 'passed' | 'conditional'; report: string; conditions?: string[] | null } | null
  fields: FieldDef[]
  forbidden: string[]
  measuredModels: string[]
}

export type Issue = { rule: string; field: string | null; severity: 'Error' | 'Warning'; message: string }

export type DocumentStatus = 'Queued' | 'Processing' | 'Processed' | 'NeedsReview' | 'Failed' | 'Duplicate' | 'Approved' | 'Rejected'

export type DocumentSummary = {
  id: number
  packId: string
  packVersion: string
  originalName: string
  status: DocumentStatus
  statusReason: string | null
  typeWarning: boolean
  source: string
  receivedAt: string
  processedAt: string | null
  reviewedAt: string | null
  purgedAt: string | null
  exported: boolean
  errorCount: number | null
  warningCount: number | null
  fallbackUsed: boolean | null
}

export type Fields = Record<string, unknown>

export type DocumentDetail = {
  document: DocumentSummary
  reviewNote: string | null
  reviewedAt: string | null
  reviewable: boolean
  exportable: boolean
  hasFile: boolean
  fileKind: 'image' | 'pdf' | 'docx' | 'other'
  fields: Fields | null
  extractedFields: Fields | null
  issues: Issue[]
  sourceText: string | null
  correctedPaths: string[]
  extraction: {
    engine: string
    model: string
    sourceKind: string
    pages: number
    ocrConfidence: number | null
    finalSource: string | null
    fallbackUsed: boolean
    fallbackReason: string | null
    textMs: number
    llmMs: number
  } | null
}

export type Settings = {
  documentsRoot: string
  retentionDays: number
  model: string
  [key: string]: unknown
}

export type CheckItem = { name: string; level: 'ok' | 'warn' | 'bad'; detail: string; fix: string | null }

export class ApiError extends Error {
  status: number
  issues: Issue[] | null
  constructor(status: number, message: string, issues: Issue[] | null) {
    super(message)
    this.status = status
    this.issues = issues
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {}
  let payload: BodyInit | undefined
  if (method !== 'GET') headers['X-Digitizer'] = '1'
  if (body instanceof FormData) payload = body
  else if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }
  const res = await fetch(url, { method, headers, body: payload })
  const text = await res.text()
  const data = text ? JSON.parse(text) : null
  if (!res.ok) throw new ApiError(res.status, data?.error ?? `요청 실패 (${res.status})`, data?.issues ?? null)
  return data as T
}

export const api = {
  packs: () => request<Pack[]>('GET', '/api/packs'),
  documents: (query: Record<string, string>) =>
    request<DocumentSummary[]>('GET', `/api/documents?${new URLSearchParams(Object.entries(query).filter(([, v]) => v !== ''))}`),
  counts: () => request<{ packId: string; status: DocumentStatus; count: number }[]>('GET', '/api/documents/counts'),
  document: (id: number) => request<DocumentDetail>('GET', `/api/documents/${id}`),
  check: (id: number, fields: Fields) => request<{ issues: Issue[] }>('POST', `/api/documents/${id}/check`, fields),
  review: (id: number, body: { fields: Fields; action: 'Save' | 'Approve' | 'Reject'; acknowledgeIssues?: boolean; note?: string }) =>
    request<{ status: DocumentStatus; statusReason: string | null; issues: Issue[] }>('POST', `/api/documents/${id}/review`, body),
  upload: (form: FormData) =>
    request<{ name: string; id: number | null; status: DocumentStatus; statusReason: string | null }[]>('POST', '/api/upload', form),
  pendingExports: () => request<{ packId: string; displayName: string; notExported: number; exported: number }[]>('GET', '/api/exports/pending'),
  exports: () =>
    request<{ id: number; packId: string; fileName: string; documentCount: number; createdAt: string; exists: boolean }[]>('GET', '/api/exports'),
  exportPack: (packId: string, includeExported: boolean) =>
    request<{ id: number; fileName: string; documentCount: number }>('POST', '/api/exports', { packId, includeExported }),
  corrections: () =>
    request<{
      packId: string
      displayName: string
      approved: number
      rejected: number
      documentsCorrected: number
      slots: number
      corrected: number
      rate: number | null
      topFields: { field: string; count: number }[]
    }[]>('GET', '/api/stats/corrections'),
  settings: () =>
    request<{ autoStart: boolean; settings: Settings; resolvedDocumentsRoot: string; availableModels: string[] | null; settingsPath: string }>('GET', '/api/settings'),
  saveSettings: (body: { documentsRoot?: string; retentionDays?: number; model?: string; autoStart?: boolean }) =>
    request<{ autoStart: boolean; settings: Settings; resolvedDocumentsRoot: string }>('PUT', '/api/settings', body),
  queue: () => request<{ paused: boolean; queued: number; processing: number }>('GET', '/api/queue'),
  setPaused: (paused: boolean) => request<{ paused: boolean }>('POST', paused ? '/api/queue/pause' : '/api/queue/resume'),
  status: () => request<{ checks: CheckItem[] }>('GET', '/api/status'),
}

export const statusLabel: Record<DocumentStatus, string> = {
  Queued: '대기',
  Processing: '처리 중',
  Processed: '검증 통과',
  NeedsReview: '확인 필요',
  Failed: '실패',
  Duplicate: '중복',
  Approved: '승인',
  Rejected: '반려',
}

export const statusTone: Record<DocumentStatus, 'ok' | 'warn' | 'bad' | 'muted' | 'accent'> = {
  Queued: 'muted',
  Processing: 'accent',
  Processed: 'ok',
  NeedsReview: 'warn',
  Failed: 'bad',
  Duplicate: 'muted',
  Approved: 'ok',
  Rejected: 'bad',
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}
