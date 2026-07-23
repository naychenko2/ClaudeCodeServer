import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './lib/theme.css'
import './index.css'
import './lib/themeMode'
import App from './App.tsx'
import { ErrorBoundary } from './components/ErrorBoundary'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <App />
    </ErrorBoundary>
  </StrictMode>,
)
