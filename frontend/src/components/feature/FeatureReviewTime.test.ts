// Тесты секции «Время и срок» (FS-FR1): полоса состава (null/0 в неё нет), доли легенды,
// «Прошло» (открытая — elapsed, закрытая — summary.durationMinutes), «Ошибки» с эпизодами,
// ровно две плитки, порядок секции и слот шкалы срока. Окружение node: разметка —
// renderToStaticMarkup (счёт и порядок — по html), состояний и кликов здесь нет.
//
// Мутации (проверены прогоном): (1) «рисовать вид с null как 0-сегмент» — в kindMinutes
// `?? null` → `?? 0` и в filter `p.minutes > 0` → `p.minutes >= 0`: краснеют «полоса из трёх
// частей» и «null и 0 в полосу нет» (появляется 0-сегмент queued); после отката зелёные.
// (2) closed-ветка «Прошло» убрана (всегда elapsedMinutes): краснеет «закрытая — итог
// summary»; (3) знаменатель долей — elapsedMinutes вместо суммы видимых видов
// (FeatureReviewTime.tsx:98 `p.minutes / sum` → `p.minutes / elapsed`): краснеет
// «approx (waiting/idle null) — только ненулевые виды» (ниже): при фикстуре 90/100
// доля стала бы 90 % вместо ожидаемых 100 %. «доли 60/30/10» остаётся зелёным:
// в фикстуре C1 сумма видимых видов равна elapsed (120+60+20=200), знаменатель не меняется.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReviewTime } from './FeatureReviewTime';
import { formatDuration } from './FeatureStatsBits';
import { COMP_LABEL } from './FeatureStatsPulseModel';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

function mkTotals(over: Partial<FeatureTimeline['totals']> = {}): FeatureTimeline['totals'] {
  return {
    elapsedMinutes: 0,
    workWallMinutes: 0,
    agentsSumMinutes: 0,
    notWorkingMinutes: 0,
    waitingMinutes: null,
    idleMinutes: null,
    waitingCount: null,
    longestWaitMinutes: null,
    errorCount: null,
    errorMinutes: null,
    ...over,
  };
}

function mkStats(totals: Partial<FeatureTimeline['totals']> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: { from: '2026-10-20T10:00:00Z', to: '2026-10-20T16:00:00Z', stages: [], segments: [], current: null, totals: mkTotals(totals) },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
  };
}

const html = (run: FeatureRun, stats: FeatureStats, etaScale?: unknown): string =>
  renderToStaticMarkup(createElement(FeatureReviewTime, { run, stats, etaScale: etaScale as never }));

// Фикстура c1: работа 120, ожидание 60, очередь null, ошибки 0, простой 20 → 200 видимых.
const C1 = { workWallMinutes: 120, waitingMinutes: 60, queuedMinutes: null, errorMinutes: 0, errorCount: 0, idleMinutes: 20, elapsedMinutes: 200, agentsSumMinutes: 150 };

const segCount = (h: string): number => (h.match(/data-testid="review-time-seg"/g) ?? []).length;

describe('полоса состава и легенда (c1)', () => {
  const h = html(mkRun(), mkStats(C1));

  it('вид с null (queued) и 0 (error) в полосу не попадает — ровно 3 части', () => {
    expect(segCount(h)).toBe(3);
    expect(h).not.toContain('data-seg="queued"');
    expect(h).not.toContain('data-seg="error"');
  });

  it('порядок видов на полосе: работа → ожидание → простой', () => {
    const iw = h.indexOf('data-seg="work"');
    const ia = h.indexOf('data-seg="waiting"');
    const ii = h.indexOf('data-seg="idle"');
    expect(iw).toBeGreaterThanOrEqual(0);
    expect(iw).toBeLessThan(ia);
    expect(ia).toBeLessThan(ii);
  });

  it('подписи легенды — из COMP_LABEL, длительности — formatDuration', () => {
    expect(h).toContain(COMP_LABEL.work);
    expect(h).toContain(COMP_LABEL.waiting);
    expect(h).toContain(COMP_LABEL.idle);
    expect(h).toContain(formatDuration(120)); // 2 ч
    expect(h).toContain(formatDuration(60)); // 1 ч
    expect(h).toContain(formatDuration(20)); // 20 мин
  });

  it('доли — от суммы видимых видов: 60 / 30 / 10 %, в сумме 100', () => {
    expect(h).toContain('60 %');
    expect(h).toContain('30 %');
    expect(h).toContain('10 %');
    const percents = [...h.matchAll(/(\d+) %/g)].map((m) => Number(m[1]));
    expect(percents.reduce((a, b) => a + b, 0)).toBe(100);
  });

  it('легенда переносится (мобила 360): flex-wrap wrap', () => {
    expect(h).toContain('flex-wrap:wrap');
  });
});

