import { useState, useRef, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { C, FONT } from '../lib/design';

// Диаграмма Mermaid: клиентский рендер в SVG. Библиотека грузится лениво (тяжёлая),
// только когда реально встретился блок ```mermaid или открыт .mmd-файл.
let mermaidInited = false;
let mermaidSeq = 0;
// Кэш отрендеренного SVG по исходному коду: при ремоунте компонента (на мобиле скролл
// прячет адресную строку → resize → ремоунт поддерева) диаграмма берётся синхронно
// из кэша, без вспышки код-фолбэка. Рендер mermaid детерминирован по коду + фикс. теме.
const mermaidSvgCache = new Map<string, string>();
async function loadMermaid() {
  const mermaid = (await import('mermaid')).default;
  if (!mermaidInited) {
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict', // санитизация SVG, без исполнения click-биндингов
      theme: 'base',
      fontFamily: FONT.sans,
      themeVariables: {
        background: 'transparent',
        primaryColor: '#EDE7DA',
        primaryBorderColor: C.border,
        primaryTextColor: C.textPrimary,
        lineColor: C.accent,
        secondaryColor: '#F4F0E8',
        tertiaryColor: '#F4F0E8',
        fontSize: '13px',
        // xychart-beta: дефолт темы base — plotColorPalette #FFF4DD (бледно-жёлтый),
        // не виден на светлом фоне. Задаём читаемую палитру и цвета осей под тему.
        xyChart: {
          backgroundColor: 'transparent',
          titleColor: C.textHeading,
          xAxisLabelColor: C.textPrimary,
          xAxisTitleColor: C.textPrimary,
          xAxisTickColor: C.border,
          xAxisLineColor: C.border,
          yAxisLabelColor: C.textPrimary,
          yAxisTitleColor: C.textPrimary,
          yAxisTickColor: C.border,
          yAxisLineColor: C.border,
          plotColorPalette: '#D97757, #5B8C6E, #4A7BA8, #B5843B, #9A5B3B',
        },
      },
    });
    mermaidInited = true;
  }
  return mermaid;
}

