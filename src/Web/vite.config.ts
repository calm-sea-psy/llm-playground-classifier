import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const api = 'http://127.0.0.1:5000'

// 개발 서버는 API 로 프록시 ➔ 같은 출처라 CORS 설정 불필요 (SignalR 웹소켓 포함)
export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': api,
      '/health': api,
      '/hubs': { target: api, ws: true },
      // 모니터링 서버 (src/Monitor) 상태 ➔ 헤더 위젯
      '/monitor': { target: 'http://127.0.0.1:5100', rewrite: (path) => path.replace(/^\/monitor/, '') },
    },
  },
})
