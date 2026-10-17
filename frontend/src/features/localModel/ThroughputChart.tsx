// Карточка «Нагрузка и кэш» панели «Локальные модели» (вкладка «LLM»):
// выработка (ток/с) областью с линией, под ней график «Кэш, %» (занято /
// попадания / из RAM) и полоса «Вытеснения» — на ОДНОЙ общей оси времени.
// Спецификация docs/mockups/local-model-stats-charts-v2.md (v2.1).
//
// Ключевые правила:
// • null в точке — РАЗРЫВ (стенд лежал / метрики нет / рестарт), а не ноль:
//   нули из null делать нельзя — null рвёт линию, ноль — настоящее значение.
// • Доля попаданий считается из СУММ приращений по интервалу, а не средним
//   долей точек: точка 5 с с одним коротким запросом дала бы 0 % или 100 % и
//   перевесила бы минуту реальной работы.
// • SVG рисуется в реальных px (ResizeObserver), без preserveAspectRatio="none":
//   подписи осей не сплющиваются, работают токены FS.xs/FONT.mono.
// • Считывание — одна вертикаль через все три поля + строка-итог над ними.

import { useEffect, useMemo, useRef, useState } from 'react';
import { C, FIELD, FONT, FS, R, SP } from '../../lib/design';
import { InlineSegmented } from '../../components/ui/InlineSegmented';
import { formatPerc, formatTps } from './format';
import type { LocalModelStats } from './api';

// === Типы ===

// Точка истории: t — unix-мс; throughput/running — как раньше; серии кэша v2.1
// (kvUsage — доля 0..1, остальные — приращения счётчиков с прошлой точки).
// genTokens/itlSeconds/itlCount — приращения для скорости генерации на поток (главная
// линия, ADR-029) и графика «Шаг модели» (время шага, токенов за шаг, агентов в
// генерации). null — нет данных (разрыв), 0 — настоящее значение.
export interface ThroughputSample {
  t: number;
  throughput: number | null;
  running: number | null;
  kvUsage: number | null;
  prefixHits: number | null;
  prefixQueries: number | null;
  ramHits: number | null;
  ramQueries: number | null;
  preemptions: number | null;
  genTokens: number | null;
  itlSeconds: number | null;
  itlCount: number | null;
  // Полоса «Куда уходит время агентов» (ADR-029, v2.3): prefillSeconds/queueSeconds —
  // приращения гистограмм (сек), promptTokens — прочитанный промпт (токены). null — разрыв.
  prefillSeconds: number | null;
  queueSeconds: number | null;
  promptTokens: number | null;
}

// Геометрия области построения в единицах SVG.
export interface ChartGeom {
  width: number;   // ширина svg (реальные px из ResizeObserver)
  height: number;  // высота поля (96 / 64 / 32 px)
  left: number;    // поле слева (0: подписи «ср.»/«данных нет» рисуются внутри)
  right: number;   // поле справа (подписи оси Y, 28 px — влезает «1.5k»)
  top: number;     // поле сверху
  bottom: number;  // поле снизу (0: ось времени общая, под последним полем)
}

// Интервал сводки: одна величина на поле, своя шкала.
export interface Interval {
  tStart: number;
  tEnd: number;
  // «выдано стендом, с паузами»: среднее genThroughput (Δтокенов/Δвремя по стене) — второй
  // план графика. null — разрыв (движок молчал/рестарт).
  throughput: number | null;
  // «скорость генерации» на поток — ГЛАВНАЯ линия: Σ genTokens / Σ itlSeconds. Это измерение
  // vLLM (время, когда модель реально выдавала токены), а не оценка по опросам. null — разрыв
  // (молчание/рестарт) или Σitl = 0 (генерации не было).
  perStream: number | null;
  kvUsage: number | null;      // максимум kvUsage (!= null)
  prefix: number | null;       // Σ prefixHits / Σ prefixQueries; null — движок молчал (разрыв)
  ram: number | null;          // Σ ramHits / Σ ramQueries; null — движок молчал (разрыв)
  preemptions: number | null;  // сумма (!= null); null — столбика нет
  // Движок отдал хотя бы одну точку с этой серией (в т.ч. 0/0 — «не спрашивали»).
  // false — серии в интервале нет вовсе: линия обрывается (рестарт / молчание).
  hasData: { prefix: boolean; ram: boolean };
  // «Куда уходит время агентов» (ADR-029, v2.4): три состояния интервала.
  // shares — составная полоса долей (в сумме 100 %): gen — генерирует (модель реально
  // выдаёт токены), prefill — читает промпт, wait — ждёт соседа (генерация стоит, пока
  // стенд читает чужой prefill), queue — в очередь; idle — движок отвечал, но шагов/
  // запросов не было (сумма четырёх = 0); break — движок молчал/рестарт/нет данных.
  timeShares: TimeState;
}

// Доли «куда уходит время» (0..1, в сумме 1). Порядок — порядок сегментов полосы.
export interface TimeShares {
  gen: number;      // генерирует
  prefill: number;  // читает промпт
  wait: number;     // ждёт соседа
  queue: number;    // в очереди
}

// Состояние интервала «куда уходит время» — три взаимоисключающих (ADR-029, v2.4):
// • shares — доли (сумма 1): цветная полоса на всю высоту поля;
// • idle   — движок отвечал, но шагов/запросов не было (сумма четырёх = 0):
//            нейтральная заливка «простой» на всю высоту;
// • break  — движок молчал/рестарт/нет данных: заливки нет (фон-разрыв).
// Простой ≠ нет данных: null-разрыв не красим «простоем» (правило null = разрыв).
export type TimeState =
  | { kind: 'shares'; shares: TimeShares }
  | { kind: 'idle' }
  | { kind: 'break' };

// === Чистая геометрия (тестируется без React/DOM) ===

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

// Доля 0..1 → y-пиксель на фиксированной шкале 0–100 % (график «Кэш, %»).
export function yForPerc(v: number, geom: ChartGeom): number {
  const f = Math.max(0, Math.min(1, v));
  const plotH = geom.height - geom.top - geom.bottom;
  return geom.top + plotH * (1 - f);
}

// Целое (штуки) → y-пиксель на шкале 0..max (график «Вытеснения»).
export function yForCount(v: number, max: number, geom: ChartGeom): number {
  const f = max > 0 ? Math.max(0, Math.min(1, v / max)) : 0;
  const plotH = geom.height - geom.top - geom.bottom;
  return geom.top + plotH * (1 - f);
}

// Линия выработки как НЕСКОЛЬКО непрерывных сегментов. null в throughput РВЁТ
// линию: точка слева от null не соединяется с точкой справа.
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

// Общая сборка линии по интервалам: одна точка на центр каждого интервала, null — обрыв
// (как у линий кэша): центр интервала по x, y на общей оси скорости.
function buildIntervalSegments(
  intervals: Interval[],
  pick: (it: Interval) => number | null,
  tMin: number,
  tMax: number,
  maxSeries: number,
  geom: ChartGeom,
): { x: number; y: number }[][] {
  const segs: { x: number; y: number }[][] = [];
  let cur: { x: number; y: number }[] = [];
  for (const it of intervals) {
    const v = pick(it);
    if (v == null) { if (cur.length) { segs.push(cur); cur = []; } continue; }
    const cx = geom.left + (geom.width - geom.left - geom.right) * ((it.tStart + (it.tEnd - it.tStart) / 2 - tMin) / (tMax - tMin));
    cur.push({ x: cx, y: yForThroughput(v, maxSeries, geom) });
  }
  if (cur.length) segs.push(cur);
  return segs;
}

// Главная линия «скорость генерации, ток/с на поток»: ΣgenTokens/ΣitlSeconds по интервалу,
// null — обрыв.
export function buildPerStreamSegments(
  intervals: Interval[],
  tMin: number,
  tMax: number,
  maxSeries: number,
  geom: ChartGeom,
): { x: number; y: number }[][] {
  return buildIntervalSegments(intervals, it => it.perStream, tMin, tMax, maxSeries, geom);
}

// Второй план «выдано стендом, с паузами»: среднее genThroughput по интервалу
// (Δтокенов/Δвремя по стене), null — обрыв.
export function buildIssuedSegments(
  intervals: Interval[],
  tMin: number,
  tMax: number,
  maxSeries: number,
  geom: ChartGeom,
): { x: number; y: number }[][] {
  return buildIntervalSegments(intervals, it => it.throughput, tMin, tMax, maxSeries, geom);
}

