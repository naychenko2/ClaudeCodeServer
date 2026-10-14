// График «Выработка стенда, всего» панели «Локальные модели» (вкладка «LLM»):
// суммарная выработка стенда (ток/с) областью с линией. По спецификации
// docs/mockups/local-model-stats-charts-v2.md (v2.1): ОДНА величина на поле,
// единица в заголовке, «круглая» шкала справа, пунктир среднего, считывание
// наведением/касанием, разрыв на null (рестарт движка), без очереди (она убрана
// из графика — число «ждут в очереди» живёт в блоке «Сейчас»).
//
// Ключевое правило: null в точке — РАЗРЫВ линии (стенд лежал, данных нет), а не
// ноль: нули из null делать нельзя — null рвёт линию, а ноль — настоящее значение.
// Пустая история — аккуратная заглушка «нет данных за период».
//
// SVG рисуем в реальных px (ResizeObserver), без preserveAspectRatio="none":
// подписи осей не сплющиваются, работают токены FS.xs/FONT.mono.

import { useEffect, useMemo, useRef, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { InlineSegmented } from '../../components/ui/InlineSegmented';
import { formatTps } from './format';
import type { LocalModelStats } from './api';

// === Чистая геометрия и сведение (тестируется без React/DOM) ===

// Точка истории в данных: t — unix-мс, throughput — ток/с (null — стенд лежал),
// running — сколько запросов шло (null — движок не отдал).
export interface ThroughputSample {
  t: number;
  throughput: number | null;
  running: number | null;
}

// Геометрия области построения в единицах SVG.
export interface ChartGeom {
  width: number;   // ширина svg (реальные px из ResizeObserver)
  height: number;  // высота поля (96 px)
  left: number;    // поле слева (0: подписи «ср.»/«данных нет» рисуются внутри)
  right: number;   // поле справа (подписи оси Y, 28 px — влезает «1.5k»)
  top: number;     // поле сверху
  bottom: number;  // поле снизу (подписи времени)
}

// unix-мс → x-пиксель внутри области построения. Degenerate (tMin===tMax) — левый край.
export function xForTime(t: number, tMin: number, tMax: number, geom: ChartGeom): number {
  const span = tMax - tMin;
  if (span <= 0) return geom.left;
  const f = (t - tMin) / span;
  return geom.left + (geom.width - geom.left - geom.right) * f;
}

// Значение выработки → y-пиксель (0 на дне, максимум наверху). Зажато в 0..max.
export function yForThroughput(v: number, maxThroughput: number, geom: ChartGeom): number {
  const f = maxThroughput > 0 ? Math.max(0, Math.min(1, v / maxThroughput)) : 0;
  const plotH = geom.height - geom.top - geom.bottom;
  return geom.top + plotH * (1 - f);
}

// Линия выработки как НЕСКОЛЬКО непрерывных сегментов. null в throughput РВЁТ
// линию: точка слева от null не соединяется с точкой справа. Возвращает по одному
// сегменту на каждый непрерывный прогон ненулевых точек (сегмент ≥ 1 точки).
// Пустой вход — нет сегментов. Одна ненулевая точка — один сегмент из одной точки.
export function buildThroughputSegments(
  samples: ThroughputSample[],
  tMin: number,
  tMax: number,
  maxThroughput: number,
  geom: ChartGeom,
): { x: number; y: number }[][] {
  const segments: { x: number; y: number }[][] = [];
  let current: { x: number; y: number }[] = [];
  for (const s of samples) {
    if (s.throughput == null) {
      if (current.length) { segments.push(current); current = []; }
    } else {
      current.push({ x: xForTime(s.t, tMin, tMax, geom), y: yForThroughput(s.throughput, maxThroughput, geom) });
    }
  }
  if (current.length) segments.push(current);
  return segments;
}

// Сегменты → атрибут points полилинии (по одному на непрерывный прогон).
export function segmentsToPoints(segments: { x: number; y: number }[][]): string[] {
  return segments.map(seg => seg.map(p => `${round1(p.x)},${round1(p.y)}`).join(' '));
}

// Замкнутая область под линией: по каждому сегменту — вниз к дну, через точки,
// обратно на дно.
export function segmentsToArea(segments: { x: number; y: number }[][], geom: ChartGeom): string[] {
  const floor = geom.height - geom.bottom;
  return segments.map(seg => {
    if (seg.length === 0) return '';
    const first = seg[0];
    const last = seg[seg.length - 1];
    const body = seg.map(p => `${round1(p.x)},${round1(p.y)}`).join(' L');
    return `M${round1(first.x)},${round1(floor)} L${body} L${round1(last.x)},${round1(floor)} Z`;
  }).filter(Boolean);
}

function round1(n: number): number {
  return Math.round(n * 10) / 10;
}

// Максимум выработки в окне (для масштаба оси); 0 — если данных нет.
export function maxThroughputOf(samples: ThroughputSample[]): number {
  let max = 0;
  for (const s of samples) {
    if (s.throughput != null && s.throughput > max) max = s.throughput;
  }
  return max;
}

// «Круглая» шкала (только график выработки): верх = ближайшее сверху число из ряда
// 1, 2, 5 × 10ⁿ от максимума, НЕ МЕНЕЕ 10 (простой не схлопывает ось в 0/1).
// Середина = верх / 2. Примеры: макс 37 → 50; 412 → 500; 980 → 1000; 0 → 10; 1 → 10.
export function niceAxisMax(max: number): number {
  if (max <= 0) return 10;
  const pow = Math.pow(10, Math.floor(Math.log10(max)));
  for (const m of [1, 2, 5, 10]) {
    const v = m * pow;
    if (v >= max) return Math.max(v, 10);
  }
  return Math.max(10 * pow, 10);
}

// Подпись оси Y: целое, без единицы (она в заголовке); ≥ 1000 — «1k», «1.5k».
export function formatAxisTps(v: number): string {
  if (v >= 1000) {
    const k = v / 1000;
    return `${Number.isInteger(k) ? k : k.toFixed(1)}k`;
  }
  return String(Math.round(v));
}

// Среднее за окно: среднее genThroughput по точкам, где genThroughput != null
// И running > 0 (простой в среднее не входит). null — нет таких точек.
export function avgThroughputOf(samples: ThroughputSample[]): number | null {
  let sum = 0;
  let n = 0;
  for (const s of samples) {
    if (s.throughput != null && s.running != null && s.running > 0) {
      sum += s.throughput;
      n++;
    }
  }
  return n > 0 ? sum / n : null;
}

// history[] → ThroughputSample[] (t, throughput=genThroughput, running) в пределах окна.
function toSamples(history: LocalModelStats['history'], windowMs: number, now: number): ThroughputSample[] {
  const from = now - windowMs;
  const out: ThroughputSample[] = [];
  for (const h of history ?? []) {
    if (h.t < from || h.t > now) continue;
    out.push({ t: h.t, throughput: h.genThroughput ?? null, running: h.running ?? null });
  }
  return out;
}

// Пик выработки (максимум genThroughput) по истории в пределах окна — для «пик»
// в блоке «Скорость». 0 — если данных нет.
export function peakThroughputOf(history: LocalModelStats['history'], windowMs: number, now: number): number {
  const from = now - windowMs;
  let max = 0;
  for (const h of history ?? []) {
    if (h.t < from || h.t > now) continue;
    if (h.genThroughput != null && h.genThroughput > max) max = h.genThroughput;
  }
  return max;
}

// === Компонент ===

// Окно графика: 5 минут, 15 минут или 1 час.
export type Window = '5m' | '15m' | '1h';
export const WINDOW_MS: Record<Window, number> = { '5m': 5 * 60_000, '15m': 15 * 60_000, '1h': 60 * 60_000 };

// Геометрия svg: высота поля 96 px (десктоп и мобила одинаково), поле справа 28 px
// (подписи оси Y), слева 0 (подписи «ср.»/«данных нет» рисуются внутри поля).
const GEOM: ChartGeom = { width: 340, height: 96, left: 0, right: 28, top: 8, bottom: 18 };

// Метки оси времени: «−5 / −3 / −1 / сейчас» (5m), «−15 / −10 / −5 / сейчас» (15m),
// «−1 ч / −45 / −30 / −15 / сейчас» (1h). Единица — только на первой метке.
function timeTicks(window: Window): { f: number; label: string }[] {
  if (window === '5m') {
    return [0, 1 / 3, 2 / 3, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(5 * (1 - f))}` }));
  }
  if (window === '15m') {
    return [0, 1 / 3, 2 / 3, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(15 * (1 - f))}` }));
  }
  return [0, 1 / 4, 2 / 4, 3 / 4, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(60 * (1 - f))}` }));
}

// Пустая заглушка: нет ни одной точки в окне — «нет данных за период».
function EmptyChart() {
  return (
    <div style={{
      height: GEOM.height, display: 'flex', alignItems: 'center', justifyContent: 'center',
      background: C.bgInset, borderRadius: R.sm, fontSize: FS.sm, color: C.textMuted,
    }}>
      Нет данных за период
    </div>
  );
}

// Тело графика: сетка, подписи оси Y (справа), пунктир среднего, линия + заливка,
// ось времени, считывание (наведение/касание).
function ChartBody({ samples, window, now, width }: {
  samples: ThroughputSample[]; window: Window; now: number; width: number;
}) {
  const tMin = now - WINDOW_MS[window];
  const tMax = now;
  const maxT = niceAxisMax(maxThroughputOf(samples));
  const avg = avgThroughputOf(samples);
  const geom: ChartGeom = { ...GEOM, width };
  const segments = useMemo(
    () => buildThroughputSegments(samples, tMin, tMax, maxT, geom),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [samples, tMin, tMax, maxT, width],
  );
  const points = segmentsToPoints(segments);
  const areas = segmentsToArea(segments, geom);

  // Подписи оси Y: 0, середина, верх («круглое» число)
  const yTicks = [0, 0.5, 1].map(f => ({
    y: yForThroughput(maxT * f, maxT, geom),
    label: formatAxisTps(maxT * f),
  }));

  // Ось времени
  const ticks = timeTicks(window);

  // Считывание: индекс интервала (по x) под курсором/пальцем; null — нет
  const [hoverX, setHoverX] = useState<number | null>(null);
  const onPointer = (e: React.PointerEvent) => {
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const x = e.clientX - rect.left;
    // x → ближайшая точка: находим sample с минимальным |x - xForTime|
    let best: number | null = null;
    let bestD = Infinity;
    for (const s of samples) {
      if (s.throughput == null) continue;
      const sx = xForTime(s.t, tMin, tMax, geom);
      const d = Math.abs(sx - x);
      if (d < bestD) { bestD = d; best = sx; }
    }
    setHoverX(best);
  };

  // Строка-итог / считывание: «в среднем … · пик …» или «HH:MM · N ток/с»
  const hoverSample = hoverX != null
    ? samples.reduce<{ t: number; throughput: number | null } | null>((acc, s) => {
        if (s.throughput == null) return acc;
        const sx = xForTime(s.t, tMin, tMax, geom);
        if (acc == null || Math.abs(sx - hoverX) < Math.abs(xForTime(acc.t, tMin, tMax, geom) - hoverX)) {
          return { t: s.t, throughput: s.throughput };
        }
        return acc;
      }, null)
    : null;

  const summary = hoverSample
    ? `${String(new Date(hoverSample.t).getHours()).padStart(2, '0')}:${String(new Date(hoverSample.t).getMinutes()).padStart(2, '0')} · ${hoverSample.throughput != null ? formatTps(hoverSample.throughput) : 'нет данных'}`
    : avg != null
      ? `в среднем ${formatTps(avg)} · пик ${formatTps(maxThroughputOf(samples))}`
      : 'за период генерации не было';

  return (
    <div>
      {/* Строка-итог / считывания: фиксированная высота, не двигает график */}
      <div aria-live="polite" style={{
        height: FS.sm * 1.45, display: 'flex', alignItems: 'center',
        fontSize: FS.sm, color: C.textSecondary,
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
      }}>
        <span style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{summary}</span>
      </div>
      <div
        style={{ position: 'relative', marginTop: SP.sm, touchAction: 'pan-y', cursor: 'crosshair' }}
        onPointerMove={onPointer}
        onPointerDown={onPointer}
        onPointerLeave={() => setHoverX(null)}
      >
        <svg
          viewBox={`0 0 ${width} ${GEOM.height}`}
          width={width}
          height={GEOM.height}
          style={{ display: 'block' }}
          role="img"
          aria-label={`Выработка стенда за ${window === '5m' ? '5 минут' : window === '15m' ? '15 минут' : 'час'}, ${summary}`}
        >
          {/* Сетка: три горизонтали на метках 0 / середина / верх */}
          {yTicks.map(t => (
            <line key={`g${t.y}`} x1={geom.left} x2={width - geom.right} y1={t.y} y2={t.y}
              stroke={t.y === GEOM.height - geom.bottom ? C.border : C.borderLight} strokeWidth={1} />
          ))}
          {/* Подписи оси Y (справа, text-anchor: end, у правого края поля) */}
          {yTicks.map(t => (
            <text key={`yl${t.y}`} x={width - geom.right + 4} y={t.y + 3} textAnchor="start"
              fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textMuted}>{t.label}</text>
          ))}
          {/* Пунктир среднего: от первой точки с данными до правого края */}
          {avg != null && (() => {
            const first = samples.find(s => s.throughput != null && s.running != null && s.running > 0);
            if (!first) return null;
            const y = yForThroughput(avg, maxT, geom);
            const x0 = xForTime(first.t, tMin, tMax, geom);
            return (
              <g>
                <line x1={x0} x2={width - geom.right} y1={y} y2={y}
                  stroke={C.textSecondary} strokeWidth={1} strokeDasharray="4 3" />
                <text x={x0 + 2} y={y - 3} fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textSecondary}>ср.</text>
              </g>
            );
          })()}
          {/* Заливка под линией (12 %) */}
          {areas.map((d, i) => (
            <path key={`a${i}`} d={d} fill={C.accent} fillOpacity={0.12} />
          ))}
          {/* Линия выработки (по одному полилинии на непрерывный прогон) */}
          {points.map((pts, i) => (
            <polyline key={`l${i}`} points={pts} fill="none" stroke={C.accent} strokeWidth={1.75}
              strokeLinejoin="round" strokeLinecap="round" />
          ))}
          {/* Вертикаль считывания + точка на линии */}
          {hoverX != null && hoverSample && hoverSample.throughput != null && (() => {
            const hx = xForTime(hoverSample.t, tMin, tMax, geom);
            const hy = yForThroughput(hoverSample.throughput, maxT, geom);
            return (
              <g>
                <line x1={hx} x2={hx} y1={geom.top} y2={GEOM.height - geom.bottom}
                  stroke={C.textMuted} strokeWidth={1} />
                <circle cx={hx} cy={hy} r={3} fill={C.accent} stroke={C.bgCard} strokeWidth={2} />
              </g>
            );
          })()}
          {/* Подписи времени (низ) */}
          {ticks.map(t => {
            const x = geom.left + (width - geom.left - geom.right) * t.f;
            return (
              <text key={`t${t.f}`} x={x} y={GEOM.height - 4}
                textAnchor={t.f === 0 ? 'start' : t.f === 1 ? 'end' : 'middle'}
                fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textMuted}>{t.label}</text>
            );
          })}
        </svg>
      </div>
    </div>
  );
}

// Переключатель окна (5 мин / 15 мин / 1 ч) — InlineSegmented из кита; живёт в
// шапке карточки.
export function WindowToggle({ value, onChange, isMobile }: { value: Window; onChange: (w: Window) => void; isMobile: boolean }) {
  return (
    <InlineSegmented<Window>
      value={value}
      isMobile={isMobile}
      onChange={onChange}
      options={[
        { value: '5m', label: '5 мин' },
        { value: '15m', label: '15 мин' },
        { value: '1h', label: '1 ч' },
      ]}
    />
  );
}

// Тело карточки «Выработка стенда, всего»: график (или пустая заглушка). Окно —
// извне (состояние держит TextTab, чтобы переключатель стоял в шапке карточки).
// Ширина поля — ResizeObserver: SVG рисуется в реальных px, без preserveAspectRatio.
export function ThroughputChart({ history, window }: { history: LocalModelStats['history']; window: Window }) {
  const now = useMemo(() => Date.now(), []);
  const samples = useMemo(() => toSamples(history, WINDOW_MS[window], now), [history, window, now]);

  // Реальная ширина обёртки (ResizeObserver): SVG рисуется в px, не в viewBox-долях.
  const wrapRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(340);
  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const ro = new ResizeObserver(entries => {
      for (const e of entries) {
        const w = Math.round(e.contentRect.width);
        if (w > 0) setWidth(w);
      }
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  return (
    <div ref={wrapRef} style={{ minWidth: 0 }}>
      {samples.length === 0
        ? <EmptyChart />
        : <ChartBody samples={samples} window={window} now={now} width={width} />}
    </div>
  );
}
