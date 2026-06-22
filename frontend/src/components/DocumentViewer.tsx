import { Suspense, lazy } from 'react';

// Тяжёлые библиотеки (pdf.js / docx-preview / SheetJS) — отдельными чанками,
// грузятся только при открытии соответствующего документа
const PdfViewer = lazy(() => import('./PdfViewer'));
const DocxViewer = lazy(() => import('./DocxViewer'));
const XlsxViewer = lazy(() => import('./XlsxViewer'));

export type DocKind = 'pdf' | 'docx' | 'xlsx';

const Fallback = () => (
  <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 14, padding: 40 }}>
    <div style={{ width: 36, height: 36, borderRadius: '50%', border: '3px solid #E0D7C8', borderTopColor: '#D97757', animation: 'spin 0.8s linear infinite' }} />
    <div style={{ fontSize: 13, color: '#9A8F7E' }}>Готовлю просмотр…</div>
  </div>
);

export function DocumentViewer({ docKind, base64 }: { docKind: DocKind; base64: string }) {
  return (
    <Suspense fallback={<Fallback />}>
      {docKind === 'pdf' && <PdfViewer base64={base64} />}
      {docKind === 'docx' && <DocxViewer base64={base64} />}
      {docKind === 'xlsx' && <XlsxViewer base64={base64} />}
    </Suspense>
  );
}
