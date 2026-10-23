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
// «оценивает →» → «reviewOfOutside > 0 даёт нейтральную подпись» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureTimelineTab } from './FeatureTimelineTab';
import {
  stageName, stageStatusLabel, stageBoundsLabel, stageDurationLabel,
  stageDotStyle, stageLineStyle, buildTaskMap, minutesSince, executorStatus, stageStatusBadge,
  stepStatusView, stepModel, buildExecutorIndex, evaluatedLine, outsideLinksLabel,
} from './FeatureTimelineBits';
import type { StepProgress } from '../../lib/featureProgress';
import { C } from '../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureStepChain, FeatureRound, FeatureTask } from '../../types/featureRun';

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
// lib/api — только avatarUrl (PersonaAvatar); остальное не зовётся вкладкой.
vi.mock('../../lib/api', () => ({
  api: { personas: { avatarUrl: () => '' } },
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));
// Навигация: журнал целей перехода (chat → openChatById, task → openTaskInSection).
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } }));
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

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
    mkStage({ key: 'cut', state: 'done', firstStartedAt: '2026-10-19T17:20:00Z', lastCompletedAt: '2026-10-19T17:55:00Z', approxMinutes: 35 }),
    mkStage({ key: 'plan', state: 'done', firstStartedAt: '2026-10-19T17:58:00Z', lastCompletedAt: '2026-10-19T18:30:00Z', approxMinutes: 30 }),
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

  // === 8. 360: тач-цель ≥ 40 (кнопка-заголовок этапа) ===
  it('на 360 кнопка-заголовок этапа — minHeight 40', () => {
    const c = renderTab(mkRun(fullStages()), true);
    const header = findNodeByAttr(c, 'data-testid', 'timeline-stage-header-prod')!;
    const st = (header as unknown as { style: Record<string, string> }).style;
    expect(st.minHeight).toBe('40px');
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
    // Цепочка «работа → принят» — узлы карточки.
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
