// Вкладка «Хронология» (волна 3, Ф2a/Ф2b, макет features-overview-3-final §5).
//
// Чистые функции (имя/состояние/границы/«≈»/точка/линия/задачи/минуты/статус/плашка;
// Ф2b: статус/тон шага (stepStatusView), модель шага) — в node, без DOM.
// Рендер-тесты (fakeDom): порядок 7 этапов, раскрытие одного этапа, исполнители
// (roleLabel + чип модели), «чата нет», пустые состояния, тач-цель 40 на 360.
// «Производство» (W4-2F3): лента карточек FeatureProdCardsView вместо «исполнителей/
// шагов» — одна карточка на шаг, «сейчас» над текущей. Сироты «Производства» (фикс R4e):
// карточек нет, но у стадии есть исполнители → прежние ExecutorRow; пустое состояние
// «Шагов пока нет» — только когда показывать действительно некого. Чистые функции Ф2b
// (статус/тон/модель) остаются: их читает лента.
//
// Мутации (в отчёте): (1) переставить этапы в run.stages → «порядок этапов» краснеет;
// (2) убрать «≈» из stageDurationLabel → «длительность ≈» краснеет; (3) не передать run
// в FeatureProdCardsView (не дать stepChains) → «карточка Ш1» краснеет; (4) не передать
// isMobile в ленту → «360: поле касания 40» краснеет; (5) убрать фолбэк на ExecutorRow
// при пустой ленте → «сироты не исчезают» краснеет; (6) убрать ветку outside из рендера
// «оценивает →» → «reviewOfOutside > 0 даёт нейтральную подпись» краснеет;
// GC-F10a: (7) tasksCountLabel → хардкод «${n} задач» → тест склонения краснеет;
// (8) в StageRow isTail={isTail(x.taskId)} → isTail={false} → «помечен маркером» краснеет;
// (9) условие строки хвостов tailIds.size > 0 → false → «хвосты после мержа · 2» краснеет.
// GC-F10b: (10) в StageRow turns={turnsByTask.get(x.taskId)} → turns={null} → «ходы задачи»
// краснеет; (11) в buildTurnsByTask убрать ветку синтеза живого хода → «живой ход» краснеет;
// (12) в StageRow не передать axis → «Время шага на оси» краснеет.
// GC-F10c: свёртка списков этапов кроме prod: 74 → 10 строк + «Показать все 74 (ранних
// скрыто: 64)», клик → все; снятые (verdict 'withdrawn') — ни в N, ни в K, ни в списке;
// короткий список — без кнопки. Мутации: (13) в StageRow убрать кнопку (условие
// hiddenEarly.length > 0 → false) → «74 ревью» краснеет; (14) rows = stage.executors
// без фильтра снятых → «снятые» краснеет.
// GC-F10bR: «сейчас» вкладки — из useNow (живой ход растёт, пока вкладка открыта). Мутация:
// (15) вернуть в FeatureTimelineTab const now = useMemo(() => Date.now(), []) вместо
// useNow(60_000) → «живой ход растёт со временем» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureTimelineTab, laneAxisInsets, laneScaleWidth } from './FeatureTimelineTab';
import {
  stageName, stageStatusLabel, stageBoundsLabel, stageDurationLabel,
  stageDotStyle, stageLineStyle, buildTaskMap, minutesSince, executorStatus, stageStatusBadge,
  stepStatusView, stepModel, buildExecutorIndex, evaluatedLine, outsideLinksLabel, tasksCountLabel,
} from './FeatureTimelineBits';
import type { StepProgress } from '../../lib/featureProgress';
import { C } from '../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { __test as backNavTest } from '../../lib/featureBackNav';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureStepChain, FeatureRound, FeatureTask } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

// === Мок персон (lib/personas): id → имя; аватар — инициалы (без image) ===
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) =>
    personaMap.has(id)
      ? { id, name: personaMap.get(id), avatar: { kind: 'initials' as const } }
      : undefined,
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
// lib/api — настоящий модуль + avatarUrl (PersonaAvatar) + tasks.get (отказ: ленивый «Итог»
// FeatureStepDetails при раскрытой строке не должен висеть на сети).
const { tasksGetMock } = vi.hoisted(() => ({ tasksGetMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, personas: { ...orig.api.personas, avatarUrl: () => '' }, tasks: { ...orig.api.tasks, get: tasksGetMock } } };
});
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));
// Навигация: журнал целей перехода (chat → openChatById, task → openTaskInSection).
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } }));
// lib/tasks — настоящий (стор для FeatureStepDetails при раскрытой строке),
// openTaskInSection — spy журнала целей.
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/tasks')>();
  return { ...orig, openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } };
});

// Дефолт tasks.get: полная задача БЕЗ итога — блок «Итог» не рисуется, тесты его не видят.
// Нужен потому, что детали (FeatureStepDetails) подняты и в карточках «Производства» (GC-F8):
// без дефолта ленивый добор падает на vi.fn() без реализации (undefined.then).
// В тесте «отказ» — свой mockImplementation в его beforeEach, он перекрывает этот (он идёт позже).
beforeEach(() => {
  tasksGetMock.mockReset();
  tasksGetMock.mockImplementation((id: string) => Promise.resolve({
    id, title: '', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 0, createdAt: '', updatedAt: '',
  } as never));
});

// === Фиксированное «сейчас» (стабильные минуты) ===
const NOW = new Date('2026-10-20T12:00:00Z').getTime();

// === Хелперы данных ===
function mkStage(over: Partial<FeatureStage> = {}): FeatureStage {
  return {
    key: 'prod',
    state: 'pending',
    tone: null,
    passes: 0,
    taskIds: [],
    runningSince: null,
    rounds: [],
    longestStepRef: null,
    firstStartedAt: null,
    lastCompletedAt: null,
    barEnd: null,
    approxMinutes: 0,
    overlapsActive: false,
    executors: [],
    ...over,
  };
}

function mkExec(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return {
    personaId: null,
    taskId: 't1',
    taskRole: null,
    stepRef: null,
    lane: null,
    status: 'pending',
    chatId: null,
    chatArchived: false,
    modelLabel: null,
    modelFull: null,
    reviewOf: [],
    reviewOfOutside: 0,
    ...over,
  };
}

function mkRun(stages: FeatureStage[], over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1',
    state: 'running',
    epic: { id: 'f1', title: 'Фича' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    stages,
    ...over,
  } as FeatureRun;
}

// Семь этапов в порядке жизненного цикла (для рендер-тестов).
function fullStages(): FeatureStage[] {
  return [
    mkStage({ key: 'cut', state: 'done', firstStartedAt: '2026-10-19T17:20:00Z', lastCompletedAt: '2026-10-19T17:55:00Z', barEnd: '2026-10-19T17:55:00Z', approxMinutes: 35 }),
    mkStage({ key: 'plan', state: 'done', firstStartedAt: '2026-10-19T17:58:00Z', lastCompletedAt: '2026-10-19T18:30:00Z', barEnd: '2026-10-19T18:30:00Z', approxMinutes: 30 }),
    mkStage({ key: 'prod', state: 'active', tone: 'run', firstStartedAt: '2026-10-20T10:05:00Z', approxMinutes: 290,
      executors: [mkExec({ personaId: 'p1', taskId: 't-prod', taskRole: 'step', stepRef: 'Ш1', lane: 'back', status: 'running', chatId: 'chat-1', modelLabel: 'Opus', modelFull: 'Opus 4.5' })] }),
    mkStage({ key: 'review', state: 'active', tone: 'run', overlapsActive: true, firstStartedAt: '2026-10-20T11:20:00Z', approxMinutes: 75 }),
    mkStage({ key: 'qa' }),
    mkStage({ key: 'final' }),
    mkStage({ key: 'merge' }),
  ];
}

