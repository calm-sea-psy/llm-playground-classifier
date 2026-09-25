import { NavLink, Route, Routes, useLocation } from 'react-router-dom'
import { NotFound } from '../../shared/components/NotFound'
import { ImageComparePage } from './pages/ImageComparePage'
import { ImageExperimentPage } from './pages/ImageExperimentPage'
import { ImageHomePage } from './pages/ImageHomePage'
import { ImageJobPage } from './pages/ImageJobPage'

/**
 * Step 2 화면: 모델 비교(첫 메뉴, /image) ➔ 판독(/image/process). Text 와 같은 순서.
 * 와일드카드 라우트 안의 상대 경로는 현재 URL 기준이라 링크는 절대 경로로 쓴다
 */
export function ImageRoutes() {
  const { pathname } = useLocation()
  const inCompare = pathname === '/image' || pathname === '/image/' || pathname.startsWith('/image/experiments')
  const inProcess = pathname.startsWith('/image/process') || pathname.startsWith('/image/jobs')

  return (
    <>
      <nav className="subnav">
        <NavLink to="/image" end className={inCompare ? 'active' : undefined}>
          모델 비교
        </NavLink>
        <NavLink to="/image/process" className={inProcess ? 'active' : undefined}>
          판독
        </NavLink>
      </nav>
      <Routes>
        <Route index element={<ImageComparePage />} />
        <Route path="experiments/:experimentId" element={<ImageExperimentPage />} />
        <Route path="process" element={<ImageHomePage />} />
        <Route path="jobs/:jobId" element={<ImageJobPage />} />
        <Route path="*" element={<NotFound />} />
      </Routes>
    </>
  )
}
