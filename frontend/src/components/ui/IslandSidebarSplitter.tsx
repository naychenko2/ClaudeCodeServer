import { useState } from 'react';
import type { PointerEvent as ReactPointerEvent } from 'react';
import { ChevronLeft } from 'lucide-react';
import { C, SHADOW } from '../../lib/design';
import { ICON_SIZE } from './icons';
import { IslandSplitter } from './IslandSplitter';

// Умеет ли устройство hover (десктоп с мышью). На тач (hover: none) кнопку
// сворачивания показываем постоянно — там hover не наступит никогда.
const CAN_HOVER = typeof window !== 'undefined' && !window.matchMedia('(hover: none)').matches;

// Ресайз-зазор левого сайдбара-острова с всплывающей кнопкой «свернуть панель».
// Аналог SidebarSplitter, но поверх прозрачного 8px-зазора IslandSplitter:
// кнопка висит absolute по центру зазора, на десктопе проявляется по наведению.
export function IslandSidebarSplitter({ active, onMouseDown, onCollapse }: {
  active: boolean;
  onMouseDown: (e: ReactPointerEvent) => void;
  onCollapse: () => void;
}) {
  const [hover, setHover] = useState(false);
  const visible = CAN_HOVER ? hover : true;

  return (
    <div
      style={{ position: 'relative', display: 'flex', alignSelf: 'stretch', flexShrink: 0 }}
      onMouseEnter={() => { if (CAN_HOVER) setHover(true); }}
      onMouseLeave={() => { if (CAN_HOVER) setHover(false); }}
    >
      <IslandSplitter active={active} onMouseDown={onMouseDown} />
      <button
        type="button"
        onClick={onCollapse}
        onPointerDown={e => e.stopPropagation()}  // не стартовать drag сплиттера
        title="Свернуть панель"
        aria-label="Свернуть панель"
        style={{
          position: 'absolute', top: 12, left: '50%', transform: 'translateX(-50%)',
          width: 24, height: 24, borderRadius: '50%', padding: 0, zIndex: 11,
          display: 'flex', alignItems: 'center', justifyContent: 'center', cursor: 'pointer',
          background: C.bgWhite, border: `1px solid ${C.border}`, boxShadow: SHADOW.dropdown,
          color: C.textSecondary, opacity: visible ? 1 : 0,
          transition: 'opacity 0.15s ease', pointerEvents: visible ? 'auto' : 'none',
        }}
      >
        <ChevronLeft size={ICON_SIZE.sm} strokeWidth={2} style={{ flexShrink: 0 }} />
      </button>
    </div>
  );
}
