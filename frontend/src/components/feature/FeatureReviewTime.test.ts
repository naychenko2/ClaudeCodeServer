// Тесты секции «Время и срок» (FS-FR1 + FS-FRt): полоса состава (null/0 в неё нет), доли легенды,
// «Прошло» (открытая — elapsed, закрытая — summary.durationMinutes), «Ошибки» с эпизодами,
// «Сдвиг срока с начала» (знак + formatDuration по модулю, 0 → «без сдвига») и «Точность срока»
// («×1,5 от обещанного», одна цифра после запятой); нет stats.estimateDrift — обеих строк нет,
// секция остаётся как в FS-FR1. Ровно две плитки, порядок строк kv и слот шкалы срока.
// Окружение node: разметка — renderToStaticMarkup (счёт и порядок — по html), состояний и
// кликов здесь нет.
//
// Мутации (проверены прогоном): (1) «рисовать вид с null как 0-сегмент» — в kindMinutes
// `?? null` → `?? 0` и в filter `p.minutes > 0` → `p.minutes >= 0`: краснеют «полоса из трёх
// частей» и «null и 0 в полосу нет» (появляется 0-сегмент queued); после отката зелёные.
// (2) closed-ветка «Прошло» убрана (всегда elapsedMinutes): краснеет «закрытая — итог
// summary»; (3) знаменатель долей — elapsedMinutes вместо суммы видимых видов
// (FeatureReviewTime.tsx:120 `p.minutes / sum` → `p.minutes / elapsed`): краснеет
// «approx (waiting/idle null) — только ненулевые виды» (ниже): при фикстуре 90/100
// доля стала бы 90 % вместо ожидаемых 100 %. «доли 60/30/10» остаётся зелёным:
// в фикстуре C1 сумма видимых видов равна elapsed (120+60+20=200), знаменатель не меняется.
// (4) FS-FRt: «показывать accuracy null как ×0» (FeatureReviewTime.tsx:91 `: null` →
// `: '×0,0 от обещанного'`): краснеют «shiftMinutes null и accuracy null», «estimateDrift null
// — …» и «старый ответ без поля» — 3 теста; после отката зелёные.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReviewTime } from './FeatureReviewTime';
import { formatDuration } from './FeatureStatsBits';
import { COMP_LABEL } from './FeatureStatsPulseModel';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureEstimateDrift, FeatureStats, FeatureTimeline } from '../../types/featureStats';

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