describe('«Прошло» и «Ошибки» (c2)', () => {
  it('открытая фича — «Прошло» из totals.elapsedMinutes', () => {
    const h = html(mkRun(), mkStats(C1));
    expect(h).toContain('Прошло');
    expect(h).toContain(formatDuration(200)); // 3 ч 20 мин
  });

  it('закрытая фича — «Прошло» = formatDuration(run.summary.durationMinutes)', () => {
    const h = html(
      mkRun({ state: 'closed', closed: true, summary: { durationMinutes: 900, tasks: 4, steps: 2 } }),
      mkStats(C1),
    );
    expect(h).toContain(formatDuration(900)); // 15 ч
    expect(h).not.toContain(formatDuration(200));
  });

  it('закрытая без summary (старый ответ) — терпимый откат к elapsedMinutes', () => {
    const h = html(mkRun({ state: 'closed', closed: true, summary: null }), mkStats(C1));
    expect(h).toContain(formatDuration(200));
  });

  it('«Ошибки» — «N мин · K эпизодов» со склонением', () => {
    const h = html(mkRun(), mkStats({ ...C1, errorMinutes: 30, errorCount: 2 }));
    expect(h).toContain('30 мин · 2 эпизода');
    const h5 = html(mkRun(), mkStats({ ...C1, errorMinutes: 125, errorCount: 5 }));
    expect(h5).toContain('2 ч 05 мин · 5 эпизодов');
    const h1 = html(mkRun(), mkStats({ ...C1, errorMinutes: 5, errorCount: 1 }));
    expect(h1).toContain('5 мин · 1 эпизод');
  });

  it('нулей и null нет строки: «Ошибки» в DOM нет, когда минут и эпизодов нет', () => {
    const h = html(mkRun(), mkStats(C1));
    expect(h).not.toContain('Ошибки');
  });

  it('эпизоды без минут (errorMinutes null) — только число', () => {
    const h = html(mkRun(), mkStats({ ...C1, errorMinutes: null, errorCount: 3 }));
    expect(h).toContain('3 эпизода');
  });
});

describe('плитки (c2)', () => {
  it('ровно две плитки: «Работали» и «Ждали вас», «Возвратов» и «Расхода» нет', () => {
    const h = html(mkRun(), mkStats(C1));
    expect(h).toContain('data-testid="tile-work"');
    expect(h).toContain('data-testid="tile-waiting"');
    expect(h).not.toContain('data-testid="tile-loops"');
    expect(h).not.toContain('data-testid="tile-spend"');
  });
});

describe('нет состава — нет полосы', () => {
  it('все видимые виды null или 0 — полосы и легенды нет', () => {
    const h = html(mkRun(), mkStats({ elapsedMinutes: 10 }));
    expect(h).not.toContain('review-time-bar');
    expect(h).not.toContain('review-time-legend');
  });

  it('approx (waiting/idle null) — только ненулевые виды', () => {
    const h = html(mkRun(), mkStats({ workWallMinutes: 90, elapsedMinutes: 100 }));
    expect(segCount(h)).toBe(1);
    expect(h).toContain('data-seg="work"');
    expect(h).toContain('100 %');
  });
});

describe('порядок секции и слот шкалы срока', () => {
  it('плитки → «ключ — значение» → полоса → легенда → шкала срока (etaScale последней)', () => {
    const h = html(mkRun(), mkStats(C1), createElement('div', { 'data-testid': 'eta-scale' }, 'шкала'));
    const order = [
      'data-testid="stats-tiles"',
      'data-testid="review-kv"',
      'data-testid="review-time-bar"',
      'data-testid="review-time-legend"',
      'data-testid="eta-scale"',
    ].map((s) => h.indexOf(s));
    for (let i = 1; i < order.length; i++) {
      expect(order[i - 1], `«${i - 1}» раньше «${i}»`).toBeGreaterThanOrEqual(0);
      expect(order[i - 1]).toBeLessThan(order[i]);
    }
  });

  it('без etaScale пропа в DOM нет ничего лишнего', () => {
    const h = html(mkRun(), mkStats(C1));
    expect(h).not.toContain('eta-scale');
  });
});
