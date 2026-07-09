import type { CSSProperties, ReactNode, MouseEvent, PointerEvent as ReactPointerEvent } from 'react';
import { useRef, useState, useCallback, useLayoutEffect, useEffect } from 'react';
import { C, TB, SHADOW } from '../lib/design';
import { IconButton } from './ui/IconButton';

// Компактные текстовые кнопки тулбара (выравниваются по 32px-линии icon-кнопок)
export const tbBtnPrimary: CSSProperties = {
  border: 'none', background: C.accent, color: C.onAccent,
  borderRadius: 8, padding: '0 14px', height: 32, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', fontFamily: 'inherit', display: 'flex', alignItems: 'center', flexShrink: 0,
};
export const tbBtnGhost: CSSProperties = {
  background: 'none', border: `1px solid ${C.border}`, color: C.textSecondary,
  borderRadius: 8, padding: '0 12px', height: 32, fontSize: 13, fontWeight: 600,
  cursor: 'pointer', fontFamily: 'inherit', display: 'flex', alignItems: 'center', flexShrink: 0,
};

// === Контейнер тулбара: единая высота, фон, бордер ===
export function Toolbar({ isMobile, noBorder, bg, children, style }: {
  isMobile?: boolean;
  noBorder?: boolean;
  bg?: string;
  children: ReactNode;
  style?: CSSProperties;
}) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: TB.gap,
      height: isMobile ? TB.heightMobile : TB.heightDesktop,
      padding: `0 ${isMobile ? TB.padXMobile : TB.padX}px`,
      background: bg ?? TB.bg,
      borderBottom: noBorder ? 'none' : TB.borderBottom,
      boxSizing: 'border-box', flexShrink: 0,
      ...style,
    }}>
      {children}
    </div>
  );
}

// === Icon-кнопка тулбара — тонкая обёртка над общим ui/IconButton ===
// Сохранена для обратной совместимости API (isMobile → размер тач-таргета).
export function ToolbarIconButton({ onClick, title, isMobile, color, disabled, active, children }: {
  onClick?: (e: MouseEvent) => void;
  title?: string;
  isMobile?: boolean;
  color?: string;
  disabled?: boolean;
  active?: boolean;
  children: ReactNode;
}) {
  return (
    <IconButton
      onClick={onClick} title={title} disabled={disabled} active={active} color={color}
      size={isMobile ? 'lg' : 'md'}
    >
      {children}
    </IconButton>
  );
}

// === Pill / сегмент-переключатель: единый стиль дорожки и активного сегмента ===
// icon (опционально) рисуется слева от подписи — stroke-иконки в общем стиле (currentColor).
// Активный сегмент — отдельная «пилюля» (thumb), которая физически скользит между
// вариантами: позиция и ширина меряются по реальным кнопкам в DOM, поэтому число
// сегментов и ширина лейблов произвольны (3-й раздел заведётся сам).
// При draggable пилюлю можно таскать пальцем/мышью — на отпускании прилипает к
// ближайшему сегменту.
const PILL_EASE = 'cubic-bezier(.32,.72,0,1)';        // «пружинистое» скольжение
const PILL_DRAG_THRESHOLD = 3;                        // px, отделяет тап от перетаскивания
// Сглаживание перетекания формы при drag: у краёв (возле сегмента) — мягкое
// прилипание к его форме, разгон/торможение в середине перехода.
const easeInOutCubic = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);

type PillGeom = { left: number; width: number };
// Память позиции пилюли ВНЕ React, по persistKey. Нужна для переключателей, которые
// перемонтируются при смене экрана (хаб «Чаты|Проекты» живёт в разных страницах):
// новый инстанс стартует с позиции старого и на следующем кадре съезжает к своей —
// так тап между вкладками анимируется, а не перескакивает.
const pillMemory = new Map<string, PillGeom>();