// Местная дата/время (те же локальные геттеры, что featureTime) — для ожидаемых строк.
function localDate(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
}
function localTime(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// === Чистые функции ===

describe('stageName', () => {
  it('семь этапов — имена из STAGE_NAME', () => {
    expect(stageName('cut')).toBe('Разрез');
    expect(stageName('plan')).toBe('План');
    expect(stageName('prod')).toBe('Производство');
    expect(stageName('review')).toBe('Ревью');
    expect(stageName('qa')).toBe('Проверка');
    expect(stageName('final')).toBe('Финальное ревью');
    expect(stageName('merge')).toBe('Мерж');
  });
});

describe('stageStatusLabel', () => {
  it('done → null (без подписи)', () => {
    expect(stageStatusLabel(mkStage({ state: 'done' }))).toBeNull();
  });
  it('active: run → «идёт», wait → «ждёт вас» (info), stopped → «стоит» (danger)', () => {
    expect(stageStatusLabel(mkStage({ state: 'active', tone: 'run' }))).toEqual({ text: 'идёт', tone: 'muted' });
    expect(stageStatusLabel(mkStage({ state: 'active', tone: 'wait' }))).toEqual({ text: 'ждёт вас', tone: 'info' });
    expect(stageStatusLabel(mkStage({ state: 'active', tone: 'stopped' }))).toEqual({ text: 'стоит', tone: 'danger' });
  });
  it('active + overlapsActive → «идёт параллельно»', () => {
    expect(stageStatusLabel(mkStage({ state: 'active', tone: 'run', overlapsActive: true })))
      .toEqual({ text: 'идёт параллельно', tone: 'muted' });
  });
  it('pending → «впереди», absent → «не было»', () => {
    expect(stageStatusLabel(mkStage({ state: 'pending' }))).toEqual({ text: 'впереди', tone: 'muted' });
    expect(stageStatusLabel(mkStage({ state: 'absent' }))).toEqual({ text: 'не было', tone: 'muted' });
  });
});

describe('stageBoundsLabel', () => {
  it('старт + конец → «dd.mm · ЧЧ:ММ – ЧЧ:ММ»', () => {
    const s = mkStage({ firstStartedAt: '2026-10-19T17:20:00Z', lastCompletedAt: '2026-10-19T17:55:00Z' });
    expect(stageBoundsLabel(s)).toBe(
      `${localDate('2026-10-19T17:20:00Z')} · ${localTime('2026-10-19T17:20:00Z')} – ${localTime('2026-10-19T17:55:00Z')}`);
  });
  it('только старт → «с dd.mm · ЧЧ:ММ»', () => {
    const s = mkStage({ firstStartedAt: '2026-10-20T10:05:00Z' });
    expect(stageBoundsLabel(s)).toBe(`с ${localDate('2026-10-20T10:05:00Z')} · ${localTime('2026-10-20T10:05:00Z')}`);
  });
  // W5-DEF-хвост п.3: идущая стадия не даёт фиктивный конец — край полосы (barEnd) в
  // подпись не выходит. Мутация: читать barEnd вместо lastCompletedAt — тест краснеет.
  it('active с barEnd: конца нет → «с dd.mm · ЧЧ:ММ»', () => {
    const s = mkStage({ state: 'active', tone: 'run', firstStartedAt: '2026-10-20T10:05:00Z', barEnd: '2026-10-20T12:00:00Z' });
    expect(stageBoundsLabel(s), 'у идущей стадии конца не печатают').toBe(
      `с ${localDate('2026-10-20T10:05:00Z')} · ${localTime('2026-10-20T10:05:00Z')}`);
  });
  it('без старта → null', () => {
    expect(stageBoundsLabel(mkStage())).toBeNull();
  });
});

describe('stageDurationLabel', () => {
  it('0/null → null; <60 → «≈ N мин»; ≥60 → «≈ N ч [M мин]»', () => {
    expect(stageDurationLabel(0)).toBeNull();
    expect(stageDurationLabel(null)).toBeNull();
    expect(stageDurationLabel(undefined)).toBeNull();
    expect(stageDurationLabel(35)).toBe('≈ 35 мин');
    expect(stageDurationLabel(90)).toBe('≈ 1 ч 30 мин');
    expect(stageDurationLabel(60)).toBe('≈ 1 ч');
    expect(stageDurationLabel(120)).toBe('≈ 2 ч');
    expect(stageDurationLabel(290)).toBe('≈ 4 ч 50 мин');
  });
});

describe('stageDotStyle / stageLineStyle', () => {
  it('точка: done → success; active run → textHeading + кольцо; wait → info; stopped → danger; pending → progressTrack; absent → dashed', () => {
    expect(stageDotStyle(mkStage({ state: 'done' })).background).toBe(C.success);
    expect(stageDotStyle(mkStage({ state: 'active', tone: 'run' })).background).toBe(C.textHeading);
    expect(stageDotStyle(mkStage({ state: 'active', tone: 'wait' })).background).toBe(C.info);
    expect(stageDotStyle(mkStage({ state: 'active', tone: 'stopped' })).background).toBe(C.danger);
    expect(stageDotStyle(mkStage({ state: 'pending' })).boxShadow).toContain('progress-track');
    expect(stageDotStyle(mkStage({ state: 'absent' })).boxShadow).toContain('dashed');
  });
  it('точка: overlapsActive → белая с inset-кольцом (не залитая)', () => {
    const st = stageDotStyle(mkStage({ state: 'active', tone: 'run', overlapsActive: true }));
    expect(st.background).toBe(C.bgWhite);
    expect(st.boxShadow).toContain('inset');
  });
  it('линия: последний → null; done → success; absent → пунктир; иначе → progressTrack', () => {
    expect(stageLineStyle(mkStage({ state: 'done' }), true)).toBeNull();
    expect(stageLineStyle(mkStage({ state: 'done' }), false).background).toBe(C.success);
    expect(stageLineStyle(mkStage({ state: 'absent' }), false).borderLeft).toContain('dashed');
    expect(stageLineStyle(mkStage({ state: 'pending' }), false).background).toBe(C.progressTrack);
  });
});

describe('buildTaskMap', () => {
  it('собирает задачи из всех списков run по id', () => {
    const run = mkRun([]);
    run.steps = [{ id: 's1', title: 'шаг' } as FeatureTask];
    run.reviews = [{ id: 'r1', title: 'ревью' } as FeatureTask];
    run.qa = [{ id: 'q1', title: 'проверка' } as FeatureTask];
    run.final = { id: 'fin', title: 'финал' } as FeatureTask;
    run.merge = { id: 'mg', title: 'мерж' } as FeatureTask;
    run.cut = [{ id: 'c1', title: 'разрез' } as FeatureTask];
    run.plan = [{ id: 'p1', title: 'план' } as FeatureTask];
    const map = buildTaskMap(run);
    expect(map.get('s1')?.id).toBe('s1');
    expect(map.get('r1')?.id).toBe('r1');
    expect(map.get('q1')?.id).toBe('q1');
    expect(map.get('fin')?.id).toBe('fin');
    expect(map.get('mg')?.id).toBe('mg');
    expect(map.get('c1')?.id).toBe('c1');
    expect(map.get('p1')?.id).toBe('p1');
    expect(map.get('f1')?.id).toBe('f1'); // epic
    expect(map.size).toBe(8);
  });
  it('пустой run → пустая карта (не падает)', () => {
    expect(buildTaskMap(mkRun([])).size).toBe(1); // только epic
  });
});

describe('minutesSince', () => {
  it('null/невалидно → null; иначе минуты (округление вверх, min 0)', () => {
    expect(minutesSince(null, NOW)).toBeNull();
    expect(minutesSince(undefined, NOW)).toBeNull();
    expect(minutesSince('not-a-date', NOW)).toBeNull();
    expect(minutesSince('2026-10-20T11:30:00Z', NOW)).toBe(30);
    expect(minutesSince('2026-10-20T12:00:00Z', NOW)).toBe(0);
    // Будущий момент → 0 (не отрицательные).
    expect(minutesSince('2026-10-20T12:30:00Z', NOW)).toBe(0);
  });
});

describe('executorStatus', () => {
  it('running → «идёт» + минуты от старта; waiting → «ждёт вас» (info); stopped → «стоит» (danger); done → «готово»; pending → «не начат»', () => {
    const running = executorStatus(mkExec({ status: 'running' }), { claudeStartedAt: '2026-10-20T11:00:00Z' } as FeatureTask, NOW);
    expect(running.label).toBe('идёт');
    expect(running.tone).toBe('muted');
    expect(running.minutes).toBe(60);

    const waiting = executorStatus(mkExec({ status: 'waiting' }), { awaitingHumanSince: '2026-10-20T11:35:00Z' } as FeatureTask, NOW);
    expect(waiting).toEqual({ label: 'ждёт вас', tone: 'info', minutes: 25 });

    const stopped = executorStatus(mkExec({ status: 'stopped' }), { executorStoppedAt: '2026-10-20T11:48:00Z' } as FeatureTask, NOW);
    expect(stopped).toEqual({ label: 'стоит', tone: 'danger', minutes: 12 });

    expect(executorStatus(mkExec({ status: 'done' }), undefined, NOW)).toEqual({ label: 'готово', tone: 'success' });
    expect(executorStatus(mkExec({ status: 'pending' }), undefined, NOW)).toEqual({ label: 'не начат', tone: 'muted' });
  });
  it('без задачи — минуты не выдумывает (undefined)', () => {
    const w = executorStatus(mkExec({ status: 'waiting' }), undefined, NOW);
    expect(w.minutes).toBeUndefined();
    expect(w.label).toBe('ждёт вас');
  });
});

describe('stageStatusBadge', () => {
  it('tone wait → «ждёт вас · N мин» (info); tone stopped → «стоит · N мин» (danger); run/loop → null', () => {
    const taskMap = new Map<string, FeatureTask>([
      ['tw', { id: 'tw', awaitingHumanSince: '2026-10-20T11:35:00Z' } as FeatureTask],
      ['ts', { id: 'ts', executorStoppedAt: '2026-10-20T11:48:00Z' } as FeatureTask],
    ]);
    const wait = stageStatusBadge(
      mkStage({ key: 'prod', state: 'active', tone: 'wait', executors: [mkExec({ taskId: 'tw', status: 'waiting' })] }),
      taskMap, NOW);
    expect(wait).toEqual({ text: 'ждёт вас · 25 мин', tone: 'info' });

    const stop = stageStatusBadge(
      mkStage({ key: 'prod', state: 'active', tone: 'stopped', executors: [mkExec({ taskId: 'ts', status: 'stopped' })] }),
      taskMap, NOW);
    expect(stop).toEqual({ text: 'стоит · 12 мин', tone: 'danger' });

    expect(stageStatusBadge(mkStage({ key: 'prod', state: 'active', tone: 'run' }), taskMap, NOW)).toBeNull();
    expect(stageStatusBadge(mkStage({ key: 'prod', state: 'active', tone: 'loop' }), taskMap, NOW)).toBeNull();
    expect(stageStatusBadge(mkStage({ key: 'prod', state: 'done' }), taskMap, NOW)).toBeNull();
  });
  it('без минут (задачи нет) — плашка без «· N мин»', () => {
    const b = stageStatusBadge(
      mkStage({ key: 'prod', state: 'active', tone: 'wait', executors: [mkExec({ taskId: 'nope', status: 'waiting' })] }),
      new Map(), NOW);
    expect(b).toEqual({ text: 'ждёт вас', tone: 'info' });
  });
});

// === Рендер-тесты (fakeDom) ===

type FNodeLike = { nodeType: number; nodeName: string; childNodes: FNodeLike[]; attributes?: Record<string, string> };

function renderTab(run: FeatureRun, isMobile = false) {
  installFakeDom();
  const { container } = mount(createElement(FeatureTimelineTab, { run, isMobile }));
  return container;
}

// Порядок data-testid этапов в DOM (обход дерева). Ключи — точный набор (иначе
// regex подхватил бы timeline-stage-duration/bounds).
const STAGE_KEYS = ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge'];
function stageOrderInDom(c: FNodeLike): string[] {
  const order: string[] = [];
  const walk = (n: FNodeLike) => {
    const at = n.attributes?.['data-testid'];
    if (at && at.startsWith('timeline-stage-')) {
      const key = at.slice('timeline-stage-'.length);
      if (STAGE_KEYS.includes(key)) order.push(key);
    }
    for (const ch of n.childNodes) walk(ch);
  };
  walk(c);
  return order;
}

describe('FeatureTimelineTab: рендер', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. Порядок 7 этапов (макет §5): cut → plan → prod → review → qa → final → merge ===
  it('семь этапов в порядке жизненного цикла', () => {
    const c = renderTab(mkRun(fullStages()));
    expect(stageOrderInDom(c as never)).toEqual(['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge']);
  });

  // Мутация (в отчёте): переставить review перед prod в fullStages → порядок краснеет.

  // === 2. Границы и «≈» в DOM ===
  it('границы «dd.mm · ЧЧ:ММ – ЧЧ:ММ» и длительность «≈» у завершённого этапа', () => {
    const c = renderTab(mkRun(fullStages()));
    // cut: старт+конец → «dd.mm · ЧЧ:ММ – ЧЧ:ММ» (первая граница в DOM — cut).
    const bounds = findNodeByAttr(c, 'data-testid', 'timeline-stage-bounds');
    expect(bounds).not.toBeNull();
    const t = textNodes(bounds!).join('');
    expect(t).toContain(`${localDate('2026-10-19T17:20:00Z')} · ${localTime('2026-10-19T17:20:00Z')} – ${localTime('2026-10-19T17:55:00Z')}`);
    // «≈» в длительности prod (290 мин → «≈ 4 ч 50 мин»): ищем внутри контейнера prod.
    const prod = findNodeByAttr(c, 'data-testid', 'timeline-stage-prod')!;
    const dur = findNodeByAttr(prod as never, 'data-testid', 'timeline-stage-duration');
    expect(dur).not.toBeNull();
    expect(textNodes(dur!).join('')).toBe('≈ 4 ч 50 мин');
  });

  // Мутация (в отчёте): убрать «≈» из stageDurationLabel → «≈ 4 ч 50 мин» краснеет.

  it('идущий этап — границы «с dd.mm · ЧЧ:ММ» (без конца)', () => {
    const c = renderTab(mkRun(fullStages()));
    // prod: только старт → «с dd.mm · ЧЧ:ММ».
    const all = textNodes(c).join('');
    expect(all).toContain(`с ${localDate('2026-10-20T10:05:00Z')} · ${localTime('2026-10-20T10:05:00Z')}`);
  });

  // === 3. overlapsActive → «идёт параллельно» ===
  it('overlapsActive → подпись «идёт параллельно»', () => {
    const c = renderTab(mkRun(fullStages()));
    // review — overlapsActive.
    const all = textNodes(c).join('');
    expect(all).toContain('идёт параллельно');
  });

  // === 4. Раскрытие: текущий (active) открыт по умолчанию; один за раз ===
  it('текущий этап (prod) открыт по умолчанию; review — закрыт', () => {
    const c = renderTab(mkRun(fullStages()));
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-body-review')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-body-cut')).toBeNull();
  });

  it('клик по другому этапу — раскрывает его и схлопывает предыдущий (один за раз)', () => {
    const c = renderTab(mkRun(fullStages()));
    const reviewHeader = findNodeByAttr(c, 'data-testid', 'timeline-stage-header-review')!;
    act(() => { (reactProps(reviewHeader)!.onClick as () => void)(); });
    // review раскрыт, prod схлопнут.
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-body-review')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')).toBeNull();
    // Повторный клик по review — схлопывает (none).
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'timeline-stage-header-review')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-body-review')).toBeNull();
  });

  // === 5. Исполнители этапов кроме «Производства»: roleLabel + чип модели + «чата нет» ===
  // «Производство» — лента карточек (W4-2F3), ExecutorRow остаётся в других этапах:
  // coverage здесь, через этап «Ревью».
  function openStage(c: FNodeLike, key: string): FNodeLike {
    const header = findNodeByAttr(c, 'data-testid', `timeline-stage-header-${key}`)!;
    act(() => { (reactProps(header)!.onClick as () => void)(); });
    return findNodeByAttr(c, 'data-testid', `timeline-stage-body-${key}`)!;
  }

  it('исполнитель этапа «Ревью»: имя персоны, роль (roleLabel: review → «ревью»), чип модели', () => {
    const stages = fullStages();
    stages[3]!.executors = [mkExec({ personaId: 'p1', taskId: 't-rev', taskRole: 'review', status: 'running', chatId: 'chat-r', modelLabel: 'Opus', modelFull: 'Opus 4.5' })];
    const c = renderTab(mkRun(stages, { reviews: [{ id: 't-rev', title: 'Ревью', status: 'inProgress' }] }));
    const body = openStage(c, 'review');
    const row = findNodeByAttr(body, 'data-testid', 'timeline-executor');
    expect(row).not.toBeNull();
    const t = textNodes(row!).join('');
    expect(t).toContain('Денис');
    expect(t).toContain('ревью');
    // Чип модели.
    const chip = findNodeByAttr(body, 'data-testid', 'timeline-model-chip');
    expect(chip).not.toBeNull();
    expect(textNodes(chip!).join('')).toBe('Opus');
  });

  it('исполнитель без чата (chatId == null) → «чата нет» (без мёртвой ссылки)', () => {
    const stages = fullStages();
    // review: исполнитель без chatId; задача t2 в run со статусом done.
    stages[3]!.executors = [mkExec({ personaId: 'p1', taskId: 't2', taskRole: 'review', status: 'done', chatId: null })];
    const c = renderTab(mkRun(stages, { reviews: [{ id: 't2', title: 'Ревью 2', status: 'done' }] }));
    const body = openStage(c, 'review');
    const noChat = findNodeByAttr(body, 'data-testid', 'nav-target-no-chat');
    expect(noChat).not.toBeNull();
    expect(textNodes(noChat!).join('')).toBe('чата нет');
    // R1 major: taskClosed={task?.status === 'done'} (один источник: статус задачи) →
    // кнопка задачи показывает «закрыта».
    const taskBtn = findNodeByAttr(body, 'data-testid', 'nav-target-task');
    expect(taskBtn, 'кнопка задачи при taskId != null').not.toBeNull();
    expect(textNodes(taskBtn!).join('')).toContain('закрыта');
  });

  it('исполнитель с чатом → кнопка «чат» (NavTargetButton)', () => {
    const stages = fullStages();
    stages[3]!.executors = [mkExec({ personaId: 'p1', taskId: 't-rev', taskRole: 'review', status: 'running', chatId: 'chat-r' })];
    const c = renderTab(mkRun(stages, { reviews: [{ id: 't-rev', title: 'Ревью', status: 'inProgress' }] }));
    // «чат» в конкретной строке раскрытого этапа (не подстрока по всему дереву).
    const pair = findNodeByAttr(openStage(c, 'review'), 'data-testid', 'nav-target-pair');
    expect(pair, 'NavTargetPair не найден').not.toBeNull();
    const pairText = textNodes(pair!).join('');
    expect(pairText).toContain('чат');
    expect(pairText).toContain('задача');
  });

  // === 6. Пустые состояния ===
  it('фича не начата: все этапы pending → «впереди», раскрытых нет', () => {
    const stages = [
      mkStage({ key: 'cut' }), mkStage({ key: 'plan' }), mkStage({ key: 'prod' }),
      mkStage({ key: 'review' }), mkStage({ key: 'qa' }), mkStage({ key: 'final' }), mkStage({ key: 'merge' }),
    ];
    const c = renderTab(mkRun(stages));
    const all = textNodes(c).join('');
    expect(all.split('впереди').length - 1).toBe(7);
    // Ни один этап не раскрыт (текущего нет).
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')).toBeNull();
  });

  it('stages нет (старые данные) → поясняющая строка есть, этапов нет', () => {
    const c = renderTab(mkRun([]));
    expect(findNodeByAttr(c, 'data-testid', 'timeline-note')).not.toBeNull();
    expect(stageOrderInDom(c as never)).toEqual([]);
  });

  it('этап absent → «не было» (приглушённо)', () => {
    const stages = fullStages();
    stages[4] = mkStage({ key: 'qa', state: 'absent' });
    const c = renderTab(mkRun(stages));
    const all = textNodes(c).join('');
    expect(all).toContain('не было');
  });

  // === 7. Плашки состояния этапа ===
  it('этап wait → плашка «ждёт вас · N мин» (info); stopped → «стоит · N мин» (danger)', () => {
    const taskMapRun = mkRun([
      mkStage({ key: 'prod', state: 'active', tone: 'wait',
        executors: [mkExec({ personaId: 'p1', taskId: 'tw', status: 'waiting' })] }),
    ]);
    taskMapRun.steps = [{ id: 'tw', awaitingHumanSince: '2026-10-20T11:35:00Z' } as FeatureTask];
    const c = renderTab(taskMapRun);
    const badge = findNodeByAttr(c, 'data-testid', 'timeline-stage-badge');
    expect(badge).not.toBeNull();
    expect(textNodes(badge!).join('')).toContain('ждёт вас · 25 мин');
  });

  // === 8. 360: тач-цель 44 (кнопка-заголовок этапа, GC-F5c) ===
  it('на 360 кнопка-заголовок этапа — minHeight 44', () => {
    const c = renderTab(mkRun(fullStages()), true);
    const header = findNodeByAttr(c, 'data-testid', 'timeline-stage-header-prod')!;
    const st = (header as unknown as { style: Record<string, string> }).style;
    expect(st.minHeight).toBe('44px');
  });
});

