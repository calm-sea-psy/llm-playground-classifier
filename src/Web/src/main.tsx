import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import App from './App.tsx'
import { FeatureProvider } from './shared/features/FeatureProvider.tsx'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <FeatureProvider>
        <App />
      </FeatureProvider>
    </BrowserRouter>
  </StrictMode>,
)
