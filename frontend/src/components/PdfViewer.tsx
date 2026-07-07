import { useEffect, useMemo, useRef, useState } from 'react';
import { Document, Page, pdfjs } from 'react-pdf';
import 'react-pdf/dist/Page/AnnotationLayer.css';
import 'react-pdf/dist/Page/TextLayer.css';
import { base64ToBytes } from '../lib/binary';
import { C, FONT, SHADOW } from '../lib/design';

pdfjs.GlobalWorkerOptions.workerSrc = new URL(
  'pdfjs-dist/build/pdf.worker.min.mjs',
  import.meta.url,
).toString();

const MAX_PAGE_WIDTH = 900;

const Spinner = ({ label }: { label: string }) => (
  <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', flex: 1, gap: 14, padding: 40 }}>
    <div style={{ width: 36, height: 36, borderRadius: '50%', border: `3px solid ${C.border}`, borderTopColor: C.accent, animation: 'spin 0.8s linear infinite' }} />
    <div style={{ fontSize: 13, color: C.textMuted }}>{label}</div>
  </div>
);

export default function PdfViewer({ base64 }: { base64: string }) {
  const file = useMemo(() => ({ data: base64ToBytes(base64) }), [base64]);
  const [numPages, setNumPages] = useState(0);
  const [containerWidth, setContainerWidth] = useState<number>();
  const [scale, setScale] = useState(1);
  const [error, setError] = useState(false);
  const [hover, setHover] = useState<'minus' | 'pct' | 'plus' | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const pinchRef = useRef<{ startDist: number; startScale: number } | null>(null);
  const scaleRef = useRef(scale);

  useEffect(() => { scaleRef.current = scale; }, [scale]);

  // Базовая ширина под контейнер
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const update = () => setContainerWidth(el.clientWidth - 32);
    const ro = new ResizeObserver(update);
    ro.observe(el);
    update();
    return () => ro.disconnect();
  }, []);

  // Pinch-to-zoom — слушаем на scroll-контейнере
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;

    const dist = (t: TouchList) => Math.hypot(
      t[0].clientX - t[1].clientX,
      t[0].clientY - t[1].clientY,
    );

    const onStart = (e: TouchEvent) => {
      if (e.touches.length === 2) {
        e.preventDefault();
        pinchRef.current = { startDist: dist(e.touches), startScale: scaleRef.current };
      }
    };

    const onMove = (e: TouchEvent) => {
      if (e.touches.length !== 2 || !pinchRef.current) return;
      e.preventDefault();
      const ratio = dist(e.touches) / pinchRef.current.startDist;
      setScale(Math.max(0.5, Math.min(4, pinchRef.current.startScale * ratio)));
    };

    const onEnd = () => { pinchRef.current = null; };

    el.addEventListener('touchstart', onStart, { passive: false });
    el.addEventListener('touchmove', onMove, { passive: false });
    el.addEventListener('touchend', onEnd);
    return () => {
      el.removeEventListener('touchstart', onStart);
      el.removeEventListener('touchmove', onMove);
      el.removeEventListener('touchend', onEnd);
    };
  }, []);

  // Ctrl+колесо (десктоп)
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey && !e.metaKey) return;
      e.preventDefault();
      setScale(s => Math.max(0.5, Math.min(4, +(s + (e.deltaY > 0 ? -0.1 : 0.1)).toFixed(2))));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  const baseWidth = containerWidth ? Math.min(containerWidth, MAX_PAGE_WIDTH) : undefined;
  const pageWidth = baseWidth ? Math.round(baseWidth * scale) : undefined;
  const modified = scale !== 1;

  const btnBase: React.CSSProperties = {
    border: 'none', background: 'none', cursor: 'pointer',
    width: 32, height: 32, display: 'flex', alignItems: 'center', justifyContent: 'center',
    borderRadius: 6, padding: 0, flexShrink: 0,
    transition: 'background 120ms ease-out, color 120ms ease-out',
  };

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', position: 'relative' }}>

      {/* Скролл-контейнер страниц — touch-action: pan-y блокирует системный пинч-зум */}
      <div
        ref={scrollRef}
        style={{
          flex: 1,
          overflowY: 'auto',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          padding: '16px 16px 80px',
          touchAction: 'pan-y',
        }}
      >
        {error ? (
          <div style={{ color: C.textMuted, fontSize: 13, padding: 40 }}>Не удалось отобразить PDF</div>
        ) : (
          <Document
            file={file}
            onLoadSuccess={({ numPages }) => setNumPages(numPages)}
            onLoadError={() => setError(true)}
            loading={<Spinner label="Загружаю PDF…" />}
            error={<div style={{ color: C.textMuted, fontSize: 13, padding: 40 }}>Не удалось отобразить PDF</div>}
          >
            {Array.from({ length: numPages }, (_, i) => (
              <div key={i} style={{ margin: '0 0 16px', boxShadow: SHADOW.card, borderRadius: 4, overflow: 'hidden' }}>
                <Page pageNumber={i + 1} width={pageWidth} loading="" />
              </div>
            ))}
          </Document>
        )}
      </div>

      {/* Плавающий контрол масштаба — снизу справа */}
      {numPages > 0 && !error && (
        <div style={{
          position: 'absolute',
          bottom: 20,
          right: 20,
          zIndex: 20,
          display: 'flex',
          alignItems: 'center',
          height: 40,
          background: C.bgCard,
          backdropFilter: 'blur(8px)',
          WebkitBackdropFilter: 'blur(8px)',
          border: `1px solid ${C.border}`,
          borderRadius: 10,
          boxShadow: SHADOW.dropdown,
          padding: '0 6px',
          gap: 2,
        }}>
          {/* Уменьшить */}
          <button
            title="Уменьшить (−25%)"
            onClick={() => setScale(s => Math.max(0.5, +(s - 0.25).toFixed(2)))}
            onMouseEnter={() => setHover('minus')}
            onMouseLeave={() => setHover(null)}
            style={{
              ...btnBase,
              background: hover === 'minus' ? C.bgSelected : 'none',
              color: hover === 'minus' ? C.textPrimary : C.textSecondary,
            }}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
              <line x1="5" y1="12" x2="19" y2="12"/>
            </svg>
          </button>

          {/* Процент — клик сбрасывает в 100% */}
          <button
            title="Сбросить масштаб"
            onClick={() => setScale(1)}
            onMouseEnter={() => setHover('pct')}
            onMouseLeave={() => setHover(null)}
            style={{
              ...btnBase,
              width: 'auto',
              minWidth: 44,
              padding: '0 6px',
              fontSize: 12,
              fontFamily: FONT.mono,
              fontWeight: 600,
              color: modified ? C.accent : hover === 'pct' ? C.textPrimary : C.textSecondary,
              background: hover === 'pct' ? C.bgSelected : 'none',
            }}
          >
            {Math.round(scale * 100)}%
          </button>

          {/* Увеличить */}
          <button
            title="Увеличить (+25%)"
            onClick={() => setScale(s => Math.min(4, +(s + 0.25).toFixed(2)))}
            onMouseEnter={() => setHover('plus')}
            onMouseLeave={() => setHover(null)}
            style={{
              ...btnBase,
              background: hover === 'plus' ? C.bgSelected : 'none',
              color: hover === 'plus' ? C.textPrimary : C.textSecondary,
            }}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
              <line x1="12" y1="5" x2="12" y2="19"/>
              <line x1="5" y1="12" x2="19" y2="12"/>
            </svg>
          </button>
        </div>
      )}
    </div>
  );
}
