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
  buildTimelineModel, nowFraction, nowCompressedFraction, PAUSE_BREAK_MIN,
} from './FeatureStatsBits';
import {
  formatTokens, formatUsd, sharePercent, roleGroupLabel, modelGroupLabel, personaGroupLabel,
} from './FeatureStatsBlocks';
import { clearFeatureStageFocus, __test } from '../../lib/featurePage';
import { openTaskInSection } from '../../lib/tasks';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureStats, FeatureTimeline, FeatureBreakdown, FeatureLongestNow, FeatureSpend } from '../../types/featureStats';

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
// === Мок персон/моделей/чатов (блоки 2–3: «дольше всех», разрезы, расход) ===
const mockPersonas = vi.hoisted(() => [
  { id: 'p-kira', name: 'Кира', role: 'Frontend' },
  { id: 'p-denis', name: 'Денис', role: 'Backend' },
]);
const mockModelLabel = vi.hoisted(() => (v: string | null | undefined): string => {
  if (v === 'claude-opus-4-8') return 'Opus';
  if (v === 'Qwen') return 'Qwen';
  return v ?? '';
});
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return {
    ...orig,
    usePersonas: () => mockPersonas,
    getPersonaById: (id: string) => mockPersonas.find(p => p.id === id),
  };
});
vi.mock('../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/models')>();
  return {
    ...orig,
    useModels: () => [],
    modelLabel: mockModelLabel,
  };
});
vi.mock('../../lib/openChat', () => ({
  openChatById: (id: string) => { mockOpenChat.calls.push(id); return Promise.resolve(true); },
}));

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

  // Правило from: ось начинается с tl.from (НЕ с первого сегмента); пауза ДО первого блока
  // считается от tl.from до первого сегмента (сжимается, если > 2 ч). Мутация «from = первый
  // сегмент» красит: ось и первый break смещаются.
  it('правило from: ось = tl.from; пауза до первого блока — от tl.from (сжимается)', () => {
    const tl = mkTimeline({
      from: '2026-10-19T10:00:00Z',
      segments: [seg('2026-10-19T17:20:00Z', '2026-10-19T17:55:00Z', 'work', 'cut', { taskId: 't-cut' })],
    });
    const m = buildTimelineModel(tl, 'approx', false, false);
    // Ось — от tl.from (10:00), не от первого сегмента (17:20).
    expect(m.axis.from).toBe('2026-10-19T10:00:00Z');
    // Пауза 10:00 → 17:20 = 440 мин > 120 → первый блок — сжатый break от tl.from.
    expect(m.blocks[0]!.type).toBe('break');
    const brk = m.blocks[0]!;
    if (brk.type === 'break') {
      expect(brk.from).toBe('2026-10-19T10:00:00Z');
      expect(brk.minutes).toBe(440);
    }
  });
});

// === Мажор 1: паузы ДЛИННЕЕ 2 ч ВНУТРИ одного этапа ===
// Старая группировка (last.key === s.stage) склеивала два куска одного этапа с ночной
// паузой в ОДИН блок, и пауза пропала (pushBreak считал разрывы только МЕЖДУ блоками).
describe('buildTimelineModel: пауза внутри этапа', () => {
  it('пауза 3,5 ч внутри prod → блок рвётся: prod, break, prod', () => {
    const tl = mkTimeline({
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T14:30:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', 'work', 'prod'),
        seg('2026-10-20T14:00:00Z', '2026-10-20T14:30:00Z', 'work', 'prod'),
      ],
    });
    const m = buildTimelineModel(tl, 'approx', false, false);
    expect(m.blocks.map(b => b.type)).toEqual(['stage', 'break', 'stage']);
    // Оба stage-блока — prod.
    const stages = m.blocks.filter(b => b.type === 'stage');
    expect(stages.length).toBe(2);
    for (const s of stages) expect(s.type === 'stage' && s.key).toBe('prod');
    // Разрыв: 10:30 → 14:00 = 210 мин.
    const brk = m.blocks.find(b => b.type === 'break')!;
    expect(brk.minutes).toBe(210);
  });

  it('два разрыва подряд: prod, break, prod, break, prod', () => {
    const tl = mkTimeline({
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T18:30:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', 'work', 'prod'),
        seg('2026-10-20T14:00:00Z', '2026-10-20T14:30:00Z', 'work', 'prod'),
        seg('2026-10-20T18:00:00Z', '2026-10-20T18:30:00Z', 'work', 'prod'),
      ],
    });
    const m = buildTimelineModel(tl, 'approx', false, false);
    expect(m.blocks.map(b => b.type)).toEqual(['stage', 'break', 'stage', 'break', 'stage']);
  });

  it('ровно 120 мин — НЕ разрыв (порог строго > 120): блок не рвётся', () => {
    const tl = mkTimeline({
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T13:00:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', 'work', 'prod'),
        seg('2026-10-20T12:30:00Z', '2026-10-20T13:00:00Z', 'work', 'prod'),
      ],
    });
    const m = buildTimelineModel(tl, 'approx', false, false);
    expect(m.blocks.map(b => b.type)).toEqual(['stage']);
    const b = m.blocks[0]!;
    if (b.type === 'stage') expect(b.minutes).toBe(60);
  });

  // === Хвостовой break: пауза ПОСЛЕ последнего блока (pushBreak(last.to, to0)) ===
  it('пауза в конце ленты (после последнего блока) → хвостовой break', () => {
    const tl = mkTimeline({
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T18:00:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', 'work', 'prod'),
      ],
    });
    const m = buildTimelineModel(tl, 'approx', false, false);
    // prod заканчивается в 10:30; to = 18:00 → пауза 450 мин > 120 → хвостовой break.
    const last = m.blocks[m.blocks.length - 1]!;
    expect(last.type, 'хвостовой break').toBe('break');
    if (last.type === 'break') expect(last.minutes).toBe(450);
  });

  // === Граница > 120 для СМЕНЫ этапа: пауза ровно 120 МЕЖДУ разными этапами → НЕ break ===
  // Мутация «>=» → break на ровно 120 → red.
  it('граница: пауза ровно 120 мин между этапами → НЕ break (строго > 120; мутация >= красит)', () => {
    const tl = mkTimeline({
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T13:00:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', 'work', 'cut'),
        seg('2026-10-20T12:30:00Z', '2026-10-20T13:00:00Z', 'work', 'plan'),
      ],
    });
    const m = buildTimelineModel(tl, 'approx', false, false);
    // cut (10:00–10:30) → plan (12:30–13:00): пауза ровно 120 мин.
    // Разные этапы → два блока; пауза 120 НЕ > 120 → break нет.
    expect(m.blocks.map(b => b.type), 'ровно 120 → без break').toEqual(['stage', 'stage']);
  });
});