// === Ф2b: чистые функции — шаги и витки ===

// Хелперы данных Ф2b.
function mkChain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'Ш1',
    lane: 'back',
    title: 'Модель шаблона',
    personaId: 'p1',
    taskIds: ['t-s1'],
    outcome: 'accepted',
    status: 'принят',
    durationMinutes: 40,
    source: null,
    loops: 0,
    rounds: [],
    chatId: 'chat-s1',
    ...over,
  };
}

function mkRound(over: Partial<FeatureRound> = {}): FeatureRound {
  return {
    n: 1,
    verdict: 'accepted',
    reviewTaskId: 't-r1',
    personaId: 'p2',
    startedAt: null,
    completedAt: null,
    reason: null,
    reviewChatId: 'chat-r1',
    ...over,
  };
}

describe('stepStatusView', () => {
  // Обёртки stepStatusLabel/stepStatusTone удалены как мёртвый код (хвост R4e) —
  // проверяется сам источник: label и tone всегда производятся вместе.
  it('готовая строка бэка (chain.status) — подпись как есть, тон из неё (не из outcome)', () => {
    expect(stepStatusView(mkChain({ status: 'ждёт ревью' }))).toEqual({ label: 'ждёт ревью', tone: 'muted' });
    expect(stepStatusView(mkChain({ status: 'снят с выкладки' }))).toEqual({ label: 'снят с выкладки', tone: 'muted' });
    // Мутация (в отчёте): вернуть тон из outcome → «идёт» (status) + outcome=accepted →
    // tone success вместо active → краснеет.
    expect(stepStatusView(mkChain({ status: 'идёт', outcome: 'accepted' }))).toEqual({ label: 'идёт', tone: 'active' });
    expect(stepStatusView(mkChain({ status: 'принят', outcome: 'running' }))).toEqual({ label: 'принят', tone: 'success' });
  });
  it('status отсутствует (старый ответ) → фолбэк из outcome, label и tone вместе', () => {
    expect(stepStatusView(mkChain({ status: undefined as unknown as string, outcome: 'pending' }))).toEqual({ label: 'ждёт ревью', tone: 'muted' });
    expect(stepStatusView(mkChain({ status: undefined as unknown as string, outcome: 'accepted' }))).toEqual({ label: 'принят', tone: 'success' });
    expect(stepStatusView(mkChain({ status: undefined as unknown as string, outcome: 'running' }))).toEqual({ label: 'идёт', tone: 'active' });
    expect(stepStatusView(mkChain({ status: undefined as unknown as string, outcome: 'returned' }))).toEqual({ label: 'закрыт без повторного ревью', tone: 'active' });
    expect(stepStatusView(mkChain({ status: undefined as unknown as string, outcome: 'withdrawn' }))).toEqual({ label: 'снят с выкладки', tone: 'muted' });
  });
});

describe('stepModel', () => {
  it('модель — у исполнителя стадии с тем же stepRef (задача-шаг приоритетнее доработки); нет совпадения → null', () => {
    const execs = [
      mkExec({ stepRef: 'Ш1', taskRole: 'rework', modelLabel: 'Sonnet', modelFull: 'Sonnet 4.5' }),
      mkExec({ stepRef: 'Ш1', taskRole: 'step', modelLabel: 'Opus', modelFull: 'Opus 4.5' }),
      mkExec({ stepRef: 'Ш2', taskRole: 'step', modelLabel: 'Qwen', modelFull: null }),
    ];
    expect(stepModel(execs, 'Ш1')).toEqual({ label: 'Opus', full: 'Opus 4.5' });
    expect(stepModel(execs, 'Ш2')).toEqual({ label: 'Qwen', full: null });
    expect(stepModel(execs, 'Ш3')).toBeNull();
    expect(stepModel(execs, '')).toBeNull();
  });
});

// === W4-2F3: лента «Производства» — подключение FeatureProdCardsView к вкладке ===

