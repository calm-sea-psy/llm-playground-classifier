/** API 오류. ASP.NET Core ProblemDetails 의 detail 을 메시지로 사용 */
export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

async function toError(response: Response): Promise<ApiError> {
  let message = `${response.status} ${response.statusText}`
  try {
    const body = await response.json()
    message = body.detail ?? body.title ?? message
  } catch {
    // 본문이 JSON 이 아님 (프록시 오류 등)
  }
  return new ApiError(response.status, message)
}

export async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal })
  if (!response.ok) throw await toError(response)
  return response.json() as Promise<T>
}

export async function postForm<T>(url: string, form: FormData): Promise<T> {
  const response = await fetch(url, { method: 'POST', body: form })
  if (!response.ok) throw await toError(response)
  return response.json() as Promise<T>
}

/** JSON 본문 요청 (PUT/POST/DELETE). 실패하면 ProblemDetails.detail 을 메시지로 */
export async function sendJson<T>(url: string, method: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? `${response.status} ${response.statusText}`)
  }
  return response.status === 204 ? (undefined as T) : response.json()
}
