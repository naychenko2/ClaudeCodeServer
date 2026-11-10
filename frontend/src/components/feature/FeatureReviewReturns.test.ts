// Тесты секции «Возвраты и потери» (FS-FR3): ровно одна плитка «Возвраты» (only=['loops']),
// 4 строки вердиктов — без строки «Откуда возвраты» (те же числа печатает хвост плитки,
// ревью-находка 2, решение постановщика), «Главные потери» как есть (nowMs, isMobile и nav
// проброшены). Окружение node: разметка — renderToStaticMarkup (счёт, порядок и текст — по
// html); проброс nav проверяется единственным тестом на fakeDom: у перехода «задача» клик
// записывает точку возврата «к фиче» (сами переходы «чат»/«задача» проверяет
// FeatureStatsLosses.test.ts).
//
// Мутации (проверены прогоном в копии дерева, 10.11): (1) убрать nav={nav} в FeatureStatsLosses
// — краснеет ровно «nav пробрасывается в потери…» (кнопка на месте, но точка возврата не
// записана); после отката зелёный. (2) вернуть строку «Откуда возвраты» — краснеет «ровно
// 4 пар». (3) убрать only=['loops'] (полные плитки) — краснеет «ровно одна плитка».
// (4) не пробросить nowMs в FeatureStatsLosses (Date.now()) — краснеет «nowMs пробрасывается»
// (минуты живой карточки считались бы от реального «сейчас»). (5) не пробросить isMobile —
// краснеет «isMobile пробрасывается».

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

// Фикстура c1: вердикты {accepted 5, acceptedWithRemarks 1, returned 1, withdrawn 0} и
// разбивка с суммой = loops = 2 (константа бэка: сумма returnsBySource = verdicts.loops).
const C1_RUN = mkRun({
  verdicts: { accepted: 5, acceptedWithRemarks: 1, returned: 1, withdrawn: 0, loops: 2 },
  returnsBySource: { review: { count: 2, open: 1 }, qa: { count: 0, open: 0 }, final: { count: 0, open: 0 } },
  stepChains: [mkChain()],
});

const html = (run: FeatureRun, stats: FeatureStats = mkStats(), isMobile?: boolean): string =>
  renderToStaticMarkup(createElement(FeatureReviewReturns, { run, stats, isMobile, nowMs: NOW }));

// Пары «ключ — значение» всех строк ReviewKv в порядке html.
function kvPairs(h: string): [string, string][] {
  return [...h.matchAll(/data-testid="review-kv-row"[^>]*><span[^>]*>([^<]*)<\/span><span[^>]*>([^<]*)<\/span>/g)]
    .map((m) => [m[1], m[2]] as [string, string]);
}

const count = (h: string, needle: string): number => (h.match(new RegExp(needle, 'g')) ?? []).length;

describe('состав секции (c1)', () => {
  it('ровно одна плитка — «Возвраты»; «Работали», «Ждали вас», «Расход» нет', () => {
    const h = html(C1_RUN);
    expect(count(h, 'data-testid="tile-loops"'), 'плитка «Возвраты»').toBe(1);
    expect(h).not.toContain('data-testid="tile-work"');
    expect(h).not.toContain('data-testid="tile-waiting"');
    expect(h).not.toContain('data-testid="tile-spend"');
  });

  it('порядок: плитка → «ключ — значение» → «Главные потери»', () => {
    const h = html(C1_RUN);
    const order = [
      'data-testid="stats-tiles"',
      'data-testid="review-kv"',
      'data-testid="stats-losses"',
    ].map((s) => h.indexOf(s));
    for (let i = 1; i < order.length; i++) {
      expect(order[i - 1], `«${i - 1}» раньше «${i}»`).toBeGreaterThanOrEqual(0);
      expect(order[i - 1]).toBeLessThan(order[i]);
    }
  });
});

describe('вердикты (c1)', () => {
  // Полное равенство списков: ровно 4 строки, порядок по постановке, дублей нет.
  // «Откуда возвраты» убрана (находка 2): те же три числа — в хвосте плитки «Возвраты»
  // (FeatureStatsTiles.tsx:105-106), в макете v4 (:740) они стоят один раз.
  // Мутация (2): вернуть строку — этот тест краснеет.
  it('4 строки вердиктов с числами фикстуры — ровно 4 пар, строки «Откуда возвраты» нет', () => {
    const h = html(C1_RUN);
    expect(kvPairs(h)).toEqual([
      ['Приняты', '5'],
      ['С замечаниями', '1'],
      ['Возвращены', '1'],
      ['Отозваны', '0'],
    ]);
    expect(h).not.toContain('Откуда возвраты');
  });

  // Старый ответ без returnsBySource: секция ведёт себя так же — 4 строки, плитка и потери
  // на месте (полные пары — в тесте выше, на фикстуре с разбивкой).
  it('returnsBySource нет (старый ответ) — 4 строки, плитка и потери на месте', () => {
    const h = html(mkRun({ verdicts: { accepted: 3, acceptedWithRemarks: 0, returned: 2, withdrawn: 1, loops: 2 } }));
    expect(kvPairs(h)).toEqual([
      ['Приняты', '3'],
      ['С замечаниями', '0'],
      ['Возвращены', '2'],
      ['Отозваны', '1'],
    ]);
    expect(h).not.toContain('Откуда возвраты');
    expect(h).toContain('data-testid="tile-loops"');
    expect(h).toContain('data-testid="stats-losses"');
  });
});

describe('«Главные потери» как есть', () => {
  it('блок потерь с числами фикстуры: карточка 1 (ожидание) и карточка 2 (витки)', () => {
    const h = html(C1_RUN);
    expect(h).toContain('Главные потери');
    expect(h).toContain('Дольше всего ждали вас');
    expect(h).toContain('ST-F7 · 2 возврата');
  });

  // Мутация (3): nowMs не пробросен (Date.now()) — минуты живой карточки считались бы
  // от реального «сейчас» (NOW — 2026-10-24, тест краснеет).
  it('nowMs пробрасывается в потери: живая карточка — минуты от NOW', () => {
    const run = mkRun({ attention: { kind: 'wait', taskId: 't-1', since: new Date(NOW - 90 * 60_000).toISOString() } });
    const h = html(run);
    expect(h).toContain('Ждёт вас сейчас');
    expect(h).toContain('1 ч 30 мин'); // formatDuration((NOW - since)/60000)
  });

  // Мутация (5): isMobile не пробросен — краснеет этот тест (плитки 4 колонки, потери 3).
  it('isMobile пробрасывается в плитки и потери: 360 — 2 колонки и 1 колонка', () => {
    const h = html(C1_RUN, mkStats(), true);
    expect(h).toContain('grid-template-columns:repeat(2, 1fr)'); // сетка плиток на 360
    expect(h).toContain('grid-template-columns:1fr'); // сетка потерь на 360
    const d = html(C1_RUN);
    expect(d).toContain('grid-template-columns:repeat(4, 1fr)');
    expect(d).toContain('grid-template-columns:repeat(3, 1fr)');
  });
});

describe('проброс nav в «Главные потери» (c1)', () => {
  // Эффект проброса nav, наблюдаемый в этой фикстуре (без nav.marks), — точка возврата
  // «к фиче» при клике «задача» (recordFeatureReturn): без nav кнопка на месте, но возврата
  // не будет. Мутация (1): убрать nav={nav} в FeatureStatsLosses — краснеет ровно этот тест.
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