describe('FeatureTimelineTab: лента «Производства»', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    mockOpenChat.calls = [];
    mockOpenTask.calls = [];
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Фикстура обычной фичи: этап «Производство» (fullStages) + начатый шаг Ш1 с витком
  // «принят» (Глеб). Карточка строится из stepChains — тот же вход, что у модели.
  function prodRun(over: Partial<FeatureRun> = {}): FeatureRun {
    return mkRun(fullStages(), {
      steps: [{ id: 't-s1', title: 'Модель шаблона', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }],
      stepChains: [mkChain({ rounds: [mkRound({ n: 1, verdict: 'accepted', personaId: 'p2', reviewChatId: 'chat-r1' })] })],
      ...over,
    });
  }

  // === 1. Одна карточка на шаг: дубля «исполнители, потом шаги» нет ===
  it('лента в теле «Производства»: карточка Ш1 с цепочкой; строк «исполнитель» и «шаг» нет', () => {
    const c = renderTab(prodRun());
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')!;
    expect(findNodeByAttr(body, 'data-testid', 'prod-lane'), 'лента в теле этапа «Производство»').not.toBeNull();
    expect(findNodeByAttr(body, 'data-testid', 'prod-card-Ш1'), 'карточка шага Ш1').not.toBeNull();
    // Задача видна один раз: ни строки исполнителя, ни строки шага.
    expect(findNodeByAttr(body, 'data-testid', 'timeline-executor'), 'строки «исполнитель» в «Производстве» нет').toBeNull();
    expect(findNodeByAttr(body, 'data-testid', 'feature-step-Ш1'), 'строки «шаг» нет').toBeNull();
    // Цепочка «работа → принят» — узлы карточки, живут в раскрытой части: раскрываем кликом по карточке.
    act(() => { (reactProps(findNodeByAttr(body, 'data-testid', 'prod-card-Ш1')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(body, 'data-testid', 'prod-chain'), 'цепочки карточки').not.toBeNull();
    expect(textNodes(findNodeByAttr(body, 'data-testid', 'prod-status')!).join('')).toBe('принят');
  });

  // === 2. Лента видит тот же run: «сейчас» над текущей карточкой ===
  it('«сейчас» — только над текущей карточкой (run.now доходит до ленты)', () => {
    const c = renderTab(prodRun({ now: [{ taskId: 't-s1', stage: 'prod', title: 'Модель шаблона', stepRef: 'Ш1' }] }));
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')!;
    expect(findNodeByAttr(body, 'data-testid', 'prod-nowline'), 'строка «сейчас»').not.toBeNull();
    // Без run.now — строки «сейчас» нет (сравнение с обычной фикстурой).
    const plain = renderTab(prodRun());
    expect(findNodeByAttr(findNodeByAttr(plain, 'data-testid', 'timeline-stage-body-prod')!, 'data-testid', 'prod-nowline'), 'без run.now строки нет').toBeNull();
  });

  // === 3. Пустая лента и никого на ней — существующее пустое состояние вкладки ===
  it('шагов нет и исполнителей нет (stepChains пуст) → «Шагов пока нет», ленты нет', () => {
    const stages = fullStages();
    stages[2]!.executors = [];
    const c = renderTab(mkRun(stages, { stepChains: [] }));
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')!;
    const empty = findNodeByAttr(body, 'data-testid', 'feature-steps-empty');
    expect(empty, 'пустое состояние «Шагов пока нет»').not.toBeNull();
    expect(textNodes(empty!).join('')).toContain('Шагов пока нет');
    expect(findNodeByAttr(body, 'data-testid', 'prod-lane'), 'ленты нет').toBeNull();
    expect(findNodeByAttr(body, 'data-testid', 'timeline-executor'), 'строк исполнителей нет').toBeNull();
  });

  // === 3b. Сироты «Производства» (фикс R4e): задачи без цепочки не исчезают из вкладки ===
  it('у prod есть исполнители, stepChains пуст → строка исполнителя, а не «Шагов пока нет»', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [] }));
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')!;
    const row = findNodeByAttr(body, 'data-testid', 'timeline-executor');
    expect(row, 'доработка-сирота — прежняя строка исполнителя').not.toBeNull();
    expect(textNodes(row!).join('')).toContain('Денис');
    expect(findNodeByAttr(body, 'data-testid', 'feature-steps-empty'), 'пустого состояния нет').toBeNull();
    expect(findNodeByAttr(body, 'data-testid', 'prod-lane'), 'ленты нет').toBeNull();
  });

  // Мутация (в отчёте): убрать фолбэк на ExecutorRow при пустой ленте → этот тест краснеет.

  it('stepChains нет в данных, исполнителей нет (старая фича) → «Шагов пока нет» (честная деградация)', () => {
    const stages = fullStages();
    stages[2]!.executors = [];
    const c = renderTab(mkRun(stages));
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')!;
    const empty = findNodeByAttr(body, 'data-testid', 'feature-steps-empty');
    expect(empty, 'пустое состояние «Шагов пока нет»').not.toBeNull();
    expect(findNodeByAttr(body, 'data-testid', 'prod-lane'), 'ленты нет').toBeNull();
  });

  // === 4. 360: isMobile доходит до ленты — зона касания 40 px на самой кнопке ===
  it('360: узел ленты — кнопка сама 40 px по вертикали, видимая капсула 20 px', () => {
    const c = renderTab(prodRun(), true);
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'prod-card-Ш1')!)!.onClick as () => void)(); });
    const node = findNodeByAttr(c, 'data-testid', 'prod-node');
    expect(node, 'узел-кнопка с чатом').not.toBeNull();
    const st = (reactProps(node as never)!.style ?? {}) as Record<string, string>;
    // Кнопка сама — поле касания: 40 px, отрицательные отступы — раскладка не смещается.
    expect(st.minHeight).toBe(40);
    expect(st.margin).toBe('-10px 0');
    // Видимая капсула — внутренний span: высота узла, как на десктопе.
    const pill = (node as { childNodes: unknown[] }).childNodes[0];
    const ps = (reactProps(pill as never)!.style ?? {}) as Record<string, string>;
    expect(ps.lineHeight).toBe('20px');
  });
});

// === W4-3F: строка «оценивает →» ===

describe('buildExecutorIndex / evaluatedLine (W4-3F)', () => {
  it('индекс — по taskId из stages[].executors[] (единственный источник статуса)', () => {
    const idx = buildExecutorIndex([
      mkStage({ executors: [mkExec({ taskId: 't-s1', status: 'done' })] }),
      mkStage({ key: 'review', executors: [mkExec({ taskId: 't-r1', taskRole: 'review' })] }),
    ]);
    expect(idx.get('t-s1')?.status).toBe('done');
    expect(idx.get('нет')).toBeUndefined();
  });

  it('review/qa — чипы оцениваемых задач: заголовок из taskMap, статус из исполнителя стадии', () => {
    const execIndex = buildExecutorIndex([mkStage({ executors: [mkExec({ taskId: 't-s1', status: 'done' })] })]);
    const taskMap = new Map<string, FeatureTask>([['t-s1', { id: 't-s1', title: 'Модель шаблона' } as FeatureTask]]);
    const line = evaluatedLine(
      mkExec({ taskId: 't-r1', taskRole: 'review', reviewOf: ['t-s1'], reviewOfOutside: 0 }),
      taskMap, execIndex, null, NOW);
    expect(line).toEqual({
      targets: [{ taskId: 't-s1', title: 'Модель шаблона', statusLabel: 'готово', tone: 'success' }],
      outside: 0,
      scopeLabel: null,
    });
  });

  it('reviewOfOutside > 0 — нейтральная подпись; ни целей, ни outside — строки нет', () => {
    const line = evaluatedLine(mkExec({ taskRole: 'review', reviewOf: [], reviewOfOutside: 2 }), new Map(), new Map(), null, NOW);
    expect(line).toEqual({ targets: [], outside: 2, scopeLabel: null });
    expect(evaluatedLine(mkExec({ taskRole: 'review', reviewOf: [], reviewOfOutside: 0 }), new Map(), new Map(), null, NOW)).toBeNull();
  });

  it('у step и rework строки нет: ReviewOf доработки — «откуда», а не «что оценивает»', () => {
    expect(evaluatedLine(mkExec({ taskRole: 'step', reviewOf: ['t-r1'] }), new Map(), new Map(), null, NOW)).toBeNull();
    expect(evaluatedLine(mkExec({ taskRole: 'rework', reviewOf: ['t-r1'] }), new Map(), new Map(), null, NOW)).toBeNull();
  });

  it('final — «фичу целиком: N из M принято» из stepProgress; прогресса нет — null', () => {
    const p: StepProgress = { planState: 'partial', total: 5, accepted: 3, review: 1, work: 1, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 };
    expect(evaluatedLine(mkExec({ taskRole: 'final' }), new Map(), new Map(), p, NOW)?.scopeLabel)
      .toBe('фичу целиком: 3 из 5 принято');
    expect(evaluatedLine(mkExec({ taskRole: 'final' }), new Map(), new Map(), null, NOW)).toBeNull();
  });

  it('plan — «по разрезу», cut — «по эпику»', () => {
    expect(evaluatedLine(mkExec({ taskRole: 'plan' }), new Map(), new Map(), null, NOW)?.scopeLabel).toBe('по разрезу');
    expect(evaluatedLine(mkExec({ taskRole: 'cut' }), new Map(), new Map(), null, NOW)?.scopeLabel).toBe('по эпику');
  });

  it('склонение «N связей не найдено»: 1/2/5 — по русским правилам', () => {
    expect(outsideLinksLabel(0)).toBe('');
    expect(outsideLinksLabel(1)).toBe('1 связь не найдена');
    expect(outsideLinksLabel(2)).toBe('2 связи не найдены');
    expect(outsideLinksLabel(5)).toBe('5 связей не найдено');
  });
});

