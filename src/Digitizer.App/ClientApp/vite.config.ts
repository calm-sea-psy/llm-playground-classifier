import { cpSync, createReadStream, existsSync, statSync } from 'node:fs'
import { join, normalize, resolve } from 'node:path'
import react from '@vitejs/plugin-react'
import { defineConfig, type Plugin } from 'vite'

// PDF.js 가 필요할 때 읽는 파일: 한글 글꼴 문자표(cmaps) · 내장되지 않은 표준 글꼴 · 스캔 PDF 이미지 해독기(wasm, JBIG2 · JPEG2000)
// ➔ 빌드 결과의 pdfjs/ 로 복사, 개발 서버에서는 node_modules 에서 바로
const pdfjsRoot = resolve('node_modules/pdfjs-dist')
const pdfjsDirs = ['cmaps', 'standard_fonts', 'wasm']

function pdfjsAssets(): Plugin {
  let outDir = 'dist'
  return {
    name: 'pdfjs-assets',
    configResolved(config) {
      outDir = resolve(config.root, config.build.outDir)
    },
    configureServer(server) {
      server.middlewares.use('/pdfjs', (req, res, next) => {
        const file = normalize(join(pdfjsRoot, decodeURIComponent((req.url ?? '').split('?')[0])))
        if (!file.startsWith(pdfjsRoot) || !existsSync(file) || !statSync(file).isFile()) return next()
        createReadStream(file).pipe(res)
      })
    },
    writeBundle() {
      for (const dir of pdfjsDirs) cpSync(join(pdfjsRoot, dir), join(outDir, 'pdfjs', dir), { recursive: true })
    },
  }
}

// 빌드 결과(dist)는 App 빌드가 출력 폴더 wwwroot 로 복사 (Digitizer.App.csproj BuildClientApp). 개발 서버는 실행 중인 App(5310)으로 프록시
export default defineConfig({
  plugins: [react(), pdfjsAssets()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    host: '127.0.0.1',
    port: 5311,
    strictPort: true,
    proxy: { '/api': 'http://127.0.0.1:5310' },
  },
})
