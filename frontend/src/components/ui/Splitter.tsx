import type { PointerEvent as ReactPointerEvent } from 'react';
import { C } from '../../lib/design';

// Единый ресайз-сплиттер для всех областей: в покое — тонкая 1px-линия (как граница
// панели), на hover/drag — accent-линия с точечным grip; широкая невидимая hit-зона ±6px.
export function Splitter({ orientation = 'v', active, onMouseDown }: {
  orientation?: 'v' | 'h';
  active: boolean;
  // Pointer Events (mouse + touch + pen). Имя onMouseDown сохранено для совместимости
  // с потребителями; внутри вешается onPointerDown + touchAction:none (тач не скроллит, а тянет).
  onMouseDown: (e: ReactPointerEvent) => void;
}) {
  const vertical = orientation === 'v';
  return (
    <div
      onPointerDown={onMouseDown}
      style={{
        position: 'relative', flexShrink: 0, cursor: vertical ? 'col-resize' : 'row-resize',
        touchAction: 'none',
        background: active ? C.accent : C.border, transition: 'background 0.15s ease',
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        ...(vertical ? { flex: '0 0 1px', width: 1, alignSelf: 'stretch' } : { height: 1, width: '100%' }),
      }}
      onMouseEnter={e => { if (!active) (e.currentTarget.firstElementChild as HTMLElement).style.opacity = '1'; }}
      onMouseLeave={e => { if (!active) (e.currentTarget.firstElementChild as HTMLElement).style.opacity = '0'; }}
    >
      <div style={{
        position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)',
        borderRadius: 3, background: C.accent, opacity: active ? 1 : 0,
        transition: 'opacity 0.15s ease', pointerEvents: 'none',
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 3,
        ...(vertical ? { width: 4, height: 34, flexDirection: 'column' } : { width: 34, height: 4, flexDirection: 'row' }),
      }}>
        {[0, 1, 2].map(i => <span key={i} style={{ width: 2, height: 2, borderRadius: '50%', background: C.onAccent }} />)}
      </div>
      <div style={vertical
        ? { position: 'absolute', top: 0, bottom: 0, left: -6, right: -6, cursor: 'col-resize' }
        : { position: 'absolute', left: 0, right: 0, top: -6, bottom: -6, cursor: 'row-resize' }} />
    </div>
  );
}