// === «Куда уходит время агентов»: составная полоса долей (ADR-029, v2.3) ===
// Вместо поля «Шаг модели»: на каждом интервале — горизонтальная лента, разделённая на
// 4 сегмента (генерирует/читает промпт/ждёт соседа/в очереди) пропорционально долям.
// «Чистый шаг» общий для окна (считается в buildIntervals), поэтому доли интервалов
// соизмеримы между собой.

// Цвета сегментов (только токены C.*): генерирует — success, читает промпт — info,
// ждёт соседа — accent, в очереди — smoke (нейтральный).
// «Генерирует» (зелёный) и «ждёт соседа» (оранжевый) — различимы в обеих темах.
const TIME_BAR_COLORS = [C.success, C.info, C.accent, C.smoke] as const;

// Нейтральная заливка «простой» (ADR-029, v2.4): движок отвечал, но шагов/запросов
// не было (сумма четырёх = 0). Спокойный утопленный тон C.bgInset — в обеих темах.
// Сплошной (opacity 1), в отличие от разрыва (C.bgInset 0.5 + пунктир): простой ≠ нет данных.
const TIME_BAR_IDLE_COLOR = C.bgInset;

// Подписи сегментов (легенда + считывание): порядок = порядок сегментов полосы.
const TIME_BAR_LABELS = ['генерирует', 'читает промпт', 'ждёт соседа', 'в очереди'] as const;

// Пояснения сегментов (title при наведении на легенду).
const TIME_BAR_TITLES = [
  'модель реально выдаёт токены',
  'стенд читает промпт (prefill)',
  'генерация стоит, пока стенд читает чужой длинный промпт. Много — параллельных агентов слишком много',
  'запрос ещё не принят стендом',
] as const;

// Подпись и пояснение состояния «простой» (нейтральная заливка).
const TIME_IDLE_LABEL = 'простой';
const TIME_IDLE_TITLE = 'движок отвечал, но шагов и запросов не было';

// Полоса интервала «куда уходит время» (ADR-029, v2.4) на x-диапазоне [x0, x1].
// shares — 4 сегмента пропорционально долям; idle — один нейтральный (простой) на всю
// ширину; break — пусто (фон-разрыв рисует renderBreaks). Возвращает {x, w, color}.
export function buildTimeSharesBar(
  state: TimeState,
  x0: number,
  x1: number,
): { x: number; w: number; color: string }[] {
  if (state.kind === 'break') return [];
  if (state.kind === 'idle') return [{ x: x0, w: x1 - x0, color: TIME_BAR_IDLE_COLOR }];
  const order = [state.shares.gen, state.shares.prefill, state.shares.wait, state.shares.queue];
  const out: { x: number; w: number; color: string }[] = [];
  let x = x0;
  for (let i = 0; i < 4; i++) {
    const w = (x1 - x0) * order[i];
    if (w > 0) out.push({ x, w, color: TIME_BAR_COLORS[i] });
    x += w;
  }
  return out;
}

// aria-label SVG «Куда уходит время агентов»: базовая подпись; при hover — состояние
// интервала: доли (в %) / «простой» / «движок не отвечал» (разрыв).
export function timeSharesAriaLabel(
  window: Window,
  hoverTime: string | null,
  hoverState: TimeState | null,
): string {
  const parts = [`Куда уходит время агентов за ${WINDOW_LABEL[window]}`];
  if (hoverTime != null && hoverState != null) {
    if (hoverState.kind === 'break') {
      parts.push(`интервал ${hoverTime}: движок не отвечал`);
    } else if (hoverState.kind === 'idle') {
      parts.push(`интервал ${hoverTime}: простой`);
    } else {
      const pct = (v: number) => `${Math.round(v * 100)} %`;
      const s = hoverState.shares;
      parts.push(`интервал ${hoverTime}: `
        + `генерирует ${pct(s.gen)} · `
        + `читает промпт ${pct(s.prefill)} · `
        + `ждёт соседа ${pct(s.wait)} · `
        + `в очереди ${pct(s.queue)}`);
    }
  }
  return parts.join(', ');
}

// Строка-считывание состояния интервала: « · генерирует X % · … · в очереди W %»
// (shares), « · простой» (idle) или «» (break — строка-итог уже показывает «не отвечал»).
function timeSharesSummary(state: TimeState | null): string {
  if (state == null || state.kind === 'break') return '';
  if (state.kind === 'idle') return ` · ${TIME_IDLE_LABEL}`;
  const pct = (v: number) => `${Math.round(v * 100)} %`;
  const s = state.shares;
  return ` · генерирует ${pct(s.gen)}`
    + ` · читает промпт ${pct(s.prefill)}`
    + ` · ждёт соседа ${pct(s.wait)}`
    + ` · в очереди ${pct(s.queue)}`;
}

// Замкнутая область под линией: по каждому сегменту — вниз к дну, через точки, обратно.
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

// Верх ОБЩЕЙ оси скорости: максимум ОБЕИХ серий (скорость генерации + выдано стендом).
// 0 — если данных нет. Обе серии — на интервалах, ни одна не уходит за верх поля.
export function maxSeriesOf(intervals: Interval[]): number {
  let max = 0;
  for (const it of intervals) {
    if (it.throughput != null && it.throughput > max) max = it.throughput;
    if (it.perStream != null && it.perStream > max) max = it.perStream;
  }
  return max;
}

// Средняя скорость генерации на поток за окно: ΣgenTokens/ΣitlSeconds по всем точкам окна,
// где ОБА приращения на месте. null — нет данных (молчание/рестарт) или Σitl = 0.
export function avgPerStreamOf(samples: ThroughputSample[]): number | null {
  let sumGen = 0, sumItl = 0, any = false;
  for (const s of samples) {
    if (s.genTokens != null && s.itlSeconds != null) {
      sumGen += s.genTokens;
      sumItl += s.itlSeconds;
      any = true;
    }
  }
  return any && sumItl > 0 ? sumGen / sumItl : null;
}

// «Выдано стендом, с паузами» за окно: среднее genThroughput по всем точкам, где оно != null
// (простой входит нулями — серия показывает стенд целиком). null — движок молчал всё окно.
export function avgIssuedOf(samples: ThroughputSample[]): number | null {
  let sum = 0, n = 0;
  for (const s of samples) {
    if (s.throughput != null) { sum += s.throughput; n++; }
  }
  return n > 0 ? sum / n : null;
}

// Пик скорости генерации за окно: максимум perStream по интервалам. 0 — если данных нет.
export function peakPerStreamOf(intervals: Interval[]): number {
  let max = 0;
  for (const it of intervals) {
    if (it.perStream != null && it.perStream > max) max = it.perStream;
  }
  return max;
}

