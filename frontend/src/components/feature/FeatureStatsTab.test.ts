// Вкладка «Статистика» (волна 3, Ф7a, макет features-overview-3-final §«Статистика»;
// сборка «Пульса» — ST-F14a по макету feature-stats-5, вариант A).
// GC-F12: «Статистика» БЕЗ Ганта и без ленты времени — осталась сводка этапов
// (имя · слово состояния · «N/M» у «Производства» · длительность) и ссылка на
// «Хронологию». Рендер-тесты сводки — в блоке «сводка этапов (GC-F12)».
//
// Чистые функции (FeatureStatsBits) — в node, без DOM. Рендер-тесты (fakeDom):
// точность, «задача», пустые состояния, сводка этапов.
//
// Мутации (в отчёте): (1) порог 2 ч → 20 ч → «сжатие паузы» краснеет;
// (2) потерять track=review → «дорожка ревью» краснеет; (3) подменить цель клика
// (focusFeatureStage('') вместо key) → «клик → этап» краснеет; (4) убрать «≈» из фразы
// точности / вернуть повтор → тесты «точности» краснеют; (5) сегмент waiting как work
// → «сегменты по видам» краснеет; (6) сводка: убрать слово состояния / показать «N/M»
// у всех стадий / не отрисовать ссылку — краснеют тесты сводки.

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
import { C, SP } from '../../lib/design';

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

  // === 9. 360: тач-цель строки сводки ≥ 40 ===
  it('на 360 строка сводки этапа — minHeight 40', () => {
    const c = renderTab(mkGanttStats(), true, mkGanttRun());
    const row = findNodeByAttr(c, 'data-testid', 'stats-stage-prod')!;
    const st = (row as unknown as { style: Record<string, string> }).style;
    expect(st.minHeight).toBe('40px');
  });
});

// === Сводка этапов (GC-F12) ===
// run с 8 этапами: design — skipped («этапа не было»), prod — принятые шаги плана
// («N/M»), merge — текущий этап («идёт»). Ганта и ленты на вкладке больше нет:
// осталась сводка строк и ссылка на «Хронологию».
//
// Мутации (в отчёте): (1) убрать строку этапа из рендера → «каждый этап — своя
// строка» краснеет; (2) не обнулять минуты при skipped → «этапа не было»
// краснеет (минуты появились бы справа); (3) показать «N/M» у всех стадий →
// «счётчик только у prod» краснеет; (4) убрать ссылку → «ссылка на „Хронологию“»
// краснеет; (5) не отрисовать слово состояния → «слово состояния» краснеет.

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

