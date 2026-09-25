// Пометки холста (ADR-016, раздел 3). Координаты — в пикселях исходника, поэтому
// экспорт не зависит от масштаба экрана. Исходник уходит чистым, а из пометок фронт
// собирает три артефакта: mask.png (кисть, белое — менять), annotated.png (исходник
// с нарисованными пометками) и marks.json (координаты в долях 0…1).

import { C, FONT } from '../../lib/design';

export type Mark =
  | { type: 'mask'; points: [number, number][]; width: number }
  | { type: 'rect'; x: number; y: number; w: number; h: number }
  | { type: 'arrow'; x1: number; y1: number; x2: number; y2: number }
  | { type: 'text'; x: number; y: number; text: string };

export type Tool = 'hand' | 'mask' | 'arrow' | 'rect' | 'text' | 'eraser';

// Размеры штрихов в макете заданы для картинки 640 px по длинной стороне
export const strokeScale = (w: number, h: number) => Math.max(w, h) / 640;

const pathD = (pts: [number, number][]) =>
  pts.length ? `M${pts.map(p => `${p[0]} ${p[1]}`).join(' L')}` : '';

const arrowHead = (m: Extract<Mark, { type: 'arrow' }>, k: number) => {
  const a = Math.atan2(m.y2 - m.y1, m.x2 - m.x1), L = 16 * k;
  const p1 = [m.x2 - L * Math.cos(a - 0.45), m.y2 - L * Math.sin(a - 0.45)];
  const p2 = [m.x2 - L * Math.cos(a + 0.45), m.y2 - L * Math.sin(a + 0.45)];
  return { a, d: `M${m.x2} ${m.y2} L${p1.join(' ')} L${p2.join(' ')}Z` };
};

const labelW = (text: string, k: number) => (text.length * 8.4 + 20) * k;

// SVG-слой пометок поверх картинки (viewBox = размер исходника)
export function MarksLayer({ marks, k, onPick }: {
  marks: Mark[];
  k: number;
  // Ластик: клик по пометке передаёт её индекс
  onPick?: (i: number) => void;
}) {
  return (
    <>
      {marks.map((m, i) => {
        const pick = onPick ? { onPointerDown: (e: React.PointerEvent) => { e.stopPropagation(); onPick(i); }, style: { cursor: 'pointer' } } : {};
        if (m.type === 'mask') {
          return (
            <path key={i} d={pathD(m.points)} fill="none" stroke={C.accent} strokeOpacity={0.45}
              strokeWidth={m.width} strokeLinecap="round" strokeLinejoin="round" {...pick} />
          );
        }
        if (m.type === 'rect') {
          return (
            <rect key={i} x={m.x} y={m.y} width={m.w} height={m.h} rx={4 * k} fill="none"
              stroke={C.accent} strokeWidth={3 * k} strokeDasharray={`${8 * k} ${6 * k}`} {...pick} />
          );
        }
        if (m.type === 'arrow') {
          const { a, d } = arrowHead(m, k);
          return (
            <g key={i} {...pick}>
              <line x1={m.x1} y1={m.y1} x2={m.x2 - 8 * k * Math.cos(a)} y2={m.y2 - 8 * k * Math.sin(a)}
                stroke={C.accent} strokeWidth={4 * k} strokeLinecap="round" />
              <path d={d} fill={C.accent} />
            </g>
          );
        }
        return (
          <g key={i} {...pick}>
            <rect x={m.x} y={m.y} width={labelW(m.text, k)} height={28 * k} rx={14 * k} fill={C.accent} />
            <text x={m.x + 10 * k} y={m.y + 19 * k} fill={C.onAccent} fontSize={14 * k} fontFamily={FONT.sans}>{m.text}</text>
          </g>
        );
      })}
    </>
  );
}

export const hasMaskMark = (marks: Mark[]) => marks.some(m => m.type === 'mask');