// Средние доли «куда уходит время» за окно: среднее по интервалам, у которых есть
// доли (kind === 'shares'; каждая строка 0..1, сумма 1). null — ни один интервал не
// отдал долей. Легенда без считывания показывает «типовой» интервал окна (средние доли).
export function avgTimeSharesOf(intervals: Interval[]): TimeShares | null {
  let gen = 0, prefill = 0, wait = 0, queue = 0, n = 0;
  for (const it of intervals) {
    if (it.timeShares.kind !== 'shares') continue;
    gen += it.timeShares.shares.gen;
    prefill += it.timeShares.shares.prefill;
    wait += it.timeShares.shares.wait;
    queue += it.timeShares.shares.queue;
    n++;
  }
  if (n === 0) return null;
  return { gen: gen / n, prefill: prefill / n, wait: wait / n, queue: queue / n };
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

// history[] → ThroughputSample[] в пределах окна.
function toSamples(history: LocalModelStats['history'], windowMs: number, now: number): ThroughputSample[] {
  const from = now - windowMs;
  const out: ThroughputSample[] = [];
  for (const h of history ?? []) {
    if (h.t < from || h.t > now) continue;
    out.push({
      t: h.t,
      throughput: h.genThroughput ?? null,
      running: h.running ?? null,
      kvUsage: h.kvUsage ?? null,
      prefixHits: h.prefixHits ?? null,
      prefixQueries: h.prefixQueries ?? null,
      ramHits: h.ramHits ?? null,
      ramQueries: h.ramQueries ?? null,
      preemptions: h.preemptions ?? null,
      genTokens: h.genTokens ?? null,
      itlSeconds: h.itlSeconds ?? null,
      itlCount: h.itlCount ?? null,
      prefillSeconds: h.prefillSeconds ?? null,
      queueSeconds: h.queueSeconds ?? null,
      promptTokens: h.promptTokens ?? null,
    });
  }
  return out;
}

// === Сведение в интервалы (чистые функции, тестируются) ===

// Число интервалов окна: 1 мин — 12 по 5 с, 5 мин — 20 по 15 с, 15 мин — 60 по 15 с,
// 1 ч — 60 по 60 с.
export function intervalCountFor(window: Window): number {
  if (window === '1m') return 12;
  if (window === '5m') return 20;
  if (window === '15m') return 60;
  return 60;
}

// Сетка интервалов: границы на абсолютном времени, кратном шагу, а НЕ на
// tMin = now − window. Правый край — первый кратный шагу момент ≥ tMax
// (ceil); сетка тянется влево ровно на n интервалов. Пока окно плывёт (now
// живёт, см. ThroughputChart), сетка стоит: новый интервал появляется справа,
// старые не пересчитываются. tMax кратный шагу → правый край = tMax (совпадает
// со старым поведением, когда tMin был кратен шагу).
export function intervalGrid(tMin: number, tMax: number, intervalMs: number): [number, number][] {
  const n = Math.max(1, Math.round((tMax - tMin) / intervalMs));
  const rightEdge = Math.ceil(tMax / intervalMs) * intervalMs;
  const grid: [number, number][] = [];
  for (let i = 0; i < n; i++) {
    const tEnd = rightEdge - (n - 1 - i) * intervalMs;
    grid.push([tEnd - intervalMs, tEnd]);
  }
  return grid;
}

// === «Куда уходит время агентов» (ADR-029, v2.3) ===
// Скорость генерации, разложенная на ПОЛЬЗОВАТЕЛЬСКИЕ доли: сколько времени модель
// реально генерирует, сколько стенд читает чужой prefill (генерация стоит), сколько
// запрос ждал в очереди. Все четыре — из уже собираемых счётчиков vLLM; «чистый шаг»
// (базовая длительность прохода без помех) считается на уровне ОКНА, а не интервала.

// Сырые суммы интервала (промежуточный результат первого прохода buildIntervals).
// has* — движок отдал хотя бы одну точку с этой серией; sum* — Σ приращений.
interface RawInterval {
  tStart: number;
  tEnd: number;
  sumTp: number; nTp: number;
  maxKv: number | null;
  sumPH: number; sumPQ: number; hasPH: boolean;
  sumRH: number; sumRQ: number; hasRH: boolean;
  sumPre: number; hasPre: boolean;
  sumGen: number; sumItl: number; hasStream: boolean;
  // «Куда уходит время»: шаги (itl), prefill/очередь (гистограммы), прочитанный промпт.
  sumItlSec: number; sumItlCnt: number; hasStep: boolean;
  sumPrefillSec: number; hasPrefillSec: boolean;
  sumQueueSec: number; hasQueueSec: boolean;
  sumPromptTokens: number; hasPromptTokens: boolean;
  // Движок отвечал в интервале: хотя бы одна non-null из itlSeconds/itlCount/prefillSeconds/
  // queueSeconds. Отличает «простой» (отвечал, суммы 0) от «разрыва» (молчал, всё null).
  hasAnyTime: boolean;
}

// 10-й перцентиль: 10 % значений не выше этого. null — пустой массив.
// Копирует и сортирует (вход не мутируется).
export function percentile10(values: number[]): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const idx = Math.min(sorted.length - 1, Math.floor(0.1 * sorted.length));
  return sorted[idx];
}

// «Чистый шаг» окна (с): базовая длительность прохода без помех. Перцентиль 10 времени
// шага (ΣitlSec/ΣitlCnt) по интервалам, где prefill НЕ шёл (ΔpromptTokens ≈ 0); нет
// таких — среднее за окно (ΣitlSec/ΣitlCnt по всем интервалам с шагами). null — шагов не
// было вовсе. Обоснование выбора (перцентиль vs среднее, fallback) — в ADR-029.
function cleanStepOf(raw: RawInterval[]): number | null {
  const clean: number[] = [];
  let sumItlSec = 0, sumItlCnt = 0;
  for (const r of raw) {
    if (r.hasStep && r.sumItlCnt > 0) {
      sumItlSec += r.sumItlSec;
      sumItlCnt += r.sumItlCnt;
      // prefill не шёл: прочитанного промпта в интервале не было (ΔpromptTokens ≈ 0)
      if (r.sumPromptTokens <= 1e-6) clean.push(r.sumItlSec / r.sumItlCnt);
    }
  }
  if (clean.length > 0) return percentile10(clean) ?? null;
  return sumItlCnt > 0 ? sumItlSec / sumItlCnt : null;
}

// Состояние «куда уходит время» интервала (ADR-029, v2.4).
// gen = ΣitlCnt × чистый шаг; wait = max(0, ΣitlSec − gen); prefill/queue — приращения
// гистограмм. Не-отчётные серии (null) — 0, не null: доля просто не участвует.
// Сумма четырёх > 0 → shares (доли, сумма 1). Сумма = 0 → idle (движок отвечал)
// или break (молчал): простой ≠ нет данных (null = разрыв).
function timeSharesOf(r: RawInterval, cleanStep: number | null): TimeState {
  const gen = cleanStep != null && r.hasStep ? r.sumItlCnt * cleanStep : 0;
  const wait = Math.max(0, r.sumItlSec - gen);
  const prefill = r.hasPrefillSec ? r.sumPrefillSec : 0;
  const queue = r.hasQueueSec ? r.sumQueueSec : 0;
  const total = gen + wait + prefill + queue;
  if (total > 0) {
    return { kind: 'shares', shares: { gen: gen / total, prefill: prefill / total, wait: wait / total, queue: queue / total } };
  }
  return r.hasAnyTime ? { kind: 'idle' } : { kind: 'break' };
}

// Точки → интервалы: выработка — среднее, занято — максимум, попадания/RAM —
// доля из СУММ приращений, вытеснения — сумма.
// Серия null — только когда движок молчал (все точки интервала null) — разрыв;
// отдал, но запросов не было (Σ queries = 0) — несётся предыдущее значение:
// линия не рвётся и новое значение не выдумывается (решение по «рваному» кэшу).
// «Куда уходит время» — ДВУХпроходно: сначала сырые суммы всех интервалов, потом
// «чистый шаг» окна, из него — доли каждого интервала (чистый шаг общий для окна).
export function buildIntervals(samples: ThroughputSample[], tMin: number, tMax: number, intervalMs: number): Interval[] {
  const raw: RawInterval[] = [];
  for (const [tStart, tEnd] of intervalGrid(tMin, tMax, intervalMs)) {
    let sumTp = 0, nTp = 0;
    let maxKv: number | null = null;
    let sumPH = 0, sumPQ = 0, hasPH = false;
    let sumRH = 0, sumRQ = 0, hasRH = false;
    let sumPre = 0, hasPre = false;
    let sumGen = 0, sumItl = 0, hasStream = false;
    // «Куда уходит время»: шаги (itl), prefill/очередь (гистограммы), прочитанный промпт.
    let sumItlSec = 0, sumItlCnt = 0, hasStep = false;
    let sumPrefillSec = 0, hasPrefillSec = false;
    let sumQueueSec = 0, hasQueueSec = false;
    let sumPromptTokens = 0, hasPromptTokens = false;
    let hasAnyTime = false;
    for (const s of samples) {
      if (s.t < tStart || s.t >= tEnd) continue;
      if (s.throughput != null) { sumTp += s.throughput; nTp++; }
      if (s.kvUsage != null && (maxKv == null || s.kvUsage > maxKv)) maxKv = s.kvUsage;
      if (s.prefixHits != null && s.prefixQueries != null) { sumPH += s.prefixHits; sumPQ += s.prefixQueries; hasPH = true; }
      if (s.ramHits != null && s.ramQueries != null) { sumRH += s.ramHits; sumRQ += s.ramQueries; hasRH = true; }
      if (s.preemptions != null) { sumPre += s.preemptions; hasPre = true; }
      // скорость на поток: только когда ОБА приращения точки на месте (иначе дробь не честная)
      if (s.genTokens != null && s.itlSeconds != null) { sumGen += s.genTokens; sumItl += s.itlSeconds; hasStream = true; }
      if (s.itlSeconds != null && s.itlCount != null) { sumItlSec += s.itlSeconds; sumItlCnt += s.itlCount; hasStep = true; }
      if (s.prefillSeconds != null) { sumPrefillSec += s.prefillSeconds; hasPrefillSec = true; }
      if (s.queueSeconds != null) { sumQueueSec += s.queueSeconds; hasQueueSec = true; }
      if (s.promptTokens != null) { sumPromptTokens += s.promptTokens; hasPromptTokens = true; }
      // «Движок отвечал»: хотя бы одна non-null из четырёх серий поля (0 — ответ, null — молчание).
      if (s.itlSeconds != null || s.itlCount != null || s.prefillSeconds != null || s.queueSeconds != null) hasAnyTime = true;
    }
    raw.push({
      tStart, tEnd,
      sumTp, nTp, maxKv,
      sumPH, sumPQ, hasPH,
      sumRH, sumRQ, hasRH,
      sumPre, hasPre,
      sumGen, sumItl, hasStream,
      sumItlSec, sumItlCnt, hasStep,
      sumPrefillSec, hasPrefillSec,
      sumQueueSec, hasQueueSec,
      sumPromptTokens, hasPromptTokens,
      hasAnyTime,
    });
  }
  // Второй проход: «чистый шаг» окна (общий для всех интервалов), из него — доли.
  const cleanStep = cleanStepOf(raw);
  const out: Interval[] = [];
  let prevPrefix: number | null = null;
  let prevRam: number | null = null;
  for (const r of raw) {
    let prefix: number | null;
    if (r.hasPH && r.sumPQ > 0) prefix = r.sumPH / r.sumPQ;
    else if (r.hasPH) prefix = prevPrefix;   // «не спрашивали» — несётся
    else prefix = null;                     // движок молчал — разрыв
    let ram: number | null;
    if (r.hasRH && r.sumRQ > 0) ram = r.sumRH / r.sumRQ;
    else if (r.hasRH) ram = prevRam;
    else ram = null;
    if (prefix != null) prevPrefix = prefix;
    if (ram != null) prevRam = ram;
    // скорость генерации на поток: ΣgenTokens/ΣitlSeconds (измерение vLLM); null — разрыв
    // (молчание/рестарт) или Σitl = 0 (токены не делим на ноль).
    const perStream = r.hasStream && r.sumItl > 0 ? r.sumGen / r.sumItl : null;
    out.push({
      tStart: r.tStart,
      tEnd: r.tEnd,
      throughput: r.nTp > 0 ? r.sumTp / r.nTp : null,
      perStream,
      kvUsage: r.maxKv,
      prefix,
      ram,
      preemptions: r.hasPre ? r.sumPre : null,
      hasData: { prefix: r.hasPH, ram: r.hasRH },
      timeShares: timeSharesOf(r, cleanStep),
    });
  }
  return out;
}

