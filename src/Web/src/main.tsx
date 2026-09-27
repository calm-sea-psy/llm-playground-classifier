import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import App from './App.tsx'
import { BusyProvider } from './shared/busy/BusyProvider.tsx'
import { FeatureProvider } from './shared/features/FeatureProvider.tsx'
import './index.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <FeatureProvider>
        <BusyProvider>
          <App />
        </BusyProvider>
      </FeatureProvider>
    </BrowserRouter>
  </StrictMode>,
)