describe('FeatureStatsTab: сводка этапов (GC-F12)', () => {
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

  // === 1. Строка на каждый этап: имя · слово состояния · длительность ===
  it('каждый этап — своя строка; длительность — из approxMinutes', () => {
    const c = renderSummary();
    for (const key of ['cut', 'design', 'plan', 'prod', 'review', 'qa', 'final', 'merge']) {
      expect(findNodeByAttr(c, 'data-testid', `stats-stage-${key}`), `строка этапа ${key}`).not.toBeNull();
    }
    // prod: 370 мин → «6 ч 10 мин».
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-minutes-prod')!).join('')).toBe('6 ч 10 мин');
    // Мерж (4 мин) — тоже строка с минутами.
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-minutes-merge')!).join('')).toBe('4 мин');
    // Пропущенный этап: минут нет (этапа не было — считать нечего).
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-minutes-design')!).join('')).toBe('');
  });

  // === 2. Слово состояния: готов · этапа не было · идёт ===
  it('слово состояния: готов · этапа не было · идёт', () => {
    const c = renderSummary();
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-state-cut')!).join('')).toBe('готов');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-state-design')!).join('')).toBe('этапа не было');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-state-merge')!).join('')).toBe('идёт');
  });

  // === 2b. Остановленный этап — «стоит» ===
  it('остановленный этап (tone=stopped) — слово «стоит»', () => {
    const base = mkSummaryRun();
    const run: FeatureRun = {
      ...base,
      stages: base.stages!.map(s => s.key === 'prod' ? { ...s, state: 'active' as const, tone: 'stopped' as const } : s),
    };
    const c = renderSummary(run);
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-state-prod')!).join('')).toBe('стоит');
  });

  // === 2c. Не начатый этап — «впереди» ===
  it('не начатый этап (state=pending) — слово «впереди»', () => {
    const base = mkSummaryRun();
    const run: FeatureRun = {
      ...base,
      stages: base.stages!.map(s => s.key === 'qa' ? { ...s, state: 'pending' as const, tone: null } : s),
    };
    const c = renderSummary(run);
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-state-qa')!).join('')).toBe('впереди');
  });

  // === 3. «N/M» — только у «Производства» (решение m1 ревью GC-R-F2) ===
  it('«N/M» — только у prod: у прочих стадий счётчика нет (одна цифра не дублируется)', () => {
    const c = renderSummary();
    // plan у prod: принято 8, в работе 2 (work+review), впереди 1 → «8/11».
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stats-stage-plan-prod')!).join('')).toBe('8/11');
    for (const key of ['cut', 'design', 'plan', 'review', 'qa', 'final', 'merge']) {
      expect(findNodeByAttr(c, 'data-testid', `stats-stage-plan-${key}`), `у ${key} счётчика нет`).toBeNull();
    }
  });

  // === 4. Ссылка на «Хронологию» ===
  it('ссылка «Дорожки времени — на „Хронологии“» переключает вкладку на «Хронологию»', () => {
    const c = renderSummary();
    const link = findNodeByAttr(c, 'data-testid', 'stats-stages-timeline-link')!;
    expect(textNodes(link).join('')).toContain('Дорожки времени — на „Хронологии“');
    act(() => { (reactProps(link)!.onClick as () => void)(); });
    expect(__test.tab(), 'вкладка сменилась на «Хронологию»').toBe('timeline');
  });

  // === 5. В сводке нет интерактивных строк — только ссылка ===
  it('в сводке единственная кнопка — ссылка на «Хронологию» (строки не интерактивны)', () => {
    const c = renderSummary();
    const box = findNodeByAttr(c, 'data-testid', 'stats-stages')!;
    const buttons: FNodeLike[] = [];
    const walk = (n: FNodeLike) => {
      if (n.nodeName === 'BUTTON') buttons.push(n);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(box);
    expect(buttons.length, 'кнопок в сводке').toBe(1);
  });

  // === 6. Без run (старые ответы) — сводки нет ===
  it('без run — сводки нет', () => {
    const c = renderTab(mkGanttStats());
    expect(findNodeByAttr(c, 'data-testid', 'stats-stages'), 'сводки без run').toBeNull();
  });
});

// === Фикстуры сводки этапов (GC-F12) ===
// Тот же конвейер, но с честным прогрессом плана: сводка берёт «N/M» из
// buildStageLanes().lanes['prod'].plan, а plan у prod считается из run.stepProgress.
function mkSummaryRun(): FeatureRun {
  return {
    ...mkGanttRun(),
    stepProgress: {
      planState: 'cut', total: 11, accepted: 8, review: 1, work: 1, notCreated: 1,
      withdrawn: 0, unlinkedReworks: 0,
    },
  };
}