// Кэш за окно (строка-итог): Σ prefixHits / Σ prefixQueries по всем точкам окна.
// Простой сам не попадает: без запросов приращения нулевые. null — запросов не было.
export function cacheHitAvg(samples: ThroughputSample[]): number | null {
  let hits = 0, queries = 0, any = false;
  for (const s of samples) {
    if (s.prefixHits != null && s.prefixQueries != null) {
      hits += s.prefixHits;
      queries += s.prefixQueries;
      any = true;
    }
  }
  return any && queries > 0 ? hits / queries : null;
}

// Сумма вытеснений по окну (строка «за окно N» / «не было»).
export function preemptionTotal(samples: ThroughputSample[]): number {
  let sum = 0;
  for (const s of samples) if (s.preemptions != null) sum += s.preemptions;
  return sum;
}

// Прогоны разрыва: максимальные непрерывные серии интервалов, где throughput == null
// (рестарт движка / стенд лежал). Один прямоугольник на прогон, а не на интервал —
// иначе на стыках интервалов видны швы. Возвращает пары индексов [start, end] включ.
export function breakRuns(intervals: Interval[]): [number, number][] {
  const runs: [number, number][] = [];
  let start = -1;
  for (let i = 0; i < intervals.length; i++) {
    const isBreak = intervals[i].throughput == null;
    if (isBreak && start === -1) start = i;
    if (!isBreak && start !== -1) { runs.push([start, i - 1]); start = -1; }
  }
  if (start !== -1) runs.push([start, intervals.length - 1]);
  return runs;
}

// Прогоны разрыва ВТОРОЙ линии (perStream == null): фон-«разрыв» для линии
// «на агента» — отдельно от фона выработки, чтобы линии не смешивались.
export function perStreamBreakRuns(intervals: Interval[]): [number, number][] {
  const runs: [number, number][] = [];
  let start = -1;
  for (let i = 0; i < intervals.length; i++) {
    const isBreak = intervals[i].perStream == null;
    if (isBreak && start === -1) start = i;
    if (!isBreak && start !== -1) { runs.push([start, i - 1]); start = -1; }
  }
  if (start !== -1) runs.push([start, intervals.length - 1]);
  return runs;
}

// === aria-label областей графика (динамический: обновляется при наведении) ===
// Базовая часть: окно + сводка (avg/кэш). При hover — добавляется интервал с
// деталью того поля, к которому относится SVG. Скринридер слышит смену при
// pointermove / ArrowLeft / ArrowRight, потому что у SVG стоит aria-label и
// строка-считывание над ним (aria-live).

const WINDOW_LABEL: Record<Window, string> = { '1m': '1 минуту', '5m': '5 минут', '15m': '15 минут', '1h': '1 час' };

// Подпись общего контейнера (сводка без привязки к полю). Используется в
// сводке над графиками; SVG получают свои, чтобы скринридеру было понятно,
// какое поле сейчас считывается.
export function chartAriaLabel(window: Window, avg: number | null, hit: number | null): string {
  const parts = [`Выработка и кэш за ${WINDOW_LABEL[window]}`];
  if (avg != null) parts.push(`в среднем ${formatTps(avg)}`);
  if (hit != null) parts.push(`попадания в кэш ${formatPerc(hit)}`);
  return parts.join(', ');
}

// aria-label SVG «Скорость генерации, ток/с»: базовая сводка; при hover — время
// интервала и значение главной серии, или «движок не отвечал» на разрыве.
export function tpChartAriaLabel(
  window: Window, avg: number | null, hit: number | null,
  hoverTime: string | null, hoverPerStream: number | null,
): string {
  const parts = [`Скорость генерации, ток/с за ${WINDOW_LABEL[window]}`];
  if (avg != null) parts.push(`в среднем ${formatTps(avg)}`);
  if (hit != null) parts.push(`кэш ${formatPerc(hit)}`);
  if (hoverTime != null) {
    if (hoverPerStream == null) {
      parts.push(`интервал ${hoverTime}: движок не отвечал`);
    } else {
      parts.push(`интервал ${hoverTime}: скорость генерации ${formatTps(hoverPerStream)}`);
    }
  }
  return parts.join(', ');
}

// aria-label SVG «Кэш, %»: базовая сводка; при hover — попадания и из RAM.
// «—» если движок интервал не отдал (или серии нет).
export function kvChartAriaLabel(
  window: Window, hit: number | null, hasRam: boolean,
  hoverTime: string | null, hoverPrefix: number | null, hoverRam: number | null,
): string {
  const parts = [`Кэш за ${WINDOW_LABEL[window]}`];
  if (hit != null) parts.push(`попадания ${formatPerc(hit)}`);
  if (hoverTime != null) {
    parts.push(`интервал ${hoverTime}: попадания ${formatPerc(hoverPrefix)}${hasRam ? `, из RAM ${formatPerc(hoverRam)}` : ''}`);
  }
  return parts.join(', ');
}

// aria-label SVG «Вытеснения»: сумма за окно; при hover — значение интервала.
export function evChartAriaLabel(
  window: Window, preTotal: number,
  hoverTime: string | null, hoverEv: number | null,
): string {
  const parts = [`Вытеснения за ${WINDOW_LABEL[window]}: ${preTotal > 0 ? `за окно ${preTotal}` : 'не было'}`];
  if (hoverTime != null) parts.push(`интервал ${hoverTime}: ${hoverEv == null ? '—' : hoverEv}`);
  return parts.join(', ');
}

// Показывать ли подпись «данных нет» в зоне отсутствия истории слева.
// Текст рисуется только в поле выработки и только если зона достаточно широкая,
// чтобы подпись влезла без наезда на данные. 40 px — порог по макету Майи.
export function shouldShowNoDataLabel(zoneWidth: number): boolean {
  return zoneWidth > 40;
}

// === Компонент ===

// Окно графика: 1 минута, 5 минут, 15 минут или 1 час.
export type Window = '1m' | '5m' | '15m' | '1h';
export const WINDOW_MS: Record<Window, number> = { '1m': 60_000, '5m': 5 * 60_000, '15m': 15 * 60_000, '1h': 60 * 60_000 };
// Окно по умолчанию — 5 минут (замечание Андрея): у нового пользователя 5m,
// выбор не персистентен, так что дефолт и есть то, что видит первый раз.
export const DEFAULT_WINDOW: Window = '5m';

