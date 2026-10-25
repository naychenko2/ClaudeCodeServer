// Вкладка «Статистика» (волна 3, Ф7a, макет features-overview-3-final §«Статистика»;
// сборка «Пульса» — ST-F14a по макету feature-stats-5, вариант A).
// БЛОК 1 — лента времени: линейка этапов, сегменты (∝ минутам), СЖАТИЕ пауз > 2 ч
// («разрыв · N ч»), дорожка ревью, ось + «сейчас», легенда, значки точности, «задача».
//
// Чистые функции (FeatureStatsBits) — в node, без DOM. Рендер-тесты (fakeDom):
// сегменты по видам, дорожка ревью, сжатие паузы (число + подпись), клик → цель перехода,
// source/truncated/current, пустые состояния, фичи без stats.
//
// Мутации (в отчёте): (1) порог 2 ч → 20 ч → «сжатие паузы» краснеет;
// (2) потерять track=review → «дорожка ревью» краснеет; (3) подменить цель клика
// (focusFeatureStage('') вместо key) → «клик → этап» краснеет; (4) убрать «≈» из фразы
// точности / вернуть повтор → тесты «точность:» краснеют; (5) сегмент waiting как work
// → «сегменты по видам» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStatsTab, MORE_OPEN_KEY } from './FeatureStatsTab';
import {
  formatDuration, formatClock, formatDateTime, kindLabel, kindBackground,
  buildTimelineModel, nowFraction, nowCompressedFraction, PAUSE_BREAK_MIN,
} from './FeatureStatsBits';
import {
  formatTokens, formatUsd, sharePercent, roleGroupLabel, modelGroupLabel, personaGroupLabel,
} from './FeatureStatsFormat';
import { clearFeatureStageFocus, setFeaturePageTab, __test } from '../../lib/featurePage';
import { getBackNav } from '../../lib/featureBackNav';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';
import type { FeatureRun, FeatureStage, StageKey, StageState } from '../../types/featureRun';
import { C, FS, SP, FONT } from '../../lib/design';

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
      { key: 'cut', startedAt: '2026-10-19T17:20:00Z', barEnd: '2026-10-19T17:55:00Z' },
      { key: 'plan', startedAt: '2026-10-19T17:58:00Z', barEnd: '2026-10-19T18:30:00Z' },
      { key: 'prod', startedAt: '2026-10-20T10:05:00Z', barEnd: null },
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

