import type { ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { ServiceStatus } from './ServiceStatus'

export interface NavItem {
  path: string
  title: string
}

/** 노출된 Step 이 둘 이상일 때만 메뉴를 보여줌 (하나면 그 Step 이 곧 홈) */
export function Layout({ nav, children }: { nav: NavItem[]; children: ReactNode }) {
  return (
    <div className="app">
      <header className="app-header">
        <NavLink to="/" className="brand">
          <img src="/favicon.svg" alt="" width={22} height={22} />
          LLM 도입 평가 도구
        </NavLink>
        {nav.length > 1 && (
          <nav>
            {nav.map((item) => (
              <NavLink key={item.path} to={item.path}>
                {item.title}
              </NavLink>
            ))}
          </nav>
        )}
        <ServiceStatus />
      </header>
      <main className="app-main">{children}</main>
    </div>
  )
}
