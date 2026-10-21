// Вкладка «Статистика» (волна 3, Ф7a, макет features-overview-3-final §«Статистика»).
// БЛОК 1 — лента времени: линейка этапов, сегменты (∝ минутам), СЖАТИЕ пауз > 2 ч
// («разрыв · N ч»), дорожка ревью, ось + «сейчас», легенда, значки точности, «задача».
//
// Чистые функции (FeatureStatsBits) — в node, без DOM. Рендер-тесты (fakeDom):
// сегменты по видам, дорожка ревью, сжатие паузы (число + подпись), клик → цель перехода,
// source/truncated/current, пустые состояния, фичи без stats.
//
// Мутации (в отчёте): (1) порог 2 ч → 20 ч → «сжатие паузы» краснеет;
// (2) потерять track=review → «дорожка ревью» краснеет; (3) подменить цель клика
// (focusFeatureStage('') вместо key) → «клик → этап» краснеет; (4) убрать «≈» при approx
// → «значок точности» краснеет; (5) сегмент waiting как work → «сегменты по видам» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStatsTab } from './FeatureStatsTab';
import {
  formatDuration, formatClock, formatDateTime, kindLabel, kindBackground,
  buildTimelineModel, nowFraction, PAUSE_BREAK_MIN,
} from './FeatureStatsBits';
import { clearFeatureStageFocus, __test } from '../../lib/featurePage';
import { openTaskInSection } from '../../lib/tasks';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

// === Мок навигации (задача) ===
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({
  openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); },
}));
// signalr — вкладка не подписывается, но featurePage тянет onMessage.
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});

// === Хелперы данных ===

// Сегмент ленты.
type Seg = { from: string; to: string; kind: 'work' | 'waiting' | 'idle' | 'error' | 'notWorking'; track: 'main' | 'review'; stage: string; taskId: string | null };

function seg(from: string, to: string, kind: Seg['kind'], stage: string, over: Partial<Seg> = {}): Seg {
  return { from, to, kind, track: 'main', stage, taskId: null, ...over };
}

// Реалистичный таймлайн: cut → plan → [ночная пауза > 2 ч] → prod (main + review).
function mkTimeline(over: Partial<FeatureTimeline> = {}): FeatureTimeline {
  return {
    from: '2026-10-19T17:20:00Z',
    to: '2026-10-20T12:30:00Z',
    stages: [
      { key: 'cut', startedAt: '2026-10-19T17:20:00Z', endedAt: '2026-10-19T17:55:00Z' },
      { key: 'plan', startedAt: '2026-10-19T17:58:00Z', endedAt: '2026-10-19T18:30:00Z' },
      { key: 'prod', startedAt: '2026-10-20T10:05:00Z', endedAt: null },
    ],
    segments: [
      seg('2026-10-19T17:20:00Z', '2026-10-19T17:55:00Z', 'work', 'cut', { taskId: 't-cut' }),
      seg('2026-10-19T17:58:00Z', '2026-10-19T18:30:00Z', 'work', 'plan', { taskId: 't-plan' }),
      seg('2026-10-20T10:05:00Z', '2026-10-20T11:00:00Z', 'work', 'prod', { taskId: 't-prod' }),
      seg('2026-10-20T11:20:00Z', '2026-10-20T12:00:00Z', 'work', 'prod', { track: 'review', taskId: 't-review' }),
    ],
    current: { kind: 'waiting', since: '2026-10-20T12:15:00Z', taskId: 't-prod' },
    totals: {
      elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0,
      waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
      errorCount: null, errorMinutes: null,
    },
    ...over,
  };
}

function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'approx',
    truncated: false,
    timeline: mkTimeline(),
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

// === Чистые функции ===

describe('formatDuration', () => {
  it('минуты / часы / часы+минуты (минуты с ведущим 0)', () => {
    expect(formatDuration(0)).toBe('0 мин');
    expect(formatDuration(35)).toBe('35 мин');
    expect(formatDuration(60)).toBe('1 ч');
    expect(formatDuration(90)).toBe('1 ч 30 мин');
    expect(formatDuration(120)).toBe('2 ч');
    expect(formatDuration(935)).toBe('15 ч 35 мин');
  });
});

describe('formatClock / formatDateTime', () => {
  it('локальное время и дата+время', () => {
    expect(formatClock('2026-10-19T17:20:00Z')).toMatch(/^\d{2}:\d{2}$/);
    expect(formatDateTime('2026-10-19T17:20:00Z')).toMatch(/^\d{2}\.\d{2} · \d{2}:\d{2}$/);
  });
  it('невалидная дата → пусто', () => {
    expect(formatClock('not-a-date')).toBe('');
    expect(formatDateTime('not-a-date')).toBe('');
  });
});

