import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './lib/theme.css'
import './index.css'
import './lib/themeMode'
import App from './App.tsx'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
