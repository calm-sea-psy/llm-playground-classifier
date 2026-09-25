import { Navigate, Route, Routes } from 'react-router-dom'
import { PromptsRoutes } from './features/prompts/PromptsPage'
import { featureModules } from './features/registry'
import { Layout } from './shared/components/Layout'
import { NotFound } from './shared/components/NotFound'
import { useFeatures } from './shared/features/featureContext'

/**
 * 서버 Features 설정에서 ShowInUi 인 Step 만 메뉴·라우트로 만든다.
 * 숨긴 Step 의 주소는 라우트 자체가 없어 404 (백엔드도 404 라 이중 차단)
 */
export default function App() {
  const { loading, error, features } = useFeatures()

  if (loading) return <div className="boot muted">불러오는 중…</div>
  if (error) {
    return (
      <div className="boot">
        <p className="text-bad">API 에 연결할 수 없습니다: {error}</p>
        <p className="muted small">src/Api 가 http://127.0.0.1:5000 에서 실행 중인지 확인하세요.</p>
      </div>
    )
  }

  const visible = featureModules.filter((m) => features.some((f) => f.key === m.key && f.showInUi))
  // 프롬프트 관리는 Step 이 아니라 도구 ➔ 켜진 Step 이 있으면 항상 메뉴 끝에
  const nav = [...visible.map((m) => ({ path: `/${m.key}`, title: m.title })), { path: '/prompts', title: '프롬프트' }]

  return (
    <Layout nav={nav}>
      {visible.length === 0 ? (
        <section className="card empty">
          <h2>사용할 수 있는 기능이 없습니다</h2>
          <p className="muted">API 설정(Features)에서 화면에 노출할 기능을 켜 주세요.</p>
        </section>
      ) : (
        <Routes>
          {/* 노출된 Step 이 하나면 홈 = 그 Step, 여럿이면 첫 번째 Step */}
          <Route index element={<Navigate to={`/${visible[0].key}`} replace />} />
          {visible.map((m) => (
            <Route key={m.key} path={`/${m.key}/*`} element={m.element} />
          ))}
          <Route path="/prompts/*" element={<PromptsRoutes />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
      )}
    </Layout>
  )
}
