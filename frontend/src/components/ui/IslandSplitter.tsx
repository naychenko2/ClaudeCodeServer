import { useState } from 'react';
import type { PointerEvent as ReactPointerEvent } from 'react';
import { C, ISLAND } from '../../lib/design';

// Ресайз-сплиттер между островами: в покое — прозрачный зазор (воздух холста),
// на hover/drag проявляется accent-grip с точками. Hit-зона — весь зазор плюс
// невидимые ±4px по бокам (как ±6px у старого 1px-Splitter).
// API совместим со Splitter: onMouseDown — Pointer Events (mouse + touch + pen),
// touchAction:none — тач тянет, а не скроллит.
export function IslandSplitter({ orientation = 'v', active, onMouseDown }: {
  orientation?: 'v' | 'h';
  active: boolean;
  onMouseDown: (e: ReactPointerEvent) => void;
}) {
  const vertical = orientation === 'v';
  const [hover, setHover] = useState(false);
  const gripVisible = active || hover;
  return (
    <div
      onPointerDown={onMouseDown}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        position: 'relative', flexShrink: 0, cursor: vertical ? 'col-resize' : 'row-resize',
        touchAction: 'none', background: 'transparent',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        ...(vertical
          ? { flex: `0 0 ${ISLAND.gap}px`, width: ISLAND.gap, alignSelf: 'stretch' }
          : { height: ISLAND.gap, width: '100%' }),
      }}
    >
      <div style={{
        position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)',
        borderRadius: 3, background: C.accent, opacity: gripVisible ? 1 : 0,
        transition: 'opacity 0.15s ease', pointerEvents: 'none',
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 3,
        ...(vertical ? { width: 4, height: 34, flexDirection: 'column' } : { width: 34, height: 4, flexDirection: 'row' }),
      }}>
        {[0, 1, 2].map(i => <span key={i} style={{ width: 2, height: 2, borderRadius: '50%', background: C.onAccent }} />)}
      </div>
      <div style={vertical
        ? { position: 'absolute', top: 0, bottom: 0, left: -4, right: -4, cursor: 'col-resize' }
        : { position: 'absolute', left: 0, right: 0, top: -4, bottom: -4, cursor: 'row-resize' }} />
    </div>
  );
}
