import { Suspense, lazy } from 'react';
import { C } from '../lib/design';

const PdfViewer = lazy(() => import('./PdfViewer'));

const Fallback = () => (
  <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 14, padding: 40 }}>
    <div style={{ width: 36, height: 36, borderRadius: '50%', border: `3px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.8s linear infinite' }} />
    <div style={{ fontSize: 13, color: C.textMuted }}>Готовлю просмотр…</div>
  </div>
);

export function DocumentViewer({ base64 }: { base64: string }) {
  return (
    <Suspense fallback={<Fallback />}>
      <PdfViewer base64={base64} />
    </Suspense>
  );
}