describe('FeatureTimelineTab: строка «оценивает →» (рендер)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  function openStage2(c: FNodeLike, key: string): FNodeLike {
    const header = findNodeByAttr(c, 'data-testid', `timeline-stage-header-${key}`)!;
    act(() => { (reactProps(header)!.onClick as () => void)(); });
    return findNodeByAttr(c, 'data-testid', `timeline-stage-body-${key}`)!;
  }

  // Мутация (в отчёте): убрать ветку timeline-eval-outside из рендера → этот тест краснеет.
  it('reviewOfOutside > 0 → нейтральная подпись «2 связи не найдены» (не «вне фичи»)', () => {
    const stages = fullStages();
    stages[3]!.executors = [mkExec({ personaId: 'p1', taskId: 't-rev', taskRole: 'review', status: 'done', reviewOf: [], reviewOfOutside: 2 })];
    const c = renderTab(mkRun(stages, { reviews: [{ id: 't-rev', title: 'Ревью', status: 'done' }] }));
    const body = openStage2(c, 'review');
    const outside = findNodeByAttr(body, 'data-testid', 'timeline-eval-outside');
    expect(outside, 'нейтральная подпись о висящих связях').not.toBeNull();
    expect(textNodes(outside!).join('')).toBe('2 связи не найдены');
    const all = textNodes(body).join('');
    expect(all).not.toContain('вне фичи');
    // Само ревью при этом виден: строка «оценивает →» есть.
    expect(findNodeByAttr(body, 'data-testid', 'timeline-eval-line')).not.toBeNull();
  });

  it('чипы оцениваемых задач у review: заголовок и статус из stages[].executors[]', () => {
    const stages = fullStages();
    // Исполнитель шага Ш1 закрыт (done) — статус чипа берётся из его записи в стадии.
    stages[2]!.executors = [mkExec({ personaId: 'p1', taskId: 't-s1', taskRole: 'step', stepRef: 'Ш1', status: 'done' })];
    stages[3]!.executors = [mkExec({ personaId: 'p2', taskId: 't-rev', taskRole: 'review', status: 'done', reviewOf: ['t-s1'] })];
    const c = renderTab(mkRun(stages, { steps: [{ id: 't-s1', title: 'Модель шаблона', status: 'done' } as FeatureTask] }));
    const body = openStage2(c, 'review');
    const target = findNodeByAttr(body, 'data-testid', 'timeline-eval-target');
    expect(target, 'чип оцениваемой задачи').not.toBeNull();
    const t = textNodes(target!).join('');
    expect(t).toContain('Модель шаблона');
    expect(t).toContain('готово');
  });

  it('сырой reviewOf задачи — не источник: у исполнителя связи нет, строки нет', () => {
    const stages = fullStages();
    stages[3]!.executors = [mkExec({ personaId: 'p1', taskId: 't-rev', taskRole: 'review', status: 'done' })];
    // reviewOf есть только в сырой FeatureTask — из неё связь не берётся.
    const c = renderTab(mkRun(stages, { reviews: [{ id: 't-rev', title: 'Ревью', status: 'done', reviewOf: ['t-s1'] } as FeatureTask] }));
    const body = openStage2(c, 'review');
    expect(findNodeByAttr(body, 'data-testid', 'timeline-eval-line'), 'строки «оценивает →» нет').toBeNull();
  });

  it('у rework чипов «оценивает →» нет (ReviewOf — «откуда вернулась»)', () => {
    const stages = fullStages();
    stages[3]!.executors = [mkExec({ personaId: 'p1', taskId: 't-rw', taskRole: 'rework', stepRef: 'Ш1', status: 'running', reviewOf: ['t-rev'], reviewOfOutside: 1 })];
    const c = renderTab(mkRun(stages, { reviews: [{ id: 't-rev', title: 'Ревью', status: 'done' }] }));
    const body = openStage2(c, 'review');
    expect(findNodeByAttr(body, 'data-testid', 'timeline-eval-target')).toBeNull();
    expect(findNodeByAttr(body, 'data-testid', 'timeline-eval-outside')).toBeNull();
  });

  it('final — «фичу целиком: 3 из 5 принято» из stepProgress', () => {
    const stages = fullStages();
    stages[5]!.executors = [mkExec({ personaId: 'p2', taskId: 't-fin', taskRole: 'final', status: 'done' })];
    const c = renderTab(mkRun(stages, {
      final: { id: 't-fin', title: 'Финальное ревью', status: 'done' } as FeatureTask,
      stepProgress: { planState: 'partial', total: 5, accepted: 3, review: 1, work: 1, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 },
    }));
    const body = openStage2(c, 'final');
    const scope = findNodeByAttr(body, 'data-testid', 'timeline-eval-scope');
    expect(scope, 'подпись финала по всей фиче').not.toBeNull();
    expect(textNodes(scope!).join('')).toBe('фичу целиком: 3 из 5 принято');
  });

  it('план — «по разрезу», разрез — «по эпику»', () => {
    const stages = fullStages();
    stages[0]!.executors = [mkExec({ personaId: 'p2', taskId: 't-cut', taskRole: 'cut', status: 'done' })];
    stages[1]!.executors = [mkExec({ personaId: 'p2', taskId: 't-plan', taskRole: 'plan', status: 'done' })];
    const c = renderTab(mkRun(stages, {
      cut: [{ id: 't-cut', title: 'Разрез' } as FeatureTask],
      plan: [{ id: 't-plan', title: 'План' } as FeatureTask],
    }));
    expect(textNodes(findNodeByAttr(openStage2(c, 'cut'), 'data-testid', 'timeline-eval-scope')!).join('')).toBe('по эпику');
    expect(textNodes(findNodeByAttr(openStage2(c, 'plan'), 'data-testid', 'timeline-eval-scope')!).join('')).toBe('по разрезу');
  });
});

// === GC-F5c: дорожка и ось в строке этапа, roving tabindex, 360 ===

// Фикстура stats: таймлайн с интервалами этапов (окно 10:00–12:00).
function mkStats(intervalsByStage: Record<string, unknown[]> | null): FeatureStats {
  return {
    timeline: {
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T12:00:00Z',
      stages: [],
      intervalsByStage,
      segments: [],
      current: null,
      totals: {},
    },
  } as unknown as FeatureStats;
}

function iv(from: string, to: string, live = false) {
  return { from, to, minutes: 30, live, lastOutcome: null, taskIds: ['t1'], turns: [] };
}

// Две стадии с интервалами: prod (10:00–10:30, 11:00–11:30) и review (10:20–10:40, 11:30–11:50).
function laneStats(): FeatureStats {
  return mkStats({
    prod: [iv('2026-10-20T10:00:00Z', '2026-10-20T10:30:00Z'), iv('2026-10-20T11:00:00Z', '2026-10-20T11:30:00Z')],
    review: [iv('2026-10-20T10:20:00Z', '2026-10-20T10:40:00Z'), iv('2026-10-20T11:30:00Z', '2026-10-20T11:50:00Z')],
  });
}

// Все узлы-интервалы дорожек: [testid, tabIndex].
function intervalNodes(c: FNodeLike): { id: string; tabIndex: number }[] {
  const out: { id: string; tabIndex: number }[] = [];
  const walk = (n: FNodeLike) => {
    const at = n.attributes?.['data-testid'];
    if (at && at.startsWith('lane-interval-')) {
      const props = reactProps(n as never) as { tabIndex?: number } | null;
      out.push({ id: at, tabIndex: props?.tabIndex ?? -999 });
    }
    for (const ch of n.childNodes) walk(ch);
  };
  walk(c);
  return out;
}

// Клавиша на корне вкладки (событие всплывает от интервала к корню).
function pressKey(c: FNodeLike, key: string) {
  const root = findNodeByAttr(c, 'data-testid', 'feature-timeline-tab')!;
  const props = reactProps(root as never) as { onKeyDown?: (e: unknown) => void } | null;
  act(() => { props?.onKeyDown?.({ key, preventDefault: () => {} }); });
}

function renderTabWithStats(run: FeatureRun, stats: FeatureStats | null, isMobile = false) {
  installFakeDom();
  const { container } = mount(createElement(FeatureTimelineTab, { run, stats, isMobile }));
  return container;
}

describe('FeatureTimelineTab: дорожки и ось (GC-F5c)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. Сетка строки: 250px | 1fr | 104px (критерий c2) ===
  it('строка этапа — сетка 250px 1fr 104px до раскрытия этапа и после', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    const grid = findNodeByAttr(c, 'data-testid', 'timeline-stage-grid-prod')!;
    const st = (grid as unknown as { style: Record<string, string> }).style;
    expect(st.gridTemplateColumns).toBe('250px 1fr 104px');
    // Раскрыли этап — сетка строки не поехала.
    const header = findNodeByAttr(c, 'data-testid', 'timeline-stage-header-prod')!;
    act(() => { (reactProps(header as never)!.onClick as () => void)(); });
    const grid2 = findNodeByAttr(c, 'data-testid', 'timeline-stage-grid-prod')!;
    expect((grid2 as unknown as { style: Record<string, string> }).style.gridTemplateColumns).toBe('250px 1fr 104px');
  });

  // === 2. Дорожка рисуется, границы и паузы под строкой ===
  it('дорожка этапа с интервалами; под строкой — границы и паузы', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    expect(findNodeByAttr(c, 'data-testid', 'lane-prod'), 'дорожка этапа').not.toBeNull();
    const prod = findNodeByAttr(c, 'data-testid', 'timeline-stage-prod')!;
    const bounds = findNodeByAttr(prod as never, 'data-testid', 'timeline-stage-bounds');
    const t = textNodes(bounds!).join('');
    expect(t).toContain('–');
    expect(t, 'число интервалов под строкой').toContain('2 интервала');
  });

  // Мутация: убрать detail из границ → «2 интервала» краснеет.

  it('ось над лентой: подписи шкалы и «сейчас» у открытой фичи', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    expect(findNodeByAttr(c, 'data-testid', 'lane-axis')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'lane-axis-label-now')).not.toBeNull();
  });

  it('у закрытой фичи подписи «сейчас» нет (контракт вызывающего)', () => {
    const c = renderTabWithStats(mkRun(fullStages(), { state: 'closed' }), laneStats());
    expect(findNodeByAttr(c, 'data-testid', 'lane-axis')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'lane-axis-label-now')).toBeNull();
  });

  // === 3. approx: журнала нет → дорожки нет, прежние границы ===
  it('approx (журнала нет): дорожки нет, границы прежние', () => {
    const c = renderTabWithStats(mkRun(fullStages()), mkStats(null));
    expect(findNodeByAttr(c, 'data-testid', 'lane-prod'), 'дорожки при approx нет').toBeNull();
    expect(intervalNodes(c as never)).toHaveLength(0);
    const bounds = textNodes(findNodeByAttr(c, 'data-testid', 'timeline-stage-bounds')!).join('');
    expect(bounds, 'прежние границы этапа').toContain('·');
  });

  // === 4. Roving tabindex: одна точка табуляции (критерий c1) ===
  it('ровно один интервал с tabIndex=0 — точка табуляции одна', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    const nodes = intervalNodes(c as never);
    expect(nodes.length).toBeGreaterThan(1);
    expect(nodes.filter(n => n.tabIndex === 0)).toHaveLength(1);
    expect(nodes.find(n => n.tabIndex === 0)!.id).toBe('lane-interval-prod-0');
  });

  it('ArrowRight → следующий интервал этапа; Home/End → первый и последний', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    pressKey(c as never, 'ArrowRight');
    let nodes = intervalNodes(c as never);
    expect(nodes.filter(n => n.tabIndex === 0)).toHaveLength(1);
    expect(nodes.find(n => n.tabIndex === 0)!.id).toBe('lane-interval-prod-1');
    pressKey(c as never, 'Home');
    nodes = intervalNodes(c as never);
    expect(nodes.find(n => n.tabIndex === 0)!.id).toBe('lane-interval-prod-0');
    pressKey(c as never, 'End');
    nodes = intervalNodes(c as never);
    expect(nodes.find(n => n.tabIndex === 0)!.id).toBe('lane-interval-prod-1');
  });

  it('ArrowDown → ближайший по x интервал соседнего этапа', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    pressKey(c as never, 'ArrowDown');
    const nodes = intervalNodes(c as never);
    expect(nodes.filter(n => n.tabIndex === 0)).toHaveLength(1);
    // Ближайший к prod-0 (центр 10:15) — review-0 (10:20–10:40), а не review-1 (11:30–11:50).
    expect(nodes.find(n => n.tabIndex === 0)!.id).toBe('lane-interval-review-0');
  });

  it('ArrowUp → ближайший по x интервал этапа выше', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    pressKey(c as never, 'ArrowDown');
    pressKey(c as never, 'ArrowUp');
    const nodes = intervalNodes(c as never);
    expect(nodes.find(n => n.tabIndex === 0)!.id).toBe('lane-interval-prod-0');
  });

  // === 5. 360: строка-кнопка 44, дорожка под именем и ВНЕ кнопки ===
  it('на 360 кнопка строки — 44 px, дорожка не внутри кнопки', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats(), true);
    const header = findNodeByAttr(c, 'data-testid', 'timeline-stage-header-prod')!;
    expect((header as unknown as { style: Record<string, string> }).style.minHeight).toBe('44px');
    // Внутри кнопки строки интервалов быть не может (вложенные кнопки).
    const inside = intervalNodes(header as never);
    expect(inside, 'в кнопке строки нет интервалов').toHaveLength(0);
    // Дорожка есть и стоит после кнопки (под именем).
    expect(findNodeByAttr(c, 'data-testid', 'lane-prod')).not.toBeNull();
  });

  // === 6. Счётчик: план «N/M» только у «Производства», у прочих — факт ===
  // GC-F12R: N = принято (accepted), M = всего (total). Шаг в работе ещё не сделан —
  // «N/M» читается как «сделано N из M»; текст несёт planCounterText() модели, та же
  // строка, что печатает сводка «Статистики». Мутация: вернуть made=done+inWork → «2/4» краснеет.
  it('счётчик плана «N/M» — только у prod, N = принято; у ревью — факт (число задач)', () => {
    const stages = fullStages();
    stages[3]!.executors = [mkExec({ taskId: 't-rev', status: 'running' })];
    const c = renderTabWithStats(mkRun(stages, {
      stepProgress: { planState: 'partial', total: 4, accepted: 2, review: 1, work: 1, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 },
    }), laneStats());
    const counters: Record<string, string> = {};
    for (const key of ['prod', 'review']) {
      const stage = findNodeByAttr(c, 'data-testid', `timeline-stage-${key}`)!;
      const counter = findNodeByAttr(stage as never, 'data-testid', 'timeline-stage-counter');
      counters[key] = counter ? textNodes(counter).join('') : '';
    }
    // accepted=2, work=1, review=1 → «2/4», а не «4/4»: в работе — не «сделано».
    expect(counters['prod'], 'у prod — план шагов, N = принято').toBe('2/4');
    // GC-F10a: факт согласован с числом — «1 задача», а не «1 задач».
    expect(counters['review'], 'у ревью — факт').toBe('1 задача');
  });
});