// === Мажор 2: дорожка ревью не отнимает у основной ленты ===
describe('buildTimelineModel: дорожка ревью', () => {
  it('60 мин main + 40 мин review → minutes блока = 60 (main), reviewMinutes = 40', () => {
    const tl = mkTimeline({
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T11:00:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z', 'work', 'prod'),
        seg('2026-10-20T10:20:00Z', '2026-10-20T11:00:00Z', 'work', 'prod', { track: 'review' }),
      ],
    });
    const m = buildTimelineModel(tl, 'approx', false, false);
    const b = m.blocks[0]!;
    if (b.type !== 'stage') throw new Error('ожидал stage-блок');
    // Основная лента ∝ 60 (main), ревью — отдельная дорожка (40), не в ширине.
    expect(b.minutes).toBe(60);
    expect(b.reviewMinutes).toBe(40);
    expect(b.segments.length).toBe(1);        // только main-сегмент в ленте
    expect(b.reviewSegments.length).toBe(1); // review — в дорожке
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

// === Мажор 3: «сейчас» — на НАРИСОВАННОЙ (сжатой) шкале, а не по календарю ===
describe('nowCompressedFraction: «сейчас» на сжатой шкале', () => {
  // prod 10:00–11:00 (60) → разрыв 11:00–20:00 (9 ч, сжат) → prod 20:00–20:30 (30).
  // Веса: stage=60, break=40, stage=30; total=130.
  function mkModel() {
    const tl = mkTimeline({
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T20:30:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z', 'work', 'prod'),
        seg('2026-10-20T20:00:00Z', '2026-10-20T20:30:00Z', 'work', 'prod'),
      ],
    });
    return buildTimelineModel(tl, 'approx', false, false);
  }

  it('«сейчас» внутри блока → позиция по сжатой шкале (≠ календарной)', () => {
    const m = mkModel();
    // 20:15 — внутри второго prod-блока [20:00, 20:30].
    const frac = nowCompressedFraction(m.blocks, '2026-10-20T20:15:00Z');
    expect(frac).not.toBeNull();
    // Сжатая: (60 + 40 + 30·0.5)/130 = 115/130 ≈ 0.8846.
    expect(frac!).toBeCloseTo(115 / 130, 5);
    // Календарная: (20:15−10:00)/(20:30−10:00) = 615/630 ≈ 0.976 — ДРУГАЯ (иначе тест пуст).
    const cal = nowFraction('2026-10-20T10:00:00Z', '2026-10-20T20:30:00Z', '2026-10-20T20:15:00Z');
    expect(frac!).not.toBeCloseTo(cal, 3);
  });

  it('«сейчас» в разрыве → на разрыве (центр сжатого блока)', () => {
    const m = mkModel();
    // 15:00 — внутри разрыва [11:00, 20:00].
    const frac = nowCompressedFraction(m.blocks, '2026-10-20T15:00:00Z');
    expect(frac).not.toBeNull();
    // (60 + 40/2)/130 = 80/130 ≈ 0.615.
    expect(frac!).toBeCloseTo(80 / 130, 5);
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
  // data-testid сегментов уникален (индекс блока+сегмента), а ВИД — в data-kind.
  it('сегменты: каждый вид — свой data-kind и цвет-токен', () => {
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
      const node = findNodeByAttr(c, 'data-kind', kind);
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
    const node = findNodeByAttr(c, 'data-kind', 'waiting')!;
    const aria = node.getAttribute('aria-label') ?? '';
    expect(aria, 'aria-label несёт вид «ждёт вас»').toContain('ждёт вас');
  });

  // === 2. Дорожка ревью (hasReview) ===
  it('дорожка ревью: легенда показывает «ревью», когда есть track=review', () => {
    const c = renderTab(mkStats());
    const legend = findNodeByAttr(c, 'data-testid', 'stats-legend')!;
    expect(textNodes(legend).join('')).toContain('ревью');
  });

  // === 2b. Дорожка ревью в РЕНДЕРЕ: полоса = доля ревью в этапе (review/(main+review) от блока) ===
  // Мутация «убрать stats-review-track» → track=null → red.
  it('дорожка ревью: полоса stats-review-track = доля ревью в этапе (не абсолютные минуты)', () => {
    const c = renderTab(mkStats());
    const track = findNodeByAttr(c, 'data-testid', 'stats-review-track');
    expect(track, 'дорожка ревью рисуется').not.toBeNull();
    // prod: main 55 + review 40 → доля 40/95 ≈ 42.1% (от ширины блока, ∝ main-минутам).
    const bar = track!.childNodes.find(n => n.nodeType === 1) as unknown as { style?: Record<string, string> } | undefined;
    expect(bar, 'полоса ревью').toBeDefined();
    const w = parseFloat(bar!.style!.width);
    expect(w, 'ширина = доля ревью в этапе (40/95)').toBeCloseTo((40 / 95) * 100, 2);
  });

  // === 3. Сжатие паузы > 2 ч: число и подпись ===
  it('сжатие: разрыв 15 ч 35 мин → блок «разрыв · 15 ч 35 мин» (число + подпись)', () => {
    const c = renderTab(mkStats());
    // На 1280 подпись разрыва видна (b.label).
    const brk = findNodeByAttr(c, 'data-testid', 'stats-seg-break');
    expect(brk, 'сжатый разрыв').not.toBeNull();
    expect(textNodes(brk!).join('')).toContain('разрыв · 15 ч 35 мин');
  });

  // === 3c. Легенда «⫽ — длинная пауза сжата» — ТОЛЬКО когда разрывы реально есть ===
  // Мутация «всегда» (легенда без условия hasBreak) → red.
  it('легенда «⫽ — длинная пауза сжата» — только когда есть разрывы (мутация «всегда» красит)', () => {
    // С разрывами (mkStats: 15 ч 35 мин) → легенда есть.
    const c1 = renderTab(mkStats());
    expect(findNodeByAttr(c1, 'data-testid', 'stats-legend-break'), 'с разрывами — легенда есть').not.toBeNull();
    // Без разрывов (сегменты подряд, паузы < 2 ч) → легенды нет.
    const noBreak = mkStats();
    noBreak.timeline = {
      ...noBreak.timeline,
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T11:00:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z', 'work', 'prod'),
        seg('2026-10-20T10:35:00Z', '2026-10-20T11:00:00Z', 'work', 'prod'),
      ],
      current: null,
    };
    const c2 = renderTab(noBreak);
    expect(findNodeByAttr(c2, 'data-testid', 'stats-legend-break'), 'без разрывов — легенды нет').toBeNull();
  });

  // === 3b. «Сейчас» — на нарисованной (сжатой) шкале (мажор 3) ===
  it('«сейчас»: отметка stats-now-marker рисуется (мутация «не рисуется» красит)', () => {
    const c = renderTab(mkStats());
    const marker = findNodeByAttr(c, 'data-testid', 'stats-now-marker');
    expect(marker, 'отметка «сейчас» на ленте').not.toBeNull();
  });

  it('«сейчас»: позиция — по СЖАТОЙ шкале (left ≠ календарной доле)', () => {
    // prod 10:00–11:00 (60) → разрыв 11:00–20:00 → prod 20:00–20:30 (30); current.since=20:15.
    // Веса: 60 + 40 + 30 = 130. 20:15 внутри второго блока: (60+40+30·0.5)/130 = 115/130.
    const stats = mkStats();
    stats.timeline = {
      ...stats.timeline,
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T20:30:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z', 'work', 'prod'),
        seg('2026-10-20T20:00:00Z', '2026-10-20T20:30:00Z', 'work', 'prod'),
      ],
      current: { kind: 'waiting', since: '2026-10-20T20:15:00Z', taskId: null },
    };
    const c = renderTab(stats);
    const marker = findNodeByAttr(c, 'data-testid', 'stats-now-marker')!;
    const left = (marker as unknown as { style: Record<string, string> }).style?.left ?? '';
    // Сжатая позиция 115/130 ≈ 88.5% (календарная была бы 615/630 ≈ 97.6%).
    expect(left, 'left по сжатой шкале').toBe('88.5%');
  });

  // Мажор (доработка-2): на 360 маркер «сейчас» позиционируется по НАРИСОВАННОЙ ленте (640),
  // а не по скроллпорту (360): содержащий блок маркера = flex-строка (minWidth 640), не обёртка.
  // Мутация «position:relative обратно на обёртку» → содержащий блок = обёртка (без minWidth 640) → red.
  it('«сейчас» (360): содержащий блок маркера = нарисованная лента (minWidth 640), не скроллпорт', () => {
    const stats = mkStats();
    stats.timeline = {
      ...stats.timeline,
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T20:30:00Z',
      segments: [
        seg('2026-10-20T10:00:00Z', '2026-10-20T11:00:00Z', 'work', 'prod'),
        seg('2026-10-20T20:00:00Z', '2026-10-20T20:30:00Z', 'work', 'prod'),
      ],
      current: { kind: 'waiting', since: '2026-10-20T20:15:00Z', taskId: null },
    };
    const c = renderTab(stats, true); // мобильный расклад
    const marker = findNodeByAttr(c, 'data-testid', 'stats-now-marker')!;
    // Содержащий блок = ближайший позиционированный предок маркера (absolute → nearest position:relative).
    type Dom = { parentNode: Dom | null; style: Record<string, string> };
    let pos: Dom | null = (marker as unknown as Dom).parentNode as Dom | null;
    while (pos && pos.style?.position !== 'relative') pos = pos.parentNode;
    expect(pos, 'содержащий блок маркера').not.toBeNull();
    // Это flex-строка ленты (minWidth 640), а НЕ обёртка-скроллпорт (360).
    expect(pos!.style.minWidth, 'содержащий = нарисованная лента 640').toBe('640px');
  });

  // === 4. Клик → цель перехода (этап в «Хронологии») ===
  it('клик по линейке/сегменту → focusFeatureStage(key) (якорь этапа)', () => {
    const c = renderTab(mkStats());
    const ruler = findNodeByAttr(c, 'data-testid', 'stats-ruler-prod-3')!;
    act(() => { (reactProps(ruler)!.onClick as () => void)(); });
    // Якорь этапа выставлен на prod (тот же приём, что плашка/панель: setFeaturePageTab + focusFeatureStage).
    const focus = __test.stageFocus();
    expect(focus, 'якорь этапа после клика').not.toBeNull();
    expect(focus!.key).toBe('prod');
  });

  // Мутация (3): подменить цель клика (focusFeatureStage('') вместо key) → якорь пуст.
  it('мутация: цель клика = key (не пустая строка)', () => {
    const c = renderTab(mkStats());
    const ruler = findNodeByAttr(c, 'data-testid', 'stats-ruler-cut-0')!;
    act(() => { (reactProps(ruler)!.onClick as () => void)(); });
    expect(__test.stageFocus()!.key).toBe('cut');
  });

  // === 4b. Клик по СЕГМЕНТУ ленты (мажор 8) — раньше покрывался только клик по линейке ===
  it('клик по сегменту → focusFeatureStage(этап сегмента) (не «zzz»)', () => {
    const c = renderTab(mkStats());
    // Первый сегмент первого блока (cut, work, taskId=t-cut).
    const seg = findNodeByAttr(c, 'data-testid', 'stats-seg-0-0');
    expect(seg, 'сегмент первого блока').not.toBeNull();
    act(() => { (reactProps(seg!)!.onClick as () => void)(); });
    // Якорь — на этапе, к которому принадлежит сегмент (cut). Мутация openStage('zzz') → red.
    const focus = __test.stageFocus();
    expect(focus, 'якорь после клика по сегменту').not.toBeNull();
    expect(focus!.key).toBe('cut');
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
    const ruler = findNodeByAttr(c, 'data-testid', 'stats-ruler-prod-3')!;
    const st = (ruler as unknown as { style: Record<string, string> }).style;
    expect(st.minHeight).toBe('40px');
  });

  // === 9b. 360: тач-цель СЕГМЕНТА ленты = 40 (высота строки сегментов) ===
  // Мутация «высота 26» (не 40) → red.
  it('на 360 тач-цель сегмента = 40 (высота строки сегментов; мутация «26» красит)', () => {
    const c = renderTab(mkStats(), true);
    const seg = findNodeByAttr(c, 'data-testid', 'stats-seg-0-0')!;
    // Родитель сегмента — строка сегментов (height 40 на 360).
    const row = (seg as unknown as { parentNode: { style: Record<string, string> } | null }).parentNode;
    expect(row, 'строка сегментов').not.toBeNull();
    expect(row!.style.height, 'высота строки сегментов на 360 = 40').toBe('40px');
  });
});

// === Чистые функции: форматирование и переводы (блоки 2–3, Ф7b) ===

describe('formatTokens', () => {
  it('0 / <1000 / тыс. / М / null', () => {
    expect(formatTokens(0)).toBe('0');
    expect(formatTokens(999)).toBe('999');
    expect(formatTokens(1000)).toBe('1 тыс.');
    expect(formatTokens(640000)).toBe('640 тыс.');
    expect(formatTokens(1200000)).toBe('1,2М');
    expect(formatTokens(null)).toBe('');
  });
});

describe('formatUsd', () => {
  it('0 / дробная / null', () => {
    expect(formatUsd(0)).toBe('$0,00');
    expect(formatUsd(0.42)).toBe('$0,42');
    expect(formatUsd(15.6)).toBe('$15,60');
    expect(formatUsd(null)).toBe('');
  });
});

describe('sharePercent', () => {
  it('0..1 → %; null → пусто', () => {
    expect(sharePercent(0)).toBe('0 %');
    expect(sharePercent(0.31)).toBe('31 %');
    expect(sharePercent(1)).toBe('100 %');
    expect(sharePercent(null)).toBe('');
  });
});

describe('roleGroupLabel', () => {
  it('сырой taskRole → русская подпись (roleLabel); step без stepRef → «шаг»', () => {
    expect(roleGroupLabel('review')).toBe('ревью');
    expect(roleGroupLabel('qa')).toBe('проверка');
    expect(roleGroupLabel('final')).toBe('финал');
    expect(roleGroupLabel('merge')).toBe('мерж');
    expect(roleGroupLabel('cut')).toBe('разрез');
    expect(roleGroupLabel('plan')).toBe('план');
    expect(roleGroupLabel('rework')).toBe('доработка');
    expect(roleGroupLabel('consult')).toBe('консультация');
    // 'step' без stepRef/lane → roleLabel пуст → «шаг» (не сырой 'step').
    expect(roleGroupLabel('step')).toBe('шаг');
    // неизвестный → как есть.
    expect(roleGroupLabel('weird')).toBe('weird');
  });
});

describe('modelGroupLabel', () => {
  it('modelFull → читаемое имя (modelLabel); незнакомый → как есть', () => {
    expect(modelGroupLabel('claude-opus-4-8')).toBe('Opus');
    expect(modelGroupLabel('Qwen')).toBe('Qwen');
    expect(modelGroupLabel('unknown-model')).toBe('unknown-model');
  });
});

describe('personaGroupLabel', () => {
  it('personaId → имя; недоступна → «персона недоступна» (не UUID)', () => {
    expect(personaGroupLabel('p-kira', mockPersonas)).toBe('Кира');
    expect(personaGroupLabel('p-denis', mockPersonas)).toBe('Денис');
    expect(personaGroupLabel('unknown-id', mockPersonas)).toBe('персона недоступна');
  });
});

// === Рендер-тесты: блоки 2–3 (Ф7b) ===

// Полный stats для блоков 2–3: журнал (все карточки), longestNow, разрезы по трём осям, расход с shared.
function mkFullStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: mkTimeline({
      totals: {
        elapsedMinutes: 900, workWallMinutes: 300, agentsSumMinutes: 520, notWorkingMinutes: 600,
        waitingMinutes: 120, idleMinutes: 200, waitingCount: 3, longestWaitMinutes: 60,
        errorCount: 2, errorMinutes: 80,
      },
    }),
    breakdown: {
      personas: [
        { key: 'p-kira', minutes: 200, tokens: 640000, costUsd: 12.5, chatIds: ['c1', 'c2'] },
        { key: 'p-denis', minutes: 125, tokens: 300000, costUsd: null, chatIds: ['c3'] },
      ],
      models: [
        { key: 'claude-opus-4-8', minutes: 150, tokens: 500000, costUsd: 10, chatIds: ['c1'] },
        { key: 'Qwen', minutes: 100, tokens: 200000, costUsd: 0, chatIds: ['c2'] },
      ],
      roles: [
        { key: 'step', minutes: 300, tokens: 800000, costUsd: 15, chatIds: ['c1', 'c3'] },
        { key: 'review', minutes: 100, tokens: 100000, costUsd: 5, chatIds: ['c2'] },
      ],
    },
    longestNow: {
      taskId: 't-longest', chatId: 'c1', personaId: 'p-kira', stepRef: 'Ш3',
      minutes: 70, tokens: 640000, share: 0.31,
    },
    spend: {
      inputTokens: 1000000, outputTokens: 200000, cacheReadTokens: 500000, cacheCreationTokens: 100000,
      costUsd: 15.6, costPartial: true, hiddenCostModels: ['Qwen'],
      from: '2026-10-19T17:20:00Z',
      shared: { inputTokens: 100000, outputTokens: 50000, cacheReadTokens: 0, cacheCreationTokens: 0, costUsd: 1.2, turns: 5, method: 'journal' },
    },
    ...over,
  };
}

