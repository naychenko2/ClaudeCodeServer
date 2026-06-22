import { useOnline } from '../hooks/useOnline';

// Плавающая плашка-индикатор офлайн-режима по центру сверху.
// Не сдвигает layout (position: fixed) — поэтому не ломает height:100vh страниц.
export function OfflineBanner() {
  const online = useOnline();
  if (online) return null;

  return (
    <div
      style={{
        position: 'fixed',
        top: 10,
        left: '50%',
        transform: 'translateX(-50%)',
        zIndex: 1000,
        display: 'flex',
        alignItems: 'center',
        gap: 8,
        padding: '7px 14px',
        borderRadius: 999,
        background: '#FBEBE0',
        border: '1px solid #E8C4AE',
        boxShadow: '0 6px 20px rgba(23,19,15,0.16)',
        fontFamily: "'Hanken Grotesk', sans-serif",
        fontSize: 13,
        fontWeight: 600,
        color: '#9A4B2C',
        pointerEvents: 'none',
        userSelect: 'none',
      }}
    >
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d="M1 1l22 22" />
        <path d="M16.72 11.06A10.94 10.94 0 0 1 19 12.55" />
        <path d="M5 12.55a10.94 10.94 0 0 1 5.17-2.39" />
        <path d="M10.71 5.05A16 16 0 0 1 22.58 9" />
        <path d="M1.42 9a15.91 15.91 0 0 1 4.7-2.88" />
        <path d="M8.53 16.11a6 6 0 0 1 6.95 0" />
        <line x1="12" y1="20" x2="12.01" y2="20" />
      </svg>
      Офлайн — показаны сохранённые данные
    </div>
  );
}