// Геометрия полей: выработка 96 px, кэш 64 px, «куда уходит время» 48 px,
// вытеснения 32 px. Поле справа 28 px (подписи оси Y), слева 0 (подписи
// «ср.»/«данных нет» рисуются внутри поля). bottom=0: ось времени общая,
// рисуется под последним полем.
const GEOM_TP: ChartGeom = { width: 340, height: 96, left: 0, right: 28, top: 8, bottom: 0 };
const GEOM_KV: ChartGeom = { width: 340, height: 64, left: 0, right: 28, top: 6, bottom: 0 };
const GEOM_TS: ChartGeom = { width: 340, height: 48, left: 0, right: 28, top: 4, bottom: 0 };
const GEOM_EV: ChartGeom = { width: 340, height: 32, left: 0, right: 28, top: 4, bottom: 0 };

// Метки общей оси времени: «−5 / −3 / −1 / сейчас» (5m), «−15 / −10 / −5 / сейчас» (15m),
// «−1 ч / −45 / −30 / −15 / сейчас» (1h). Единица — только на первой метке.
function timeTicks(window: Window): { f: number; label: string }[] {
  if (window === '1m') {
    // Окно короче минуты округления: тики в секундах (−1 мин / −30 с / сейчас).
    return [
      { f: 0, label: '−1 мин' },
      { f: 1 / 2, label: '−30 с' },
      { f: 1, label: 'сейчас' },
    ];
  }
  if (window === '5m') {
    return [0, 1 / 3, 2 / 3, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(5 * (1 - f))}` }));
  }
  if (window === '15m') {
    return [0, 1 / 3, 2 / 3, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(15 * (1 - f))}` }));
  }
  return [0, 1 / 4, 2 / 4, 3 / 4, 1].map(f => ({ f, label: f === 1 ? 'сейчас' : `−${Math.round(60 * (1 - f))}` }));
}

// Пустая заглушка: нет ни одной точки в окне — «нет данных за период».
// Высота — сумма всех полей (скорость, кэш, «куда уходит время», вытеснения) + ось времени.
function EmptyChart() {
  return (
    <div style={{
      height: GEOM_TP.height + GEOM_KV.height + GEOM_TS.height + GEOM_EV.height + SP.sm * 4 + 18,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      background: C.bgInset, borderRadius: R.sm, fontSize: FS.sm, color: C.textMuted,
    }}>
      Нет данных за период
    </div>
  );
}

// Строка-итог / считывания: фиксированная высота, не двигает графики.
// «в среднем … · пик … · кэш …» (кэш — только если есть) или «HH:MM · N ток/с».
function SummaryLine({ text }: { text: string }) {
  return (
    <div aria-live="polite" style={{
      height: FS.sm * 1.45, display: 'flex', alignItems: 'center',
      fontSize: FS.sm, color: C.textSecondary,
      whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
    }}>
      <span style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{text}</span>
    </div>
  );
}

// Подпись поля (слева над полем): FS.xs, weight 600, C.textSecondary.
function FieldLabel({ children, right }: { children: React.ReactNode; right?: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.sm, marginBottom: SP.xxs }}>
      <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>{children}</span>
      {right}
    </div>
  );
}