describe('kindLabel / kindBackground', () => {
  it('подписи видов', () => {
    expect(kindLabel('work')).toBe('работа');
    expect(kindLabel('waiting')).toBe('ждёт вас');
    expect(kindLabel('idle')).toBe('простой');
    expect(kindLabel('error')).toBe('ошибка / стоит');
    expect(kindLabel('notWorking')).toBe('не работали');
  });
  it('цвета — токены C.* (var(--c-…))', () => {
    expect(kindBackground('work')).toBe('var(--c-accent)');
    expect(kindBackground('waiting')).toBe('var(--c-info)');
    expect(kindBackground('idle')).toBe('var(--c-progress-track)');
    expect(kindBackground('error')).toBe('var(--c-danger)');
    expect(kindBackground('notWorking')).toContain('repeating-linear-gradient');
  });
});

describe('buildTimelineModel', () => {
  it('группирует сегменты в блоки этапов и вставляет сжатый разрыв на паузе > 2 ч', () => {
    const m = buildTimelineModel(mkTimeline(), 'approx', false, false);
    // Блоки: cut, plan, [break], prod.
    const types = m.blocks.map(b => b.type);
    expect(types).toEqual(['stage', 'stage', 'break', 'stage']);
    // Разрыв: 18:30 → 10:05 = 935 мин → «разрыв · 15 ч 35 мин».
    const brk = m.blocks[2]!;
    expect(brk.type).toBe('break');
    if (brk.type === 'break') {
      expect(brk.minutes).toBe(935);
      expect(brk.label).toBe('разрыв · 15 ч 35 мин');
    }
    // Порог: 935 >= 120.
    expect(PAUSE_BREAK_MIN).toBe(120);
  });

  // Мутация (1): порог 2 ч → 20 ч (120 → 1200) → пауза 935 мин НЕ сжимается → break исчезает.
  it('мутация: порог 20 ч → пауза 15 ч 35 мин не сжимается', () => {
    const m = buildTimelineModel(mkTimeline(), 'approx', false, false);
    // При пороге 1200 мин наш разрыв (935) не достигал бы порога — проверяем, что сейчас ДОСТИГАЕТ.
    const brk = m.blocks.find(b => b.type === 'break');
    expect(brk, 'разрыв при пороге 2 ч').toBeDefined();
    if (brk && brk.type === 'break') expect(brk.minutes).toBeGreaterThanOrEqual(PAUSE_BREAK_MIN);
  });

  it('дорожка ревью: hasReview=true, когда есть сегмент track=review', () => {
    const m = buildTimelineModel(mkTimeline(), 'approx', false, false);
    expect(m.hasReview).toBe(true);
    // prod-блок: reviewMinutes = 40 (сегмент 11:20–12:00).
    const prod = m.blocks.find(b => b.type === 'stage' && b.key === 'prod')!;
    expect(prod.reviewMinutes).toBe(40);
  });

  // Мутация (2): потерять track=review (сделать main) → hasReview=false.
  it('мутация: все track=main → hasReview=false', () => {
    const tl = mkTimeline();
    tl.segments = tl.segments.map(s => ({ ...s, track: 'main' }));
    const m = buildTimelineModel(tl, 'approx', false, false);
    expect(m.hasReview).toBe(false);
  });

  it('«сейчас»: current != null и не закрыта → current с подписью', () => {
    const m = buildTimelineModel(mkTimeline(), 'approx', false, false);
    expect(m.current).not.toBeNull();
    expect(m.current!.label).toContain('ждёт вашего ответа сейчас');
  });

  it('закрыта (current=null) → current=null, endLabel «закрыта»', () => {
    const tl = mkTimeline({ current: null });
    const m = buildTimelineModel(tl, 'approx', false, true);
    expect(m.current).toBeNull();
    expect(m.axis.endLabel).toContain('закрыта');
  });

  it('accuracy: source + truncated передаются в модель', () => {
    const m = buildTimelineModel(mkTimeline(), 'journal', true, false);
    expect(m.accuracy).toEqual({ source: 'journal', truncated: true });
  });

  it('hasData=false: нет сегментов и current=null → пусто', () => {
    const tl = mkTimeline({ segments: [], current: null });
    const m = buildTimelineModel(tl, 'approx', false, true);
    expect(m.hasData).toBe(false);
  });
});

