// Холст редактора: картинка с SVG-слоем пометок, масштаб (кнопки и колесо) и
// перемещение инструментом «рука». Пометки хранятся в пикселях исходника.

import { useEffect, useRef, useState, type PointerEvent as RPointerEvent } from 'react';
import { Minus, Plus, Scan } from 'lucide-react';
import { IconButton } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FS, R, SHADOW, SP } from '../../lib/design';
import { MarksLayer, strokeScale, type Mark, type Tool } from './marks';

const TOOL_HINT: Record<Tool, string> = {
  hand: 'Тяните картинку, колесо — масштаб',
  mask: 'Закрасьте место, которое нужно изменить',
  arrow: 'Проведите стрелку к нужному месту',
  rect: 'Обведите рамкой область',
  text: 'Нажмите, чтобы добавить подпись',
  eraser: 'Нажмите на пометку, чтобы убрать её',
};

const clampZoom = (z: number) => Math.min(4, Math.max(0.25, z));

export function EditorCanvas({ src, size, marks, onMarksChange, tool, onTextAt, onImageLoad, mobile }: {
  src: string;
  // Натуральный размер исходника; null — ещё грузится
  size: { w: number; h: number } | null;
  marks: Mark[];
  onMarksChange: (marks: Mark[]) => void;
  tool: Tool;
  // Инструмент «подпись»: точка на картинке, текст спрашивает родитель
  onTextAt: (x: number, y: number) => void;
  onImageLoad: (img: HTMLImageElement) => void;
  mobile: boolean;
}) {
  const stageRef = useRef<HTMLDivElement>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const [stage, setStage] = useState({ w: 0, h: 0 });
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [draft, setDraft] = useState<Mark | null>(null);
  const drag = useRef<
    | { kind: 'pan'; x: number; y: number; px: number; py: number }
    | { kind: 'draw'; x0: number; y0: number; pts: [number, number][] }
    | null
  >(null);

  useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setStage({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Колесо — масштаб; слушатель не пассивный, иначе страница прокрутится вместе с ним
  useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      setZoom(z => clampZoom(z * (e.deltaY < 0 ? 1.1 : 0.9)));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  const pad = SP.lg * 2;
  const fit = size && stage.w && stage.h
    ? Math.min((stage.w - pad) / size.w, (stage.h - pad) / size.h, 1)
    : 0;
  const k = size ? strokeScale(size.w, size.h) : 1;

  const toImage = (e: { clientX: number; clientY: number }): [number, number] | null => {
    const svg = svgRef.current;
    const ctm = svg?.getScreenCTM();
    if (!svg || !ctm) return null;
    const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(ctm.inverse());
    return [p.x, p.y];
  };

  const buildMark = (d: { x0: number; y0: number; pts: [number, number][] }, pt: [number, number]): Mark | null => {
    if (tool === 'mask') return { type: 'mask', points: d.pts, width: 30 * k };
    if (tool === 'rect') return { type: 'rect', x: Math.min(d.x0, pt[0]), y: Math.min(d.y0, pt[1]), w: Math.abs(pt[0] - d.x0), h: Math.abs(pt[1] - d.y0) };
    if (tool === 'arrow') return { type: 'arrow', x1: d.x0, y1: d.y0, x2: pt[0], y2: pt[1] };
    return null;
  };

  const onPointerDown = (e: RPointerEvent<HTMLDivElement>) => {
    if (!size || tool === 'eraser') return;
    // Жест не должен начинать выделение в документе: браузер тащит выделенное
    // нативным drag и обрывает жест pointercancel'ом
    e.preventDefault();
    window.getSelection()?.removeAllRanges();
    if (tool === 'hand') {
      drag.current = { kind: 'pan', x: e.clientX, y: e.clientY, px: pan.x, py: pan.y };
      e.currentTarget.setPointerCapture(e.pointerId);
      return;
    }
    const pt = toImage(e);
    if (!pt) return;
    if (tool === 'text') { onTextAt(pt[0], pt[1] - 14 * k); return; }
    drag.current = { kind: 'draw', x0: pt[0], y0: pt[1], pts: [pt] };
    e.currentTarget.setPointerCapture(e.pointerId);
  };

  const onPointerMove = (e: RPointerEvent<HTMLDivElement>) => {
    const d = drag.current;
    if (!d) return;
    if (d.kind === 'pan') { setPan({ x: d.px + e.clientX - d.x, y: d.py + e.clientY - d.y }); return; }
    const pt = toImage(e);
    if (!pt) return;
    d.pts.push(pt);
    setDraft(buildMark(d, pt));
  };

  const onPointerUp = () => {
    const d = drag.current;
    drag.current = null;
    if (d?.kind === 'draw' && draft) onMarksChange([...marks, draft]);
    setDraft(null);
  };

  const zoomBtn = { size: 'sm' as const, tone: 'muted' as const };
  const shown = draft ? [...marks, draft] : marks;

  return (
    <div
      ref={stageRef}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      onDragStart={e => e.preventDefault()}
      style={{
        position: 'relative', overflow: 'hidden', background: C.bgInset, userSelect: 'none',
        flex: mobile ? '0 0 300px' : 1, minHeight: 0, touchAction: 'none',
        cursor: tool === 'hand' ? 'grab' : tool === 'text' ? 'text' : 'crosshair',
      }}
    >
      {size && fit > 0 && (
        <div style={{
          position: 'absolute', left: '50%', top: '50%', width: size.w * fit, height: size.h * fit,
          transform: `translate(-50%, -50%) translate(${pan.x}px, ${pan.y}px) scale(${zoom})`,
          boxShadow: SHADOW.card,
        }}>
          <img src={src} alt="" draggable={false}
            style={{ width: '100%', height: '100%', display: 'block', userSelect: 'none', pointerEvents: 'none' }} />
          <svg ref={svgRef} viewBox={`0 0 ${size.w} ${size.h}`} preserveAspectRatio="none"
            style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }}>
            <MarksLayer marks={shown} k={k}
              onPick={tool === 'eraser' ? i => onMarksChange(marks.filter((_, j) => j !== i)) : undefined} />
          </svg>
        </div>
      )}
      {/* Пока натуральный размер неизвестен, картинку грузим невидимо ради onLoad */}
      {!size && (
        <img src={src} alt="" style={{ position: 'absolute', opacity: 0, pointerEvents: 'none' }}
          onLoad={e => onImageLoad(e.currentTarget)} />
      )}
      <div style={{
        position: 'absolute', top: SP.sm, left: '50%', transform: 'translateX(-50%)', pointerEvents: 'none',
        background: C.glass, color: C.textSecondary, fontSize: FS.sm, padding: `${SP.xs}px ${SP.md}px`,
        borderRadius: R.max, whiteSpace: 'nowrap', maxWidth: '90%', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        {TOOL_HINT[tool]}
      </div>
      <div
        onPointerDown={e => e.stopPropagation()}
        style={{
          position: 'absolute', right: SP.sm, bottom: SP.sm, display: 'flex', alignItems: 'center', gap: SP.xxs,
          background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: SP.xxs, boxShadow: SHADOW.card,
        }}
      >
        <IconButton {...zoomBtn} title="Уменьшить" onClick={() => setZoom(z => clampZoom(z * 0.8))}>
          <Minus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        <span style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: 40, textAlign: 'center' }}>{Math.round(zoom * 100)}%</span>
        <IconButton {...zoomBtn} title="Увеличить" onClick={() => setZoom(z => clampZoom(z * 1.25))}>
          <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
        <IconButton {...zoomBtn} title="Вписать" onClick={() => { setZoom(1); setPan({ x: 0, y: 0 }); }}>
          <Scan size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
    </div>
  );
}
