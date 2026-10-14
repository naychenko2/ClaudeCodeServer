// График «Нагрузка» панели «Локальные модели»: суммарная выработка стенда (ток/с)
// линией и очередь (waiting) столбиками по history[]. Inline SVG, без библиотек.
//
// Ключевое правило: null в точке — РАЗРЫВ линии (стенд лежал, данных нет), а не
// ноль: нули из null делать нельзя — null рвёт линию, а ноль — настоящее значение.
// Пустая история — аккуратная заглушка «нет данных за период».
//
// Оси/подписи — по макету docs/mockups/local-model-stats-panel.html (раздел «График»):
// линия ток/с — C.accent 1.75 + заливка 10 %; очередь — столбики C.info 55 %;
// сетка C.borderLight; подписи моно 9 px C.textMuted. Переключатель 15 мин / 1 ч.

import { useMemo } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { InlineSegmented } from '../../components/ui/InlineSegmented';
import type { LocalModelStats } from './api';

// === Чистая геометрия (тестируется без React/DOM) ===

// Точка истории в данных: t — unix-мс, throughput — ток/с (null — стенд лежал),
// waiting — очередь (null — данных нет).
export interface ThroughputSample {
  t: number;
  throughput: number | null;
  waiting: number | null;
}

// Геометрия области построения в единицах SVG.
export interface ChartGeom {
  width: number;   // ширина svg
  height: number;  // высота svg
  left: number;    // поле слева (подписи оси ток/с)
  right: number;   // поле справа (подписи оси очереди)
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

// Значение очереди → y-пикселя верха столбика (0 на дне, max наверху).
export function yForWaiting(v: number, maxWaiting: number, geom: ChartGeom): number {
  const f = maxWaiting > 0 ? Math.max(0, Math.min(1, v / maxWaiting)) : 0;
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

// Замкнутая область под линией (для 10 %-ной заливки): по каждому сегменту —
// вниз к дну, через точки, обратно на дно.
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

// Максимум очереди в окне (для масштаба правой оси); 0 — если данных нет.
export function maxWaitingOf(samples: ThroughputSample[]): number {
  let max = 0;
  for (const s of samples) {
    if (s.throughput != null && s.waiting != null && s.waiting > max) max = s.waiting;
  }
  return max;
}

// === Компонент ===

// Окно графика: 15 минут или 1 час.
export type Window = '15m' | '1h';
const WINDOW_MS: Record<Window, number> = { '15m': 15 * 60_000, '1h': 60 * 60_000 };

// Геометрия svg: фиксированные единицы, preserveAspectRatio="none" растягивает
// на ширину контейнера; подписи осей моно 9 px C.textMuted.
const GEOM: ChartGeom = { width: 340, height: 120, left: 26, right: 18, top: 8, bottom: 18 };

// history[] → ThroughputSample[] (t, throughput=genThroughput, waiting) в пределах окна.
function toSamples(history: LocalModelStats['history'], windowMs: number, now: number): ThroughputSample[] {
  const from = now - windowMs;
  const out: ThroughputSample[] = [];
  for (const h of history ?? []) {
    if (h.t < from || h.t > now) continue;
    out.push({ t: h.t, throughput: h.genThroughput ?? null, waiting: h.waiting ?? null });
  }
  return out;
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

// Тело графика: сетка, подписи осей, очередь (столбики), линия + заливка.
function ChartBody({ samples, windowMs, now }: { samples: ThroughputSample[]; windowMs: number; now: number }) {
  const tMin = now - windowMs;
  const tMax = now;
  const maxT = Math.max(maxThroughputOf(samples), 1);   // ≥ 1, чтобы ось не была 0
  const maxW = Math.max(maxWaitingOf(samples), 1);
  const segments = useMemo(
    () => buildThroughputSegments(samples, tMin, tMax, maxT, GEOM),
    [samples, tMin, tMax, maxT],
  );
  const points = segmentsToPoints(segments);
  const areas = segmentsToArea(segments, GEOM);

  // Подписи оси времени: «−15 мин / −10 / −5 / сейчас» (15m) или «−1 ч / −45 / −30 / −15 / сейчас» (1h)
  const ticks = windowMs === WINDOW_MS['15m']
    ? [0, 1 / 3, 2 / 3, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(15 * (1 - f))} мин` }))
    : [0, 1 / 4, 2 / 4, 3 / 4, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(60 * (1 - f))} мин` }));