// === GC-F5cR: ось времени по той же колонке, что и дорожка ===
//
// Отступы оси выводятся ЗДЕСЬ из разметки самой строки этапа (отступ строки +
// колонки сетки + зазор), а не из констант компонента: иначе тест повторял бы
// реализацию и зеленел бы после любой правки. Мутация: сдвинуть шкалу на 24 px
// (вернуть insetLeft без отступа строки) — «ось по колонке дорожек» краснеет.
describe('FeatureTimelineTab: ось по колонке дорожек (GC-F5cR)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Отступы колонки дорожек в координатах корня вкладки: считаются по стилям
  // самой строки этапа (отступ строки + колонки сетки + зазор).
  function laneOffsets(c: FNodeLike): { left: number; right: number } {
    const row = findNodeByAttr(c as never, 'data-testid', 'timeline-stage-prod')!;
    const grid = findNodeByAttr(c as never, 'data-testid', 'timeline-stage-grid-prod')!;
    const rowStyle = (row as unknown as { style: Record<string, string> }).style;
    const gridStyle = (grid as unknown as { style: Record<string, string> }).style;
    const pad = Number(String(rowStyle.paddingLeft).replace('px', ''));
    const gap = Number(String(gridStyle.gap).replace('px', ''));
    const cols = String(gridStyle.gridTemplateColumns).split(' ');
    const px = (s: string) => Number(s.replace('px', ''));
    if (cols.length === 1) {
      // 360: одна колонка — дорожка под именем, во всю ширину строки.
      return { left: pad, right: 0 };
    }
    // Десктоп: колонка имени слева, колонка длительности справа, дорожка между ними.
    return { left: pad + px(cols[0]!) + gap, right: px(cols[2]!) + gap };
  }

  function axisInsets(c: FNodeLike): { left: number; right: number } {
    const scale = findNodeByAttr(c as never, 'data-testid', 'lane-axis-scale')!;
    const st = (scale as unknown as { style: Record<string, string> }).style;
    return {
      left: Number(String(st.marginLeft).replace('px', '')),
      right: Number(String(st.marginRight).replace('px', '')),
    };
  }

  it('десктоп: ось начинается там же, где дорожка, и кончается там же', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats());
    const lane = laneOffsets(c as never);
    const axis = axisInsets(c as never);
    expect(axis.left, 'левый край шкалы = левый край дорожки').toBe(lane.left);
    expect(axis.right, 'правый край шкалы = правый край дорожки').toBe(lane.right);
    // Числа — для опознания сдвига: 24 (отступ строки) + 250 (имя) + 12 (зазор).
    expect(lane.left).toBe(286);
    expect(lane.right).toBe(116);
  });

  it('360: ось встаёт по отступу строки, во всю ширину дорожки', () => {
    const c = renderTabWithStats(mkRun(fullStages()), laneStats(), true);
    const lane = laneOffsets(c as never);
    const axis = axisInsets(c as never);
    expect(axis.left, 'левый край шкалы = левый край дорожки').toBe(lane.left);
    expect(axis.right, 'правый край шкалы = правый край дорожки').toBe(lane.right);
    expect(lane.left).toBe(24);
    expect(lane.right).toBe(0);
  });

  it('ширина шкалы = ширина дорожки (порог плотного режима меряет ту же шкалу)', () => {
    // 1280: дорожка 878 px (286 | … | 116), шкала — та же ширина.
    expect(laneScaleWidth(1280, false)).toBe(878);
    expect(laneAxisInsets(false)).toEqual({ left: 286, right: 116 });
    // 360: дорожка 336 px (24 | … | 0).
    expect(laneScaleWidth(360, true)).toBe(336);
    expect(laneAxisInsets(true)).toEqual({ left: 24, right: 0 });
  });
});

// === GC-F10a: хвосты после мержа и согласование счётчика этапа ===

describe('tasksCountLabel: согласование счётчика этапа (GC-F10a)', () => {
  it('1 задача, 2 задачи, 5 задач, 11 задач, 12 задач, 21 задача, 22 задачи, 25 задач', () => {
    expect(tasksCountLabel(1)).toBe('1 задача');
    expect(tasksCountLabel(2)).toBe('2 задачи');
    expect(tasksCountLabel(5)).toBe('5 задач');
    expect(tasksCountLabel(11)).toBe('11 задач');
    expect(tasksCountLabel(12)).toBe('12 задач');
    expect(tasksCountLabel(21)).toBe('21 задача');
    expect(tasksCountLabel(22)).toBe('22 задачи');
    expect(tasksCountLabel(25)).toBe('25 задач');
  });
});

// Смерженная фича с хвостами: открытый хвост держит свою стадию active (правило бэка,
// FeaturePostMergeTailsTests), но тревоги бэк из него не строит и tone не даёт
// (FeatureProjector.cs:139-141). Статус исполнителя-хвоста — данные бэка как есть
// ('waiting' для задачи с AwaitingHumanSince): фронт его не выдумывает и не переносит
// на этап.
function mergedTailRun(tails: string[]): FeatureRun {
  const stages = fullStages();
  for (const s of stages) { s.state = 'done'; s.tone = null; s.overlapsActive = false; }
  stages[4] = mkStage({
    key: 'qa', state: 'active', firstStartedAt: '2026-10-20T11:40:00Z',
    executors: [
      mkExec({ personaId: 'p1', taskId: 't-tail1', taskRole: 'qa', status: 'waiting' }),
      mkExec({ personaId: 'p1', taskId: 't-tail2', taskRole: 'qa', status: 'pending' }),
    ],
  });
  return mkRun(stages, {
    state: 'merged', closed: true, postMergeTails: tails,
    qa: [
      { id: 't-tail1', title: 'Хвост-проверка', status: 'inProgress', awaitingHumanSince: '2026-10-20T11:50:00Z' } as FeatureTask,
      { id: 't-tail2', title: 'Хвост-дефект', status: 'inProgress' } as FeatureTask,
    ],
  });
}

describe('FeatureTimelineTab: хвосты после мержа (GC-F10a)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Порядок data-testid в DOM (порядок документа).
  function testidOrder(c: FNodeLike): string[] {
    const order: string[] = [];
    const walk = (n: FNodeLike) => {
      const at = n.attributes?.['data-testid'];
      if (at) order.push(at);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(c);
    return order;
  }

  // Все узлы с точным data-testid внутри поддерева.
  function nodesByTestid(root: FNodeLike, testid: string): FNodeLike[] {
    const out: FNodeLike[] = [];
    const walk = (n: FNodeLike) => {
      if (n.attributes?.['data-testid'] === testid) out.push(n);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(root);
    return out;
  }

  it('смерженная фича с 2 хвостами → «хвосты после мержа · 2» после этапа «Мерж»', () => {
    const c = renderTab(mergedTailRun(['t-tail1', 't-tail2']));
    const tails = findNodeByAttr(c, 'data-testid', 'timeline-tails');
    expect(tails, 'сводная строка').not.toBeNull();
    expect(textNodes(tails!).join('')).toBe('хвосты после мержа · 2');
    // Строка стоит после ленты этапов: «Мерж» — последний этап жизненного цикла.
    const order = testidOrder(c as never);
    expect(order.indexOf('timeline-tails'), 'строка после «Мержа»').toBeGreaterThan(order.indexOf('timeline-stage-merge'));
  });

  // Мутация (в отчёте): условие строки tailIds.size > 0 → false → «· 2» краснеет.

  it('хвосты не дают «ждёт вас» ни у одного этапа (тон этапа — только с бэка)', () => {
    // Исполнитель-хвост со статусом 'waiting' (данные бэка): плашка этапа и шапка
    // этапа не должны повторять это как состояние этапа.
    const c = renderTab(mergedTailRun(['t-tail1', 't-tail2']));
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-badge'), 'плашки этапа нет').toBeNull();
    for (const key of STAGE_KEYS) {
      const header = findNodeByAttr(c, 'data-testid', `timeline-stage-header-${key}`)!;
      expect(textNodes(header).join(''), `шапка этапа «${key}» без «ждёт вас»`).not.toContain('ждёт вас');
    }
  });

  it('хвост помечен «· хвост после мержа» в списке этапа и не дублируется отдельной строкой', () => {
    const c = renderTab(mergedTailRun(['t-tail1', 't-tail2']));
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-qa');
    expect(body, 'активный этап «Проверка» раскрыт').not.toBeNull();
    // Оба хвоста — обычные строки списка этапа (их не перенесли в отдельный список).
    expect(nodesByTestid(body as never, 'timeline-executor').length, 'строки хвостов в списке этапа').toBe(2);
    // Каждая помечена ровно один раз; нигде в DOM второй копии маркера нет.
    expect(nodesByTestid(body as never, 'timeline-executor-tail').length, 'каждый хвост помечен').toBe(2);
    expect(nodesByTestid(c as never, 'timeline-executor-tail').length, 'маркер только в строках этапа').toBe(2);
    // Сводка — только число, без id (иначе хвост показали бы дважды).
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'timeline-tails')!).join('')).toBe('хвосты после мержа · 2');
  });

  // Мутация (в отчёте): в StageRow isTail={isTail(x.taskId)} → isTail={false} →
  // «помечен маркером» краснеет.

  it('хвостов нет в данных (старые ответы) → строки нет', () => {
    const c = renderTab(mkRun(fullStages(), { state: 'merged' }));
    expect(findNodeByAttr(c, 'data-testid', 'timeline-tails')).toBeNull();
  });

  it('postMergeTails пуст → строки нет', () => {
    const c = renderTab(mkRun(fullStages(), { state: 'merged', postMergeTails: [] }));
    expect(findNodeByAttr(c, 'data-testid', 'timeline-tails')).toBeNull();
  });
});

// === GC-F10b: ходы и «Время шага на оси» в строке исполнителя ===
//
// Ходы считает buildTurnsByTask (единственный источник): закрытые — из intervalsByStage по
// taskId хода; живой — синтезирован из live-интервала и run.now (контракт — комментарий в
// types/featureStats.ts). Строка получает только ходы своей задачи; та же ось, что у дорожек,
// питает полосу «Время шага на оси фичи» в FeatureStepDetails.