// Полноэкранный просмотр диаграммы: зум колесом/кнопками + панорамирование мышью,
// на тач-устройствах — пинч-зум двумя пальцами и перетаскивание одним.
function MermaidLightbox({ svg, onClose }: { svg: string; onClose: () => void }) {
  // Единое transform-состояние: s — масштаб, x/y — сдвиг (transform-origin = центр области).
  const [tf, setTf] = useState({ s: 1, x: 0, y: 0 });
  const [gesturing, setGesturing] = useState(false); // во время жеста выключаем transition (без лага)
  const areaRef = useRef<HTMLDivElement | null>(null);
  // Панорамирование одним указателем (мышь/палец)
  const drag = useRef<{ x: number; y: number; tx: number; ty: number } | null>(null);
  // Пинч: запоминаем стартовые масштаб/сдвиг и фокус (середину пальцев) относительно центра области
  const pinch = useRef<{ dist: number; s0: number; x0: number; y0: number; fx0: number; fy0: number } | null>(null);

  const clamp = (s: number) => Math.min(8, Math.max(0.2, s));
  const reset = () => setTf({ s: 1, x: 0, y: 0 });

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden'; // не скроллим страницу под лайтбоксом
    return () => { window.removeEventListener('keydown', onKey); document.body.style.overflow = prevOverflow; };
  }, [onClose]);

  // Центр области зума в экранных координатах (transform-origin по умолчанию — центр элемента).
  const center = () => {
    const r = areaRef.current?.getBoundingClientRect();
    return r ? { cx: r.left + r.width / 2, cy: r.top + r.height / 2 }
             : { cx: window.innerWidth / 2, cy: window.innerHeight / 2 };
  };
  const dist2 = (t: React.TouchList) => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
  const mid2 = (t: React.TouchList) => ({ x: (t[0].clientX + t[1].clientX) / 2, y: (t[0].clientY + t[1].clientY) / 2 });

  // Зум вокруг фокальной точки (fx,fy в экранных координатах): точка под фокусом остаётся на месте.
  const zoomAround = (nextRaw: number, fx: number, fy: number) => setTf(p => {
    const { cx, cy } = center();
    const ns = clamp(nextRaw), ratio = ns / p.s;
    const gx = fx - cx, gy = fy - cy;
    return { s: ns, x: gx - ratio * (gx - p.x), y: gy - ratio * (gy - p.y) };
  });
  const zoomCenter = (factor: number) => { const { cx, cy } = center(); zoomAround(tf.s * factor, cx, cy); };

  const btn: React.CSSProperties = {
    width: 34, height: 34, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    cursor: 'pointer', background: 'rgba(255,255,255,0.92)', border: `1px solid ${C.border}`,
    borderRadius: 8, fontFamily: FONT.sans, fontSize: 16, color: C.textPrimary, lineHeight: 1,
  };

  return (
    <div
      onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}
      style={{
        position: 'fixed', inset: 0, zIndex: 9999, background: 'rgba(22,17,12,0.96)',
        display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden',
      }}
    >
      {/* Панель управления */}
      <div style={{ position: 'absolute', top: 12, right: 12, display: 'flex', gap: 8, zIndex: 2 }}>
        <button type="button" title="Уменьшить" style={btn} onClick={() => zoomCenter(0.8)}>−</button>
        <button type="button" title="Сбросить" style={{ ...btn, fontSize: 13 }} onClick={reset}>1:1</button>
        <button type="button" title="Увеличить" style={btn} onClick={() => zoomCenter(1.25)}>+</button>
        <button type="button" title="Закрыть (Esc)" style={btn} onClick={onClose}>✕</button>
      </div>
      {/* Область с диаграммой: зум/пан */}
      <div
        ref={areaRef}
        onWheel={(e) => zoomAround(tf.s * (e.deltaY < 0 ? 1.12 : 0.89), e.clientX, e.clientY)}
        onPointerDown={(e) => { if (e.pointerType === 'mouse') { drag.current = { x: e.clientX, y: e.clientY, tx: tf.x, ty: tf.y }; (e.currentTarget as Element).setPointerCapture?.(e.pointerId); } }}
        onPointerMove={(e) => { if (e.pointerType === 'mouse' && drag.current) { const d = drag.current; setTf(p => ({ ...p, x: d.tx + (e.clientX - d.x), y: d.ty + (e.clientY - d.y) })); } }}
        onPointerUp={() => { drag.current = null; }}
        onPointerCancel={() => { drag.current = null; }}
        onTouchStart={(e) => {
          setGesturing(true);
          if (e.touches.length === 2) {
            const { cx, cy } = center(); const m = mid2(e.touches);
            pinch.current = { dist: dist2(e.touches), s0: tf.s, x0: tf.x, y0: tf.y, fx0: m.x - cx, fy0: m.y - cy };
            drag.current = null;
          } else if (e.touches.length === 1) {
            drag.current = { x: e.touches[0].clientX, y: e.touches[0].clientY, tx: tf.x, ty: tf.y };
          }
        }}
        onTouchMove={(e) => {
          if (e.touches.length === 2 && pinch.current) {
            // Пинч + одновременный пан: содержимое под серединой пальцев следует за ней и масштабируется
            const p0 = pinch.current, { cx, cy } = center(), m = mid2(e.touches);
            const s1 = clamp(p0.s0 * dist2(e.touches) / p0.dist), ratio = s1 / p0.s0;
            const f1x = m.x - cx, f1y = m.y - cy;
            setTf({ s: s1, x: f1x - ratio * (p0.fx0 - p0.x0), y: f1y - ratio * (p0.fy0 - p0.y0) });
          } else if (e.touches.length === 1 && drag.current) {
            const d = drag.current, tX = e.touches[0].clientX, tY = e.touches[0].clientY;
            setTf(p => ({ ...p, x: d.tx + (tX - d.x), y: d.ty + (tY - d.y) }));
          }
        }}
        onTouchEnd={(e) => { if (e.touches.length === 0) { drag.current = null; pinch.current = null; setGesturing(false); } else if (e.touches.length === 1) { pinch.current = null; drag.current = { x: e.touches[0].clientX, y: e.touches[0].clientY, tx: tf.x, ty: tf.y }; } }}
        style={{ touchAction: 'none', cursor: 'grab', width: '100%', height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}
      >
        <div
          style={{
            transform: `translate(${tf.x}px, ${tf.y}px) scale(${tf.s})`,
            transition: (gesturing || drag.current) ? 'none' : 'transform 0.08s',
            maxWidth: '92vw', maxHeight: '92vh',
            // Светлая подложка-лист: SVG прозрачный, иначе диаграмма висит на чёрном фоне
            background: '#F4F0E8', padding: 20, borderRadius: 10, boxShadow: '0 8px 40px rgba(0,0,0,0.45)',
          }}
          dangerouslySetInnerHTML={{ __html: svg }}
        />
      </div>
    </div>
  );
}

// Мягкая правка распространённых грабель синтаксиса. Применяется ТОЛЬКО как запасной
// вариант, если исходник не распарсился — валидные диаграммы не трогаем.
// quadrantChart: круглые скобки в title/осях/квадрантах ломают парсер, а структурного
// смысла в этом типе не несут (координаты — в []), поэтому убираем их.
function sanitizeMermaid(code: string): string {
  const first = code.replace(/^\s+/, '').split('\n', 1)[0].trim();
  if (first.startsWith('quadrantChart')) return code.replace(/[()]/g, '');
  return code;
}

