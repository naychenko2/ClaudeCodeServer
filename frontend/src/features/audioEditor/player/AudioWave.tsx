import { useMemo, useRef, type KeyboardEvent, type PointerEvent } from 'react';
import { C, R, SP } from 'aihome_shell/kit';
import { resamplePeaks } from './peaks';
import { fmtTime, keySelection, resolveEnd, selectionFromDrag, type AudioSelection } from './selection';

// Порог, отделяющий клик-перемотку от протяжки-выделения, px
const DRAG_PX = 3;
const SEEK_STEP = 1;
const SEEK_STEP_BIG = 5;

export interface AudioWaveProps {
  /** Пики 0…1; пусто — ровная заглушка (ещё считаем) */
  peaks: number[];
  /** Длина версии, с */
  duration: number;
  /** Позиция воспроизведения, с; курсор рисуется, когда > 0 или играет */
  position?: number;
  showCursor?: boolean;
  onSeek?: (sec: number) => void;
  /** Выделение куска — контролируемое. Без onSelectionChange выделять нельзя */
  selection?: AudioSelection | null;
  onSelectionChange?: (sel: AudioSelection | null) => void;
  size?: 'md' | 'sm' | 'lg';
  bars?: number;
  dim?: boolean;
  ariaLabel?: string;
}

const FLAT = 0.08;

// Высоты волны — из шкалы отступов: строка стема и крупный плеер
// lg — волна редактора на весь экран
export const WAVE_H = { sm: SP.xl, md: SP.xxl + SP.sm, lg: SP.xxxl * 4 } as const;

export function AudioWave({
  peaks, duration, position = 0, showCursor, onSeek, selection = null, onSelectionChange,
  size = 'md', bars, dim, ariaLabel = 'Волна',
}: AudioWaveProps) {
  const ref = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; t: number; moved: boolean } | null>(null);
  const n = bars ?? (size === 'sm' ? 50 : size === 'lg' ? 160 : 90);
  const h = WAVE_H[size];
  const cols = useMemo(() => {
    const r = resamplePeaks(peaks, n);
    return r.length ? r.map(v => Math.max(FLAT, v)) : new Array<number>(n).fill(FLAT);
  }, [peaks, n]);
  const len = duration > 0 ? duration : 0;
  const played = len ? Math.min(1, Math.max(0, position / len)) : 0;
  const selectable = !!onSelectionChange && len > 0;

  const timeAt = (clientX: number) => {
    const box = ref.current?.getBoundingClientRect();
    if (!box || !box.width) return 0;
    return Math.min(len, Math.max(0, ((clientX - box.left) / box.width) * len));
  };

  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    if (!len || e.button !== 0) return;
    e.currentTarget.setPointerCapture?.(e.pointerId);
    drag.current = { x: e.clientX, t: timeAt(e.clientX), moved: false };
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d || !selectable) return;
    if (!d.moved && Math.abs(e.clientX - d.x) < DRAG_PX) return;
    d.moved = true;
    onSelectionChange!(selectionFromDrag(d.t, timeAt(e.clientX), len));
  };
  const onPointerUp = (e: PointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    drag.current = null;
    if (d && !d.moved) onSeek?.(timeAt(e.clientX));
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (!len) return;
    if (selectable) {
      const next = keySelection(selection, e, position, len);
      if (next !== undefined) {
        e.preventDefault();
        onSelectionChange!(next);
        return;
      }
    }
    if (!onSeek) return;
    const step = e.shiftKey ? SEEK_STEP_BIG : SEEK_STEP;
    let t: number | null = null;
    if (e.key === 'ArrowLeft') t = position - step;
    else if (e.key === 'ArrowRight') t = position + step;
    else if (e.key === 'Home') t = 0;
    else if (e.key === 'End') t = len;
    if (t === null) return;
    e.preventDefault();
    onSeek(Math.min(len, Math.max(0, t)));
  };

  const selA = selection && len ? selection.start / len : 0;
  const selB = selection && len ? resolveEnd(selection, len) / len : 0;
  const w = 100 / n;

  return (
    <div
      ref={ref}
      // Кольцо фокуса с клавиатуры — общее с IconButton (класс инжектирует он)
      className="cc-iconbtn"
      role="slider"
      tabIndex={len ? 0 : -1}
      aria-label={ariaLabel}
      aria-valuemin={0}
      aria-valuemax={Math.round(len * 10) / 10}
      aria-valuenow={Math.round(position * 10) / 10}
      aria-valuetext={len ? `${fmtTime(position)} из ${fmtTime(len)}` : 'длина ещё неизвестна'}
      title={selectable ? 'Протяните, чтобы выделить кусок; клик — перемотать' : 'Клик — перемотать'}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={() => { drag.current = null; }}
      onKeyDown={onKeyDown}
      style={{
        // Высота жёсткая, svg — абсолютно: в колонке flex: 1 и height: 100% растягивали волну по вертикали
        position: 'relative', flex: 1, minWidth: 0, height: h, minHeight: h, maxHeight: h,
        cursor: len ? (selectable ? 'crosshair' : 'pointer') : 'default',
        userSelect: 'none', touchAction: 'none', borderRadius: R.sm,
        opacity: dim ? 0.35 : 1,
      }}
    >
      <svg viewBox="0 0 100 100" preserveAspectRatio="none" style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', display: 'block' }}>
        {cols.map((v, i) => (
          <rect
            key={i}
            x={i * w + w * 0.18}
            y={50 - v * 46}
            width={w * 0.64}
            height={v * 92}
            rx={0.6}
            fill={(i + 0.5) / n <= played ? C.accent : C.track}
          />
        ))}
      </svg>
      {selection && selB > selA && (
        <div data-wave-selection style={{
          position: 'absolute', top: -2, bottom: -2, left: `${selA * 100}%`, width: `${(selB - selA) * 100}%`,
          background: `color-mix(in srgb, ${C.accent} 16%, transparent)`,
          borderLeft: `2px solid ${C.accent}`, borderRight: `2px solid ${C.accent}`,
          pointerEvents: 'none', boxSizing: 'border-box',
        }} />
      )}
      {(showCursor || position > 0) && len > 0 && (
        <div style={{
          position: 'absolute', top: -3, bottom: -3, width: 2, left: `calc(${played * 100}% - 1px)`,
          background: C.textHeading, pointerEvents: 'none',
        }} />
      )}
    </div>
  );
}