// Найти узел по его точному текстовому содержимому (для кнопок SegmentedControl без data-testid).
type FNodeLike2 = { nodeType: number; nodeName: string; childNodes: FNodeLike2[]; attributes?: Record<string, string> };
function findNodeByText(container: FNodeLike2, text: string): FNodeLike2 | null {
  const stack: FNodeLike2[] = [container];
  while (stack.length) {
    const n = stack.pop()!;
    if (textNodes(n as never).join('').trim() === text) return n;
    for (const ch of n.childNodes) stack.push(ch);
  }
  return null;
}

describe('FeatureStatsTab: блоки 2–3 (Ф7b)', () => {
  beforeEach(() => {
    mockOpenTask.calls = [];
    mockOpenChat.calls = [];
    clearFeatureStageFocus();
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === Карточки итогов ===
  it('«Всего прошло» + «Работа»: значения из totals (журнал)', () => {
    const c = renderTab(mkFullStats());
    const all = textNodes(c).join('');
    expect(all).toContain('Всего прошло');
    expect(all).toContain('Работа');
    // «Всего прошло»: значение карточки — по data-testid (stats-card-0), НЕ toContain по всему
    // тексту (там «15 ч» ловилось ещё и подписью разрыва «15 ч 35 мин»).
    const card0 = findNodeByAttr(c, 'data-testid', 'stats-card-0')!;
    expect(textNodes(card0).join(''), 'значение «Всего прошло» = 15 ч (900 мин)').toContain('15 ч');
    // «Работа»: 300 мин → «5 ч»; «агенты в сумме 8 ч 40 мин» (520): в журнале число точное, без «≈».
    expect(all).toContain('5 ч');
    expect(all).toContain('агенты в сумме 8 ч 40 мин');
    expect(all).not.toContain('агенты в сумме ≈');
  });

  it('journal: «Ожидание»/«Простой»/«Ошибки» — только если не null', () => {
    const c = renderTab(mkFullStats());
    const all = textNodes(c).join('');
    expect(all).toContain('Ожидание');
    expect(all).toContain('Простой');
    expect(all).toContain('Ошибки');
  });

  // Мутация: null у waitingMinutes → карточки «Ожидание» нет (не «0 мин»).
  it('мутация: null у waitingMinutes → карточки «Ожидание» нет', () => {
    const s = mkFullStats();
    s.timeline.totals.waitingMinutes = null;
    const c = renderTab(s);
    const all = textNodes(c).join('');
    expect(all).not.toContain('Ожидание');
  });

  it('approx: «Всего прошло» (точно) + «Работа ≈»; без «Ожидание»/«Простой»/«Ошибки»', () => {
    const s = mkFullStats({ source: 'approx' });
    // В approx прошлые ожидания/простой/ошибки не считаются → null.
    s.timeline.totals.waitingMinutes = null;
    s.timeline.totals.idleMinutes = null;
    s.timeline.totals.errorMinutes = null;
    const c = renderTab(s);
    const all = textNodes(c).join('');
    expect(all).toContain('Всего прошло');
    expect(all).toContain('≈ 5 ч'); // работа ≈
    expect(all).not.toContain('Ожидание');
    expect(all).not.toContain('Простой');
    expect(all).not.toContain('Ошибки');
  });

  // === «Дольше всех» ===
  it('longestNow: персона · шаг · минуты · доля · токены', () => {
    const c = renderTab(mkFullStats());
    const node = findNodeByAttr(c, 'data-testid', 'stats-longest');
    expect(node, 'блок «дольше всех»').not.toBeNull();
    const all = textNodes(node!).join('');
    expect(all).toContain('Дольше всех сейчас');
    expect(all).toContain('Кира'); // имя персоны (не UUID)
    expect(all).toContain('шаг Ш3');
    expect(all).toContain('1 ч 10 мин'); // 70 мин
    expect(all).toContain('31 % фичи'); // share
    expect(all).toContain('640 тыс. токенов'); // tokens
  });

  it('longestNow=null → блока нет', () => {
    const c = renderTab(mkFullStats({ longestNow: null }));
    expect(findNodeByAttr(c, 'data-testid', 'stats-longest'), 'нет блока').toBeNull();
  });

  // Мутация: personaId недоступна → «персона недоступна» (не UUID).
  it('мутация: longestNow с неизвестным personaId → «персона недоступна»', () => {
    const s = mkFullStats();
    s.longestNow = { ...s.longestNow!, personaId: 'unknown-persona' };
    const c = renderTab(s);
    const all = textNodes(findNodeByAttr(c, 'data-testid', 'stats-longest')!).join('');
    expect(all).toContain('персона недоступна');
  });

  // === Разрезы ===
  it('разрезы: заголовок + ось «персоны» (имена, не UUID) + токены', () => {
    const c = renderTab(mkFullStats());
    const bd = findNodeByAttr(c, 'data-testid', 'stats-breakdown');
    expect(bd, 'блок разрезов').not.toBeNull();
    const all = textNodes(bd!).join('');
    expect(all).toContain('Кто и на чём работал');
    expect(all).toContain('Кира');
    expect(all).toContain('Денис');
    expect(all).toContain('640 тыс.'); // токены крупно
  });

  // Минор: минуты группы в разрезе (formatDuration(g.minutes)) — раньше без теста (мутация зелёная).
  it('разрезы: минуты группы показываются (p-kira 200 мин → «3 ч 20 мин»)', () => {
    const c = renderTab(mkFullStats());
    const bd = findNodeByAttr(c, 'data-testid', 'stats-breakdown')!;
    const all = textNodes(bd).join('');
    // p-kira: minutes=200 → «3 ч 20 мин»; p-denis: minutes=125 → «2 ч 05 мин».
    expect(all, 'минуты p-kira').toContain('3 ч 20 мин');
    expect(all, 'минуты p-denis').toContain('2 ч 05 мин');
  });

  it('мутация: ось «модели» — modelFull → читаемое имя (Opus, не raw id)', () => {
    const c = renderTab(mkFullStats());
    const bd = findNodeByAttr(c, 'data-testid', 'stats-breakdown')!;
    // Переключаю ось на «модели».
    const btn = findNodeByText(bd as never, 'модели');
    expect(btn, 'кнопка оси «модели»').not.toBeNull();
    act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
    const all = textNodes(bd).join('');
    expect(all, 'модель claude-opus-4-8 → «Opus»').toContain('Opus');
    expect(all, 'не сырой id модели').not.toContain('claude-opus-4-8');
  });

  it('мутация: ось «роли» — taskRole → roleLabel (русские подписи, не сырой key)', () => {
    const c = renderTab(mkFullStats());
    const bd = findNodeByAttr(c, 'data-testid', 'stats-breakdown')!;
    const btn = findNodeByText(bd as never, 'роли');
    expect(btn, 'кнопка оси «роли»').not.toBeNull();
    act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
    const all = textNodes(bd).join('');
    // 'step' → «шаг», 'review' → «ревью» (не сырые 'step'/'review').
    expect(all, '«шаг» для step').toContain('шаг');
    expect(all, '«ревью» для review').toContain('ревью');
  });

  it('мутация: costPartial → пометка «цена по части моделей недоступна»', () => {
    const c = renderTab(mkFullStats()); // spend.costPartial = true
    const node = findNodeByAttr(c, 'data-testid', 'stats-breakdown-cost-partial');
    expect(node, 'пометка costPartial').not.toBeNull();
    expect(textNodes(node!).join('')).toContain('цена по части моделей недоступна');
  });

  it('мутация: costPartial=false → пометки нет', () => {
    const s = mkFullStats();
    s.spend = { ...s.spend!, costPartial: false, hiddenCostModels: [] };
    const c = renderTab(s);
    expect(findNodeByAttr(c, 'data-testid', 'stats-breakdown-cost-partial'), 'без пометки').toBeNull();
  });

  it('разрезы: costUsd=null у группы → цены нет; чаты раскрываются с «чат»', () => {
    const c = renderTab(mkFullStats());
    const bd = findNodeByAttr(c, 'data-testid', 'stats-breakdown')!;
    // Группы (personas): [p-kira (costUsd=12.5), p-denis (costUsd=null)].
    const groups = findAllByAttr(bd, 'data-testid', 'stats-break-group');
    expect(groups.length, 'две группы').toBe(2);
    // Открываю группу с costUsd=null (p-denis, вторая) — клик по строке.
    const denisBtn = findFirstButton(groups[1] as never);
    expect(denisBtn, 'кнопка-строка группы p-denis').not.toBeNull();
    act(() => { (reactProps(denisBtn as never)!.onClick as () => void)(); });
    // costUsd=null → на строке группы НЕТ цены (stats-break-cost). Мутация «показывать цену
    // при null» добавит stats-break-cost → тест краснеет.
    expect(findNodeByAttr(groups[1], 'data-testid', 'stats-break-cost'), 'цены нет при costUsd=null').toBeNull();
    // Чаты раскрываются: строка чата + кнопка «чат».
    const chats = findNodeByAttr(groups[1], 'data-testid', 'stats-break-chats');
    expect(chats, 'список чатов группы').not.toBeNull();
    expect(findNodeByAttr(chats!, 'data-testid', 'stats-break-chat'), 'строка чата').not.toBeNull();
    expect(findNodeByAttr(chats!, 'data-testid', 'nav-target-chat'), 'кнопка «чат»').not.toBeNull();
  });

  it('зонд: пустые breakdown (все оси) → «Пока нечего разрезать»', () => {
    const s = mkFullStats();
    s.breakdown = { personas: [], models: [], roles: [] };
    const c = renderTab(s);
    const all = textNodes(c).join('');
    expect(all).toContain('Пока нечего разрезать');
  });

  it('зонд: 1000 групп + длинные названия — рендер не падает', () => {
    const s = mkFullStats();
    const big: FeatureBreakdown = {
      personas: Array.from({ length: 1000 }, (_, i) => ({
        key: `p-${i}`, minutes: 10, tokens: null, costUsd: null, chatIds: [],
      })),
      models: [], roles: [],
    };
    s.breakdown = big;
    const c = renderTab(s);
    const bd = findNodeByAttr(c, 'data-testid', 'stats-breakdown');
    expect(bd, 'блок разрезов рендерится').not.toBeNull();
  });

  // === Расход ===
  it('spend: токены (вход+выход) крупно · кэш мелко · цена · from · shared', () => {
    const c = renderTab(mkFullStats());
    const sp = findNodeByAttr(c, 'data-testid', 'stats-spend');
    expect(sp, 'блок расхода').not.toBeNull();
    const all = textNodes(sp!).join('');
    // input 1 000 000 + output 200 000 = 1 200 000 → «1,2М».
    expect(all).toContain('1,2М');
    expect(all).toContain('токенов (вход + выход)');
    // Кэш (мелко, отдельной строкой): чтение 500 000 → «500 тыс.», создание 100 000 → «100 тыс.».
    expect(all).toContain('кэш:');
    expect(all).toContain('500 тыс. чтение');
    expect(all).toContain('100 тыс. создание');
    // Цена 15.6 → «$15,60» + пометка costPartial (hiddenCostModels).
    expect(all).toContain('$15,60');
    expect(all).toContain('часть моделей без цены: Qwen');
    // from (расход ненулевой → показываем).
    expect(all).toContain('данные с');
    // shared: 100 000 + 50 000 = 150 000 → «150 тыс.» + «оценка, по журналу».
    expect(all).toContain('доля общих чатов');
    expect(all).toContain('150 тыс.');
    expect(all).toContain('оценка, по журналу');
  });

  // === Item 11: shared — «150 тыс. токенов» (число = токены, не часы) ===
  it('shared: «в т. ч. доля общих чатов ≈ 150 тыс. токенов» (число = токены, не часы)', () => {
    const c = renderTab(mkFullStats());
    const shared = findNodeByAttr(c, 'data-testid', 'stats-spend-shared')!;
    expect(textNodes(shared).join(''), 'shared несёт «токенов» после числа').toContain('150 тыс. токенов');
  });

  it('spend=null → блока нет (без ошибки)', () => {
    const c = renderTab(mkFullStats({ spend: null }));
    expect(findNodeByAttr(c, 'data-testid', 'stats-spend'), 'нет блока расхода').toBeNull();
  });

  // Минор: правило «данные с …» — только если расход НЕРО (иначе у пустой фичи это дата эпика).
  it('расход нулевой (0 токенов, $0) → строки «данные с …» нет', () => {
    const s = mkFullStats();
    s.spend = {
      inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0,
      costUsd: 0, costPartial: false, hiddenCostModels: [],
      from: '2026-10-19T17:20:00Z', shared: null,
    };
    const c = renderTab(s);
    expect(findNodeByAttr(c, 'data-testid', 'stats-spend-from'), '«данные с …» нет при нулевом расходе').toBeNull();
  });

  // Мутация: кэш НЕ в крупную цифру (крупная = вход+выход, кэш отдельной строкой).
  it('мутация: крупная цифра = вход+выход (1,2М), кэш не смешан в неё', () => {
    const c = renderTab(mkFullStats());
    const sp = findNodeByAttr(c, 'data-testid', 'stats-spend')!;
    const big = findNodeByAttr(sp, 'data-testid', 'stats-spend-tokens')!;
    // Крупная цифра ровно вход+выход (1 200 000 → «1,2М»), кэш (600 тыс.) в неё не входит.
    expect(textNodes(big).join('')).toBe('1,2М');
  });

  // Мутация: shared помечен «оценка» (не просто число).
  it('мутация: shared помечен «оценка, по …»', () => {
    const c = renderTab(mkFullStats());
    const shared = findNodeByAttr(c, 'data-testid', 'stats-spend-shared')!;
    const t = textNodes(shared).join('');
    expect(t).toContain('оценка, по');
  });

  it('мутация: approx → shared «оценка, по задачам ≈»', () => {
    const s = mkFullStats({ source: 'approx' });
    s.spend = { ...s.spend!, shared: { ...s.spend!.shared!, method: 'approx' } };
    const c = renderTab(s);
    const shared = findNodeByAttr(c, 'data-testid', 'stats-spend-shared')!;
    expect(textNodes(shared).join('')).toContain('оценка, по задачам ≈');
  });

  // === Мажор 5: подпись метода shared — из spend.shared.method, НЕ из stats.source ===
  it('source=journal + shared.method=approx → «оценка, по задачам ≈» (метод ≠ source)', () => {
    const s = mkFullStats({ source: 'journal' }); // source=journal
    s.spend = { ...s.spend!, shared: { ...s.spend!.shared!, method: 'approx' } }; // method=approx
    const c = renderTab(s);
    const shared = findNodeByAttr(c, 'data-testid', 'stats-spend-shared')!;
    // Если бы подпись бралась из source (journal) → было бы «по журналу»; из method (approx) → «по задачам ≈».
    expect(textNodes(shared).join(''), 'метод shared — из spend.shared.method').toContain('оценка, по задачам ≈');
  });

  // === «Не работали»: значение = totals.notWorkingMinutes (600 → «10 ч»; мутация «0» красит) ===
  it('«Не работали»: значение = totals.notWorkingMinutes (600 → «10 ч», не «0 мин»)', () => {
    const c = renderTab(mkFullStats());
    const card = findNodeByAttr(c, 'data-testid', 'stats-card-2')!;
    const t = textNodes(card).join('');
    expect(t, 'карточка «Не работали»').toContain('Не работали');
    expect(t, 'значение 10 ч (600 мин), не «0 мин»').toContain('10 ч');
  });

  // === «время · токены»: при «токены» полоса и доля группы меняются (мутация «метрика игнорируется» красит) ===
  it('«время · токены»: при «токены» полоса и доля группы меняются (метрика не игнорируется)', () => {
    const c = renderTab(mkFullStats());
    const bd = findNodeByAttr(c, 'data-testid', 'stats-breakdown')!;
    // p-denis = 2-я группа (personas: [p-kira, p-denis]).
    let groups = findAllByAttr(bd, 'data-testid', 'stats-break-group');
    const denis = groups[1]!;
    const shareBefore = textNodes(findNodeByAttr(denis, 'data-testid', 'stats-break-share')!).join('');
    const barBefore = (findNodeByAttr(denis, 'data-testid', 'stats-break-bar') as unknown as { style: Record<string, string> }).style.width ?? '';
    // Переключаю метрику на «токены».
    const btn = findNodeByText(bd as never, 'токены');
    expect(btn, 'кнопка метрики «токены»').not.toBeNull();
    act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
    groups = findAllByAttr(bd, 'data-testid', 'stats-break-group');
    const denis2 = groups[1]!;
    const shareAfter = textNodes(findNodeByAttr(denis2, 'data-testid', 'stats-break-share')!).join('');
    const barAfter = (findNodeByAttr(denis2, 'data-testid', 'stats-break-bar') as unknown as { style: Record<string, string> }).style.width ?? '';
    // p-denis: время (125/325=38%, полоса 125/200=62.5%) → токены (300000/940000=32%, полоса 300000/640000=46.875%).
    expect(shareBefore, 'доля p-denis (время)').toBe('38 %');
    expect(shareAfter, 'доля p-denis (токены) — изменилась').toBe('32 %');
    expect(barBefore, 'полоса p-denis (время)').toBe('62.5%');
    expect(barAfter, 'полоса p-denis (токены) — изменилась').toBe('46.875%');
  });
});

// Первый <button> внутри узла (кнопка-строка группы разреза). nodeName в fakeDom — в верхнем.
function findFirstButton(n: FNodeLike2): FNodeLike2 | null {
  if (n.nodeType === 1 && n.nodeName.toLowerCase() === 'button') return n;
  for (const ch of n.childNodes) {
    const r = findFirstButton(ch);
    if (r) return r;
  }
  return null;
}

// Все узлы с атрибутом attr=value в поддереве (findNodeByAttr даёт только первый).
function findAllByAttr(n: FNodeLike2, attr: string, value: string): FNodeLike2[] {
  const out: FNodeLike2[] = [];
  const walk = (x: FNodeLike2) => {
    if (x.nodeType === 1 && (x.attributes?.[attr] ?? null) === value) out.push(x);
    for (const ch of x.childNodes) walk(ch);
  };
  walk(n);
  return out;
}
