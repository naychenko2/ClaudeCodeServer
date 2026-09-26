// Рамка обрезки поверх картинки холста: тянется за середину и за углы, вне рамки —
// затемнение. Координаты — доли от картинки, как у операции crop на сервере.

import { useRef, type PointerEvent as RPointerEvent } from 'react';
import { C, R } from 'aihome_shell/kit';
import type { ImageFractionRect } from './api';
import { moveCrop, resizeCrop, type CropCorner, type CropRatio, type Dims } from './transforms';

const CORNERS: CropCorner[] = ['nw', 'ne', 'sw', 'se'];
const HANDLE = 22;

const pct = (v: number) => `${v * 100}%`;

export function CropOverlay({ rect, ratio, size, onChange }: {
  rect: ImageFractionRect;
  ratio: CropRatio;
  size: Dims;
  onChange: (r: ImageFractionRect) => void;
}) {
  const box = useRef<HTMLDivElement>(null);
  const drag = useRef<{ corner: CropCorner | null; x: number; y: number; start: ImageFractionRect } | null>(null);

  // Точка указателя в долях картинки: рамка картинки уже учитывает масштаб и сдвиг холста
  const frac = (e: { clientX: number; clientY: number }) => {
    const b = box.current?.getBoundingClientRect();
    return b && b.width && b.height ? { x: (e.clientX - b.left) / b.width, y: (e.clientY - b.top) / b.height } : null;
  };

  // Угол — из data-crop-handle; без него тянут рамку целиком
  const down = (e: RPointerEvent<HTMLDivElement>) => {
    e.stopPropagation();
    const corner = (e.currentTarget.dataset.cropHandle as CropCorner | undefined) ?? null;
    e.preventDefault();
    const p = frac(e);
    if (!p) return;
    drag.current = { corner, x: p.x, y: p.y, start: rect };
    e.currentTarget.setPointerCapture(e.pointerId);
  };

  const move = (e: RPointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    const p = d && frac(e);
    if (!d || !p) return;
    onChange(d.corner
      ? resizeCrop(d.start, d.corner, p.x, p.y, ratio, size)
      : moveCrop(d.start, p.x - d.x, p.y - d.y));
  };

  const up = () => { drag.current = null; };

  return (
    <div ref={box} data-crop="true" style={{ position: 'absolute', inset: 0 }}
      onPointerDown={e => e.stopPropagation()}>
      <div onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={up}
        style={{
          position: 'absolute', left: pct(rect.x), top: pct(rect.y), width: pct(rect.width), height: pct(rect.height),
          // Затемнение вне рамки — тень рамки на всю сцену
          boxShadow: `0 0 0 9999px ${C.overlay}`, outline: `2px solid ${C.bgWhite}`, cursor: 'move', touchAction: 'none',
          backgroundImage: `linear-gradient(${C.glass}, ${C.glass}), linear-gradient(${C.glass}, ${C.glass}), linear-gradient(${C.glass}, ${C.glass}), linear-gradient(${C.glass}, ${C.glass})`,
          backgroundSize: '1px 100%, 1px 100%, 100% 1px, 100% 1px',
          backgroundPosition: '33.33% 0, 66.66% 0, 0 33.33%, 0 66.66%',
          backgroundRepeat: 'no-repeat',
        }}>
        {CORNERS.map(c => (
          <div key={c} data-crop-handle={c} onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={up}
            style={{
              position: 'absolute', width: HANDLE, height: HANDLE, borderRadius: R.sm, touchAction: 'none',
              background: C.bgWhite, border: `2px solid ${C.accent}`, boxSizing: 'border-box',
              ...(c.includes('n') ? { top: -HANDLE / 2 } : { bottom: -HANDLE / 2 }),
              ...(c.includes('w') ? { left: -HANDLE / 2 } : { right: -HANDLE / 2 }),
              cursor: c === 'nw' || c === 'se' ? 'nwse-resize' : 'nesw-resize',
            }} />
        ))}
      </div>
    </div>
  );
}
