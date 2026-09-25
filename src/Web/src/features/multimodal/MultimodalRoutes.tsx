import { Route, Routes } from 'react-router-dom'
import { NotFound } from '../../shared/components/NotFound'
import { MultimodalHomePage } from './pages/MultimodalHomePage'
import { MultimodalJobPage } from './pages/MultimodalJobPage'

/** Step 3 화면: 업로드·최근 작업(/multimodal) ➔ 종합 보고서(/multimodal/jobs/:jobId) */
export function MultimodalRoutes() {
  return (
    <Routes>
      <Route index element={<MultimodalHomePage />} />
      <Route path="jobs/:jobId" element={<MultimodalJobPage />} />
      <Route path="*" element={<NotFound />} />
    </Routes>
  )
}
