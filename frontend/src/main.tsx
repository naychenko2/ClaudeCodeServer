import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './lib/theme.css'
import './index.css'
import './lib/themeMode'
import App from './App.tsx'
import { ErrorBoundary } from './components/ErrorBoundary'
import { purgeLegacyComposerKeys } from './lib/chatContext/legacyKeys'

// Остатки выбора и свёрнутости прежних полос над полем ввода (ADR-023) устройству больше не нужны
try { purgeLegacyComposerKeys(localStorage) } catch { /* приватный режим */ }

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <App />
    </ErrorBoundary>
  </StrictMode>,
)