describe('FeatureTimelineTab: ходы и «Время шага на оси» (GC-F10b)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
    // «Итог» при раскрытой строке догружается из сети: здесь — немедленный отказ (без висения).
    tasksGetMock.mockReset();
    tasksGetMock.mockImplementation(() => Promise.reject(new Error('в тесте сети нет')));
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Интервал с ходами — настоящая форма StageInterval.
  function ivT(
    from: string, to: string,
    opts: { live?: boolean; taskIds?: string[]; turns?: { taskId: string; from: string; to: string; outcome: string | null; model: string | null }[] } = {},
  ) {
    return { from, to, minutes: 30, live: opts.live ?? false, lastOutcome: null, taskIds: opts.taskIds ?? [], turns: opts.turns ?? [] };
  }

  // Раскрыть этап (если не открыт) и первую строку исполнителя; вернуть её тело.
  function openRowBody(c: FNodeLike, stageKey: string, index = 0): FNodeLike {
    let body = findNodeByAttr(c, 'data-testid', `timeline-stage-body-${stageKey}`);
    if (!body) {
      const header = findNodeByAttr(c, 'data-testid', `timeline-stage-header-${stageKey}`)!;
      act(() => { (reactProps(header as never)!.onClick as () => void)(); });
      body = findNodeByAttr(c, 'data-testid', `timeline-stage-body-${stageKey}`)!;
    }
    const rows: FNodeLike[] = [];
    const walk = (n: FNodeLike) => {
      if (n.attributes?.['data-testid'] === 'timeline-executor') rows.push(n);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(body);
    const row = rows[index]!;
    const head = findNodeByAttr(row, 'data-testid', 'timeline-executor-head')!;
    act(() => { (reactProps(head as never)!.onClick as () => void)(); });
    return findNodeByAttr(row, 'data-testid', 'timeline-executor-body')!;
  }

  const kinds = (root: FNodeLike): (string | undefined)[] => {
    const out: (string | undefined)[] = [];
    const walk = (n: FNodeLike) => {
      if (n.attributes?.['data-testid'] === 'step-turn') out.push(n.attributes?.['data-kind']);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(root);
    return out;
  };

  // Инлайн-стиль узла (left/width полосы): React пишет их прямо в node.style (как в DOM).
  const styleOf = (n: FNodeLike): Record<string, string> => (n as unknown as { style: Record<string, string> }).style;
  // Живая полоса на полосе «Время шага на оси» (data-kind='live').
  const liveTurn = (root: FNodeLike): FNodeLike | null => {
    const walk = (n: FNodeLike): FNodeLike | null => {
      if (n.attributes?.['data-testid'] === 'step-turn' && n.attributes?.['data-kind'] === 'live') return n;
      for (const ch of n.childNodes) { const r = walk(ch); if (r) return r; }
      return null;
    };
    return walk(root);
  };

  // === c1: строка и полоса получают ходы своей задачи ===
  it('ходы задачи из intervalsByStage: в строке — ровно её ходы; «Время шага на оси» видно', () => {
    const stages = fullStages();
    stages[3]!.executors = [mkExec({ personaId: 'p1', taskId: 't-rev', taskRole: 'review', status: 'done', chatId: 'chat-r' })];
    const c = renderTabWithStats(mkRun(stages, { reviews: [{ id: 't-rev', title: 'Ревью', status: 'done' } as FeatureTask] }), mkStats({
      review: [
        ivT('2026-10-20T10:20:00Z', '2026-10-20T10:40:00Z', {
          taskIds: ['t-rev', 't-other'],
          turns: [
            { taskId: 't-rev', from: '2026-10-20T10:20:00Z', to: '2026-10-20T10:40:00Z', outcome: 'cancelled', model: null },
            { taskId: 't-other', from: '2026-10-20T10:25:00Z', to: '2026-10-20T10:35:00Z', outcome: null, model: null },
          ],
        }),
        ivT('2026-10-20T11:30:00Z', '2026-10-20T11:50:00Z', {
          taskIds: ['t-rev'],
          turns: [{ taskId: 't-rev', from: '2026-10-20T11:30:00Z', to: '2026-10-20T11:50:00Z', outcome: null, model: null }],
        }),
      ],
    }));
    const after = openRowBody(c, 'review');
    const turnsNode = findNodeByAttr(after, 'data-testid', 'timeline-executor-turns');
    expect(turnsNode, 'строки ходов нет').not.toBeNull();
    expect(textNodes(turnsNode!).join('')).toBe(
      `ходы: ${localTime('2026-10-20T10:20:00Z')}–${localTime('2026-10-20T10:40:00Z')} (cancelled) · ` +
      `${localTime('2026-10-20T11:30:00Z')}–${localTime('2026-10-20T11:50:00Z')}`);
    // Ход чужой задачи в строку не попал (отбор по taskId хода, не по taskIds интервала).
    expect(textNodes(turnsNode!).join('')).not.toContain(localTime('2026-10-20T10:25:00Z'));
    // «Время шага на оси фичи»: полоса есть, на ней — только ходы этой задачи, вид — review
    // (роль задачи — тот же источник, что атрибуция стадий у бэка).
    const stepTime = findNodeByAttr(after, 'data-testid', 'step-time');
    expect(stepTime, 'блока «Время шага на оси» нет').not.toBeNull();
    expect(kinds(after), 'ходы на полосе — только этой задачи').toEqual(['review', 'review']);
  });

  // === c2: живой ход идущей задачи ===
  it('живой ход: у идущей задачи — «…–идёт» (синтез из live-интервала и run.now)', () => {
    const c = renderTabWithStats(mkRun(fullStages(), {
      stepChains: [],
      now: [{ taskId: 't-prod', stage: 'prod', title: 'Шаг', stepRef: 'Ш1', startedAt: '2026-10-20T10:00:00Z' }],
    }), mkStats({
      prod: [ivT('2026-10-20T10:00:00Z', '2026-10-20T12:00:00Z', {
        live: true,
        taskIds: ['t-prod'],
        turns: [{ taskId: 't-prod', from: '2026-10-20T10:00:00Z', to: '2026-10-20T11:30:00Z', outcome: 'interrupted', model: null }],
      })],
    }));
    // «Производство» — текущий этап (открыт по умолчанию); карточек нет (пустые цепочки) — строка исполнителя.
    const after = openRowBody(c, 'prod');
    const t = textNodes(findNodeByAttr(after, 'data-testid', 'timeline-executor-turns')!).join('');
    // Живой ход начинается с конца последнего закрытого хода (11:30), а не с старта задачи (10:00):
    // та же формула, что у живого хвоста бэка.
    expect(t).toBe(
      `ходы: ${localTime('2026-10-20T10:00:00Z')}–${localTime('2026-10-20T11:30:00Z')} (interrupted) · ` +
      `${localTime('2026-10-20T11:30:00Z')}–идёт`);
    // На полосе живой ход — отдельный вид (live), закрытый — work (роль step).
    expect(kinds(after)).toEqual(['work', 'live']);
  });

  // === c4: живой ход живой: «сейчас» из useNow, не заморожен (GC-F10bR) ===
  // Вкладка рендерится условно без ключа (FeaturePage): при обновлении данных она НЕ
  // перемонтируется — замороженный Date.now() оставил бы живой ход стоящим на минуте
  // открытия. Окно здесь длиннее текущего момента (ось бэка уходит в 14:00): «to» живого
  // хода лежит внутри шкалы, его движение видно по ширине полосы.
  it('живой ход растёт со временем: через 10 минут ширина живой полосы больше, левый край тот же', () => {
    const stats = {
      timeline: {
        from: '2026-10-20T10:00:00Z', to: '2026-10-20T14:00:00Z',
        stages: [], segments: [], current: null, totals: {},
        intervalsByStage: {
          prod: [ivT('2026-10-20T10:00:00Z', '2026-10-20T12:00:00Z', {
            live: true,
            taskIds: ['t-prod'],
            turns: [{ taskId: 't-prod', from: '2026-10-20T10:00:00Z', to: '2026-10-20T11:30:00Z', outcome: 'interrupted', model: null }],
          })],
        },
      },
    } as unknown as FeatureStats;
    const c = renderTabWithStats(mkRun(fullStages(), {
      stepChains: [],
      now: [{ taskId: 't-prod', stage: 'prod', title: 'Шаг', stepRef: 'Ш1', startedAt: '2026-10-20T10:00:00Z' }],
    }), stats);
    const after = openRowBody(c, 'prod');
    const live1 = liveTurn(after);
    expect(live1, 'живой полосы на оси нет').not.toBeNull();
    expect(kinds(after)).toEqual(['work', 'live']);
    // Живой ход: from = конец последнего закрытого хода (11:30), to = «сейчас» (12:00).
    const w1 = parseFloat(styleOf(live1!).width!);
    const l1 = parseFloat(styleOf(live1!).left!);
    // Прошло 10 минут: useNow обновляет «сейчас» по интервалу (60 с), вкладка НЕ перемонтирована.
    act(() => { vi.advanceTimersByTime(10 * 60_000); });
    const live2 = liveTurn(after);
    expect(live2, 'живая полоса пропала').not.toBeNull();
    const w2 = parseFloat(styleOf(live2!).width!);
    expect(w2, 'ширина живой полосы обязана расти вместе с «сейчас»').toBeGreaterThan(w1);
    expect(parseFloat(styleOf(live2!).left!), 'левый край — конец закрытого хода, он не двигается').toBeCloseTo(l1, 6);
  });

  // === c3: approx — ходам неоткуда взяться, падения нет ===
  it('approx (intervalsByStage null): строки ходов нет, блока времени нет, не падает', () => {
    const c = renderTabWithStats(mkRun(fullStages(), { stepChains: [] }), mkStats(null));
    const after = openRowBody(c, 'prod');
    expect(findNodeByAttr(after, 'data-testid', 'timeline-executor-turns'), 'при approx ходов нет').toBeNull();
    expect(findNodeByAttr(after, 'data-testid', 'step-time'), 'при approx блока времени нет').toBeNull();
  });

  // === п.3: хвост после мержа считается один раз, живого хода у закрытой фичи нет ===
  it('хвост после мержа: его ходы — один раз из интервалов стадии; живого хода нет', () => {
    // Открытая хвост-задача может быть в run.Now (бэк отдаёт InProgress-задачи, пока эпик
    // открыт). Для merged/закрытой фичи живого хода всё равно нет: ось закрыта.
    const tailRun = mergedTailRun(['t-tail1', 't-tail2']);
    tailRun.now = [{ taskId: 't-tail1', stage: 'qa', title: 'Хвост-проверка', startedAt: '2026-10-20T11:40:00Z' }];
    const c = renderTabWithStats(tailRun, mkStats({
      qa: [ivT('2026-10-20T11:40:00Z', '2026-10-20T11:55:00Z', {
        live: true,
        taskIds: ['t-tail1'],
        turns: [
          { taskId: 't-tail1', from: '2026-10-20T11:40:00Z', to: '2026-10-20T11:45:00Z', outcome: null, model: null },
          { taskId: 't-tail1', from: '2026-10-20T11:50:00Z', to: '2026-10-20T11:55:00Z', outcome: null, model: null },
        ],
      })],
    }));
    // Хвост — обычная строка списка этапа (GC-F10a): его ходы берутся из интервалов ровно раз.
    const after = openRowBody(c, 'qa', 0);
    const t = textNodes(findNodeByAttr(after, 'data-testid', 'timeline-executor-turns')!).join('');
    expect(t).toBe(
      `ходы: ${localTime('2026-10-20T11:40:00Z')}–${localTime('2026-10-20T11:45:00Z')} · ` +
      `${localTime('2026-10-20T11:50:00Z')}–${localTime('2026-10-20T11:55:00Z')}`);
    expect(t, 'у закрытой фичи живого хода нет').not.toContain('идёт');
  });
});

// === GC-F10c: свёртка длинных списков этапов, кроме prod ===
//
// Список этапа (кроме «Производства») — последние 10 строк, ранние скрыты; кнопка
// «Показать все N (ранних скрыто: K)» берётся из результата свёртки (N = visible + hiddenEarly,
// K = hiddenEarly): то, что откроется по кнопке, равно тому, что посчитано в её подписи.
// Снятые задачи (verification.verdict 'withdrawn' — тот же признак, что у бэка,
// FeatureStepAccounting.cs:145,190) исключены до свёртки: их нет ни в N, ни в K, ни в списке.

// Фича с длинным «Ревью»: n закрытых исполнителей; withdrawnIds — задачи с вердиктом 'withdrawn'.
function longReviewRun(n: number, withdrawnIds: string[] = []): FeatureRun {
  const stages = fullStages();
  const taskIds = Array.from({ length: n }, (_, i) => `t-r${i}`);
  stages[3] = mkStage({
    key: 'review', state: 'active', tone: 'run', firstStartedAt: '2026-10-20T11:20:00Z',
    approxMinutes: 75, taskIds,
    executors: taskIds.map(id => mkExec({ personaId: 'p1', taskId: id, taskRole: 'review', status: 'done', chatId: null })),
  });
  const reviews: FeatureTask[] = taskIds.map((id, i) => {
    const t: FeatureTask = { id, title: `Ревью ${i + 1}`, status: 'done' };
    if (withdrawnIds.includes(id)) t.verification = { verdict: 'withdrawn' };
    return t;
  });
  return mkRun(stages, { reviews });
}

describe('FeatureTimelineTab: свёртка длинных списков этапов (GC-F10c)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Все узлы с точным data-testid внутри поддерева.
  function nodesByTestid(root: FNodeLike, testid: string): FNodeLike[] {
    const out: FNodeLike[] = [];
    const walk = (n: FNodeLike) => {
      if (n.attributes?.['data-testid'] === testid) out.push(n);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(root);
    return out;
  }

  // Раскрыть «Ревью» (текущий по умолчанию — prod) и вернуть его тело.
  function openReview(c: FNodeLike): FNodeLike {
    const header = findNodeByAttr(c, 'data-testid', 'timeline-stage-header-review')!;
    act(() => { (reactProps(header)!.onClick as () => void)(); });
    return findNodeByAttr(c, 'data-testid', 'timeline-stage-body-review')!;
  }

  // === п.1: 74 ревью → видно 10, кнопка с точными N и K; клик → все ===
  it('74 ревью → видно 10 строк и кнопка «Показать все 74 (ранних скрыто: 64)»; клик — все 74, кнопки нет', () => {
    const c = renderTab(longReviewRun(74));
    const body = openReview(c);
    expect(nodesByTestid(body, 'timeline-executor').length, 'свёрнутый список').toBe(10);
    const btn = findNodeByAttr(c, 'data-testid', 'timeline-stage-showall-review');
    expect(btn, 'кнопки свёртки нет').not.toBeNull();
    expect(textNodes(btn!).join(''), 'подпись кнопки').toBe('Показать все 74 (ранних скрыто: 64)');
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    const opened = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-review')!;
    expect(nodesByTestid(opened, 'timeline-executor').length, 'раскрытый список').toBe(74);
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-showall-review'), 'кнопки после раскрытия нет').toBeNull();
  });

  // Мутация (в отчёте): убрать кнопку (условие hiddenEarly.length > 0 → false) → п.1 краснеет.

  // === п.2: снятые не входят ни в N, ни в K ===
  it('снятые (verdict withdrawn) не входят ни в N, ни в K и не открываются по кнопке', () => {
    const withdrawn = ['t-r0', 't-r1', 't-r2', 't-r3'];
    const c = renderTab(longReviewRun(74, withdrawn));
    const body = openReview(c);
    expect(nodesByTestid(body, 'timeline-executor').length, 'свёрнутый список без снятых').toBe(10);
    const btn = findNodeByAttr(c, 'data-testid', 'timeline-stage-showall-review')!;
    expect(textNodes(btn!).join(''), 'N и K без снятых').toBe('Показать все 70 (ранних скрыто: 60)');
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    const opened = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-review')!;
    expect(nodesByTestid(opened, 'timeline-executor').length, 'снятые не открылись').toBe(70);
  });

  // Мутация (в отчёте): убрать фильтр снятых из StageRow (rows = stage.executors) → п.2 краснеет.

  // === п.3: короткий список — свёртки нет ===
  it('список из 10 строк и меньше — без кнопки, виден весь', () => {
    const c = renderTab(longReviewRun(10));
    const body = openReview(c);
    expect(nodesByTestid(body, 'timeline-executor').length).toBe(10);
    expect(findNodeByAttr(c, 'data-testid', 'timeline-stage-showall-review'), 'кнопки нет при полном списке').toBeNull();
  });
});

// === GC-F11: снимок вида и восстановление по чипу ===
// Трасса: раскрыть этап + карточку → меню «Карточка задачи» (recordFeatureReturn берёт
// снимок у зарегистрированного поставщика этой вкладки) → клик чипа (consume — сам чип
// проверяется в FeatureBackChip.test.ts) → закрыть страницу (unmount) → открыть заново:
// этап раскрыт, карточка раскрыта с фокусом на шапке, прокрутка — записанная. Без клика
// чипа — ничего не применяется (открытие из списка — не возврат).
// Мутации (в отчёте): (1) не звать поставщика в recordFeatureReturn → c1/c2 краснеют
// (снимка нет); (2) убрать window.scrollTo в восстановлении → c2 краснеет;
// (3) не передать initialExpandedKey в ленту → c1 краснеет (карточка не раскрыта);
// (4) убрать setExpanded(v.stageKey) → c1 краснеет (этап не восстановлен).
// Фикстура: все этапы pending — при монтировании не раскрыто НИЧЕГО (дефолт не маскирует
// восстановление); шаг ST-F9 с меню (есть старт и чат цепочки).
function returnRun(): FeatureRun {
  return mkRun([
    mkStage({ key: 'cut' }), mkStage({ key: 'plan' }), mkStage({ key: 'prod' }),
    mkStage({ key: 'review' }), mkStage({ key: 'qa' }), mkStage({ key: 'final' }), mkStage({ key: 'merge' }),
  ], {
    steps: [{ id: 't-s9', title: 'Возврат', status: 'done', stepRef: 'ST-F9', claudeStartedAt: '2026-10-20T10:00:00Z' }],
    stepChains: [mkChain({ stepRef: 'ST-F9', title: 'Возврат', taskIds: ['t-s9'], chatId: 'chat-s9' })],
  });
}

describe('FeatureTimelineTab: восстановление по чипу (GC-F11)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    personaMap.clear();
    personaMap.set('p1', 'Денис');
    backNavTest.reset();
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Первая половина трассы (общая для c1–c3): раскрыть этап и карточку, «стоять» на
  // прокрутке 1234, уйти в задачу из меню карточки. Возвращает root монтирования —
  // закрытие страницы делает тест.
  function leaveToTask(run: FeatureRun) {
    installFakeDom();
    const { root, container } = mount(createElement(FeatureTimelineTab, { run }));
    const header = findNodeByAttr(container, 'data-testid', 'timeline-stage-header-prod')!;
    act(() => { (reactProps(header)!.onClick as () => void)(); });
    const card = findNodeByAttr(container, 'data-testid', 'prod-card-ST-F9')!;
    act(() => { (reactProps(card)!.onClick as () => void)(); });
    // Прокрутка в момент перехода (у фейк-DOM window.scrollY нет — ставим явно).
    (globalThis as { window: Record<string, unknown> }).window.scrollY = 1234;
    const code = findNodeByAttr(container, 'data-testid', 'prod-code-ST-F9')!;
    act(() => { (reactProps(code)!.onClick as () => void)(); });
    // Меню — портал в document.body; кликаем кнопку внутри обёртки пункта.
    const body = (globalThis as { document: { body: never } }).document.body as never;
    const item = findNodeByAttr(body, 'data-testid', 'prod-menu-task') as { children: never[] } | null;
    expect(item, 'пункта меню «Карточка задачи» нет').toBeTruthy();
    act(() => { (reactProps((item as { children: never[] }).children[0] as never)!.onClick as () => void)(); });
    return root;
  }

  // Открыть страницу заново (тот же документ): восстановление применяется на монтировании.
  function reopen() {
    return mount(createElement(FeatureTimelineTab, { run: returnRun() })).container;
  }

  it('c1: возврат по чипу — этап раскрыт, карточка раскрыта, фокус на её шапке', () => {
    const root = leaveToTask(returnRun());
    expect(backNavTest.getBackNav()?.view, 'снимок не записан при переходе')
      .toEqual({ stageKey: 'prod', expandedKey: 'ST-F9', scrollY: 1234 });
    // Действие чипа (компонент чипа — в FeatureBackChip.test.ts): снимок в однократный канал.
    expect(backNavTest.consumeFeatureViewReturn('f1'), 'снимка для забора нет').toBe(true);
    act(() => { root.unmount(); }); // страница фичи закрыта (открыта задача)

    const doc = (globalThis as { document: any }).document;
    // focus() → activeElement (у фейк-DOM focus — no-op: без патча фокус не виден).
    const proto = Object.getPrototypeOf(doc.createElement('div'));
    const origFocus = proto.focus;
    proto.focus = function (this: any) { doc.activeElement = this; };
    const c2 = reopen();
    proto.focus = origFocus;

    const body2 = findNodeByAttr(c2, 'data-testid', 'timeline-stage-body-prod');
    expect(body2, 'раскрытый этап не восстановлен').not.toBeNull();
    const card = findNodeByAttr(body2!, 'data-testid', 'prod-card-ST-F9')!;
    expect(card.getAttribute('aria-expanded'), 'карточка не раскрыта').toBe('true');
    expect(findNodeByAttr(card, 'data-testid', 'prod-details'), 'раскрытой части карточки нет').not.toBeNull();
    expect(doc.activeElement, 'фокус не встал на шапку карточки').toBe(card);
  });

  it('c2: прокрутка восстановлена ровно (window.scrollTo(0, записанное))', () => {
    const root = leaveToTask(returnRun());
    expect(backNavTest.consumeFeatureViewReturn('f1')).toBe(true);
    act(() => { root.unmount(); });
    const calls: [number, number][] = [];
    (globalThis as { window: Record<string, unknown> }).window.scrollTo = (x: number, y: number) => { calls.push([x, y]); };
    reopen();
    // ±2 px допуск критерия здесь не нужен: значение проходит число в число.
    expect(calls, 'прокрутка не восстановлена').toEqual([[0, 1234]]);
  });

  it('c3: открытие фичи БЕЗ чипа снимок не применяет', () => {
    const root = leaveToTask(returnRun());
    // Снимок на указателе есть (переход записан), но чип не нажат — это не «возврат».
    expect(backNavTest.getBackNav()?.view, 'снимок не записан').not.toBeNull();
    act(() => { root.unmount(); });
    const calls: [number, number][] = [];
    (globalThis as { window: Record<string, unknown> }).window.scrollTo = (x: number, y: number) => { calls.push([x, y]); };
    const c2 = reopen();
    expect(findNodeByAttr(c2, 'data-testid', 'timeline-stage-body-prod'), 'этап раскрылся без возврата').toBeNull();
    expect(calls, 'прокрутка сдвинулась без возврата').toEqual([]);
  });
});
