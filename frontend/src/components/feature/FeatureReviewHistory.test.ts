// Тесты секции «Хронология» вкладки «Разбор» (FS-FR6) и блока дорожек (FeatureReviewStageLanes,
// F18b). Окружение node: renderToStaticMarkup (интеракции здесь нет: строки событий и их
// переходы тестирует FeatureEventsTab.test.ts).
//
// c1: из 5 событий видов created/started/accepted/returned/merged → в DOM 3 строки (только
//     ключевые виды); eventsTotal=40 → подпись «из 40 в истории».
// c2: intervalsByStage для 2 этапов → 2 строки дорожек и ось над ними; intervalsByStage=null →
//     0 дорожек и «Шкала появится, когда у фичи будет журнал времени».
// Порядок (контракт): «Хронология» — события → подпись; дорожек в ней нет с F18b (M33) — они
// в секции «Время и срок», их подключение к секции тестирует FeatureReviewTime.test.ts.
//
// Мутация c3 (проверена прогоном, 10.11, в копии дерева): убрать фильтр по видам
// (`KEY_EVENT_KINDS.has(e.kind)` → `true`) → «3 из 5» краснеет (5 строк вместо 3);
// после отката — зелёные.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReviewHistory, FeatureReviewStageLanes } from './FeatureReviewHistory';
import type { FeatureRun, FeatureStage, FeatureStageExecutor } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';
import type { FeatureEvent } from '../../types/featureIndex';

// === Фикстуры ===

function mkStage(over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key: 'prod', state: 'pending', tone: null, passes: 0, taskIds: [], runningSince: null,
    rounds: [], longestStepRef: null, firstStartedAt: null, lastCompletedAt: null, barEnd: null,
    approxMinutes: 0, overlapsActive: false, executors: [], ...over,
  };
}

function mkExec(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return {
    personaId: null, taskId: 't-prod', taskRole: null, stepRef: null, lane: null,
    status: 'done', chatId: null, chatArchived: false, modelLabel: null, modelFull: null,
    reviewOf: [], reviewOfOutside: 0, ...over,
  };
}

// Фича с двумя этапами (prod+review) — те же два, что дают дорожки в фикстуре stats.
function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    // approxMinutes (999) заведомо отличается от минут интервалов (30): иначе тест
    // длительности не различает факт «30 мин» и оценку «≈ 16 ч 39 мин» — подмена
    // приоритета факт/≈ в компоненте проходила бы незамеченной (находка ревью).
    stages: [
      mkStage({ key: 'prod', state: 'active', tone: 'run', firstStartedAt: '2026-10-20T10:00:00Z', approxMinutes: 999, taskIds: ['t-prod'], executors: [mkExec()] }),
      mkStage({ key: 'review', state: 'done', firstStartedAt: '2026-10-20T11:00:00Z', approxMinutes: 999, taskIds: ['t-rev'] }),
    ],
    ...over,
  } as FeatureRun;
}

function mkTotals(over: Partial<FeatureTimeline['totals']> = {}): FeatureTimeline['totals'] {
  return {
    elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0,
    waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
    errorCount: null, errorMinutes: null, ...over,
  };
}

// Интервал журнала — форма bэкового StageInterval (FeatureStatsBits/FeatureLaneModel их читают).
function iv(from: string, to: string, taskIds: string[]) {
  return { from, to, minutes: 30, live: false, lastOutcome: null, taskIds, turns: [] };
}

function mkStats(intervalsByStage: Record<string, unknown[]> | null, over: Partial<FeatureTimeline> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-20T10:00:00Z', to: '2026-10-20T12:00:00Z',
      stages: [], segments: [], current: null, totals: mkTotals(),
      intervalsByStage, ...over,
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
  } as unknown as FeatureStats;
}

// Журнал с ходом у каждого из двух этапов.
function laneStats(): FeatureStats {
  return mkStats({
    prod: [iv('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', ['t-prod'])],
    review: [iv('2026-10-20T11:00:00Z', '2026-10-20T11:30:00Z', ['t-rev'])],
  });
}

function ev(over: Partial<FeatureEvent> = {}): FeatureEvent {
  return {
    at: '2026-10-20T10:00:00Z', kind: 'started', taskId: 't1',
    role: null, stepRef: null, personaId: null, sessionId: null, round: null, reason: null,
    ...over,
  };
}

function html(props: Partial<Parameters<typeof FeatureReviewHistory>[0]> = {}): string {
  return renderToStaticMarkup(createElement(FeatureReviewHistory, {
    run: mkRun(),
    stats: laneStats(),
    events: [],
    eventsTotal: 0,
    featureId: 'f1',
    ...props,
  }));
}

// Блок дорожек сам по себе (он живёт в секции «Время и срок», но модель и рендеры — здесь).
function lanesHtml(props: Partial<Parameters<typeof FeatureReviewStageLanes>[0]> = {}): string {
  return renderToStaticMarkup(createElement(FeatureReviewStageLanes, {
    run: mkRun(),
    stats: laneStats(),
    ...props,
  }));
}