function renderTab(stats: FeatureStats | null, isMobile = false, run: FeatureRun | null = null, featureId: string | null = null) {
  installFakeDom();
  const { container } = mount(createElement(FeatureStatsTab, { stats, isMobile, run, featureId }));
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

  // === 5. Строка точности (Майя, P1): одна фраза без повтора ===
  it('точность: approx → одна фраза «≈ по задачам — числа приблизительные»', () => {
    const c = renderTab(mkStats({ source: 'approx' }));
    const t = textNodes(findNodeByAttr(c, 'data-testid', 'stats-accuracy')!).join('');
    expect(t, 'точная фраза approx-источника').toBe('≈ по задачам — числа приблизительные');
  });

  // Мутация (4): убрать «≈» из фразы → тест выше краснеет.
  it('точность: journal → «по журналу ходов», без «≈»', () => {
    const c = renderTab(mkStats({ source: 'journal' }));
    const t = textNodes(findNodeByAttr(c, 'data-testid', 'stats-accuracy')!).join('');
    expect(t, 'точная фраза journal-источника').toBe('по журналу ходов');
  });

  // Прежняя реализация говорила «по журналу по журналу» и «по задачам (≈) ≈» — этот тест
  // ловит повтор: источник назван ровно один раз. Мутация: вернуть дубль → краснеет.
  it('точность: без повтора — источник назван ровно один раз', () => {
    const c = renderTab(mkStats({ source: 'journal', truncated: true }));
    const t = textNodes(findNodeByAttr(c, 'data-testid', 'stats-accuracy')!).join('');
    expect((t.match(/по журналу/g) ?? []).length, 'повтор «по журналу»').toBe(1);
    expect((t.match(/по задачам/g) ?? []).length, '«по задачам» при journal-источнике').toBe(0);
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
  // Дефект ee6825ff: без projectId openTaskInSection ведёт в личный «Календарь».
  // Мутация: убрать projectId из вызова → тест краснеет.
  it('«задача»: по каждому этапу с taskId — кнопка; клик → openTaskInSection(taskId, projectId эпика)', async () => {
    const run: FeatureRun = {
      featureId: 'f-cut', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
      epic: { id: 'e-1', title: 'Эпик', projectId: 'proj-1' },
    };
    const c = renderTab(mkStats(), false, run, 'f-cut');
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
    // Клик по ПЕРВОЙ (cut) → openTaskInSection({ id: 't-cut', projectId: 'proj-1' }).
    await act(async () => { (reactProps(btns[0]!)!.onClick as () => void)(); });
    expect(mockOpenTask.calls.some(x => x.id === 't-cut'), 'клик открыл задачу t-cut').toBe(true);
    // Дефект ee6825ff: переход несёт projectId — задача открывается в разделе проекта,
    // а не в личном «Календаре».
    expect(mockOpenTask.calls[0], 'в навигацию уходит projectId эпика').toEqual({ id: 't-cut', projectId: 'proj-1' });
    // recordFeatureReturn у перехода: записана точка возврата «к фиче».
    const back = getBackNav();
    expect(back?.from, 'точка возврата — фича').toBe('f-cut');
    expect(back?.fromKind, 'дискриминатор — feature').toBe('feature');
    expect(back?.to, 'цель возврата — задача t-cut').toEqual({ kind: 'task', id: 't-cut' });
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

// === Ступени Ганта (W4-4F2, макет features-overview-4 §pt4, вариант А) ===
// run с 8 этапами: design — skipped («этапа не было»), prod — разбивка
// «работа · ожидание · простой», review — ревью-трек, merge — текущий (отметка
// «сейчас»). Без run (старые ответы) — прежняя полоса: её тесты выше.
//
// Мутации (в отчёте): (1) убрать строку этапа из рендера → «каждый этап — своя
// строка» краснеет; (2) не обнулять minutes при skipped в модели → «этапа не было»
// краснеет (минуты появились бы справа).

function mkGanttStage(key: StageKey, state: StageState, minutes: number, over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key, state, passes: 0, taskIds: [], rounds: [],
    firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: minutes,
    overlapsActive: false, executors: [], ...over,
  };
}

// Конвейер из 8 этапов: cut → design(skipped) → plan → prod → review → qa → final → merge(active).
function mkGanttRun(): FeatureRun {
  return {
    featureId: 'f-gantt',
    state: 'running',
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    closed: false,
    stages: [
      mkGanttStage('cut', 'done', 35, { firstStartedAt: '2026-10-19T17:20:00Z', lastCompletedAt: '2026-10-19T17:55:00Z', barEnd: '2026-10-19T17:55:00Z' }),
      mkGanttStage('design', 'skipped', 0),
      mkGanttStage('plan', 'done', 32, { firstStartedAt: '2026-10-19T17:58:00Z', lastCompletedAt: '2026-10-19T18:30:00Z', barEnd: '2026-10-19T18:30:00Z' }),
      mkGanttStage('prod', 'done', 370, { firstStartedAt: '2026-10-20T10:05:00Z', lastCompletedAt: '2026-10-20T16:15:00Z', barEnd: '2026-10-20T16:15:00Z' }),
      mkGanttStage('review', 'done', 40, { firstStartedAt: '2026-10-20T11:20:00Z', lastCompletedAt: '2026-10-20T12:00:00Z', barEnd: '2026-10-20T12:00:00Z' }),
      mkGanttStage('qa', 'done', 15, { firstStartedAt: '2026-10-20T16:20:00Z', lastCompletedAt: '2026-10-20T16:35:00Z', barEnd: '2026-10-20T16:35:00Z' }),
      mkGanttStage('final', 'done', 12, { firstStartedAt: '2026-10-20T16:40:00Z', lastCompletedAt: '2026-10-20T16:52:00Z', barEnd: '2026-10-20T16:52:00Z' }),
      mkGanttStage('merge', 'active', 4, { firstStartedAt: '2026-10-20T17:00:00Z', tone: 'run' }),
    ],
    now: [{ taskId: 't-merge', stage: 'merge', title: 'Мерж', startedAt: '2026-10-20T17:00:00Z' }],
    attention: null,
  };
}

// Таймлайн тех же этапов: prod — 250 мин работа + 80 ожидание + 40 простой; review —
// ревью-трек; остальные — короткие рабочие куски.
function mkGanttStats(): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-19T17:20:00Z',
      to: '2026-10-20T17:10:00Z',
      stages: [],
      segments: [
        seg('2026-10-19T17:20:00Z', '2026-10-19T17:55:00Z', 'work', 'cut', { taskId: 't-cut' }),
        seg('2026-10-19T17:58:00Z', '2026-10-19T18:30:00Z', 'work', 'plan', { taskId: 't-plan' }),
        seg('2026-10-20T10:05:00Z', '2026-10-20T14:15:00Z', 'work', 'prod', { taskId: 't-prod' }),
        seg('2026-10-20T14:15:00Z', '2026-10-20T15:35:00Z', 'waiting', 'prod'),
        seg('2026-10-20T15:35:00Z', '2026-10-20T16:15:00Z', 'idle', 'prod'),
        seg('2026-10-20T11:20:00Z', '2026-10-20T12:00:00Z', 'work', 'review', { track: 'review', taskId: 't-review' }),
        seg('2026-10-20T16:20:00Z', '2026-10-20T16:35:00Z', 'work', 'qa'),
        seg('2026-10-20T16:40:00Z', '2026-10-20T16:52:00Z', 'work', 'final'),
        seg('2026-10-20T17:00:00Z', '2026-10-20T17:04:00Z', 'work', 'merge'),
      ],
      current: { kind: 'waiting', since: '2026-10-20T17:10:00Z', taskId: 't-merge' },
      totals: {
        elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0,
        waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
        errorCount: null, errorMinutes: null,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
  };
}

function renderGantt(isMobile = false) {
  installFakeDom();
  const { container } = mount(createElement(FeatureStatsTab, { stats: mkGanttStats(), run: mkGanttRun(), isMobile }));
  return container;
}

describe('FeatureStatsTab: ступени Ганта (W4-4F2)', () => {
  beforeEach(() => {
    mockOpenTask.calls = [];
    clearFeatureStageFocus();
    // Вкладка «Статистика» — как в жизни: тест перехода должен видеть смену
    // вкладки, а не дефолт страницы.
    setFeaturePageTab('stats');
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. Строка на каждый этап, минуты справа (мутация: убрать строку — краснеет) ===
  it('каждый этап — своя строка; минуты — справа от полосы', () => {
    const c = renderGantt();
    for (const key of ['cut', 'design', 'plan', 'prod', 'review', 'qa', 'final', 'merge']) {
      expect(findNodeByAttr(c, 'data-testid', `gantt-row-${key}`), `строка этапа ${key}`).not.toBeNull();
    }
    // prod: 370 мин → «6 ч 10 мин» справа.
    const min = findNodeByAttr(c, 'data-testid', 'gantt-minutes-prod')!;
    expect(textNodes(min).join('')).toBe('6 ч 10 мин');
    // Мерж (4 мин) — тоже строка с минутами: подпись не зависит от ширины полосы.
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'gantt-minutes-merge')!).join('')).toBe('4 мин');
  });

  // === 2. skipped: «этапа не было», без минут (мутация: не обнулять minutes — краснеет) ===
  it('skipped-этап: строка «этапа не было», без минут и без полосы', () => {
    const c = renderGantt();
    const row = findNodeByAttr(c, 'data-testid', 'gantt-row-design')!;
    expect(textNodes(row).join(''), 'подпись пропущенного этапа').toContain('этапа не было');
    expect(findNodeByAttr(row, 'data-testid', 'gantt-bar-design'), 'полосы у skipped нет').toBeNull();
    expect(textNodes(findNodeByAttr(row, 'data-testid', 'gantt-minutes-design')!).join(''), 'минут у skipped нет').toBe('');
  });

  // === 3. Разбивка внутри строки: сегменты по видам ===
  it('разбивка «работа · ожидание · простой» — сегменты внутри полосы этапа', () => {
    const c = renderGantt();
    const bar = findNodeByAttr(c, 'data-testid', 'gantt-bar-prod')!;
    for (const kind of ['work', 'waiting', 'idle']) {
      expect(findNodeByAttr(bar, 'data-kind', kind), `сегмент ${kind} на полосе prod`).not.toBeNull();
    }
    // Ревью-трек — отдельный вид на строке Ревью, а не внутри полосы prod.
    expect(findNodeByAttr(bar, 'data-kind', 'review'), 'ревью не внутри полосы prod').toBeNull();
    const revBar = findNodeByAttr(c, 'data-testid', 'gantt-bar-review')!;
    expect(findNodeByAttr(revBar, 'data-kind', 'review'), 'сегмент review на строке review').not.toBeNull();
  });

  // === 3b. Активный этап без края (barEnd): полоса до «сейчас» (мутация: убрать
  // фолбэк right на axis.to — полоса merge исчезает, тест краснеет) ===
  it('активный этап: полоса дотягивается до правого края оси (текущего момента)', () => {
    const c = renderGantt();
    const bar = findNodeByAttr(c, 'data-testid', 'gantt-bar-merge');
    expect(bar, 'полоса активного этапа есть').not.toBeNull();
    const s = (bar as unknown as { style: Record<string, string> }).style;
    const left = parseFloat(s.left);
    const width = parseFloat(s.width);
    expect(Number.isFinite(left) && Number.isFinite(width), 'left/width числовые').toBe(true);
    expect(width, 'ширина полосы nonzero').toBeGreaterThan(0);
    // Правый край = конец оси: left + width = 100%.
    expect(left + width, 'правый край полосы — конец оси').toBeCloseTo(100, 1);
  });

  // === 4. Подсказка по касанию (360): разбивка по минутам и переход в «Хронологию» ===
  it('касание строки на 360 открывает подсказку с разбивкой; ссылка ведёт в «Хронологию»', () => {
    const c = renderGantt(true);
    expect(findNodeByAttr(c, 'data-testid', 'gantt-tip'), 'подсказки нет, пока не коснулись').toBeNull();
    const row = findNodeByAttr(c, 'data-testid', 'gantt-row-prod')!;
    act(() => { (reactProps(row)!.onClick as () => void)(); });
    const tip = findNodeByAttr(c, 'data-testid', 'gantt-tip');
    expect(tip, 'подсказка по касанию').not.toBeNull();
    const br = textNodes(findNodeByAttr(tip!, 'data-testid', 'gantt-tip-breakdown')!).join('');
    expect(br, 'разбивка по минутам').toContain('работа 4 ч 10 мин');
    expect(br).toContain('ожидание 1 ч 20 мин');
    expect(br).toContain('простой 40 мин');
    // Ссылка в подсказке — тот же путь, что плашка/панель: смена вкладки на
    // «Хронологию» + якорь этапа. Без setFeaturePageTab('timeline') в openStage
    // вкладка осталась бы 'stats' — тест краснеет.
    const open = findNodeByAttr(c, 'data-testid', 'gantt-tip-open')!;
    act(() => { (reactProps(open)!.onClick as () => void)(); });
    expect(__test.tab(), 'вкладка сменилась на «Хронологию»').toBe('timeline');
    const focus = __test.stageFocus();
    expect(focus, 'якорь этапа после перехода').not.toBeNull();
    expect(focus!.key).toBe('prod');
  });

  // === 5. Подсказка по наведению (десктоп) ===
  it('наведение открывает подсказку, уход мыши — закрывает', () => {
    const c = renderGantt();
    const row = findNodeByAttr(c, 'data-testid', 'gantt-row-qa')!;
    act(() => { (reactProps(row)!.onMouseEnter as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'gantt-tip'), 'подсказка по наведению').not.toBeNull();
    act(() => { (reactProps(row)!.onMouseLeave as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'gantt-tip'), 'после ухода мыши подсказки нет').toBeNull();
  });

  // === 6. «Сейчас» — отметка на правом краю оси (текущий момент), не на начале шага ===
  it('«сейчас»: отметка на правом краю оси строки текущего этапа (merge)', () => {
    const c = renderGantt();
    const marker = findNodeByAttr(c, 'data-testid', 'gantt-now-marker');
    expect(marker, 'отметка «сейчас»').not.toBeNull();
    const s = (marker as unknown as { style: Record<string, string> }).style;
    // Правый край трека = текущий момент; «левая» позиция по startedAt (середина
    // полосы) — краснеет.
    expect(parseFloat(s.right), 'маркер прижат к правому краю оси').toBe(0);
    expect(s.left, 'маркер не на начале этапа').toBeUndefined();
    // Легенда обещает «сейчас», когда есть текущий этап.
    const legend = findNodeByAttr(c, 'data-testid', 'stats-legend')!;
    expect(textNodes(legend).join('')).toContain('сейчас');
  });

  // === 7. 360: горизонтального переполнения нет — фиксированы только колонки имени
  // и минут (86/52), полоса берёт остаток (1fr); принудительной ширины нет. ===
  it('на 360 строка — grid 86px 1fr 52px без принудительной ширины', () => {
    const c = renderGantt(true);
    const gantt = findNodeByAttr(c, 'data-testid', 'stats-gantt')!;
    const gs = (gantt as unknown as { style: Record<string, string> }).style;
    expect(gs.minWidth, 'нет minWidth — переполнения на 360 не будет').toBeUndefined();
    const row = findNodeByAttr(c, 'data-testid', 'gantt-row-prod')!;
    const rs = (row as unknown as { style: Record<string, string> }).style;
    expect(rs.gridTemplateColumns, 'полоса — колонка 1fr, не фиксированная ширина').toBe('86px 1fr 52px');
    // Тач-цель строки — 40 px (как в других мобильных контролах).
    expect(rs.minHeight).toBe('40px');
  });
});

// === Строка «тихо» на Ганте (W5-F3b, спецификация features-overview-5.md §«Точка 6») ===
// Мутации (в отчёте): (3) убрать строку из рендера → «строка после этапов, перед осью»
// краснеет; (4) убрать пункт легенды → «в легенде „тихо“» краснеет; (5) убрать условие
// isMobile у «· сжато» → «на 360 без „сжато“» краснеет; (6) убрать дату из центра трека →
// «дата начала тишины» краснеет; (7) снять типографику правой колонки (mono/xs/700) →
// «типографика правой колонки» краснеет; (8) рисовать «{дата} → сейчас» и при from=null →
// «без даты» краснеет; (9) строить Гант и без stats → «без stats — Ганта нет» краснеет.

function mkQuietRun(): FeatureRun {
  // Тот же конвейер, но бэк сказал «тихо ≥ 24 ч»: состояние берёт analysisLineState.
  return { ...mkGanttRun(), quiet: true, lastActivityAt: '2026-10-20T17:10:00Z' };
}

function renderQuietGantt(isMobile = false) {
  installFakeDom();
  const { container } = mount(createElement(FeatureStatsTab, { stats: mkGanttStats(), run: mkQuietRun(), isMobile }));
  return container;
}

// data-testid в порядке документа — проверить место строки (после этапов, до оси).
function testIdsInOrder(n: FNodeLike): string[] {
  const out: string[] = [];
  const walk = (x: FNodeLike) => {
    const id = x.attributes?.['data-testid'];
    if (id) out.push(id);
    for (const ch of x.childNodes) walk(ch);
  }
  walk(n);
  return out;
}

describe('FeatureStatsTab: строка «тихо» на Ганте (W5-F3b)', () => {
  beforeEach(() => {
    clearFeatureStageFocus();
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('«тихо» — последняя строка после этапов, перед осью; маркера «сейчас» нет', () => {
    const c = renderQuietGantt();
    const ids = testIdsInOrder(c);
    const quietAt = ids.indexOf('gantt-quiet');
    expect(quietAt, 'строка «тихо» есть').toBeGreaterThanOrEqual(0);
    const lastRow = Math.max(...ids.map((id, i) => (id.startsWith('gantt-row-') ? i : -1)));
    const axisAt = ids.indexOf('stats-axis');
    expect(quietAt, 'строка после всех строк этапов').toBeGreaterThan(lastRow);
    expect(quietAt, 'строка до оси').toBeLessThan(axisAt);
    // В «тихо» текущего этапа нет: ни маркера, ни «сейчас» в легенде.
    expect(findNodeByAttr(c, 'data-testid', 'gantt-now-marker'), 'маркера «сейчас» нет').toBeNull();
    const legend = textNodes(findNodeByAttr(c, 'data-testid', 'stats-legend')!).join('');
    expect(legend, 'в легенде нет «сейчас»').not.toContain('сейчас');
  });

  it('трек и имя — по токенам spec: имя «тихо» C.warningText 700, рамка dashed C.warning, штриховка C.warningBg', () => {
    const c = renderQuietGantt();
    const row = findNodeByAttr(c, 'data-testid', 'gantt-quiet')!;
    const name = row.childNodes[0] as unknown as { style: Record<string, string> };
    expect(textNodes(row.childNodes[0] as never).join('')).toBe('тихо');
    expect(name.style.color, 'имя — C.warningText').toBe('var(--c-warning-text)');
    expect(name.style.fontWeight).toBe('700');
    const track = row.childNodes[1] as unknown as { style: Record<string, string> };
    expect(track.style.height).toBe('16px');
    expect(track.style.borderRadius, 'SP.xs = 4').toBe('4px');
    expect(track.style.border).toContain('dashed');
    expect(track.style.border).toContain('var(--c-warning)');
    expect(track.style.background).toContain('repeating-linear-gradient(135deg');
    expect(track.style.background).toContain('var(--c-warning-bg)');
    // Центр трека: «{дата} → сейчас», моно, FS.xxs, C.warningText.
    const inner = track.childNodes[0] as unknown as { style: Record<string, string> };
    const innerText = textNodes(track.childNodes[0] as never).join('');
    expect(innerText).toContain('→ сейчас');
    // Дата начала тишины в центре трека — из lastActivityAt (spec: «{дата} → сейчас»).
    expect(innerText, 'в центре трека — дата начала тишины').toContain(formatDateTime('2026-10-20T17:10:00Z'));
    expect(inner.style.fontFamily).toBe(FONT.mono);
    expect(inner.style.fontSize).toBe(`${FS.xxs}px`);
    expect(inner.style.color).toBe(C.warningText);
    // Справа — длительность: не только «число есть», но и типографика по spec —
    // FONT.mono FS.xs 700 C.warningText.
    const dur = row.childNodes[2] as unknown as { style: Record<string, string> };
    expect(textNodes(row.childNodes[2] as never).join('')).toMatch(/\d+/);
    expect(dur.style.fontFamily).toBe(FONT.mono);
    expect(dur.style.fontSize).toBe(`${FS.xs}px`);
    expect(dur.style.fontWeight).toBe('700');
    expect(dur.style.color).toBe(C.warningText);
  });

  it('«· сжато» — только на десктопе; на 360 — сетка 86px 1fr 52px и minHeight 40', () => {
    const desktop = renderQuietGantt();
    expect(textNodes(findNodeByAttr(desktop, 'data-testid', 'gantt-quiet')!).join('')).toContain('· сжато');
    const mobile = renderQuietGantt(true);
    const rowM = findNodeByAttr(mobile, 'data-testid', 'gantt-quiet')!;
    expect(textNodes(rowM).join(''), 'на 360 без «сжато»').not.toContain('сжато');
    const ms = (rowM as unknown as { style: Record<string, string> }).style;
    expect(ms.gridTemplateColumns).toBe('86px 1fr 52px');
    expect(ms.minHeight).toBe('40px');
  });

  it('легенда: пункт «тихо» с dashed-рамкой и фоном warningBg; в не-quiet — строки и пункта нет', () => {
    const c = renderQuietGantt();
    const item = findNodeByAttr(c, 'data-testid', 'gantt-legend-quiet')!;
    expect(textNodes(item).join('')).toContain('тихо');
    const swatch = item.childNodes[0] as unknown as { style: Record<string, string> };
    expect(swatch.style.border).toContain('dashed');
    expect(swatch.style.background).toBe('var(--c-warning-bg)');
    // Не-quiet (обычный конвейер) — ни строки, ни пункта легенды.
    const plain = renderGantt();
    expect(findNodeByAttr(plain, 'data-testid', 'gantt-quiet'), 'в не-quiet строки нет').toBeNull();
    expect(findNodeByAttr(plain, 'data-testid', 'gantt-legend-quiet'), 'в не-quiet пункта легенды нет').toBeNull();
  });

  // П.3 вердикта W5-R-F3b: ветка quiet.from === null (период без начала) — «сейчас»
  // в центре трека без даты, правая колонка пустая. Мутация: всегда рисовать
  // formatDateTime(from) даже при null → краснеет «без даты».
  it('без lastActivityAt — в центре трека «сейчас» без даты, правая колонка пустая', () => {
    const run: FeatureRun = { ...mkGanttRun(), quiet: true }; // начала тишины нет
    installFakeDom();
    const { container } = mount(createElement(FeatureStatsTab, { stats: mkGanttStats(), run, isMobile: false }));
    const row = findNodeByAttr(container, 'data-testid', 'gantt-quiet');
    expect(row, 'строка «тихо» всё равно есть').not.toBeNull();
    const inner = textNodes((row as never).childNodes[1].childNodes[0] as never).join('');
    expect(inner).toContain('сейчас');
    expect(inner, 'без начала периода — никакой даты и стрелки').not.toContain('→');
    expect(textNodes((row as never).childNodes[2] as never).join(''), 'длительности нет').toBe('');
  });

  // П.4 вердикта: без stats Ганта нет — строка «тихо» не рисуется; сигнал несёт строка
  // анализа. Осознанная деградация: закреплена тестом, а не случайностью.
  it('без stats — Ганта и строки «тихо» нет; сигнал несёт строка анализа с бейджем «тихо»', () => {
    installFakeDom();
    const { container } = mount(createElement(FeatureStatsTab, { stats: null, run: mkQuietRun(), isMobile: false }));
    expect(findNodeByAttr(container, 'data-testid', 'stats-gantt'), 'Ганта нет').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'gantt-quiet'), 'строки «тихо» нет').toBeNull();
    const line = findNodeByAttr(container, 'data-testid', 'feature-analysis-line');
    expect(line, 'строка анализа на месте').not.toBeNull();
    expect(textNodes(line as never).join(''), 'сигнал «тихо» — в строке анализа').toContain('тихо');
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
    expect(roleGroupLabel('feature')).toBe('координация');
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

// === Сборка «Пульса» (ST-F14a, вариант A макета feature-stats-5) ===
// Порядок на вкладке: строка анализа → значки точности → герой → плитки →
// «Сейчас работают» → «Главные потери» → Гант → «Кто и на чём работал» → свёрнутый
// «Подробнее». Без run (старые ответы) — прежняя полоса: пульс-блоки требуют
// конвейера. «Подробнее» — только то, что новые блоки не покрывают.
//
// Мутации (проверены прогоном): (1) убрать FeatureStatsLosses из рендера → тест
// порядка краснеет (stats-losses выпадает из цепочки); (2) renderTab без run →
// тест «без run — прежняя полоса» краснеет, если пульс-блоки рендерятся и без run.

describe('FeatureStatsTab: сборка «Пульса» (ST-F14a)', () => {
  beforeEach(() => {
    mockOpenTask.calls = [];
    clearFeatureStageFocus();
    setFeaturePageTab('stats');
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Фикстура НЕ в порядке выдачи: run — этапы cut→design→plan→… (порядок данных
  // не равен порядку блоков), а проверяется именно порядок блоков в DOM.
  it('порядок блоков: точность → герой → плитки → «Сейчас» → потери → Гант → разрез → «Подробнее»', () => {
    const c = renderTab(mkFullStats(), false, mkGanttRun());
    const seq = testIdsInOrder(c);
    const want = [
      'stats-accuracy', 'hero-running', 'stats-tiles', 'stats-now', 'stats-losses',
      'stats-gantt', 'feature-breakdown-block', 'stats-more',
    ];
    let prev = -1;
    for (const id of want) {
      const i = seq.indexOf(id);
      expect(i, `блок ${id} на вкладке`).toBeGreaterThanOrEqual(0);
      expect(i, `блок ${id} — после предыдущего`).toBeGreaterThan(prev);
      prev = i;
    }
  });

  // Мутация (1): без FeatureStatsLosses в рендере — 'stats-losses' не находится.
  it('«Главные потери» стоят между «Сейчас работают» и Гантом', () => {
    const c = renderTab(mkFullStats(), false, mkGanttRun());
    const seq = testIdsInOrder(c);
    expect(seq.indexOf('stats-now'), '«Сейчас» до потерь').toBeLessThan(seq.indexOf('stats-losses'));
    expect(seq.indexOf('stats-losses'), 'потери до Ганта').toBeLessThan(seq.indexOf('stats-gantt'));
  });

  // Мутация (2): без run — старая полоса, пульс-блоков нет.
  it('без run — нет героя, плиток, «Сейчас», потерь и разреза; прежняя полоса на месте', () => {
    const c = renderTab(mkFullStats());
    for (const id of ['hero-running', 'stats-tiles', 'stats-now', 'stats-losses', 'feature-breakdown-block']) {
      expect(findNodeByAttr(c, 'data-testid', id), `${id} без run`).toBeNull();
    }
    expect(findNodeByAttr(c, 'data-testid', 'stats-timeline'), 'лента без run').not.toBeNull();
  });

  // «Подробнее» несёт ровно то, что не покрыто новыми блоками.
  it('«Подробнее»: «не работали», эпизоды ошибок и детали расхода (кэш, общие чаты, цены)', () => {
    const c = renderTab(mkFullStats());
    const more = findNodeByAttr(c, 'data-testid', 'stats-more');
    expect(more, 'раскрывашка есть').not.toBeNull();
    const all = textNodes(more!).join('');
    expect(all).toContain('не работали — 10 ч');
    expect(all).toContain('ошибки: 2 эпизодов');
    expect(all).toContain('кэш: 500 тыс. чтение · 100 тыс. создание');
    expect(all).toContain('доля общих чатов');
    expect(all).toContain('150 тыс. токенов');
    expect(all).toContain('оценка, по журналу');
    expect(all).toContain('моделей без цены: Qwen');
  });

  it('approx: «не работали» с «≈» (та же точность, что у работы)', () => {
    const c = renderTab(mkFullStats({ source: 'approx' }));
    const all = textNodes(findNodeByAttr(c, 'data-testid', 'stats-more')!).join('');
    expect(all).toContain('не работали — ≈ 10 ч');
  });

  // Память «Подробнее»: раскрытие пишется в localStorage и читается при монтировании.
  // Мутация: не писать localStorage в onToggle — тест краснеет.
  it('«Подробнее» помнит раскрытие в localStorage[featureStats.moreOpen]', () => {
    const store = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => { store.set(k, v); },
      removeItem: (k: string) => { store.delete(k); },
    });
    try {
      const c = renderTab(mkFullStats());
      const more = findNodeByAttr(c, 'data-testid', 'stats-more')!;
      expect(reactProps(more)!.open, 'по умолчанию свёрнуто').toBe(false);
      act(() => {
        (reactProps(more)!.onToggle as (e: unknown) => void)({ currentTarget: { open: true } });
      });
      expect(store.get(MORE_OPEN_KEY)).toBe('1');
      // новый заход: раскрыто сразу
      const c2 = renderTab(mkFullStats());
      expect(reactProps(findNodeByAttr(c2, 'data-testid', 'stats-more')!)!.open).toBe(true);
      const more2 = findNodeByAttr(c2, 'data-testid', 'stats-more')!;
      act(() => {
        (reactProps(more2)!.onToggle as (e: unknown) => void)({ currentTarget: { open: false } });
      });
      expect(store.get(MORE_OPEN_KEY)).toBe('0');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  // Гарды чтения: localStorage недоступен или в значении мусор — свёрнуто, без падения.
  // Мутация: снять try/catch в readMoreOpen или сравнивать не строго — тест краснеет.
  it('«Подробнее»: getItem бросает → свёрнуто без краша', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('приватный режим'); },
      setItem: () => { throw new Error('приватный режим'); },
      removeItem: () => {},
    });
    try {
      const c = renderTab(mkFullStats());
      expect(reactProps(findNodeByAttr(c, 'data-testid', 'stats-more')!)!.open).toBe(false);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('«Подробнее»: мусор в значении («да») → свёрнуто', () => {
    vi.stubGlobal('localStorage', {
      getItem: (k: string) => (k === MORE_OPEN_KEY ? 'да' : null),
      setItem: () => {},
      removeItem: () => {},
    });
    try {
      const c = renderTab(mkFullStats());
      expect(reactProps(findNodeByAttr(c, 'data-testid', 'stats-more')!)!.open).toBe(false);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  // Мутация «всегда»: если раскрывашку рендерить без строк — тест краснеет.
  it('нечего добавить — «Подробнее» нет (null-итоги, расхода нет)', () => {
    const c = renderTab(mkStats());
    expect(findNodeByAttr(c, 'data-testid', 'stats-more'), 'пустой раскрывашки нет').toBeNull();
  });

  // Мобила: isMobile доезжает до плиток (сетка 2 колонки на 360).
  it('на 360 плитки — сетка 2 колонки (isMobile доходит до блока)', () => {
    const c = renderTab(mkFullStats(), true, mkGanttRun());
    const tiles = findNodeByAttr(c, 'data-testid', 'stats-tiles') as unknown as { style: Record<string, string> };
    expect(tiles.style.gridTemplateColumns, '2 колонки на 360').toBe('repeat(2, 1fr)');
  });

  // П2 (QA: на 360 summary «Подробнее» был 17 px): карточка-строка высотой ≥ 44 (макет .more)
  // с кольцом фокуса. Мутация: убрать minHeight → тест краснеет.
  it('«Подробнее» — карточка-строка высотой ≥ 44 с кольцом фокуса', () => {
    const c = renderTab(mkFullStats(), true, mkGanttRun());
    const sum = findNodeByAttr(c, 'data-testid', 'stats-more-summary');
    expect(sum, 'summary «Подробнее» отсутствует').toBeTruthy();
    const st = (sum as unknown as { style: Record<string, string> }).style;
    expect(st.minHeight, 'тач-цель ниже 44 px').toBe('44px');
    expect(st.background, 'строка не карточка').toBe(C.bgCard);
    // React пишет className в атрибут «class» (тот же приём, что FeatureBreakdownTable).
    expect((sum as unknown as { getAttribute: (k: string) => string | null }).getAttribute('class'), 'нет класса кольца фокуса').toContain('cc-stats-more');
  });

  // П2 задачи: isMobile передаётся со страницы в блок разбивки (плотные поля на 360).
  // Мутация: убрать isMobile из вызова → сработает detectedMobile (десктоп в тестах) → red.
  it('на 360 isMobile доходит до блока разбивки (плотный padding)', () => {
    const c = renderTab(mkFullStats(), true, mkGanttRun());
    const block = findNodeByAttr(c, 'data-testid', 'feature-breakdown-block');
    expect(block, 'блок разбивки отсутствует').toBeTruthy();
    const st = (block as unknown as { style: Record<string, string> }).style;
    expect(st.padding, 'разбивка не в мобильном раскладе').toBe(`${SP.md}px`);
  });
});
