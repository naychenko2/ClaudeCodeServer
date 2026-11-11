// Тесты секции «Возвраты и потери» (FP-R1, вариант Б): карточка «Исход ревью»
// (FeatureReturnsOutcomeBar) и ниже «Главные потери» (FeatureStatsLosses как есть); прежних
// плитки «Возвраты» (FeatureStatsTiles) и строк вердиктов (ReviewKv) в секции нет. Числа
// бар — из run.verdicts (сама модель и состояния бар — в FeatureReturnsOutcomeBar.test.ts).
// Окружение node: разметка — renderToStaticMarkup (состав, порядок, текст — по html);
// проброс nav проверяется единственным тестом на fakeDom: у перехода «задача» клик
// записывает точку возврата «к фиче» (сами переходы «чат»/«задача» проверяет
// FeatureStatsLosses.test.ts).
//
// Мутации (проверены прогоном в копии дерева, 11.11): (1) не пробросить isMobile в бар —
// краснеет «isMobile пробрасывается» (на 360 не было бы мобильной легенды min-height 40);
// (2) не пробросить nowMs в FeatureStatsLosses (Date.now()) — краснеет «nowMs пробрасывается»;
// (3) вернуть FeatureStatsTiles/ReviewKv в начало — краснеет «состав секции» (плитка и
// строки вердиков появились бы снова); (4) убрать nav={nav} в FeatureStatsLosses — краснеет
// ровно «nav пробрасывается в потери…».

import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, vi } from 'vitest';
import { FeatureReviewReturns } from './FeatureReviewReturns';
import { getBackNav, clearBackNav } from '../../lib/featureBackNav';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureStepChain } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

// Переход «задача» моким: проверяем аргументы, не трогая настоящий window
// (тот же приём, что в FeatureStatsLosses.test.ts).
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

// Фиксированное «сейчас» (как в FeatureStatsLosses.test.ts): даты — относительно него.
const NOW = Date.parse('2026-10-24T12:00:00Z');

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

// Полные totals — с ожиданием (longestWait 120): карточка 1 «Главных потерь» не пустая.
function mkStats(over: Partial<FeatureTimeline['totals']> = {}): FeatureStats {
  return {
    source: 'approx',
    truncated: false,
    timeline: {
      from: '2026-10-23T00:00:00Z',
      to: '2026-10-24T12:00:00Z',
      stages: [],
      segments: [],
      current: null,
      totals: {
        elapsedMinutes: 400, workWallMinutes: 250, agentsSumMinutes: 300, notWorkingMinutes: 150,
        waitingMinutes: 65, idleMinutes: 70, waitingCount: 2, longestWaitMinutes: 120,
        errorCount: 1, errorMinutes: 15,
        ...over,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
  };
}

function mkChain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'ST-F7',
    title: 'Выпилить блок',
    personaId: 'p-kira',
    taskIds: ['t-step'],
    outcome: 'returned',
    status: 'Работа',
    durationMinutes: 65,
    loops: 2,
    rounds: [
      { n: 1, verdict: 'returned', reviewChatId: null },
      { n: 2, verdict: 'accepted', reviewChatId: null },
    ],
    chatId: null,
    ...over,
  };
}

// Фикстура c1: вердикты {accepted 5, acceptedWithRemarks 1, returned 1, withdrawn 0}.
// returnsBySource в фикстуре нет намеренно: вариант Б его не читает (разбор Майи, п. 5) —
// каждый тест заодно подтверждает, что секция живёт и со старым ответом бэка без этого поля.
const C1_RUN = mkRun({
  verdicts: { accepted: 5, acceptedWithRemarks: 1, returned: 1, withdrawn: 0, loops: 2 },
  stepChains: [mkChain()],
});

const html = (run: FeatureRun, stats: FeatureStats = mkStats(), isMobile?: boolean): string =>
  renderToStaticMarkup(createElement(FeatureReviewReturns, { run, stats, isMobile, nowMs: NOW }));

const count = (h: string, needle: string): number => (h.match(new RegExp(needle, 'g')) ?? []).length;
// Текст каждого чипа легенды (метки, счётчики и проценты — в порядке html), как в
// FeatureReturnsOutcomeBar.test.ts.
const chipTexts = (h: string): string[] =>
  [...h.matchAll(/data-testid="returns-chip-\w+"[^>]*>([\s\S]*?)<\/button>/g)]
    .map((m) => m[1].replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim());

