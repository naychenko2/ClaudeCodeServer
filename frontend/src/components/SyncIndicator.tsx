import { useSyncExternalStore } from 'react';
import { getSyncProgress, subscribeSyncProgress } from '../lib/sync';

// Ненавязчивый индикатор прогресса офлайн-синхронизации (правый нижний угол).
export function SyncIndicator() {
  const progress = useSyncExternalStore(subscribeSyncProgress, getSyncProgress, getSyncProgress);
  if (!progress.active) return null;

  const label = progress.total > 0
    ? `Синхронизация ${progress.done}/${progress.total}`
    : 'Синхронизация…';

  return (
    <div
      style={{
        position: 'fixed',
        bottom: 14,
        right: 14,
        zIndex: 1000,
        display: 'flex',
        alignItems: 'center',
        gap: 8,
        padding: '7px 13px',
        borderRadius: 999,
        background: '#FFFFFF',
        border: '1px solid #E0D7C8',
        boxShadow: '0 6px 20px rgba(23,19,15,0.14)',
        fontFamily: "'Hanken Grotesk', sans-serif",
        fontSize: 12.5,
        fontWeight: 600,
        color: '#756B5E',
        pointerEvents: 'none',
        userSelect: 'none',
      }}
    >
      <div style={{ width: 13, height: 13, borderRadius: '50%', border: '2px solid #E0D7C8', borderTopColor: '#D97757', animation: 'spin 0.8s linear infinite' }} />
      {label}
    </div>
  );
}