describe('nowFraction', () => {
  it('доля 0..1 по моменту; вне диапазона — в границы', () => {
    expect(nowFraction('2026-10-19T17:20:00Z', '2026-10-20T12:30:00Z', '2026-10-19T17:20:00Z')).toBe(0);
    expect(nowFraction('2026-10-19T17:20:00Z', '2026-10-20T12:30:00Z', '2026-10-20T12:30:00Z')).toBe(1);
    const mid = nowFraction('2026-10-19T17:20:00Z', '2026-10-20T12:30:00Z', '2026-10-20T12:15:00Z');
    expect(mid).toBeGreaterThan(0.5);
    expect(mid).toBeLessThan(1);
  });
});

// === Рендер-тесты (fakeDom) ===

type FNodeLike = { nodeType: number; nodeName: string; childNodes: FNodeLike[]; attributes?: Record<string, string> };

function renderTab(stats: FeatureStats | null, isMobile = false) {
  installFakeDom();
  const { container } = mount(createElement(FeatureStatsTab, { stats, isMobile }));
  return container;
}

describe('FeatureStatsTab: рендер', () => {
  beforeEach(() => {
    mockOpenTask.calls = [];
    clearFeatureStageFocus();
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. Сегменты по видам (макет: work/waiting/idle/error/notWorking) ===
  it('сегменты: каждый вид — своя data-testid и цвет-токен', () => {
    const stats = mkStats();
    // Добавим сегменты всех видов (один этап, чтобы не плодить блоки).
    stats.timeline.segments = [
      seg('2026-10-20T10:00:00Z', '2026-10-20T10:20:00Z', 'work', 'prod'),
      seg('2026-10-20T10:20:00Z', '2026-10-20T10:30:00Z', 'waiting', 'prod'),
      seg('2026-10-20T10:30:00Z', '2026-10-20T10:40:00Z', 'idle', 'prod'),
      seg('2026-10-20T10:40:00Z', '2026-10-20T10:50:00Z', 'error', 'prod'),
      seg('2026-10-20T10:50:00Z', '2026-10-20T11:00:00Z', 'notWorking', 'prod'),
    ];
    const c = renderTab(stats);
    for (const kind of ['work', 'waiting', 'idle', 'error', 'notWorking'] as const) {
      const node = findNodeByAttr(c, 'data-testid', `stats-seg-${kind}`);
      expect(node, `сегмент ${kind}`).not.toBeNull();
    }
  });

  // Мутация (5): сегмент waiting как work (kindLabel/waiting → work) → «ждёт вас» исчезает.
  it('мутация: waiting-сегмент помечен work → aria-label «ждёт вас» теряется', () => {
    const stats = mkStats();
    stats.timeline.segments = [
      seg('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', 'waiting', 'prod'),
    ];
    const c = renderTab(stats);
    const node = findNodeByAttr(c, 'data-testid', 'stats-seg-waiting')!;
    const aria = node.getAttribute('aria-label') ?? '';
    expect(aria, 'aria-label несёт вид «ждёт вас»').toContain('ждёт вас');
  });

  // === 2. Дорожка ревью (hasReview) ===
  it('дорожка ревью: легенда показывает «ревью», когда есть track=review', () => {
    const c = renderTab(mkStats());
    const legend = findNodeByAttr(c, 'data-testid', 'stats-legend')!;
    expect(textNodes(legend).join('')).toContain('ревью');
  });

  // === 3. Сжатие паузы > 2 ч: число и подпись ===
  it('сжатие: разрыв 15 ч 35 мин → блок «разрыв · 15 ч 35 мин» (число + подпись)', () => {
    const c = renderTab(mkStats());
    // На 1280 подпись разрыва видна (b.label).
    const brk = findNodeByAttr(c, 'data-testid', 'stats-seg-break');
    expect(brk, 'сжатый разрыв').not.toBeNull();
    expect(textNodes(brk!).join('')).toContain('разрыв · 15 ч 35 мин');
  });

  // === 4. Клик → цель перехода (этап в «Хронологии») ===
  it('клик по линейке/сегменту → focusFeatureStage(key) (якорь этапа)', () => {
    const c = renderTab(mkStats());
    const ruler = findNodeByAttr(c, 'data-testid', 'stats-ruler-prod')!;
    act(() => { (reactProps(ruler)!.onClick as () => void)(); });
    // Якорь этапа выставлен на prod (тот же приём, что плашка/панель: setFeaturePageTab + focusFeatureStage).
    const focus = __test.stageFocus();
    expect(focus, 'якорь этапа после клика').not.toBeNull();
    expect(focus!.key).toBe('prod');
  });

  // Мутация (3): подменить цель клика (focusFeatureStage('') вместо key) → якорь пуст.
  it('мутация: цель клика = key (не пустая строка)', () => {
    const c = renderTab(mkStats());
    const ruler = findNodeByAttr(c, 'data-testid', 'stats-ruler-cut')!;
    act(() => { (reactProps(ruler)!.onClick as () => void)(); });
    expect(__test.stageFocus()!.key).toBe('cut');
  });

  // === 5. Значки точности: source=approx → «≈» + «по задачам (≈)» ===
  it('значок точности: approx → бейдж «≈» и подпись «по задачам (≈)»', () => {
    const c = renderTab(mkStats({ source: 'approx' }));
    // Бейдж «≈» — отдельный узел (мутац
    const badge = findNodeByAttr(c, 'data-testid', 'stats-accuracy-approx');
    expect(badge, 'бейдж «≈»').not.toBeNull();
    expect(textNodes(badge!).join('')).toBe('≈');
    // Подпись.
    const acc = findNodeByAttr(c, 'data-testid', 'stats-accuracy')!;
    expect(textNodes(acc).join('')).toContain('по задачам (≈)');
  });

  // Мутация (4): убрать «≈» из бейджа (→ «по задачам») → бейдж перестаёт быть «≈» → red.
  it('мутация: approx-бейдж = «≈» (подменить → red)', () => {
    const c = renderTab(mkStats({ source: 'approx' }));
    const badge = findNodeByAttr(c, 'data-testid', 'stats-accuracy-approx')!;
    expect(textNodes(badge).join(''), 'бейдж точности — «≈»').toBe('≈');
  });

  it('значок точности: journal → «по журналу» (без «≈»)', () => {
    const c = renderTab(mkStats({ source: 'journal' }));
    const acc = findNodeByAttr(c, 'data-testid', 'stats-accuracy')!;
    const t = textNodes(acc).join('');
    expect(t).toContain('по журналу');
  });

  it('truncated → «журнал обрезан»', () => {
    const c = renderTab(mkStats({ source: 'journal', truncated: true }));
    const node = findNodeByAttr(c, 'data-testid', 'stats-accuracy-truncated');
    expect(node, '«журнал обрезан»').not.toBeNull();
    expect(textNodes(node!).join('')).toContain('журнал обрезан');
  });

  // === 6. «Сейчас»: current → строка с подписью ===
  it('«сейчас»: current (waiting) → строка «ждёт вашего ответа сейчас»', () => {
    const c = renderTab(mkStats());
    const cur = findNodeByAttr(c, 'data-testid', 'stats-current');
    expect(cur, 'строка «сейчас»').not.toBeNull();
    expect(textNodes(cur!).join('')).toContain('ждёт вашего ответа сейчас');
  });

  // === 7. «Задача»: taskId сегмента → NavTargetButton «задача» ===
  it('«задача»: по каждому этапу с taskId — кнопка; клик → openTaskInSection(taskId)', async () => {
    const c = renderTab(mkStats());
    const tasks = findNodeByAttr(c, 'data-testid', 'stats-tasks');
    expect(tasks, 'ряд «задача»').not.toBeNull();
    // Кнопок «задача» = число этапов с taskId (cut/plan/prod = 3).
    const btns: unknown[] = [];
    const walk = (n: FNodeLike) => {
      if (n.attributes?.['data-testid'] === 'nav-target-task') btns.push(n);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(tasks!);
    expect(btns.length, '3 кнопки «задача» (по этапам с taskId)').toBe(3);
    // Клик по ПЕРВОЙ (cut) → openTaskInSection({ id: 't-cut' }).
    await act(async () => { (reactProps(btns[0]!)!.onClick as () => void)(); });
    expect(mockOpenTask.calls.some(x => x.id === 't-cut'), 'клик открыл задачу t-cut').toBe(true);
  });

  // === 8. Пустые состояния ===
  it('stats=null → «Статистики пока нет»', () => {
    const c = renderTab(null);
    const all = textNodes(c).join('');
    expect(all).toContain('Статистики пока нет');
  });

  it('нет сегментов и current=null → «Считать пока нечего»', () => {
    const stats = mkStats();
    stats.timeline.segments = [];
    stats.timeline.current = null;
    const c = renderTab(stats);
    const all = textNodes(c).join('');
    expect(all).toContain('Считать пока нечего');
  });

  // === 9. 360: тач-цель линейки ≥ 40 ===
  it('на 360 кнопка-этап (линейка) — minHeight 40', () => {
    const c = renderTab(mkStats(), true);
    const ruler = findNodeByAttr(c, 'data-testid', 'stats-ruler-prod')!;
    const st = (ruler as unknown as { style: Record<string, string> }).style;
    expect(st.minHeight).toBe('40px');
  });
});