describe('состав секции (c1)', () => {
  // Мутация (3): вернуть прежние плитку/строки вердиктов — краснеет этот тест.
  it('карточка «Исход ревью» → «Главные потери»; плитки «Возвраты» и строк вердиктов нет', () => {
    const h = html(C1_RUN);
    const order = ['data-testid="returns-outcome"', 'data-testid="stats-losses"'].map((s) => h.indexOf(s));
    expect(order[0], 'карточник бар').toBeGreaterThanOrEqual(0);
    expect(order[0]).toBeLessThan(order[1], '«Главные потери» ниже бара');
    expect(h).not.toContain('data-testid="stats-tiles"');
    expect(h).not.toContain('data-testid="tile-loops"');
    expect(h).not.toContain('data-testid="review-kv"');
  });

  // П. 2 разбора Майи: размер блока не зависит от объёма данных — ни значков на виток,
  // ни строк, растущих с числом ревью.
  it('число узлов блока одинаково при 12 и при 300 ревью', () => {
    const nodes = (h: string) => (h.match(/<[a-z]/g) ?? []).length;
    expect(nodes(html(mkRun({ verdicts: { accepted: 8, acceptedWithRemarks: 2, returned: 2, withdrawn: 0, loops: 4 }, stepChains: [mkChain()] })))).toBe(
      nodes(html(mkRun({ verdicts: { accepted: 200, acceptedWithRemarks: 60, returned: 40, withdrawn: 0, loops: 100 }, stepChains: [mkChain()] }))));
  });

  it('0 ревью: вместо карточки — строка «Ревью ещё не было», «Главные потери» на месте', () => {
    const h = html(mkRun({ stepChains: [mkChain()] }));
    expect(h).toContain('Ревью ещё не было');
    expect(h).not.toContain('data-testid="returns-outcome"');
    expect(h).toContain('data-testid="stats-losses"');
  });
});

describe('бар — из run.verdicts как есть (c1)', () => {
  it('заголовок и легенда по вердиктам фикстуры (5/1/1 → «71 % с первого раза»)', () => {
    const h = html(C1_RUN);
    expect(h).toContain('71 % ревью — с первого раза');
    expect(chipTexts(h)).toEqual(['Сразу 5 71 %', 'С замечаниями 1 14 %', 'Возвращены 1 14 %']);
    expect(h).not.toContain('отозвано');
  });
});

describe('«Главные потери» как есть (c1)', () => {
  it('блок потерь с числами фикстуры: карточка 1 (ожидание) и карточка 2 (витки)', () => {
    const h = html(C1_RUN);
    expect(h).toContain('Главные потери');
    expect(h).toContain('Дольше всего ждали вас');
    expect(h).toContain('ST-F7 · 2 возврата');
  });

  // Мутация (2): nowMs не пробросен (Date.now()) — минуты живой карточки считались бы
  // от реального «сейчас» (NOW — 2026-10-24, тест краснеет).
  it('nowMs пробрасывается в потери: живая карточка — минуты от NOW', () => {
    const run = mkRun({ attention: { kind: 'wait', taskId: 't-1', since: new Date(NOW - 90 * 60_000).toISOString() } });
    const h = html(run);
    expect(h).toContain('Ждёт вас сейчас');
    expect(h).toContain('1 ч 30 мин'); // formatDuration((NOW - since)/60000)
  });

  // Мутация (1): isMobile не пробросен — краснеет этот тест (бар с десктопной легендой,
  // потери в 3 колонки).
  it('isMobile пробрасывается в бар и потери: 360 — легенда min-height 40 и 1 колонка', () => {
    const h = html(C1_RUN, mkStats(), true);
    expect(h).toContain('min-height:40px'); // мобильные чипы легенды
    expect(h).toContain('grid-template-columns:1fr'); // сетка потерь на 360
    const d = html(C1_RUN);
    expect(d).toContain('min-height:32px'); // десктопные чипы
    expect(d).not.toContain('min-height:40px');
    expect(d).toContain('grid-template-columns:repeat(3, 1fr)');
  });
});

describe('проброс nav в «Главные потери» (c1)', () => {
  // Эффект проброса nav, наблюдаемый в этой фикстуре (без nav.marks), — точка возврата
  // «к фиче» при клике «задача» (recordFeatureReturn): без nav кнопка на месте, но возврата
  // не будет. Мутация (4): убрать nav={nav} в FeatureStatsLosses — краснеет ровно этот тест.
  it('nav пробрасывается в потери: клик «задача» пишет точку возврата «к фиче»', async () => {
    mockOpenTask.calls.length = 0;
    // Фикстура карточки 3 из FeatureStatsLosses.test.ts:364-366: ненулевой longestNow —
    // карточка «Дольше всех сейчас» с парой переходов «чат»/«задача».
    const stats: FeatureStats = {
      ...mkStats(),
      longestNow: { taskId: 't-live', chatId: 's-1', personaId: 'p-kira', stepRef: 'ST-F6', minutes: 90, tokens: 1200, share: 0.42 },
    };
    installFakeDom();
    try {
      const { container } = mount(createElement(FeatureReviewReturns, {
        run: C1_RUN, stats, nav: { featureId: 'f-1', featureTitle: 'Фича' }, nowMs: NOW,
      }));
      const btn = findNodeByAttr(container, 'data-testid', 'nav-target-task');
      expect(btn, 'перехода «задача» в секции нет').toBeTruthy();
      await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
      expect(mockOpenTask.calls, 'openTaskInSection не вызван').toEqual([{ id: 't-live' }]);
      const back = getBackNav();
      expect(back?.from, 'точка возврата — фича f-1').toBe('f-1');
      expect(back?.fromKind, 'дискриминатор — feature').toBe('feature');
      expect(back?.to, 'цель — задача t-live').toEqual({ kind: 'task', id: 't-live' });
      expect(back?.fromTitle, 'имя фичи для точки возврата').toBe('Фича');
    } finally {
      clearBackNav(); // указатель не оставляем в сторе другим тестам
      uninstallFakeDom();
    }
  });
});