// «Движок {имя} это не сообщает» — заменяет поле, чьи серии движок не отдал.
function NotReported({ engine }: { engine: string }) {
  return (
    <p style={{ margin: 0, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>
      Движок {engine} это не сообщает
    </p>
  );
}

// Тело карточки: строка-итог + три поля на общей оси времени + ось времени + считывание.
// Каждое поле само решает, рисовать график или NotReported/«не было»: выработка-NotReported
// не убирает кэш (правило спецификации).
// grid — диапазон СЕТОК интервалов (от первого tStart до последнего tEnd): все
// x-координаты считаются от него, а не от [now − window, now] — иначе сетка
// (кратная шагу) и поле (окно) расходились бы до шага вправо/влево.
function CardBody({ samples, intervals, window, width, engine, grid }: {
  samples: ThroughputSample[]; intervals: Interval[]; window: Window; width: number; engine: string;
  grid: { from: number; to: number };
}) {
  const tMin = grid.from;
  const tMax = grid.to;
  // Верх ОБЩЕЙ оси — по максимуму ОБЕИХ серий (обе на интервалах): ни одна не
  // уходит за верх поля.
  // Верх ОБЩЕЙ оси — по пику ГЛАВНОЙ серии (скорость на поток). «Выдано стендом»
  // (total по всем потокам) сейчас не рисуется, поэтому в масштаб не входит: иначе
  // при нескольких агентах total > на-поток, ось раздувалась и линия прижималась к низу.
  const maxT = niceAxisMax(peakPerStreamOf(intervals));
  // «в среднем» — скорость генерации на поток за окно: ΣgenTokens/ΣitlSeconds.
  const avg = avgPerStreamOf(samples);
  const hitAvg = cacheHitAvg(samples);
  const preTotal = preemptionTotal(samples);

  const geomTp: ChartGeom = { ...GEOM_TP, width };
  const geomKv: ChartGeom = { ...GEOM_KV, width };
  const geomTs: ChartGeom = { ...GEOM_TS, width };
  const geomEv: ChartGeom = { ...GEOM_EV, width };

  // Состояния полей: движок не отдал серию → NotReported; вытеснения null → строки нет.
  const tpNotReported = samples.every(s => s.throughput == null);
  const cacheNotReported = samples.every(s => s.kvUsage == null && s.prefixQueries == null && s.ramQueries == null);
  const evNotReported = samples.every(s => s.preemptions == null);
  // «Куда уходит время» нечего показывать, когда движок молчал всё окно (все
  // интервалы — break) — поле не рисуется вовсе. Простой (idle) и доли (shares) — рисуем.
  const tsNotReported = intervals.every(it => it.timeShares.kind === 'break');
  const hasRam = intervals.some(it => it.ram != null);

  // «Куда уходит время»: средние доли за окно (легенда без считывания) — «типовой»
  // интервал. null — ни один интервал не отдал долей.
  const avgShares = avgTimeSharesOf(intervals);

  // Главная линия «скорость генерации»: из интервалов (ΣgenTokens/ΣitlSeconds), с заливкой.
  const tpSegments = useMemo(
    () => buildPerStreamSegments(intervals, tMin, tMax, maxT, geomTp),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [intervals, tMin, tMax, maxT, width],
  );
  const tpPoints = segmentsToPoints(tpSegments);
  const tpAreas = segmentsToArea(tpSegments, geomTp);
  // Поле «Куда уходит время» (ADR-029, v2.4): на каждом интервале — полоса на ВСЮ
  // высоту поля. shares — 4 цветных сегмента; idle — нейтральная заливка «простой»;
  // break — фон-разрыв (renderBreaks). Высота по нагрузке НЕ вводится (ADR-029 v2.3).
  const tsPlotTop = geomTs.top;
  const tsPlotH = GEOM_TS.height - geomTs.top - geomTs.bottom;
  const tsBars = intervals.map(it => {
    const x0 = geomTs.left + (width - geomTs.left - geomTs.right) * ((it.tStart - tMin) / (tMax - tMin));
    const x1 = geomTs.left + (width - geomTs.left - geomTs.right) * ((it.tEnd - tMin) / (tMax - tMin));
    return buildTimeSharesBar(it.timeShares, x0, x1);
  });

  // Считывание: индекс интервала (0..n-1) под курсором/пальцем; null — нет.
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);
  const [pointerDown, setPointerDown] = useState(false);
  const holdTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const clearHold = () => { if (holdTimer.current) { clearTimeout(holdTimer.current); holdTimer.current = null; } };
  useEffect(() => () => clearHold(), []);

  // Клавиатура: ←/→ двигают интервал, Esc сбрасывает. Focus ring (FIELD.borderFocus,
  // 2 px, R.md) — только при фокусе с клавиатуры (matches(':focus-visible')), не от клика.
  const [focusVisible, setFocusVisible] = useState(false);
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
      e.preventDefault();
      const d = e.key === 'ArrowLeft' ? -1 : 1;
      setHoverIdx(prev => prev == null ? (d > 0 ? 0 : intervals.length - 1) : Math.max(0, Math.min(intervals.length - 1, prev + d)));
    } else if (e.key === 'Escape') {
      setHoverIdx(null);
    }
  };

  const pickInterval = (e: React.PointerEvent): number => {
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const x = e.clientX - rect.left;
    const plotW = width - geomTp.left - geomTp.right;
    const f = plotW > 0 ? (x - geomTp.left) / plotW : 0;
    return Math.max(0, Math.min(intervals.length - 1, Math.round(f * (intervals.length - 1))));
  };
  const onPointerMove = (e: React.PointerEvent) => {
    if (!pointerDown && e.pointerType === 'touch') return; // касание без прижатия — не считываем
    setHoverIdx(pickInterval(e));
  };
  const onPointerDown = (e: React.PointerEvent) => { setPointerDown(true); setHoverIdx(pickInterval(e)); };
  const onPointerUp = () => {
    setPointerDown(false);
    clearHold();
    holdTimer.current = setTimeout(() => setHoverIdx(null), 2000);
  };
  const onPointerLeave = () => { setPointerDown(false); clearHold(); setHoverIdx(null); };

  const hover = hoverIdx != null ? intervals[hoverIdx] : null;

  // Строка-итог / считывание
  let summary: string;
  let hoverTime: string | null = null;
  if (hover) {
    const d = new Date(hover.tStart);
    const hh = String(d.getHours()).padStart(2, '0');
    const mm = String(d.getMinutes()).padStart(2, '0');
    const time = window === '5m' ? `${hh}:${mm}:${String(d.getSeconds()).padStart(2, '0')}` : `${hh}:${mm}`;
    hoverTime = time;
    if (hover.timeShares.kind === 'break') {
      // Движок молчал (все серии null) — «не отвечал»; perStream здесь тоже null.
      summary = `${time} · движок не отвечал`;
    } else {
      // «скорость N · <куда уходит время>»: shares — доли, idle — «простой».
      // «—» — скорости в интервале не было (idle: движок отвечал, но шагов не было).
      summary = `${time} · скорость ${hover.perStream != null ? formatTps(hover.perStream) : '—'}`
        + timeSharesSummary(hover.timeShares);
    }
  } else {
    const parts: string[] = [];
    if (avg != null) parts.push(`в среднем ${formatTps(avg)}`);
    const peak = peakPerStreamOf(intervals);
    if (avg != null && peak > 0) parts.push(`пик ${formatTps(peak)}`);
    if (hitAvg != null) parts.push(`кэш ${formatPerc(hitAvg)}`);
    summary = parts.length ? parts.join(' · ') : 'за период генерации не было';
  }

  // Вертикаль считывания: x на центре интервала (общая для всех полей).
  const hoverX = hover
    ? geomTp.left + (width - geomTp.left - geomTp.right) * ((hover.tStart + (hover.tEnd - hover.tStart) / 2 - tMin) / (tMax - tMin))
    : null;

  // Зона «данных нет» слева (история короче окна): от левого края до первой точки.
  const firstT = samples.length ? samples[0].t : null;
  const noDataX = firstT != null ? xForTime(firstT, tMin, tMax, geomTp) : null;

  // Разрывы главной линии (perStream == null): один прямоугольник на непрерывный
  // прогон + пунктирная вертикаль на левой границе. X общий для всех полей (left/right равны).
  const breaks = perStreamBreakRuns(intervals).map(([a, b]) => ({
    x0: xForTime(intervals[a].tStart, tMin, tMax, geomTp),
    x1: xForTime(intervals[b].tEnd, tMin, tMax, geomTp),
  }));

  // «Занято» — область без линии (C.textMuted 0.18), рисуется фоном под линиями.
  const kvArea = useMemo(() => {
    const pts: { x: number; y: number }[] = [];
    for (const it of intervals) {
      if (it.kvUsage == null) continue;
      const cx = geomKv.left + (width - geomKv.left - geomKv.right) * ((it.tStart + (it.tEnd - it.tStart) / 2 - tMin) / (tMax - tMin));
      pts.push({ x: cx, y: yForPerc(it.kvUsage, geomKv) });
    }
    if (pts.length === 0) return null;
    const floor = geomKv.height - geomKv.bottom;
    const body = pts.map(p => `${round1(p.x)},${round1(p.y)}`).join(' L');
    return `M${round1(pts[0].x)},${round1(floor)} L${body} L${round1(pts[pts.length - 1].x)},${round1(floor)} Z`;
  }, [intervals, tMin, tMax, width]);

  // Линии попаданий / из RAM: по одному полилинии на непрерывный прогон (null — обрыв).
  const buildLine = (get: (it: Interval) => number | null): string[] => {
    const segs: { x: number; y: number }[][] = [];
    let cur: { x: number; y: number }[] = [];
    for (const it of intervals) {
      const v = get(it);
      if (v == null) { if (cur.length) { segs.push(cur); cur = []; } continue; }
      const cx = geomKv.left + (width - geomKv.left - geomKv.right) * ((it.tStart + (it.tEnd - it.tStart) / 2 - tMin) / (tMax - tMin));
      cur.push({ x: cx, y: yForPerc(v, geomKv) });
    }
    if (cur.length) segs.push(cur);
    return segs.map(seg => seg.map(p => `${round1(p.x)},${round1(p.y)}`).join(' '));
  };
  const prefixLines = buildLine(it => it.prefix);
  const ramLines = buildLine(it => it.ram);

  // Вытеснения: столбики. Верх оси = max(2, ceil(max)).
  const evMax = Math.max(2, Math.ceil(Math.max(0, ...intervals.map(it => it.preemptions ?? 0))));
  const evBars = intervals
    .map((it, i) => {
      if (it.preemptions == null || it.preemptions === 0) return null;
      const x0 = geomEv.left + (width - geomEv.left - geomEv.right) * (i / intervals.length);
      const x1 = geomEv.left + (width - geomEv.left - geomEv.right) * ((i + 1) / intervals.length);
      const bw = Math.max(1, (x1 - x0) - 1);
      const y = yForCount(it.preemptions, evMax, geomEv);
      const h = (geomEv.height - geomEv.bottom) - y;
      return { x: x0 + 0.5, y, w: bw, h, i };
    })
    .filter((b): b is { x: number; y: number; w: number; h: number; i: number } => b != null);

  // Легенда графика 2: значения интервала (при считывании) или за окно.
  const legendPrefix = hover ? formatPerc(hover.prefix) : (hitAvg != null ? formatPerc(hitAvg) : '—');
  const legendRam = hover ? formatPerc(hover.ram) : (hasRam ? '—' : null);
  const legendKv = hover ? formatPerc(hover.kvUsage) : '—';

  // Легенда графика 1: значение главной серии — интервала (считывание) или за окно.
  // formatTps(null) = '—' (серия в интервале/окне не была).
  const legendPerStream = hover ? formatTps(hover.perStream) : formatTps(avg);
  // Легенда «Куда уходит время»: доли интервала (считывание) или средние за окно.
  // hover.timeShares — TimeState; доли — только если kind === 'shares', иначе «—».
  const legendShares: TimeShares | null = hover
    ? (hover.timeShares.kind === 'shares' ? hover.timeShares.shares : null)
    : avgShares;
  // «простой»: 100 %, если интервал под курсором — простой; иначе «—».
  const legendIdle = hover ? hover.timeShares.kind === 'idle' : false;

  // Ось времени (общая, под последным полем)
  const ticks = timeTicks(window);

  // Зона разрыва: фон C.bgInset 0.5 + пунктирная вертикаль 2 2 на левой границе.
  // Один прямоугольник на непрерывный прогон; рисуется фоном под линиями/заливками.
  const renderBreaks = (geom: ChartGeom, height: number) => (
    <>
      {breaks.map((b, i) => (
        <g key={`brk${i}`}>
          <rect x={b.x0} y={geom.top} width={Math.max(0, b.x1 - b.x0)} height={height - geom.top - geom.bottom}
            fill={C.bgInset} fillOpacity={0.5} />
          <line x1={b.x0} x2={b.x0} y1={geom.top} y2={height - geom.bottom}
            stroke={C.textMuted} strokeWidth={1} strokeDasharray="2 2" />
        </g>
      ))}
    </>
  );

  return (
    <div>
      <SummaryLine text={summary} />
      <div
        tabIndex={0}
        onKeyDown={onKeyDown}
        onFocus={e => setFocusVisible(e.currentTarget.matches(':focus-visible'))}
        onBlur={() => setFocusVisible(false)}
        style={{
          position: 'relative', marginTop: SP.sm, touchAction: 'pan-y', cursor: 'crosshair', minWidth: 0,
          borderRadius: R.md, outline: 'none',
          boxShadow: focusVisible ? `0 0 0 2px ${FIELD.borderFocus}` : 'none',
        }}
        onPointerMove={onPointerMove}
        onPointerDown={onPointerDown}
        onPointerUp={onPointerUp}
        onPointerLeave={onPointerLeave}
      >
        {/* === Поле 1: выработка (или NotReported) === */}
        {tpNotReported
          ? <NotReported engine={engine} />
          : (
            <>
              <FieldLabel>Скорость генерации, ток/с на поток</FieldLabel>
              <svg viewBox={`0 0 ${width} ${GEOM_TP.height}`} width={width} height={GEOM_TP.height} style={{ display: 'block' }}
                aria-label={tpChartAriaLabel(window, avg, hitAvg, hoverTime, hover?.perStream ?? null)}>
                {[0, 0.5, 1].map(f => {
                  const y = yForThroughput(maxT * f, maxT, geomTp);
                  return (
                    <g key={`tpg${f}`}>
                      <line x1={geomTp.left} x2={width - geomTp.right} y1={y} y2={y}
                        stroke={f === 0 ? C.border : C.borderLight} strokeWidth={1} />
                      <text x={width - geomTp.right + 4} y={y + 3} textAnchor="start" fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textMuted}>{formatAxisTps(maxT * f)}</text>
                    </g>
                  );
                })}
                {renderBreaks(geomTp, GEOM_TP.height)}
                {noDataX != null && noDataX > geomTp.left + 1 && (() => {
                  const w = Math.max(0, noDataX - geomTp.left);
                  return (
                    <g>
                      <rect x={geomTp.left} y={geomTp.top} width={w} height={GEOM_TP.height - geomTp.top - geomTp.bottom}
                        fill={C.bgInset} fillOpacity={0.5} />
                      {shouldShowNoDataLabel(w) && (
                        <text x={geomTp.left + w / 2} y={(geomTp.top + GEOM_TP.height - geomTp.bottom) / 2 + 3}
                          textAnchor="middle" fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textMuted}>данных нет</text>
                      )}
                    </g>
                  );
                })()}
                {avg != null && (() => {
                  const first = samples.find(s => s.throughput != null && s.running != null && s.running > 0);
                  if (!first) return null;
                  const y = yForThroughput(avg, maxT, geomTp);
                  const x0 = xForTime(first.t, tMin, tMax, geomTp);
                  return (
                    <g>
                      <line x1={x0} x2={width - geomTp.right} y1={y} y2={y}
                        stroke={C.textSecondary} strokeWidth={1} strokeDasharray="4 3" />
                      <text x={x0 + 2} y={y - 3} fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textSecondary}>ср.</text>
                    </g>
                  );
                })()}
                {tpAreas.map((d, i) => <path key={`tpa${i}`} d={d} fill={C.accent} fillOpacity={0.12} />)}
                {tpPoints.map((pts, i) => (
                  <polyline key={`tpl${i}`} points={pts} fill="none" stroke={C.accent} strokeWidth={1.75} strokeLinejoin="round" strokeLinecap="round" />
                ))}
                {hoverX != null && hover && hover.perStream != null && (() => {
                  const psY = hover.perStream != null ? yForThroughput(hover.perStream, maxT, geomTp) : null;
                  return (
                    <g>
                      <line x1={hoverX} x2={hoverX} y1={geomTp.top} y2={GEOM_TP.height - geomTp.bottom} stroke={C.textMuted} strokeWidth={1} />
                      {psY != null && <circle cx={hoverX} cy={psY} r={3} fill={C.accent} stroke={C.bgCard} strokeWidth={2} />}
                    </g>
                  );
                })()}
              </svg>
              {/* Легенда: главная серия. Значение — интервала (считывание) или за окно.
                  Пояснение — в title (подсказка при наведении). */}
              <div aria-live="polite" style={{
                marginTop: SP.xxs, minHeight: FS.xs * 1.45, display: 'flex', flexWrap: 'wrap', alignItems: 'center',
                columnGap: SP.md, rowGap: SP.xxs, fontSize: FS.xs, color: C.textSecondary,
              }}>
                <span title="сколько токенов в секунду модель выдаёт одному агенту, без чтения промпта и пауз"
                  style={{ display: 'inline-flex', alignItems: 'center', gap: 4, whiteSpace: 'nowrap' }}>
                  <span style={{ width: 10, height: 0, borderTop: `2px solid ${C.accent}` }} />
                  скорость генерации <b style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{legendPerStream}</b>
                </span>
              </div>
            </>
          )}

        {/* === Поле 2: кэш, % (или NotReported) === */}
        <div style={{ marginTop: SP.sm }}>
          {cacheNotReported
            ? <NotReported engine={engine} />
            : (
              <>
                <FieldLabel>Кэш, %</FieldLabel>
                <svg viewBox={`0 0 ${width} ${GEOM_KV.height}`} width={width} height={GEOM_KV.height} style={{ display: 'block' }}
                  aria-label={kvChartAriaLabel(window, hitAvg, hasRam, hoverTime, hover?.prefix ?? null, hover?.ram ?? null)}>
                  {[0, 0.5, 1].map(f => {
                    const y = yForPerc(f, geomKv);
                    return (
                      <g key={`kvg${f}`}>
                        <line x1={geomKv.left} x2={width - geomKv.right} y1={y} y2={y}
                          stroke={f === 0 ? C.border : C.borderLight} strokeWidth={1} />
                        <text x={width - geomKv.right + 4} y={y + 3} textAnchor="start" fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textMuted}>{Math.round(f * 100)}</text>
                      </g>
                    );
                  })}
                  {renderBreaks(geomKv, GEOM_KV.height)}
                  {noDataX != null && noDataX > geomKv.left + 1 && (
                    <rect x={geomKv.left} y={geomKv.top} width={Math.max(0, noDataX - geomKv.left)} height={GEOM_KV.height - geomKv.top - geomKv.bottom}
                      fill={C.bgInset} fillOpacity={0.5} />
                  )}
                  {kvArea && <path d={kvArea} fill={C.textMuted} fillOpacity={0.18} />}
                  {prefixLines.map((pts, i) => (
                    <polyline key={`kpl${i}`} points={pts} fill="none" stroke={C.success} strokeWidth={1.75} strokeLinejoin="round" strokeLinecap="round" />
                  ))}
                  {hasRam && ramLines.map((pts, i) => (
                    <polyline key={`krl${i}`} points={pts} fill="none" stroke={C.info} strokeWidth={1.25} strokeLinejoin="round" strokeLinecap="round" />
                  ))}
                  {hoverX != null && hover && (() => {
                    // Точки 6 px (r=3) на линиях попаданий и «из RAM» под курсором.
                    const dots: React.ReactNode[] = [];
                    if (hover.prefix != null) {
                      const cy = yForPerc(hover.prefix, geomKv);
                      dots.push(<circle key="kdp" cx={hoverX} cy={cy} r={3} fill={C.success} stroke={C.bgCard} strokeWidth={2} />);
                    }
                    if (hasRam && hover.ram != null) {
                      const cy = yForPerc(hover.ram, geomKv);
                      dots.push(<circle key="kdr" cx={hoverX} cy={cy} r={3} fill={C.info} stroke={C.bgCard} strokeWidth={2} />);
                    }
                    return (
                      <g>
                        <line x1={hoverX} x2={hoverX} y1={geomKv.top} y2={GEOM_KV.height - geomKv.bottom} stroke={C.textMuted} strokeWidth={1} />
                        {dots}
                      </g>
                    );
                  })()}
                </svg>
                <div aria-live="polite" style={{
                  marginTop: SP.xxs, height: FS.xs * 1.45, display: 'flex', alignItems: 'center', gap: SP.md,
                  fontSize: FS.xs, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden',
                }}>
                  <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                    <span style={{ width: 6, height: 6, borderRadius: R.full, background: C.success }} />
                    попадания <b style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{legendPrefix}</b>
                  </span>
                  {hasRam && (
                    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                      <span style={{ width: 6, height: 6, borderRadius: R.full, background: C.info }} />
                      из RAM <b style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{legendRam ?? '—'}</b>
                    </span>
                  )}
                  <span
                    title="Это не заполненность памяти: блоки освобождаются и переиспользуются следующими запросами"
                    style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}
                  >
                    <span style={{ width: 8, height: 8, background: C.textMuted, opacity: 0.18, border: `1px solid ${C.border}` }} />
                    занято <b style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{legendKv}</b>
                  </span>
                </div>
              </>
            )}
        </div>

        {/* === Поле 3: «Куда уходит время агентов» — составная полоса долей (ADR-029, v2.3) === */}
        {/* Полоса 100 %: 4 сегмента (генерирует/читает промпт/ждёт соседа/в очереди).
            Ориентир уровня — нижняя базовая линия (как сетка у линейных графиков). */}
        {!tsNotReported && (
          <div style={{ marginTop: SP.sm }}>
            <FieldLabel>Куда уходит время агентов</FieldLabel>
            <svg viewBox={`0 0 ${width} ${GEOM_TS.height}`} width={width} height={GEOM_TS.height} style={{ display: 'block' }}
              aria-label={timeSharesAriaLabel(window, hoverTime, hover?.timeShares ?? null)}>
              <line x1={geomTs.left} x2={width - geomTs.right} y1={GEOM_TS.height - geomTs.bottom} y2={GEOM_TS.height - geomTs.bottom}
                stroke={C.border} strokeWidth={1} />
              {renderBreaks(geomTs, GEOM_TS.height)}
              {noDataX != null && noDataX > geomTs.left + 1 && (
                <rect x={geomTs.left} y={geomTs.top} width={Math.max(0, noDataX - geomTs.left)} height={GEOM_TS.height - geomTs.top - geomTs.bottom}
                  fill={C.bgInset} fillOpacity={0.5} />
              )}
              {tsBars.map((segs, i) => segs.map((s, j) => (
                <rect key={`tsb${i}-${j}`} x={s.x} y={tsPlotTop} width={Math.max(0, s.w)} height={tsPlotH} fill={s.color} />
              )))}
              {hoverX != null && hover && (
                <line x1={hoverX} x2={hoverX} y1={geomTs.top} y2={GEOM_TS.height - geomTs.bottom} stroke={C.textMuted} strokeWidth={1} />
              )}
            </svg>
            {/* Легенда: 4 доли. Значения — интервала (считывание) или средние за окно.
                Пояснения — в title. */}
            <div aria-live="polite" style={{
              marginTop: SP.xxs, minHeight: FS.xs * 1.45, display: 'flex', flexWrap: 'wrap', alignItems: 'center',
              columnGap: SP.md, rowGap: SP.xxs, fontSize: FS.xs, color: C.textSecondary,
            }}>
              {TIME_BAR_LABELS.map((label, i) => {
                const v = legendShares ? [legendShares.gen, legendShares.prefill, legendShares.wait, legendShares.queue][i] : null;
                return (
                  <span key={label} title={TIME_BAR_TITLES[i]}
                    style={{ display: 'inline-flex', alignItems: 'center', gap: 4, whiteSpace: 'nowrap' }}>
                    <span style={{ width: 8, height: 8, background: TIME_BAR_COLORS[i] }} />
                    {label} <b style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{v == null ? '—' : `${Math.round(v * 100)} %`}</b>
                  </span>
                );
              })}
              {/* «простой»: нейтральная заливка; 100 %, когда интервал под курсором — простой. */}
              <span title={TIME_IDLE_TITLE}
                style={{ display: 'inline-flex', alignItems: 'center', gap: 4, whiteSpace: 'nowrap' }}>
                <span style={{ width: 8, height: 8, background: TIME_BAR_IDLE_COLOR, border: `1px solid ${C.border}` }} />
                {TIME_IDLE_LABEL} <b style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading }}>{legendIdle ? '100 %' : '—'}</b>
              </span>
            </div>
          </div>
        )}

        {/* === Поле 5: вытеснения (нет строки, если движок не отдаёт счётчик) === */}
        {!evNotReported && (
          <div style={{ marginTop: SP.sm }}>
            <FieldLabel right={
              <span aria-live="polite" style={{ fontFamily: FONT.mono, fontWeight: 700, color: C.textHeading, fontSize: FS.xs }}>
                {hover ? (hover.preemptions != null ? String(hover.preemptions) : '—') : (preTotal > 0 ? `за окно ${preTotal}` : 'не было')}
              </span>
            }>
              Вытеснения
            </FieldLabel>
            {preTotal > 0 ? (
              <svg viewBox={`0 0 ${width} ${GEOM_EV.height}`} width={width} height={GEOM_EV.height} style={{ display: 'block' }}
                aria-label={evChartAriaLabel(window, preTotal, hoverTime, hover?.preemptions ?? null)}>
                <line x1={geomEv.left} x2={width - geomEv.right} y1={GEOM_EV.height - geomEv.bottom} y2={GEOM_EV.height - geomEv.bottom} stroke={C.border} strokeWidth={1} />
                <text x={width - geomEv.right + 4} y={geomEv.top + 3} textAnchor="start" fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textMuted}>{evMax}</text>
                {renderBreaks(geomEv, GEOM_EV.height)}
                {noDataX != null && noDataX > geomEv.left + 1 && (
                  <rect x={geomEv.left} y={geomEv.top} width={Math.max(0, noDataX - geomEv.left)} height={GEOM_EV.height - geomEv.top - geomEv.bottom}
                    fill={C.bgInset} fillOpacity={0.5} />
                )}
                {evBars.map(b => (
                  <rect key={`evb${b.i}`} x={b.x} y={b.y} width={b.w} height={Math.max(1, b.h)} rx={1}
                    fill={C.danger} fillOpacity={hoverIdx === b.i ? 1 : 0.85} />
                ))}
                {hoverX != null && hover && (
                  <line x1={hoverX} x2={hoverX} y1={geomEv.top} y2={GEOM_EV.height - geomEv.bottom} stroke={C.textMuted} strokeWidth={1} />
                )}
              </svg>
            ) : (
              <div style={{ height: FS.xs * 1.45, display: 'flex', alignItems: 'center', fontSize: FS.xs, color: C.textMuted }}>
                не было
              </div>
            )}
          </div>
        )}

        {/* === Общая ось времени === */}
        <svg viewBox={`0 0 ${width} 18`} width={width} height={18} style={{ display: 'block', marginTop: SP.xxs }}>
          {ticks.map(t => {
            const x = geomTp.left + (width - geomTp.left - geomTp.right) * t.f;
            return (
              <text key={`axt${t.f}`} x={x} y={12}
                textAnchor={t.f === 0 ? 'start' : t.f === 1 ? 'end' : 'middle'}
                fontFamily={FONT.mono} fontSize={FS.xs} fill={C.textMuted}>{t.label}</text>
            );
          })}
        </svg>
      </div>
    </div>
  );
}