function mkStats(totals: Partial<FeatureTimeline['totals']> = {}, over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: { from: '2026-10-20T10:00:00Z', to: '2026-10-20T16:00:00Z', stages: [], segments: [], current: null, totals: mkTotals(totals) },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

// Дрейф оценок (величины 4, 7, 8): сдвиг +300 мин (5 ч), точность ×1,5.
function mkDrift(over: Partial<FeatureEstimateDrift> = {}): FeatureEstimateDrift {
  return {
    initialEtaMinutes: 600, firstEtaMinutes: 600, currentEtaMinutes: 900, shiftMinutes: 300,
    denominatorChanges: 0, accuracy: 1.5, ...over,
  };
}

const html = (run: FeatureRun, stats: FeatureStats, etaScale?: unknown): string =>
  renderToStaticMarkup(createElement(FeatureReviewTime, { run, stats, etaScale: etaScale as never }));

// Фикстура c1: работа 120, ожидание 60, очередь null, ошибки 0, простой 20 → 200 видимых.
const C1 = { workWallMinutes: 120, waitingMinutes: 60, queuedMinutes: null, errorMinutes: 0, errorCount: 0, idleMinutes: 20, elapsedMinutes: 200, agentsSumMinutes: 150 };

const segCount = (h: string): number => (h.match(/data-testid="review-time-seg"/g) ?? []).length;
// Плитки: сам якорь `tile-<key>` (без `-big`/`-hint` — они со дефисом и в шаблон не входят).
const tileCount = (h: string): number => (h.match(/data-testid="tile-[a-z]+"/g) ?? []).length;

// Пары «ключ — значение» всех строк ReviewKv в порядке html (строки с value=null
// в html нет — ReviewKv их не рендерит).
const kvPairs = (h: string): [string, string][] =>
  [...h.matchAll(/data-testid="review-kv-row"[^>]*><span[^>]*>([^<]*)<\/span><span[^>]*>([^<]*)<\/span>/g)]
    .map((m) => [m[1], m[2]] as [string, string]);
const kvValue = (h: string, label: string): string | undefined => kvPairs(h).find((p) => p[0] === label)?.[1];

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

describe('«Сдвиг срока с начала» и «Точность срока» (c1)', () => {
  it('shiftMinutes 300 → «+5 ч», accuracy 1.5 → «×1,5 от обещанного»', () => {
    const h = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift() }));
    expect(kvValue(h, 'Сдвиг срока с начала')).toBe('+5 ч');
    expect(kvValue(h, 'Точность срока')).toBe('×1,5 от обещанного');
  });

  it('отрицательный сдвиг — знак и модуль: −40 → «−40 мин»', () => {
    const h = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift({ shiftMinutes: -40 }) }));
    expect(kvValue(h, 'Сдвиг срока с начала')).toBe('−40 мин');
  });

  it('сдвиг 0 — «без сдвига»: строка есть, это факт, а не «нет данных»', () => {
    const h = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift({ shiftMinutes: 0 }) }));
    expect(kvValue(h, 'Сдвиг срока с начала')).toBe('без сдвига');
  });

  it('сдвиг за порог дней — тот же formatDuration по модулю: −3000 → «−2 дн»', () => {
    const h = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift({ shiftMinutes: -3000 }) }));
    expect(kvValue(h, 'Сдвиг срока с начала')).toBe(`−${formatDuration(3000)}`); // «−2 дн»
  });

  it('точность — одна цифра после запятой, запятая: 2 → «×2,0», 0.8 → «×0,8»', () => {
    const h2 = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift({ accuracy: 2 }) }));
    expect(kvValue(h2, 'Точность срока')).toBe('×2,0 от обещанного');
    const h08 = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift({ accuracy: 0.8 }) }));
    expect(kvValue(h08, 'Точность срока')).toBe('×0,8 от обещанного');
  });

  it('shiftMinutes null и accuracy null — обеих строк нет (открытой бэк точность не даёт)', () => {
    const h = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift({ shiftMinutes: null, accuracy: null }) }));
    expect(h).not.toContain('Сдвиг срока');
    expect(h).not.toContain('Точность срока');
  });

  it('порядок строк: «Прошло» → «Ошибки» → «Сдвиг срока с начала» → «Точность срока»', () => {
    const h = html(mkRun(), mkStats({ ...C1, errorMinutes: 30, errorCount: 2 }, { estimateDrift: mkDrift() }));
    expect(kvPairs(h).map((p) => p[0])).toEqual(['Прошло', 'Ошибки', 'Сдвиг срока с начала', 'Точность срока']);
  });

  it('строки дрейфа не трогают полосу и шкалу: сегментов те же 3, полоса и etaScale — после kv', () => {
    const h = html(mkRun(), mkStats(C1, { estimateDrift: mkDrift() }), createElement('div', { 'data-testid': 'eta-scale' }, 'шкала'));
    expect(segCount(h)).toBe(3);
    const iKv = h.indexOf('data-testid="review-kv"');
    const iBar = h.indexOf('data-testid="review-time-bar"');
    const iEta = h.indexOf('data-testid="eta-scale"');
    expect(iKv).toBeGreaterThanOrEqual(0);
    expect(iKv).toBeLessThan(iBar);
    expect(iBar).toBeLessThan(iEta);
  });
});

describe('нет estimateDrift — обеих строк нет (c2)', () => {
  it('estimateDrift null — строк нет, секция как в FS-FR1: ровно 2 плитки, полоса и шкала на месте', () => {
    const h = html(mkRun(), mkStats(C1, { estimateDrift: null }), createElement('div', { 'data-testid': 'eta-scale' }, 'шкала'));
    expect(h).not.toContain('Сдвиг срока');
    expect(h).not.toContain('Точность срока');
    expect(tileCount(h)).toBe(2);
    expect(segCount(h)).toBe(3);
    expect(h).toContain('data-testid="eta-scale"');
  });

  it('старый ответ без поля (undefined) — той же двух строк нет', () => {
    const h = html(mkRun(), mkStats(C1));
    expect(h).not.toContain('Сдвиг срока');
    expect(h).not.toContain('Точность срока');
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