// Рендер блока mermaid: SVG + тумблер «диаграмма ⇄ код» + разворот на весь экран с зумом.
// Используется и в чате (```mermaid), и в файловом менеджере (.mmd).
export function MermaidDiagram({ code }: { code: string }) {
  // Ленивая инициализация из кэша — если диаграмма уже рендерилась, показываем её
  // сразу первым кадром (без вспышки кода при ремоунте).
  const [svg, setSvg] = useState<string | null>(() => mermaidSvgCache.get(code) ?? null);
  const [failed, setFailed] = useState(false);
  const [view, setView] = useState<'diagram' | 'code'>('diagram');
  const [zoom, setZoom] = useState(false);

  useEffect(() => {
    // Уже в кэше — перерисовывать нечего, берём готовый SVG (не мигаем при ремоунте).
    const cached = mermaidSvgCache.get(code);
    if (cached) { setSvg(cached); setFailed(false); return; }
    let cancelled = false;
    setFailed(false);
    // Дебаунс: во время стриминга код досылается по частям и невалиден —
    // не дёргаем рендер на каждую дельту.
    const t = setTimeout(async () => {
      try {
        const mermaid = await loadMermaid();
        if (cancelled) return;
        // Пробуем как есть; если не парсится — пробуем «подчищенный» вариант (запасной путь).
        let src = code;
        let ok = !!(await mermaid.parse(code, { suppressErrors: true }));
        if (cancelled) return;
        if (!ok) {
          const cleaned = sanitizeMermaid(code);
          if (cleaned !== code && !!(await mermaid.parse(cleaned, { suppressErrors: true }))) { ok = true; src = cleaned; }
          if (cancelled) return;
        }
        if (!ok) { setFailed(true); return; }
        const { svg: rendered } = await mermaid.render(`mermaid-svg-${++mermaidSeq}`, src);
        // Кэшируем по ОРИГИНАЛЬНОМУ коду (ключ = проп code).
        if (!cancelled) { mermaidSvgCache.set(code, rendered); setSvg(rendered); setFailed(false); }
      } catch {
        if (!cancelled) setFailed(true);
      }
    }, 120);
    return () => { cancelled = true; clearTimeout(t); };
  }, [code]);

  const codeBlock = (
    <pre style={{ background: C.outputBg, border: `1px solid ${C.outputBorder}`, borderRadius: 8, padding: '10px 14px', margin: 0, overflowX: 'auto' }}>
      <code style={{ fontFamily: FONT.mono, fontSize: 12.5, color: C.textPrimary, lineHeight: 1.5 }}>{code}</code>
    </pre>
  );

  // Не распарсилось (failed ставится только после дебаунса — во время стриминга код
  // меняется чаще, таймер сбрасывается, плашка не мигает). Показываем плашку + исходник.
  if (failed) {
    return (
      <div style={{ margin: '6px 0' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4, fontFamily: FONT.sans, fontSize: 12, color: '#9A5B3B' }}>
          ⚠ Не удалось построить диаграмму — показан исходный код
        </div>
        {codeBlock}
      </div>
    );
  }
  // Ещё рендерится (стриминг/загрузка mermaid) — тихо показываем код как заглушку, без плашки.
  if (!svg) {
    return <div style={{ margin: '6px 0' }}>{codeBlock}</div>;
  }

  // Диаграмма готова — даём тумблер «диаграмма ⇄ код» и разворот на весь экран.
  const showCode = view === 'code';
  const toggleBtn: React.CSSProperties = {
    display: 'inline-flex', alignItems: 'center', gap: 5, cursor: 'pointer',
    background: 'transparent', border: `1px solid ${C.border}`, borderRadius: 6,
    padding: '2px 8px', fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, lineHeight: 1.6,
  };
  return (
    <div style={{ margin: '8px 0' }}>
      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 6, marginBottom: 4 }}>
        {!showCode && (
          <button type="button" onClick={() => setZoom(true)} title="Открыть на весь экран" style={toggleBtn}>
            ⤢ Развернуть
          </button>
        )}
        <button
          type="button"
          onClick={() => setView(showCode ? 'diagram' : 'code')}
          title={showCode ? 'Показать диаграмму' : 'Показать исходный код'}
          style={toggleBtn}
        >
          {showCode ? '◇ Диаграмма' : '</> Код'}
        </button>
      </div>
      {showCode
        ? codeBlock
        : (
          <div className="cc-mermaid" onClick={() => setZoom(true)}
            title="Нажмите, чтобы открыть на весь экран"
            style={{ overflowX: 'auto', textAlign: 'center', cursor: 'zoom-in' }}
            dangerouslySetInnerHTML={{ __html: svg }} />
        )}
      {zoom && createPortal(<MermaidLightbox svg={svg} onClose={() => setZoom(false)} />, document.body)}
    </div>
  );
}
