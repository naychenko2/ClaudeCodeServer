// Мини-график (sparkline) вкладки «Железо»: 15 мин, 90 точек (шаг 10 с), без осей
// и сетки. viewBox + preserveAspectRatio="none" + vector-effect: non-scaling-stroke —
// линия тянется на всю ширину плитки, толщина 1,5 px не масштабируется.
//
// Масштаб по Y — min…max ряда ±1 °C (чтобы линия не прилипала к кромкам).
// Точка r 2,5 на последнем значении. null в ряду рвёт линию (разрыв).
// Цвет линии задаёт вызывающий (по level узла).

import { C } from 'aihome_shell/kit';

const W = 120;
const H = 24;

// Строит polyline-точки: null → разрыв (новый сегмент). Возвращает список сегментов.
function segments(points: (number | null)[]): string[][] {
  const vs = points.filter((v): v is number => v != null);
  if (vs.length === 0) return [];
  const lo = Math.min(...vs) - 1;
  const hi = Math.max(...vs) + 1;
  const span = hi - lo || 1;
  const X = (i: number) => (i * W) / (points.length - 1);
  const Y = (v: number) => H - 2 - ((v - lo) / span) * (H - 4);
  const segs: string[][] = [];
  let cur: string[] = [];
  points.forEach((v, i) => {
    if (v == null) {
      if (cur.length) { segs.push(cur); cur = []; }
    } else {
      cur.push(`${X(i).toFixed(1)},${Y(v).toFixed(1)}`);
    }
  });
  if (cur.length) segs.push(cur);
  return segs;
}

// Координаты последней непустой точки (для маркера).
function lastPoint(points: (number | null)[]): { x: number; y: number } | null {
  const vs = points.filter((v): v is number => v != null);
  if (vs.length === 0) return null;
  const lo = Math.min(...vs) - 1;
  const hi = Math.max(...vs) + 1;
  const span = hi - lo || 1;
  const X = (i: number) => (i * W) / (points.length - 1);
  const Y = (v: number) => H - 2 - ((v - lo) / span) * (H - 4);
  for (let i = points.length - 1; i >= 0; i--) {
    const v = points[i];
    if (v != null) return { x: X(i), y: Y(v) };
  }
  return null;
}

export function Sparkline({ points, color, ariaLabel = 'за 15 минут' }: {
  points: (number | null)[];
  color: string;
  ariaLabel?: string;
}) {
  const segs = segments(points);
  const lp = lastPoint(points);
  // Без непустых точек — линия не рисуется (подложку ставит вызывающий).
  if (segs.length === 0) return null;
  return (
    <svg className="spark" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none"
      style={{ display: 'block', width: '100%', height: H, overflow: 'visible' }}
      role="img" aria-label={ariaLabel}>
      {segs.map((s, i) => (
        <polyline key={i} points={s.join(' ')} fill="none" stroke={color}
          strokeWidth={1.5} vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
      ))}
      {lp && <circle cx={lp.x} cy={lp.y} r={2.5} fill={color} vectorEffect="non-scaling-stroke" />}
    </svg>
  );
}

// Подложка вместо графика, когда датчика нет / нет данных (C.bgInset, h 24).
export function SparklinePlaceholder() {
  return (
    <div style={{ height: 24, marginTop: 2, borderRadius: 4, background: C.bgInset }} aria-hidden />
  );
}
