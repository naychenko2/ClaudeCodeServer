// Плитки «Пульса» (ST-F5). Рендер-тесты на fakeDom: состав сетки, отсутствие «Расхода»
// без stats.spend, мобильная сетка 2 колонки, ряд ✓/↩ по учтённым виткам и склонение
// числительных. Числа не пересчитываем — проверяем, что рендер отдаёт ровно то, что
// дал tilesModel (ST-F3).
//
// Покрытие:
//   · четыре плитки с testid tile-work/tile-waiting/tile-loops/tile-spend, подпись
//     «агенты суммарно» (к1);
//   · spend = null → плитки «Расход» в DOM нет, в сетке три карточки (к1);
//   · isMobile → gridTemplateColumns repeat(2, 1fr), десктоп — repeat(4, 1fr) (к2);
//   · ряд витков: значок на каждый учтённый ход (returned → ↩, принятые → ✓), withdrawn
//     и running не показываются; их число равно числу таких ходов в фикстуре (к3);
//   · склонение: «1 возврат» / «2 возврата» / «5 возвратов», «1 токен» / «2 токена» /
//     «5 токенов», «1 раз» / «2 раза» (к3);
//   · число плитки — FONT.serif, FS.h2 (на 360 — FS.xl), как в макете §Токены.
//
// Мутации (проверены прогоном): (1) плитка «Расход» добавляется всегда (убрать guard
// `if (spend)` в FeatureStatsPulseModel.ts:118 и пушить безусловно) — краснеет
// DOM-утверждение `findNodeByAttr(..., 'tile-spend')` в тесте «без spend нет плитки
// „Расход"»; (2) `isMobile ? 'repeat(2, 1fr)'` → `'repeat(4, 1fr)'` — краснеет тест
// мобильной сетки; (3) `roundMark` возвращает 'ok' и для 'withdrawn' — краснеет тест
// числа значков; (4) `plural` (lib/plural.ts) без ветки mod10 === 1 — краснеет тест
// «1 возврат»; (5) `tile.count == null` → `tile.count ?? 0` — краснеет тест
// «ожиданий не считалось».

import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { FeatureStatsTiles } from './FeatureStatsTiles';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureRound, FeatureStepChain } from '../../types/featureRun';
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

// Полноценный таймлайн: работа 1 ч 40 мин, ожидания 1 ч, агенты 2 ч.
function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T16:00:00Z',
      stages: [],
      segments: ['cut', 'prod'].map((stage, i) => ({
        from: `2026-10-20T1${i}:00:00Z`,
        to: `2026-10-20T1${i}:30:00Z`,
        kind: 'work' as const,
        track: 'main' as const,
        stage,
        taskId: null,
      })),
      current: null,
      totals: mkTotals({
        elapsedMinutes: 200,
        workWallMinutes: 100,
        agentsSumMinutes: 120,
        waitingMinutes: 60,
        waitingCount: 2,
        longestWaitMinutes: 34,
      }),
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

function mkSpend(over: Partial<FeatureStats['spend']> = {}): FeatureStats['spend'] {
  return {
    inputTokens: 1000,
    outputTokens: 500,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    costUsd: 21.8,
    costPartial: false,
    hiddenCostModels: [],
    from: '2026-10-20T10:00:00Z',
    shared: null,
    ...over,
  };
}