  // Горизонтальная сетка + подписи оси ток/с (0, 50 % max, max)
  const yTicks = [0, 0.5, 1].map(f => ({
    y: yForThroughput(maxT * f, maxT, GEOM),
    label: String(Math.round(maxT * f)),
  }));
  // Подписи оси очереди (0, max)
  const wTicks = [0, 1].map(f => ({
    y: yForWaiting(maxW * f, maxW, GEOM),
    label: String(Math.round(maxW * f)),
  }));

  return (
    <svg
      viewBox={`0 0 ${GEOM.width} ${GEOM.height}`}
      preserveAspectRatio="none"
      style={{ width: '100%', height: GEOM.height, display: 'block' }}
      role="img"
      aria-label="Выработка стенда (ток/с) и очередь за период"
    >
      {/* Сетка */}
      {yTicks.map(t => (
        <line key={`g${t.y}`} x1={GEOM.left} x2={GEOM.width - GEOM.right} y1={t.y} y2={t.y}
          stroke={C.borderLight} strokeWidth={1} />
      ))}
      {/* Подписи оси ток/с (слева) */}
      {yTicks.map(t => (
        <text key={`yl${t.y}`} x={GEOM.left - 4} y={t.y + 3} textAnchor="end"
          fontFamily={FONT.mono} fontSize={9} fill={C.textMuted}>{t.label}</text>
      ))}
      {/* Подписи оси очереди (справа) */}
      {wTicks.map(t => (
        <text key={`wl${t.y}`} x={GEOM.width - GEOM.right + 4} y={t.y + 3} textAnchor="start"
          fontFamily={FONT.mono} fontSize={9} fill={C.textMuted}>{t.label}</text>
      ))}
      {/* Очередь: столбики C.info 55 % */}
      {samples.map((s, i) => {
        if (s.throughput == null || s.waiting == null || s.waiting <= 0) return null;
        const x = xForTime(s.t, tMin, tMax, GEOM);
        const y = yForWaiting(s.waiting, maxW, GEOM);
        const floor = GEOM.height - GEOM.bottom;
        return <rect key={`q${i}`} x={x - 2} y={y} width={4} height={Math.max(0, floor - y)} rx={1}
          fill={C.info} opacity={0.55} />;
      })}
      {/* Заливка под линией (10 %) */}
      {areas.map((d, i) => (
        <path key={`a${i}`} d={d} fill={C.accent} opacity={0.10} />
      ))}
      {/* Линия выработки (по одному полилинии на непрерывный прогон) */}
      {points.map((pts, i) => (
        <polyline key={`l${i}`} points={pts} fill="none" stroke={C.accent} strokeWidth={1.75}
          strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
      ))}
      {/* Подписи времени (низ) */}
      {ticks.map(t => {
        const x = GEOM.left + (GEOM.width - GEOM.left - GEOM.right) * t.f;
        return (
          <text key={`t${t.f}`} x={x} y={GEOM.height - 4}
            textAnchor={t.f === 0 ? 'start' : t.f === 1 ? 'end' : 'middle'}
            fontFamily={FONT.mono} fontSize={9} fill={C.textMuted}>{t.label}</text>
        );
      })}
    </svg>
  );
}

// Переключатель окна (15 мин / 1 ч) — InlineSegmented из кита; живёт в шапке карточки.
export function WindowToggle({ value, onChange, isMobile }: { value: Window; onChange: (w: Window) => void; isMobile: boolean }) {
  return (
    <InlineSegmented<Window>
      value={value}
      isMobile={isMobile}
      onChange={onChange}
      options={[
        { value: '15m', label: '15 мин' },
        { value: '1h', label: '1 ч' },
      ]}
    />
  );
}

// Тело карточки «Нагрузка»: график (или пустая заглушка) + легенда. Окно — извне
// (состояние держит TextTab, чтобы переключатель стоял в шапке карточки).
export function ThroughputChart({ history, window }: { history: LocalModelStats['history']; window: Window }) {
  const now = useMemo(() => Date.now(), []);
  const samples = useMemo(() => toSamples(history, WINDOW_MS[window], now), [history, window, now]);

  return (
    <div>
      {samples.length === 0
        ? <EmptyChart />
        : <ChartBody samples={samples} windowMs={WINDOW_MS[window]} now={now} />}
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', marginTop: SP.xs, fontSize: FS.xs, color: C.textSecondary }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          <i style={{ display: 'inline-block', width: 10, height: 3, borderRadius: 2, background: C.accent }} />
          ток/с, левая ось
        </span>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
          <i style={{ display: 'inline-block', width: 10, height: 3, borderRadius: 2, background: C.info }} />
          очередь, правая ось
        </span>
      </div>
    </div>
  );
}
