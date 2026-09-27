import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// 빌드 결과(dist)는 App 빌드가 출력 폴더 wwwroot 로 복사 (Digitizer.App.csproj BuildClientApp). 개발 서버는 실행 중인 App(5310)으로 프록시
export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    host: '127.0.0.1',
    port: 5311,
    strictPort: true,
    proxy: { '/api': 'http://127.0.0.1:5310' },
  },
})
