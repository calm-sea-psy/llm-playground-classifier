import { NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { NotFound } from '../../shared/components/NotFound'
import { ComparePage } from './pages/ComparePage'
import { ExperimentPage } from './pages/ExperimentPage'
import { PacksRoutes } from './pages/PacksPage'
import { TextHomePage } from './pages/TextHomePage'
import { TextJobPage } from './pages/TextJobPage'

/**
 * Step 1 화면: 모델 비교(첫 메뉴, /text) ➔ 문서 처리(/text/process) ➔ 문서 종류 팩 관리(/text/packs).
 * 와일드카드 라우트(/text/*) 안의 상대 경로 "."는 현재 URL 기준이라 링크는 절대 경로로 쓴다
 */
export function TextRoutes() {
  const { pathname } = useLocation()
  const inCompare = pathname === '/text' || pathname === '/text/' || pathname.startsWith('/text/experiments')
  const inProcess = pathname.startsWith('/text/process') || pathname.startsWith('/text/jobs')
  const inPacks = pathname.startsWith('/text/packs')

  return (
    <>
      <nav className="subnav">
        <NavLink to="/text" end className={inCompare ? 'active' : undefined}>
          모델 비교
        </NavLink>
        <NavLink to="/text/process" className={inProcess ? 'active' : undefined}>
          문서 처리
        </NavLink>
        <NavLink to="/text/packs" className={inPacks ? 'active' : undefined}>
          문서 종류
        </NavLink>
      </nav>
      <Routes>
        <Route index element={<ComparePage />} />
        <Route path="experiments/:experimentId" element={<ExperimentPage />} />
        <Route path="process" element={<TextHomePage />} />
        <Route path="jobs/:jobId" element={<TextJobPage />} />
        <Route path="packs/*" element={<PacksRoutes />} />
        <Route path="*" element={<NotFound />} />
      </Routes>
    </>
  )
}
