import { useEffect, useRef, useState } from 'react';
import { renderAsync } from 'docx-preview';
import { base64ToBytes } from '../lib/binary';

export default function DocxViewer({ base64 }: { base64: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(false);
    const container = ref.current;
    if (!container) return;
    container.innerHTML = '';
    renderAsync(base64ToBytes(base64), container, undefined, {
      className: 'docx',
      inWrapper: true,
      breakPages: true,
      ignoreLastRenderedPageBreak: true,
      experimental: true,
    })
      .then(() => { if (!cancelled) setLoading(false); })
      .catch(() => { if (!cancelled) { setError(true); setLoading(false); } });
    return () => { cancelled = true; };
  }, [base64]);

  return (
    <div style={{ width: '100%', position: 'relative' }}>
      {loading && (
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 14, padding: 40 }}>
          <div style={{ width: 36, height: 36, borderRadius: '50%', border: '3px solid #E0D7C8', borderTopColor: '#D97757', animation: 'spin 0.8s linear infinite' }} />
          <div style={{ fontSize: 13, color: '#9A8F7E' }}>Загружаю документ…</div>
        </div>
      )}
      {error && (
        <div style={{ color: '#8A8070', fontSize: 13, padding: 40, textAlign: 'center' }}>Не удалось отобразить документ</div>
      )}
      {/* docx-preview сам рисует «листы» со своими стилями внутри контейнера */}
      <div ref={ref} style={{ display: error ? 'none' : 'block' }} />
    </div>
  );
}
