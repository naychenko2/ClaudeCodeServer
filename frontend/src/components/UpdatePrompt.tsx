import { useRegisterSW } from 'virtual:pwa-register/react'
import { C, FONT, R, SHADOW } from '../lib/design'

// Как часто спрашивать сервер о новой версии SW (браузер сам делает это только при навигации)
const UPDATE_CHECK_INTERVAL_MS = 60_000

export function UpdatePrompt() {
  const {
    needRefresh: [needRefresh],
    updateServiceWorker,
  } = useRegisterSW({
    onRegisteredSW(_swUrl, registration) {
      if (!registration) return
      const check = () => {
        if (registration.installing || !navigator.onLine) return
        registration.update().catch(() => {})
      }
      setInterval(check, UPDATE_CHECK_INTERVAL_MS)
      document.addEventListener('visibilitychange', () => {
        if (!document.hidden) check()
      })
    },
  })

  if (!needRefresh) return null

  return (
    <div style={{
      position: 'fixed',
      bottom: 24,
      left: '50%',
      transform: 'translateX(-50%)',
      zIndex: 9999,
      display: 'flex',
      alignItems: 'center',
      gap: 12,
      background: C.bgCard,
      border: `1px solid ${C.border}`,
      borderRadius: R.xl,
      padding: '10px 16px',
      boxShadow: SHADOW.dropdown,
      fontFamily: FONT.sans,
      fontSize: 14,
      color: C.textPrimary,
      whiteSpace: 'nowrap',
    }}>
      <span>Доступно обновление приложения</span>
      <button
        onClick={() => updateServiceWorker(true)}
        style={{
          background: C.accent,
          color: C.onAccent,
          border: 'none',
          borderRadius: R.lg,
          padding: '5px 14px',
          fontSize: 13,
          fontWeight: 600,
          cursor: 'pointer',
          fontFamily: 'inherit',
        }}
      >
        Обновить
      </button>
    </div>
  )
}
