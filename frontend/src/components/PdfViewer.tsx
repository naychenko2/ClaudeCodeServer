import { useEffect, useMemo, useRef, useState } from 'react';
import { Document, Page, pdfjs } from 'react-pdf';
import 'react-pdf/dist/Page/AnnotationLayer.css';
import 'react-pdf/dist/Page/TextLayer.css';
import { base64ToBytes } from '../lib/binary';

// Worker pdf.js — через URL-ассет, понятный сборщику Vite (работает и в production)
pdfjs.GlobalWorkerOptions.workerSrc = new URL(
  'pdfjs-dist/build/pdf.worker.min.mjs',
  import.meta.url,
).toString();

const MAX_PAGE_WIDTH = 900;

const Spinner = ({ label }: { label: string }) => (
  <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', flex: 1, gap: 14, padding: 40 }}>
    <div style={{ width: 36, height: 36, borderRadius: '50%', border: '3px solid #E0D7C8', borderTopColor: '#D97757', animation: 'spin 0.8s linear infinite' }} />
    <div style={{ fontSize: 13, color: '#9A8F7E' }}>{label}</div>
  </div>
);

export default function PdfViewer({ base64 }: { base64: string }) {
  // Стабильная ссылка на данные — иначе react-pdf перезагружает документ на каждый рендер
  const file = useMemo(() => ({ data: base64ToBytes(base64) }), [base64]);
  const [numPages, setNumPages] = useState(0);
  const [width, setWidth] = useState<number>();
  const [error, setError] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);

  // Подгоняем ширину страниц под контейнер
  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const update = () => setWidth(el.clientWidth - 32);
    const ro = new ResizeObserver(update);
    ro.observe(el);
    update();
    return () => ro.disconnect();
  }, []);

  const pageWidth = width ? Math.min(width, MAX_PAGE_WIDTH) : undefined;

  return (
    <div ref={wrapRef} style={{ width: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
      {error ? (
        <div style={{ color: '#8A8070', fontSize: 13, padding: 40 }}>Не удалось отобразить PDF</div>
      ) : (
        <Document
          file={file}
          onLoadSuccess={({ numPages }) => setNumPages(numPages)}
          onLoadError={() => setError(true)}
          loading={<Spinner label="Загружаю PDF…" />}
          error={<div style={{ color: '#8A8070', fontSize: 13, padding: 40 }}>Не удалось отобразить PDF</div>}
        >
          {Array.from({ length: numPages }, (_, i) => (
            <div key={i} style={{ margin: '0 0 16px', boxShadow: '0 2px 12px rgba(42,37,31,0.12)', borderRadius: 4, overflow: 'hidden' }}>
              <Page pageNumber={i + 1} width={pageWidth} loading="" />
            </div>
          ))}
        </Document>
      )}
    </div>
  );
}
