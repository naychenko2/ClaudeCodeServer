import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import type { ReactNode, PointerEvent as ReactPointerEvent, WheelEvent as ReactWheelEvent } from 'react';
import { X } from 'lucide-react';
import { C, SP, Z } from '../../lib/design';
import { getPopupDepth } from '../../lib/popupEscape';
import { IconButton } from './IconButton';
import { ICON_SIZE, ICON_STROKE } from './icons';

// Пределы масштаба и порог «палец сдвинулся» (px): короче — это ещё тап
const MIN_SCALE = 1;
const MAX_SCALE = 5;
const DOUBLE_TAP_SCALE = 2.5;
const TAP_SLOP = 10;
const DOUBLE_TAP_MS = 300;

type Pt = { x: number; y: number };
type View = { s: number; x: number; y: number };
const IDENTITY: View = { s: 1, x: 0, y: 0 };

const clampScale = (s: number) => Math.min(MAX_SCALE, Math.max(MIN_SCALE, s));
const dist = (a: Pt, b: Pt) => Math.hypot(a.x - b.x, a.y - b.y);
const mid = (a: Pt, b: Pt): Pt => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });

// Полноэкранный просмотр картинки: чёрная подложка, кадр по центру, крестик в углу.
// Закрытие — по Escape, по крестику и по ЧИСТОМУ тапу мимо кадра: одно касание без сдвига.
// Щипок, перетаскивание и любой жест с двумя пальцами окно не закрывают — иначе браузер
// досылает click в конце жеста и превью схлопывается под пальцами.
// Масштаб: щипок, двойной тап, колесо мыши; увеличенный кадр двигается пальцем или мышью.
// children — ряд действий под кадром (скачать, сохранить в проект); жесты на нём не ловим.
export function ImageLightbox({ src, alt = '', onClose, children }: {
  src: string;
  alt?: string;
  onClose: () => void;
  children?: ReactNode;
}) {
  const overlayRef = useRef<HTMLDivElement>(null);
  const imgRef = useRef<HTMLImageElement>(null);
  const [view, setView] = useState<View>(IDENTITY);
  // Плавный переход — только для скачков (двойной тап, сброс), не для движения за пальцем
  const [animate, setAnimate] = useState(false);
  const viewRef = useRef(view);
  // Вид пишем сразу и в ref: обработчики жеста читают его раньше, чем дойдёт рендер
  const applyView = (v: View) => { viewRef.current = v; setView(v); };

  // Жест: активные указатели и снимок на начало текущей фазы (одним пальцем / двумя)
  const pointers = useRef(new Map<number, Pt>());
  const gesture = useRef<{
    onImage: boolean; maxPointers: number; moved: boolean; downAt: Pt;
    start: View; startPt: Pt; startDist: number; center: Pt;
  } | null>(null);
  const lastTap = useRef<{ t: number; p: Pt } | null>(null);

  useEffect(() => {
    // Тот же протокол Escape, что у Modal: обработанный — помечаем, открытый попап — его очередь
    const handler = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      if (getPopupDepth() > 0) return;
      e.preventDefault();
      onClose();
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  useEffect(() => {
    // touch-action: none iOS Safari соблюдает не до конца: щипок всё равно зумит страницу.
    // Гасим его вручную — нужны непассивные слушатели, React вешает touchmove пассивным.
    const el = overlayRef.current;
    if (!el) return;
    const prevent = (e: Event) => e.preventDefault();
    el.addEventListener('touchmove', prevent, { passive: false });
    el.addEventListener('gesturestart', prevent);
    return () => {
      el.removeEventListener('touchmove', prevent);
      el.removeEventListener('gesturestart', prevent);
    };
  }, []);

  // Центр кадра без учёта сдвига: масштаб идёт от центра и его не двигает
  const imageCenter = (v: View): Pt => {
    const r = imgRef.current?.getBoundingClientRect();
    if (!r) return { x: 0, y: 0 };
    return { x: r.left + r.width / 2 - v.x, y: r.top + r.height / 2 - v.y };
  };

  // Масштаб s так, чтобы точка экрана at осталась под тем же местом картинки
  const zoomAt = (from: View, center: Pt, fromPt: Pt, at: Pt, s: number): View => {
    const qx = (fromPt.x - center.x - from.x) / from.s;
    const qy = (fromPt.y - center.y - from.y) / from.s;
    return { s, x: at.x - center.x - s * qx, y: at.y - center.y - s * qy };
  };

  // Новая фаза жеста: снимок вида и положения пальцев на текущий момент
  const rebase = () => {
    const g = gesture.current;
    if (!g) return;
    const pts = [...pointers.current.values()];
    g.start = viewRef.current;
    g.center = imageCenter(viewRef.current);
    g.startPt = pts.length >= 2 ? mid(pts[0], pts[1]) : pts[0];
    g.startDist = pts.length >= 2 ? dist(pts[0], pts[1]) : 0;
  };

  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    const p = { x: e.clientX, y: e.clientY };
    if (pointers.current.size === 0) {
      gesture.current = {
        onImage: e.target === imgRef.current, maxPointers: 0, moved: false, downAt: p,
        start: viewRef.current, startPt: p, startDist: 0, center: { x: 0, y: 0 },
      };
    }
    if (!gesture.current) return;
    pointers.current.set(e.pointerId, p);
    gesture.current.maxPointers = Math.max(gesture.current.maxPointers, pointers.current.size);
    e.currentTarget.setPointerCapture(e.pointerId);
    setAnimate(false);
    rebase();
  };

  const onPointerMove = (e: ReactPointerEvent<HTMLDivElement>) => {
    const g = gesture.current;
    if (!g || !pointers.current.has(e.pointerId)) return;
    const p = { x: e.clientX, y: e.clientY };
    pointers.current.set(e.pointerId, p);
    if (dist(p, g.downAt) > TAP_SLOP) g.moved = true;

    const pts = [...pointers.current.values()];
    if (pts.length >= 2) {
      const m = mid(pts[0], pts[1]);
      const s = clampScale(g.start.s * dist(pts[0], pts[1]) / (g.startDist || 1));
      applyView(zoomAt(g.start, g.center, g.startPt, m, s));
    } else if (g.start.s > 1) {
      // Одним пальцем двигаем только увеличенный кадр
      applyView({ ...g.start, x: g.start.x + p.x - g.startPt.x, y: g.start.y + p.y - g.startPt.y });
    }
  };

  const onPointerEnd = (e: ReactPointerEvent<HTMLDivElement>) => {
    const g = gesture.current;
    if (!g || !pointers.current.delete(e.pointerId)) return;
    if (pointers.current.size > 0) { rebase(); return; }
    gesture.current = null;

    const tap = e.type === 'pointerup' && g.maxPointers === 1 && !g.moved;
    if (!tap) {
      // Отпустили почти без увеличения — возвращаем кадр на место
      if (viewRef.current.s <= 1.01) { setAnimate(true); applyView(IDENTITY); }
      return;
    }
    if (!g.onImage) { onClose(); return; }

    // Двойной тап по кадру: увеличить в точке касания или вернуть как было
    const now = Date.now();
    const prev = lastTap.current;
    if (prev && now - prev.t < DOUBLE_TAP_MS && dist(prev.p, g.downAt) < TAP_SLOP * 3) {
      lastTap.current = null;
      setAnimate(true);
      const v = viewRef.current;
      applyView(v.s > 1 ? IDENTITY : zoomAt(v, imageCenter(v), g.downAt, g.downAt, DOUBLE_TAP_SCALE));
    } else {
      lastTap.current = { t: now, p: g.downAt };
    }
  };

  const onWheel = (e: ReactWheelEvent<HTMLDivElement>) => {
    if (e.target !== imgRef.current && viewRef.current.s === 1) return;
    const v = viewRef.current;
    const s = clampScale(v.s * Math.exp(-e.deltaY * 0.002));
    const at = { x: e.clientX, y: e.clientY };
    setAnimate(false);
    applyView(s <= 1 ? IDENTITY : zoomAt(v, imageCenter(v), at, at, s));
  };

  // Кнопки и ряд действий живут обычными кликами: жест с них не начинается
  const stop = (e: ReactPointerEvent) => e.stopPropagation();

  return createPortal(
    <div
      ref={overlayRef}
      className="cc-overlay"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerEnd}
      onPointerCancel={onPointerEnd}
      onWheel={onWheel}
      style={{
        position: 'fixed', inset: 0, zIndex: Z.modal, background: C.mediaBackdrop,
        display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
        padding: SP.lg, touchAction: 'none', overflow: 'hidden', overscrollBehavior: 'contain',
      }}
    >
      <img
        ref={imgRef}
        src={src}
        alt={alt}
        draggable={false}
        style={{
          maxWidth: '92vw', maxHeight: children ? '76vh' : '88vh', objectFit: 'contain', borderRadius: 8, display: 'block',
          transform: `translate(${view.x}px, ${view.y}px) scale(${view.s})`,
          transition: animate ? 'transform 0.2s ease-out' : 'none',
          cursor: view.s > 1 ? 'grab' : 'zoom-in', userSelect: 'none', WebkitUserSelect: 'none',
        }}
      />
      {children && (
        <div onPointerDown={stop} style={{ marginTop: SP.lg, position: 'relative', zIndex: 1 }}>
          {children}
        </div>
      )}
      <div style={{ position: 'absolute', top: `calc(${SP.lg}px + env(safe-area-inset-top))`, right: SP.lg, zIndex: 1 }}
        onPointerDown={stop}>
        <IconButton size="lg" variant="soft" ariaLabel="Закрыть" onClick={onClose}>
          <X size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
    </div>,
    document.body,
  );
}