// Цепочка шага с витками заданных вердиктов.
function mkChain(verdicts: FeatureRound['verdict'][]): FeatureStepChain {
  return {
    stepRef: 'Ш1',
    title: 'Шаг',
    taskIds: ['t-1'],
    outcome: 'accepted',
    status: 'принят',
    durationMinutes: 40,
    loops: verdicts.filter(v => v === 'returned' || v === 'accepted_with_remarks').length,
    rounds: verdicts.map((verdict, i) => ({ n: i + 1, verdict })),
    chatId: null,
  };
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

function mountTiles(run: FeatureRun, stats: FeatureStats, isMobile?: boolean) {
  return mount(createElement(FeatureStatsTiles, { run, stats, isMobile }));
}

function grid(container: ReturnType<typeof mount>['container']) {
  return findNodeByAttr(container, 'data-testid', 'stats-tiles');
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('состав плиток (к1)', () => {
  it('со spend — четыре плитки: Работали, Ждали вас, Возвраты, Расход', () => {
    const { container } = mountTiles(mkRun(), mkStats({ spend: mkSpend() }));
    const g = grid(container);
    expect(g, 'сетка плиток не найдена').toBeTruthy();
    expect(g.children.length).toBe(4);
    for (const key of ['work', 'waiting', 'loops', 'spend']) {
      expect(findNodeByAttr(g, 'data-testid', `tile-${key}`), `плитка ${key} отсутствует`).toBeTruthy();
    }
    // Числа — как дал бэк: работа 1 ч 40 мин, ожидание 1 ч, возвраты 2.
    expect(textOf(findNodeByAttr(g, 'data-testid', 'tile-work-big'))).toBe('1 ч 40 мин');
    expect(textOf(findNodeByAttr(g, 'data-testid', 'tile-work-hint'))).toContain('агенты суммарно 2 ч');
    expect(textOf(findNodeByAttr(g, 'data-testid', 'tile-spend-big'))).toBe('$21,80');
  });

  it('без stats.spend — плитки «Расход» в DOM нет, в сетке три карточки', () => {
    const { container } = mountTiles(mkRun(), mkStats());
    const g = grid(container);
    expect(findNodeByAttr(g, 'data-testid', 'tile-spend')).toBeNull();
    expect(g.children.length).toBe(3);
  });

  it('agentsSum нет — «не считалось», а не ноль', () => {
    const stats = mkStats({ spend: mkSpend() });
    stats.timeline.totals.agentsSumMinutes = null;
    const { container } = mountTiles(mkRun(), stats);
    const hint = textOf(findNodeByAttr(container, 'data-testid', 'tile-work-hint'));
    expect(hint).toContain('агенты суммарно — не считалось');
    expect(hint).not.toMatch(/агенты суммарно 0 мин/);
  });
});

describe('ожидания: null и 0 разведены (вердикт, п.1)', () => {
  it('waitingCount null — «ожиданий не считалось», а не «не было»', () => {
    // Approx-режим бэка: waitingCount = null (в полной фикстуре — 2, здесь сбрасываем).
    const stats = mkStats();
    stats.timeline.totals.waitingCount = null;
    const { container } = mountTiles(mkRun(), stats);
    const hint = textOf(findNodeByAttr(container, 'data-testid', 'tile-waiting-hint'));
    expect(hint).toBe('ожиданий не считалось');
  });

  it('waitingCount 0 — «ожиданий не было»', () => {
    const stats = mkStats();
    stats.timeline.totals.waitingCount = 0;
    const { container } = mountTiles(mkRun(), stats);
    const hint = textOf(findNodeByAttr(container, 'data-testid', 'tile-waiting-hint'));
    expect(hint).toBe('ожиданий не было');
  });
});

describe('раскладка (к2)', () => {
  it('десктоп: repeat(4, 1fr)', () => {
    const { container } = mountTiles(mkRun(), mkStats({ spend: mkSpend() }));
    const style = grid(container)!.style as Record<string, string>;
    expect(style.gridTemplateColumns).toBe('repeat(4, 1fr)');
  });

  it('360 (isMobile): repeat(2, 1fr)', () => {
    const { container } = mountTiles(mkRun(), mkStats({ spend: mkSpend() }), true);
    const style = grid(container)!.style as Record<string, string>;
    expect(style.gridTemplateColumns).toBe('repeat(2, 1fr)');
  });

  it('число плитки — FONT.serif, FS.h2 на десктопе и FS.xl на 360', () => {
    const desktop = mountTiles(mkRun(), mkStats()).container;
    const big = findNodeByAttr(desktop, 'data-testid', 'tile-work-big').style as Record<string, string>;
    expect(big.fontFamily).toBe(FONT.serif);
    expect(big.fontSize).toBe(`${FS.h2}px`);
    expect(big.fontWeight).toBe('700'); // макет .a-kpi .n: font:700 …

    const mobile = mountTiles(mkRun(), mkStats(), true).container;
    const mBig = findNodeByAttr(mobile, 'data-testid', 'tile-work-big').style as Record<string, string>;
    expect(mBig.fontSize).toBe(`${FS.xl}px`);
  });

  it('карточка — токены дизайн-системы: bgCard, borderLight, R.lg', () => {
    const { container } = mountTiles(mkRun(), mkStats());
    const tile = findNodeByAttr(container, 'data-testid', 'tile-work').style as Record<string, string>;
    expect(tile.background).toBe(C.bgCard);
    expect(tile.border).toBe(`1px solid ${C.borderLight}`);
    expect(tile.borderRadius).toBe('10px');
  });
});

describe('ряд ✓/↩ в «Возвраты» (к3)', () => {
  it('значок на каждый учтённый ход: returned → ↩, принятые → ✓; withdrawn и running — нет', () => {
    const run = mkRun({
      verdicts: { accepted: 1, acceptedWithRemarks: 1, returned: 1, withdrawn: 1, loops: 2 },
      stepChains: [mkChain(['returned', 'accepted', 'accepted_with_remarks', 'withdrawn', 'running'])],
    });
    const { container } = mountTiles(run, mkStats());
    const rounds = findNodeByAttr(container, 'data-testid', 'tile-rounds');
    expect(rounds, 'ряд витков отсутствует').toBeTruthy();
    // В фикстуре 5 витков, из них учтённых (по tilesModel) — 3: ↩, ✓, ✓.
    const marks = rounds.children.map(c => textOf(c));
    expect(marks.length).toBe(3);
    expect(marks.filter(m => m === '↩').length).toBe(1);
    expect(marks.filter(m => m === '✓').length).toBe(2);
    // Тон значков — из токенов вердиктов макета.
    const first = rounds.children[0].style as Record<string, string>;
    expect(first.background).toBe(C.dangerBg);
    expect(first.color).toBe(C.dangerText);
    const second = rounds.children[1].style as Record<string, string>;
    expect(second.background).toBe(C.successBg);
    expect(second.color).toBe(C.successText);
  });

  it('стиль значка по макету: 14×14, R.sm, FS.xxs, font-style normal; ряд — отступ SP.sm', () => {
    const run = mkRun({
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 1, withdrawn: 0, loops: 1 },
      stepChains: [mkChain(['returned'])],
    });
    const { container } = mountTiles(run, mkStats());
    const rounds = findNodeByAttr(container, 'data-testid', 'tile-rounds');
    const mark = rounds.children[0].style as Record<string, string>;
    expect(mark.width).toBe('14px');
    expect(mark.height).toBe('14px');
    expect(mark.borderRadius).toBe(`${R.sm}px`);
    expect(mark.fontSize).toBe(`${FS.xxs}px`);
    expect(mark.fontStyle).toBe('normal');
    expect(rounds.style.marginTop).toBe(`${SP.sm}px`);
  });

  it('все витки withdrawn/running — ряда значков нет', () => {
    const run = mkRun({ stepChains: [mkChain(['withdrawn', 'running'])] });
    const { container } = mountTiles(run, mkStats());
    expect(findNodeByAttr(container, 'data-testid', 'tile-rounds')).toBeNull();
  });

  it('без цепочек — ряда нет, число 0 и «возвратов не было»', () => {
    const { container } = mountTiles(mkRun(), mkStats());
    expect(findNodeByAttr(container, 'data-testid', 'tile-rounds')).toBeNull();
    expect(textOf(findNodeByAttr(container, 'data-testid', 'tile-loops-big'))).toBe('0');
    expect(textOf(findNodeByAttr(container, 'data-testid', 'tile-loops-hint'))).toContain('возвратов не было');
  });
});

describe('склонение числительных (к3)', () => {
  for (const [n, word] of [[1, '1 возврат'], [2, '2 возврата'], [5, '5 возвратов'], [11, '11 возвратов']] as const) {
    it(`${n} — «${word}»`, () => {
      const run = mkRun({
        verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: n, withdrawn: 0, loops: n },
        stepChains: [mkChain(Array(n).fill('returned'))],
      });
      const { container } = mountTiles(run, mkStats());
      expect(textOf(findNodeByAttr(container, 'data-testid', 'tile-loops-big'))).toBe(String(n));
      const hint = textOf(findNodeByAttr(container, 'data-testid', 'tile-loops-hint'));
      expect(hint).toContain(word);
      // «1 возврат» — префикс «1 возвратов»: хвост проверяем явно, иначе мутация
      // plural не краснит тест.
      if (n === 1) expect(hint).not.toContain('1 возвратов');
    });
  }

  it('токены: 1 токен / 2 токена / 5 токенов', () => {
    for (const [tokens, word] of [[1, '1 токен'], [2, '2 токена'], [5, '5 токенов']] as const) {
      const { container } = mountTiles(mkRun(), mkStats({ spend: mkSpend({ inputTokens: tokens, outputTokens: 0 }) }));
      const hint = textOf(findNodeByAttr(container, 'data-testid', 'tile-spend-hint'));
      expect(hint).toContain(word);
      if (tokens === 1) expect(hint).not.toContain('1 токенов');
    }
  });

  it('ожидания: 1 раз / 2 раза; «дольше всего» — только если есть longest', () => {
    const one = mkStats({ spend: mkSpend() });
    one.timeline.totals.waitingCount = 1;
    one.timeline.totals.longestWaitMinutes = null;
    const { container } = mountTiles(mkRun(), one);
    const hint = textOf(findNodeByAttr(container, 'data-testid', 'tile-waiting-hint'));
    expect(hint).toContain('1 раз');
    expect(hint).not.toContain('дольше всего');

    const two = mountTiles(mkRun(), mkStats()).container;
    expect(textOf(findNodeByAttr(two, 'data-testid', 'tile-waiting-hint'))).toContain('2 раза · дольше всего 34 мин');
  });

  it('costPartial — цены нет, о частичности сказано прямо', () => {
    const { container } = mountTiles(mkRun(), mkStats({ spend: mkSpend({ costPartial: true, hiddenCostModels: ['Qwen'] }) }));
    const text = textOf(findNodeByAttr(container, 'data-testid', 'tile-spend'));
    expect(text).not.toContain('$21,80');
    expect(text).toContain('часть моделей без цены');
  });
});