// «Сейчас» графика: время последней точки истории (серверные таймштампы),
// не Date.now() клиента. Дата клиента и часы сервера рассинхронизированы
// (контракт: t точек — unix-мс сервера) — при живом Date.now() окно плывёт
// мимо точек: зона «данных нет» слева растёт, а новые точки справа от client-now
// фильтруются вовсе. Последняя точка отстаёт от настоящего «сейчас» не более
// чем на шаг 5 с — для окна от 5 мин это незаметно.
export function chartNow(history: LocalModelStats['history'], fallback: number): number {
  let max = 0;
  for (const h of history ?? []) if (h.t > max) max = h.t;
  return max > 0 ? max : fallback;
}

// Тело карточки «Нагрузка и кэш»: график (или пустая заглушка). Окно — извне
// (состояние держит LocalModelPanel, его же читает серверный опрос).
// Ширина поля — ResizeObserver: SVG рисуется в реальных px, без preserveAspectRatio.
export function ThroughputChart({ history, window, engine }: {
  history: LocalModelStats['history']; window: Window; engine?: string;
}) {
  // now живёт: пересчитывается на каждом опросе (history меняется), окно
  // плывёт вправо; сетка при этом стоит на абсолютных кратных шагу (intervalGrid).
  const now = useMemo(() => chartNow(history, Date.now()), [history]);
  const samples = useMemo(() => toSamples(history, WINDOW_MS[window], now), [history, window, now]);
  const tMin = now - WINDOW_MS[window];
  const tMax = now;
  const intervalMs = Math.round(WINDOW_MS[window] / intervalCountFor(window));
  const intervals = useMemo(
    () => buildIntervals(samples, tMin, tMax, intervalMs),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [samples, tMin, tMax, intervalMs],
  );
  // X-маппинг — на диапазон СЕТОК (не на [tMin, tMax]): сетка начинается на
  // кратном шагу моменте до tMin и кончается после tMax; за его пределами
  // xForTime дал бы координаты вне поля.
  const grid = intervals.length
    ? { from: intervals[0].tStart, to: intervals[intervals.length - 1].tEnd }
    : { from: tMin, to: tMax };

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

  const eng = (engine ?? '').trim() || 'движок';

  return (
    <div ref={wrapRef} style={{ minWidth: 0 }}>
      {samples.length === 0
        ? <EmptyChart />
        : <CardBody samples={samples} intervals={intervals} window={window} width={width} engine={eng} grid={grid} />}
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
        { value: '1m', label: '1 мин' },
        { value: '5m', label: '5 мин' },
        { value: '15m', label: '15 мин' },
        { value: '1h', label: '1 ч' },
      ]}
    />
  );
}
