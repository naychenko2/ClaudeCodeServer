// Полоса «Куда уходит время агентов» на ЖИВЫХ замерах vLLM: база шага одна
// (из ответа бэкенда, stats.cleanStepMs), поэтому доли не зависят от окна и полоса
// не перекрашивается при сдвиге окна.
//
// Фикстур — накопленные счётчики vLLM (шаг замера ~5 с), снятые с живого стенда
// 19.10.2026: t(unix с) itlSecSum itlCntSum promptTokensSum prefillSecSum queueSecSum running.
// Строки накопительные: приращение точки = разность соседних строк.
// Мутации, которые должны красить тесты: «база по окну» (доли 1 мин ≠ 5 мин),
// «шаг базы из окна вместо базы бэкенда», «клетка 5 с в окне 1 мин».

import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  buildIntervals, intervalCountFor, WINDOW_MS,
  type TimeShares, type ThroughputSample, type Window,
} from './ThroughputChart';

const RAW = readFileSync(
  fileURLToPath(new URL('./fixtures/live-samples-2026-10-19.txt', import.meta.url)),
  'utf8',
);

interface LivePoint {
  t: number;            // unix-мс
  itlSeconds: number;   // приращение Σinter_token_latency_seconds
  itlCount: number;     // приращение Σinter_token_latency_seconds_count
  prefillSeconds: number;
  queueSeconds: number;
}

// Точки = приращения между соседними замерами (как точки кольца истории).
const POINTS: LivePoint[] = (() => {
  const rows = RAW.trim().split('\n').map(l => l.trim().split(/\s+/).map(Number));
  const out: LivePoint[] = [];
  for (let i = 1; i < rows.length; i++) {
    const a = rows[i - 1];
    const b = rows[i];
    out.push({
      t: Math.round(b[0] * 1000),
      itlSeconds: b[1] - a[1],
      itlCount: b[2] - a[2],
      prefillSeconds: b[4] - a[4],
      queueSeconds: b[5] - a[5],
    });
  }
  return out;
})();

// База шага: 10-й перцентиль времени шага по живым замерам (медиана 80 мс, максимум
// 1000 мс) — та величина, которую бэкенд кладёт в stats.cleanStepMs. Одна на стенд.
const CLEAN_STEP_MS = 58;

const T_MAX = POINTS[POINTS.length - 1].t;

// Точки окна в виде ThroughputSample (остальные серии в замерах нет — они не нужны
// для долей «куда уходит время»).
function samplesInWindow(tMax: number, windowMs: number): ThroughputSample[] {
  const from = tMax - windowMs;
  return POINTS
    .filter(p => p.t >= from && p.t <= tMax)
    .map(p => ({
      t: p.t,
      throughput: null,
      running: null,
      kvUsage: null,
      prefixHits: null,
      prefixQueries: null,
      ramHits: null,
      ramQueries: null,
      preemptions: null,
      genTokens: null,
      itlSeconds: p.itlSeconds,
      itlCount: p.itlCount,
      prefillSeconds: p.prefillSeconds,
      queueSeconds: p.queueSeconds,
    }));
}

// Интервалы окна с заданной базой шага (мс; null — базы нет).
function intervalsOf(window: Window, tMax: number, cleanStepMs: number | null) {
  const windowMs = WINDOW_MS[window];
  const intervalMs = Math.round(windowMs / intervalCountFor(window));
  return buildIntervals(
    samplesInWindow(tMax, windowMs),
    tMax - windowMs,
    tMax,
    intervalMs,
    cleanStepMs,
  );
}

// Доли интервала (null — интервал без долей: простой/разрыв).
function sharesOf(it: { timeShares: { kind: string } }): TimeShares | null {
  const st = it.timeShares as { kind: string; shares?: TimeShares };
  return st.kind === 'shares' && st.shares ? st.shares : null;
}

// Расхождение двух наборов долей по п.п. (максимум по сегментам).
function maxDiffPct(a: TimeShares, b: TimeShares): number {
  const keys: (keyof TimeShares)[] = ['gen', 'prefill', 'wait', 'queue'];
  return Math.max(...keys.map(k => {
    const x = a[k];
    const y = b[k];
    if (x == null || y == null) return x == null && y == null ? 0 : 100;
    return Math.abs(x - y) * 100;
  }));
}