// marks.json: тип, координаты в долях 0…1, текст подписи
export function marksToJson(marks: Mark[], w: number, h: number): string {
  const fx = (x: number) => Math.round((x / w) * 1e4) / 1e4;
  const fy = (y: number) => Math.round((y / h) * 1e4) / 1e4;
  return JSON.stringify(marks.map(m => {
    switch (m.type) {
      case 'mask': return { type: 'mask', points: m.points.map(([x, y]) => [fx(x), fy(y)]), width: fx(m.width) };
      case 'rect': return { type: 'rect', x: fx(m.x), y: fy(m.y), w: fx(m.w), h: fy(m.h) };
      case 'arrow': return { type: 'arrow', x1: fx(m.x1), y1: fy(m.y1), x2: fx(m.x2), y2: fy(m.y2) };
      default: return { type: 'text', x: fx(m.x), y: fy(m.y), text: m.text };
    }
  }));
}

const toBlob = (canvas: HTMLCanvasElement) =>
  new Promise<Blob>((resolve, reject) =>
    canvas.toBlob(b => (b ? resolve(b) : reject(new Error('Не удалось собрать картинку'))), 'image/png'));

function strokePath(ctx: CanvasRenderingContext2D, pts: [number, number][]) {
  ctx.beginPath();
  pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
  if (pts.length === 1) ctx.lineTo(pts[0][0] + 0.1, pts[0][1]);
  ctx.stroke();
}

// Бинарная маска ровно в размер исходника: чёрный фон, белое — менять
export async function exportMask(marks: Mark[], w: number, h: number): Promise<Blob | null> {
  const masks = marks.filter((m): m is Extract<Mark, { type: 'mask' }> => m.type === 'mask');
  if (!masks.length) return null;
  const canvas = document.createElement('canvas');
  canvas.width = w; canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.fillStyle = 'black';
  ctx.fillRect(0, 0, w, h);
  ctx.strokeStyle = 'white';
  ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  for (const m of masks) { ctx.lineWidth = m.width; strokePath(ctx, m.points); }
  return toBlob(canvas);
}

// Значение CSS-переменной токена (C.accent = 'var(--c-accent)') — canvas переменных не понимает
function tokenColor(token: string, fallback: string): string {
  const name = token.match(/var\((--[\w-]+)\)/)?.[1];
  const v = name ? getComputedStyle(document.documentElement).getPropertyValue(name).trim() : '';
  return v || fallback;
}

// Размеченная копия: исходник плюс стрелки, рамки и подписи (кисть уходит маской)
export async function exportAnnotated(img: HTMLImageElement, marks: Mark[], w: number, h: number): Promise<Blob | null> {
  const drawn = marks.filter(m => m.type !== 'mask');
  if (!drawn.length) return null;
  const canvas = document.createElement('canvas');
  canvas.width = w; canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  ctx.drawImage(img, 0, 0, w, h);
  const k = strokeScale(w, h);
  const accent = tokenColor(C.accent, 'orange');
  const onAccent = tokenColor(C.onAccent, 'white');
  ctx.strokeStyle = accent; ctx.fillStyle = accent;
  ctx.lineCap = 'round'; ctx.lineJoin = 'round';
  for (const m of drawn) {
    if (m.type === 'rect') {
      ctx.lineWidth = 3 * k;
      ctx.setLineDash([8 * k, 6 * k]);
      ctx.strokeRect(m.x, m.y, m.w, m.h);
      ctx.setLineDash([]);
    } else if (m.type === 'arrow') {
      const { a, d } = arrowHead(m, k);
      ctx.lineWidth = 4 * k;
      strokePath(ctx, [[m.x1, m.y1], [m.x2 - 8 * k * Math.cos(a), m.y2 - 8 * k * Math.sin(a)]]);
      ctx.fill(new Path2D(d));
    } else if (m.type === 'text') {
      ctx.fillStyle = accent;
      ctx.beginPath();
      ctx.roundRect(m.x, m.y, labelW(m.text, k), 28 * k, 14 * k);
      ctx.fill();
      ctx.fillStyle = onAccent;
      ctx.font = `${14 * k}px sans-serif`;
      ctx.fillText(m.text, m.x + 10 * k, m.y + 19 * k);
      ctx.fillStyle = accent;
    }
  }
  return toBlob(canvas);
}