// Строки дорожек блока (testid строки — review-stage-lane-<этап>).
function laneRows(h: string): string[] {
  return [...h.matchAll(/data-testid="review-stage-lane-([a-z]+)"/g)].map((m) => m[1]!);
}
// Строки событий FeatureEventsTab (testid строки — event-row-<вид>-<задача>-<индекс>).
function eventRows(h: string): string[] {
  return [...h.matchAll(/data-testid="event-row-([a-z_]+)-/g)].map((m) => m[1]!);
}

describe('дорожки этапов (c2) — блок FeatureReviewStageLanes', () => {
  it('журнал для 2 этапов → ровно 2 строки дорожек, в порядке этапов; третьих нет', () => {
    const h = lanesHtml();
    expect(laneRows(h)).toEqual(['prod', 'review']);
    // Дорожка и её интервалы — из модели (FeatureStageLane рисует lane-interval-<этап>-<i>).
    expect(h).toContain('data-testid="lane-prod"');
    expect(h).toContain('data-testid="lane-interval-prod-0"');
    expect(h).toContain('data-testid="lane-interval-review-0"');
  });

  it('ось времени стоит над строками дорожек', () => {
    const h = lanesHtml();
    expect(h.indexOf('data-testid="lane-axis"')).toBeGreaterThanOrEqual(0);
    expect(h.indexOf('data-testid="lane-axis"')).toBeLessThan(h.indexOf('data-testid="review-stage-lane-prod"'));
  });

  it('длительность справа от дорожки — факт времени модели, а не оценка «≈» (30 мин интервала prod)', () => {
    const h = lanesHtml();
    expect(h).toContain('30 мин');
    // Инвариант: факт приоритетнее «≈». Оценка этапа (999) отличается от минут
    // интервала (30), поэтому «≈» в DOM означает, что приоритет подменён.
    expect(h).not.toContain('≈');
  });

  it('нет журнала (intervalsByStage=null) → 0 дорожек и строка о журнале времени (c2)', () => {
    const h = lanesHtml({ stats: mkStats(null) });
    expect(laneRows(h)).toEqual([]);
    expect(h).not.toContain('data-testid="lane-axis"');
    expect(h).toContain('Шкала появится, когда у фичи будет журнал времени');
  });

  it('stats=null → те же 0 дорожек и та же строка', () => {
    const h = lanesHtml({ stats: null });
    expect(laneRows(h)).toEqual([]);
    expect(h).toContain('Шкала появится, когда у фичи будет журнал времени');
  });
});

describe('ключевые события (c1)', () => {
  // 5 событий: два неключевых (created, started) и три ключевых (accepted, returned, merged).
  const MIXED = [
    ev({ kind: 'created', taskId: 't0' }),
    ev({ kind: 'started', taskId: 't1' }),
    ev({ kind: 'accepted', taskId: 't2' }),
    ev({ kind: 'returned', taskId: 't3' }),
    ev({ kind: 'merged', taskId: 't4' }),
  ];

  it('из 5 событий в DOM 3 строки — только ключевые виды', () => {
    const h = html({ events: MIXED, eventsTotal: 40 });
    expect(eventRows(h).sort()).toEqual(['accepted', 'merged', 'returned']);
  });

  it('подпись внизу — настоящий eventsTotal из пропа', () => {
    const h = html({ events: MIXED, eventsTotal: 40 });
    expect(h).toContain('из 40 в истории');
  });

  it('вкладка не получает настоящий eventsTotal: при 12 ключевых из 40 она сворачивает свои 12 (8 строк + «· 12»), не зовёт полную ленту', () => {
    const many = Array.from({ length: 12 }, (_, i) => ev({ kind: 'accepted', taskId: `t${i}`, at: `2026-10-2${i % 5}T10:00:00Z` }));
    const h = html({ events: many, eventsTotal: 40 });
    // fold вкладки (EVENTS_COLLAPSED_LIMIT=8) работает над отфильтрованным списком: 8 строк + кнопка.
    expect(eventRows(h).length).toBe(8);
    expect(h).toContain('Показать все · 12');
    expect(h).not.toContain('Показать все · 40');
  });

  it('пустой список событий — состояние FeatureEventsTab, подпись остаётся', () => {
    const h = html({ events: [], eventsTotal: 7 });
    expect(eventRows(h)).toEqual([]);
    expect(h).toContain('Событий пока нет');
    expect(h).toContain('из 7 в истории');
  });
});

describe('порядок блоков (контракт)', () => {
  it('«Хронология» — события → подпись; дорожек в ней нет (F18b, M33: они в «Время и срок»)', () => {
    const h = html({ events: [ev({ kind: 'accepted', taskId: 't2' })], eventsTotal: 9 });
    const iEvent = h.indexOf('data-testid="event-row-accepted-t2-0"');
    const iNote = h.indexOf('data-testid="review-history-events-note"');
    expect(iEvent).toBeGreaterThanOrEqual(0);
    expect(iEvent).toBeLessThan(iNote);
    // Блок дорожек в «Хронологии» не рендерится: ни его контейнера, ни оси.
    expect(h).not.toContain('data-testid="review-stage-lanes"');
    expect(h).not.toContain('data-testid="lane-axis"');
  });
});