describe('полоса «куда уходит время» на живых замерах: база шага одна, окно не влияет', () => {
  it('доли последней клетки в окнах 1 мин / 5 мин / 15 мин совпадают', () => {
    // Окна 1 мин, 5 мин и 15 мин делят время на одни и те же 15-секундные клетки
    // (сетка привязана к кратным шагу абсолютным моментам) — поэтому последняя
    // клетка одна и та же, и доли в ней обязаны совпадать.
    const last = (w: Window) => {
      const iv = intervalsOf(w, T_MAX, CLEAN_STEP_MS);
      return sharesOf(iv[iv.length - 1])!;
    };
    const m = last('1m');
    const five = last('5m');
    const fifteen = last('15m');
    expect(m).not.toBeNull();
    expect(maxDiffPct(m, five)).toBeLessThanOrEqual(0.5);
    expect(maxDiffPct(m, fifteen)).toBeLessThanOrEqual(0.5);
    // Доли не нулевые (иначе тест проходил бы на пустых данных)
    expect(m.gen).toBeGreaterThan(0.5);
  });

  it('сдвиг окна на 1 точку (5 с) не перекрашивает полосу: общие клетки — те же доли', () => {
    const a = intervalsOf('1m', T_MAX, CLEAN_STEP_MS);
    const b = intervalsOf('1m', T_MAX - 5_000, CLEAN_STEP_MS);
    const byStart = new Map(b.map(it => [it.tStart, it]));
    expect(a.length).toBe(b.length);
    let common = 0;
    let maxJump = 0;
    for (const it of a) {
      const other = byStart.get(it.tStart);
      if (!other) continue;
      const s1 = sharesOf(it);
      const s2 = sharesOf(other);
      if (!s1 || !s2) continue;
      common++;
      maxJump = Math.max(maxJump, maxDiffPct(s1, s2));
      // Клетка, из которой окно сдвинулось (последняя) потеряла точку — там доли
      // другие; остальные клетки содержат те же замеры ⇒ доли совпадают.
      if (it.tStart !== a[a.length - 1].tStart) {
        expect(maxDiffPct(s1, s2)).toBeLessThanOrEqual(0.05);
      }
    }
    expect(common).toBeGreaterThanOrEqual(3);
    // Полоса не перекрашивается скачком: даже потерявшая точку клетка меняется
    // в пределах 15 п.п. (это данные, а не смена базы).
    expect(maxJump).toBeLessThanOrEqual(15);
  });

  it('клетка 15 с в окне 1 мин: доля prefill в соседних клетках скачет не сильнее, чем при 5 с', () => {
    // vLLM пишет время prefill и очереди при ЗАВЕРШЕНИИ запроса: запрос с длинным
    // prefill целиком попадает в одну клетку. Крупная клетка (15 с) размывает всплеск.
    const maxJumpPrefill = (count: number, intervalMs: number): number => {
      const windowMs = 60_000;
      const iv = buildIntervals(
        samplesInWindow(T_MAX, windowMs),
        T_MAX - windowMs,
        T_MAX,
        intervalMs,
        CLEAN_STEP_MS,
      );
      const vals = iv.map(sharesOf).filter((s): s is TimeShares => s != null);
      let jump = 0;
      for (let i = 1; i < vals.length; i++) {
        jump = Math.max(jump, Math.abs(vals[i].prefill - vals[i - 1].prefill) * 100);
      }
      return jump;
    };
    // Прод-конфигурация окна 1 мин (сколько клеток и какой длины — из intervalCountFor),
    // против прежней: 12 клеток по 5 с.
    const count = intervalCountFor('1m');
    const intervalMs = Math.round(WINDOW_MS['1m'] / count);
    const jumpProd = maxJumpPrefill(count, intervalMs);
    const jumpOld = maxJumpPrefill(12, 5_000);
    expect(jumpOld).toBeGreaterThan(15);         // всплеск prefill есть (данные не пустые)
    // Строго: крупная клетка размывает всплеск, мелкая — нет (живой замер:
    // 69 п.п. на 15 с против 87 п.п. на 5 с). Равенство означало бы, что вернулись
    // 5-секундные клетки.
    expect(jumpProd).toBeLessThan(jumpOld);
  });

  it('окно 1 ч: та же база шага — доли последней клетки (60 с) посчитаны по ней', () => {
    const iv = intervalsOf('1h', T_MAX, CLEAN_STEP_MS);
    const s = sharesOf(iv[iv.length - 1])!;
    // Клетка 1 ч покрывает 60 с, поэтому доли другие, чем у 15-секундной, — но
    // считает их ТА ЖЕ база 58 мс: «генерирует» = ΣitlCnt × база.
    expect(s.gen).toBeCloseTo(0.433, 2);
    expect(s.prefill).toBeCloseTo(0.463, 2);
    expect(s.wait).toBeCloseTo(0.073, 2);
    expect(s.queue).toBeCloseTo(0.031, 2);
  });
});

describe('полоса «куда уходит время» без базы шага: «ждёт соседа» не выделяется', () => {
  it('wait = null (не ноль), остальные доли в сумме 1', () => {
    const iv = intervalsOf('1m', T_MAX, null);
    const withShares = iv.map(sharesOf).filter((s): s is TimeShares => s != null);
    expect(withShares.length).toBeGreaterThan(0);
    for (const s of withShares) {
      expect(s.wait).toBeNull();
      expect(s.gen + s.prefill + s.queue).toBeCloseTo(1, 6);
    }
  });

  it('без базы всё измеренное время декодирования — «генерирует»', () => {
    const iv = intervalsOf('1m', T_MAX, null);
    const withShares = iv.map(sharesOf).filter((s): s is TimeShares => s != null);
    // Первая клетка с данными: 73.5 % prefill при 4 клетках по 15 с — здесь важно,
    // что «генерирует» НЕ обнулилась (иначе доли перестали бы быть в сумме 1).
    expect(withShares[0].gen).toBeGreaterThan(0);
    expect(withShares[0].prefill).toBeGreaterThan(0);
  });
});