export function PillSwitch<T extends string>({ value, options, onChange, fill, isMobile, draggable, persistKey, compact, variant = 'default' }: {
  value: T;
  options: { value: T; label: string; icon?: ReactNode }[];
  onChange: (v: T) => void;
  fill?: boolean;
  isMobile?: boolean;
  draggable?: boolean;
  persistKey?: string;
  // Компактный режим (узкие экраны): неактивные сегменты — только иконка,
  // подпись видна лишь у активного; пилюля перетекает в новую ширину сама.
  compact?: boolean;
  // 'hub' — навигатор верхнего уровня: своя «чернильная» гамма мимо accent
  // (тёмная плашка активного, кремовый текст), без утопленной дорожки —
  // чтобы отличаться от локальных переключателей внутри панелей.
  variant?: 'default' | 'hub';
}) {
  const hub = variant === 'hub';
  const trackRef = useRef<HTMLDivElement>(null);
  const btnRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const suppressClick = useRef(false);               // гасим клик, если это был drag

  const activeIndex = Math.max(0, options.findIndex(o => o.value === value));
  // Геометрия покоящейся пилюли (по активному сегменту). Стартовое значение — из памяти
  // (позиция на прошлом экране), чтобы новый инстанс доехал до места, а не возник на нём.
  const [thumb, setThumb] = useState<PillGeom | null>(() => (persistKey ? pillMemory.get(persistKey) ?? null : null));
  // Геометрия во время перетаскивания (null — когда не тащим).
  const [drag, setDrag] = useState<PillGeom | null>(null);
  // Подсветка текста: на кого «нацелена» пилюля прямо сейчас (при drag — ближайший).
  const [highlight, setHighlight] = useState(activeIndex);
  const thumbRef = useRef(thumb);
  thumbRef.current = thumb;

  // Ставит пилюлю на активный сегмент (меряя его по DOM). Если пилюля уже где-то стоит
  // и позиция сменилась — оставляем прошлый кадр отрисоваться и на след. кадре съезжаем
  // (rAF), тогда CSS-transition отрабатывает переезд. Работает для любого числа опций.
  const labelsKey = options.map(o => o.label).join('|');
  useLayoutEffect(() => {
    const btn = btnRefs.current[activeIndex];
    if (!btn) return;
    const next: PillGeom = { left: btn.offsetLeft, width: btn.offsetWidth };
    if (persistKey) pillMemory.set(persistKey, next);
    const prev = thumbRef.current;
    if (prev && (prev.left !== next.left || prev.width !== next.width)) {
      const id = requestAnimationFrame(() => setThumb(next));
      return () => cancelAnimationFrame(id);
    }
    setThumb(next);
  }, [activeIndex, labelsKey, isMobile, fill, persistKey, compact]);

  useEffect(() => { setHighlight(activeIndex); }, [activeIndex]);

  // Пересчёт при ресайзе контейнера (адаптив, смена шрифта/масштаба) — мгновенно.
  useEffect(() => {
    const track = trackRef.current;
    if (!track || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => {
      const btn = btnRefs.current[activeIndex];
      if (!btn) return;
      const g: PillGeom = { left: btn.offsetLeft, width: btn.offsetWidth };
      if (persistKey) pillMemory.set(persistKey, g);
      setThumb(g);
    });
    ro.observe(track);
    return () => ro.disconnect();
  }, [activeIndex, persistKey]);

  // Индекс сегмента, чей центр ближе всего к заданной X (для прилипания).
  const nearestIndex = useCallback((centerX: number) => {
    let best = activeIndex, bestDist = Infinity;
    btnRefs.current.forEach((b, i) => {
      if (!b) return;
      const c = b.offsetLeft + b.offsetWidth / 2;
      const d = Math.abs(c - centerX);
      if (d < bestDist) { bestDist = d; best = i; }
    });
    return best;
  }, [activeIndex]);

  const onPointerDown = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (!draggable || !thumb || e.button !== 0) return;
    e.preventDefault();
    // Снимок геометрии всех сегментов на момент старта.
    const rects = (btnRefs.current.filter(Boolean) as HTMLButtonElement[])
      .map(b => ({ left: b.offsetLeft, width: b.offsetWidth, center: b.offsetLeft + b.offsetWidth / 2 }));
    if (!rects.length) return;
    const startX = e.clientX;
    const startCenter = thumb.left + thumb.width / 2;  // центр пилюли следует за пальцем
    const minC = rects[0].center;
    const maxC = rects[rects.length - 1].center;
    let curCenter = startCenter;
    let lastGeom: PillGeom | null = null;             // последняя форма пилюли под пальцем
    let moved = false;

    const onMove = (ev: PointerEvent) => {
      const dx = ev.clientX - startX;
      if (Math.abs(dx) > PILL_DRAG_THRESHOLD) moved = true;
      curCenter = Math.max(minC, Math.min(maxC, startCenter + dx));
      // Между какими соседними сегментами сейчас центр — и насколько (t).
      let i = 0;
      while (i < rects.length - 1 && rects[i + 1].center <= curCenter) i++;
      if (i >= rects.length - 1) {
        const last = rects[rects.length - 1];
        lastGeom = { left: last.left, width: last.width };
        setHighlight(rects.length - 1);
      } else {
        const a = rects[i], b = rects[i + 1];
        const t = (curCenter - a.center) / (b.center - a.center);
        const e = easeInOutCubic(t);
        // Пилюля плавно перетекает из формы a в форму b — и ширина, и позиция (со сглаживанием).
        lastGeom = { left: a.left + (b.left - a.left) * e, width: a.width + (b.width - a.width) * e };
        setHighlight(t < 0.5 ? i : i + 1);
      }
      setDrag(lastGeom);
    };
    const onUp = () => {
      window.removeEventListener('pointermove', onMove);
      window.removeEventListener('pointerup', onUp);
      setDrag(null);
      if (moved) {
        suppressClick.current = true;                 // не дать сработать клику после drag
        setTimeout(() => { suppressClick.current = false; }, 0);
        // Позиция отпускания → в память, чтобы новый инстанс (при смене экрана) доехал
        // с неё до финального сегмента, а не отпрыгнул к старому активному.
        if (persistKey && lastGeom) pillMemory.set(persistKey, lastGeom);
        const idx = nearestIndex(curCenter);
        const opt = options[idx];
        if (opt && opt.value !== value) onChange(opt.value);
        else setHighlight(activeIndex);               // вернулись на тот же — вернуть подсветку
      }
    };
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
  };

  const geom = drag ?? thumb;

  return (
    <div
      ref={trackRef}
      onPointerDown={onPointerDown}
      style={{
        position: 'relative', display: 'flex', gap: 3,
        background: hub ? 'transparent' : TB.pillTrack,
        borderRadius: TB.pillRadius + 1, padding: 3,
        flexShrink: 0, width: fill ? '100%' : undefined, boxSizing: 'border-box',
        touchAction: draggable ? 'none' : undefined,  // не даём странице скроллиться при drag
      }}
    >
      {/* Скользящая пилюля */}
      {geom && (
        <div
          aria-hidden
          style={{
            position: 'absolute', top: 3, bottom: 3,
            left: geom.left, width: geom.width,
            background: hub ? C.navInk : TB.pillThumbBg,
            boxShadow: hub ? SHADOW.card : TB.pillThumbShadow,
            borderRadius: TB.pillRadius - 2,
            transition: drag ? 'none' : `left 0.32s ${PILL_EASE}, width 0.32s ${PILL_EASE}`,
            cursor: draggable ? (drag ? 'grabbing' : 'grab') : undefined,
            zIndex: 0,
          }}
        />
      )}
      {options.map((opt, i) => {
        const active = highlight === i;
        // В compact подпись привязана к value (не к highlight): при drag ширины
        // сегментов не меняются под пальцем — снапшот rects остаётся валидным
        const showLabel = !compact || opt.value === value;
        return (
          <button key={opt.value} ref={el => { btnRefs.current[i] = el; }}
            onClick={() => { if (suppressClick.current) return; onChange(opt.value); }}
            aria-label={opt.label}
            style={{
              position: 'relative', zIndex: 1,
              flex: fill ? 1 : undefined,
              padding: compact ? (showLabel ? '0 12px' : '0 11px') : isMobile ? '8px 12px' : '6px 12px',
              minHeight: compact ? 40 : isMobile ? 40 : 32,
              borderRadius: TB.pillRadius - 2, border: 'none', cursor: 'pointer',
              fontSize: compact ? 12 : 13, fontWeight: 600, fontFamily: 'inherit', whiteSpace: 'nowrap',
              transition: 'color 0.15s',
              background: 'transparent',
              color: active ? (hub ? C.onNavInk : C.textHeading) : C.textSecondary,
              display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 6,
              touchAction: draggable ? 'none' : undefined,
            }}
          >
            {opt.icon}
            {showLabel && opt.label}
          </button>
        );
      })}
    </div>
  );
}
