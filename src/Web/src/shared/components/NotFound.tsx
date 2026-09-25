import { Link } from 'react-router-dom'

export function NotFound() {
  return (
    <section className="card empty">
      <h2>페이지를 찾을 수 없습니다</h2>
      <p className="muted">주소가 잘못되었거나 이 서비스에서 사용하지 않는 기능입니다.</p>
      <Link to="/">처음으로</Link>
    </section>
  )
}
