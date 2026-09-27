// 주소는 #/경로?조건 (서버 설정 없이 새로 고침 · 즐겨찾기 가능)
export type Route = { path: string; query: URLSearchParams }

export function parseRoute(): Route {
  const hash = window.location.hash.replace(/^#/, '') || '/'
  const [path, search] = hash.split('?')
  return { path, query: new URLSearchParams(search ?? '') }
}

export function go(path: string) {
  window.location.hash = path
}