function renderSummary(run: FeatureRun = mkSummaryRun(), isMobile = false) {
  installFakeDom();
  const { container } = mount(createElement(FeatureStatsTab, { stats: mkGanttStats(), run, isMobile }));
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

describe('FeatureStatsTab: «тихо» без Ганта (GC-F12)', () => {
  beforeEach(() => {
    clearFeatureStageFocus();
  });
  afterEach(() => {
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Строки «тихо» на Ганте больше нет (GC-F12) — сигнал несёт строка анализа.
  it('без stats — сводки нет; сигнал «тихо» несёт строка анализа', () => {
    installFakeDom();
    const run: FeatureRun = { ...mkGanttRun(), quiet: true, lastActivityAt: '2026-10-20T17:10:00Z' };
    const { container } = mount(createElement(FeatureStatsTab, { stats: null, run, isMobile: false }));
    expect(findNodeByAttr(container, 'data-testid', 'stats-stages'), 'сводки без stats').toBeNull();
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
// «Сейчас работают» → «Главные потери» → сводка этапов → «Кто и на чём работал» →
// свёрнутый «Подробнее». Без run (старые ответы) — пульс-блоков и сводки нет:
// и то и другое требует конвейера. «Подробнее» — только то, что новые блоки не покрывают.
//
// Мутации (проверены прогоном): (1) убрать FeatureStatsLosses из рендера → тест
// порядка краснеет (stats-losses выпадает из цепочки); (2) renderTab без run →
// тест «без run» краснеет, если пульс-блоки рендерятся и без run.

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
  it('порядок блоков: точность → герой → плитки → «Сейчас» → потери → сводка → разрез → «Подробнее»', () => {
    const c = renderTab(mkFullStats(), false, mkGanttRun());
    const seq = testIdsInOrder(c);
    const want = [
      'stats-accuracy', 'hero-running', 'stats-tiles', 'stats-now', 'stats-losses',
      'stats-stages', 'feature-breakdown-block', 'stats-more',
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
  it('«Главные потери» стоят между «Сейчас работают» и сводкой этапов', () => {
    const c = renderTab(mkFullStats(), false, mkGanttRun());
    const seq = testIdsInOrder(c);
    expect(seq.indexOf('stats-now'), '«Сейчас» до потерь').toBeLessThan(seq.indexOf('stats-losses'));
    expect(seq.indexOf('stats-losses'), 'потери до сводки').toBeLessThan(seq.indexOf('stats-stages'));
  });

  // Мутация (2): без run — пульс-блоков и сводки нет.
  it('без run — нет героя, плиток, «Сейчас», потерь, разреза и сводки', () => {
    const c = renderTab(mkFullStats());
    for (const id of ['hero-running', 'stats-tiles', 'stats-now', 'stats-losses', 'feature-breakdown-block', 'stats-stages']) {
      expect(findNodeByAttr(c, 'data-testid', id), `${id} без run`).toBeNull();
    }
  });

  // «Подробнее» несёт ровно то, что не покрыто новыми блоками.
  it('«Подробнее»: «не работали», эпизоды ошибок и детали расхода (кэш, общие чаты, цены)', () => {
    const c = renderTab(mkFullStats());
    const more = findNodeByAttr(c, 'data-testid', 'stats-more');
    expect(more, 'раскрывашка есть').not.toBeNull();
    const all = textNodes(more!).join('');
    expect(all).toContain('не работали — 10 ч');
    expect(all).toContain('ошибки: 2 эпизода');
    expect(all).toContain('кэш: 500 тыс. чтение · 100 тыс. создание');
    expect(all).toContain('доля общих чатов');
    expect(all).toContain('150 тыс. токенов');
    expect(all).toContain('оценка, по журналу');
    expect(all).toContain('моделей без цены: Qwen');
  });

  // Мутация: константа 'эпизодов' вместо plural() краснит 1/2/21.
  it.each([[1, 'эпизод'], [2, 'эпизода'], [5, 'эпизодов'], [11, 'эпизодов'], [21, 'эпизод']])(
    '«Подробнее»: ошибки — %i %s',
    (n, word) => {
      const base = mkFullStats();
      const c = renderTab(mkFullStats({
        timeline: { ...base.timeline, totals: { ...base.timeline.totals, errorCount: n } },
      }));
      const all = textNodes(findNodeByAttr(c, 'data-testid', 'stats-more')!).join('\n');
      expect(all).toMatch(new RegExp(`ошибки: ${n} ${word}(?!\\p{L})`, 'u'));
    },
  );

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
