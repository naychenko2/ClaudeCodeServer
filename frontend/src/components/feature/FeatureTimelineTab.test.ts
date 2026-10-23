// Вкладка «Хронология» (волна 3, Ф2a/Ф2b, макет features-overview-3-final §5).
//
// Чистые функции (имя/состояние/границы/«≈»/точка/линия/задачи/минуты/статус/плашка;
// Ф2b: вердикт витка, статус/тон/модель шага, пустое состояние витков) — в node, без DOM.
// Рендер-тесты (fakeDom): порядок 7 этапов, раскрытие одного этапа, исполнители
// (roleLabel + чип модели), «чата нет», пустые состояния, тач-цель 40 на 360.
// «Производство» (W4-2F3): лента карточек FeatureProdCardsView вместо «исполнителей/
// шагов» — одна карточка на шаг, «сейчас» над текущей; при пустой ленте — существующее
// пустое состояние «Шагов пока нет». Чистые функции Ф2b (вердикт/статус/тон/модель)
// остаются: их читает лента.
//
// Мутации (в отчёте): (1) переставить этапы в run.stages → «порядок этапов» краснеет;
// (2) убрать «≈» из stageDurationLabel → «длительность ≈» краснеет; (3) не передать run
// в FeatureProdCardsView (не дать stepChains) → «карточка Ш1» краснеет; (4) не передать
// isMobile в ленту → «360: поле касания 40» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureTimelineTab } from './FeatureTimelineTab';
import {
  stageName, stageStatusLabel, stageBoundsLabel, stageDurationLabel,
  stageDotStyle, stageLineStyle, buildTaskMap, minutesSince, executorStatus, stageStatusBadge,
  roundVerdictView, stepStatusLabel, stepStatusTone, stepModel, roundsAreaLabel,
} from './FeatureTimelineBits';
import { C } from '../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureStepChain, FeatureRound, FeatureTask, StageKey } from '../../types/featureRun';

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

describe('roundVerdictView', () => {
  it('принят → success; принят с замечаниями → warning; вернул → warning; снят → muted; идёт → active', () => {
    expect(roundVerdictView('accepted')).toEqual({ label: 'принят', tone: 'success' });
    expect(roundVerdictView('accepted_with_remarks')).toEqual({ label: 'принят с замечаниями', tone: 'warning' });
    expect(roundVerdictView('returned')).toEqual({ label: 'вернул', tone: 'warning' });
    expect(roundVerdictView('withdrawn')).toEqual({ label: 'снят', tone: 'muted' });
    expect(roundVerdictView('running')).toEqual({ label: 'идёт', tone: 'active' });
  });
});

describe('stepStatusLabel', () => {
  it('готовая строка бэка (chain.status) — как есть', () => {
    expect(stepStatusLabel(mkChain({ status: 'ждёт ревью' }))).toBe('ждёт ревью');
    expect(stepStatusLabel(mkChain({ status: 'снят с выкладки' }))).toBe('снят с выкладки');
  });
  it('status отсутствует (старый ответ) → фолбэк из outcome, без выдуманных подписей', () => {
    expect(stepStatusLabel(mkChain({ status: undefined as unknown as string, outcome: 'pending' }))).toBe('ждёт ревью');
    expect(stepStatusLabel(mkChain({ status: undefined as unknown as string, outcome: 'accepted' }))).toBe('принят');
    expect(stepStatusLabel(mkChain({ status: undefined as unknown as string, outcome: 'running' }))).toBe('идёт');
    expect(stepStatusLabel(mkChain({ status: undefined as unknown as string, outcome: 'returned' }))).toBe('закрыт без повторного ревью');
    expect(stepStatusLabel(mkChain({ status: undefined as unknown as string, outcome: 'withdrawn' }))).toBe('снят с выкладки');
  });
});

describe('stepStatusTone', () => {
  it('status отсутствует (старый ответ) → тон из outcome: accepted→success; running/returned→active; pending/withdrawn→muted', () => {
    expect(stepStatusTone(mkChain({ status: undefined as unknown as string, outcome: 'accepted' }))).toBe('success');
    expect(stepStatusTone(mkChain({ status: undefined as unknown as string, outcome: 'running' }))).toBe('active');
    expect(stepStatusTone(mkChain({ status: undefined as unknown as string, outcome: 'returned' }))).toBe('active');
    expect(stepStatusTone(mkChain({ status: undefined as unknown as string, outcome: 'pending' }))).toBe('muted');
    expect(stepStatusTone(mkChain({ status: undefined as unknown as string, outcome: 'withdrawn' }))).toBe('muted');
  });
  // Один источник: когда бэк дал готовую строку (chain.status), тон — из НЕЁ, а не из
  // outcome (раньше подпись из status, тон из outcome — могли расконсистентиться).
  // Мутация (в отчёте): вернуть тон из outcome → «идёт» (status) + outcome=accepted →
  // tone success вместо active → краснеет.
  it('status есть → тон из status (не из outcome): «идёт»→active, «принят»→success', () => {
    // status='идёт' (active), но outcome=accepted (было бы success) — тон берём из status.
    expect(stepStatusTone(mkChain({ status: 'идёт', outcome: 'accepted' }))).toBe('active');
    expect(stepStatusTone(mkChain({ status: 'принят', outcome: 'running' }))).toBe('success');
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

describe('roundsAreaLabel', () => {
  it('витков нет: есть исполнитель → «ещё не ревьюили»; нет исполнителя → «нет исполнителя»', () => {
    expect(roundsAreaLabel(mkChain({ personaId: 'p1' }))).toBe('ещё не ревьюили');
    expect(roundsAreaLabel(mkChain({ personaId: null }))).toBe('нет исполнителя');
  });
  it('последний виток идёт → «ревью идёт»; витки закрыты → null (строки рендерятся)', () => {
    expect(roundsAreaLabel(mkChain({ rounds: [mkRound({ verdict: 'running' })] }))).toBe('ревью идёт');
    expect(roundsAreaLabel(mkChain({ rounds: [mkRound({ verdict: 'accepted' })] }))).toBeNull();
    expect(roundsAreaLabel(mkChain({ rounds: [mkRound({ n: 1, verdict: 'returned' }), mkRound({ n: 2, verdict: 'running' })] }))).toBe('ревью идёт');
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

  // === 3. Пустая лента — существующее пустое состояние вкладки ===
  it('шагов нет (stepChains пуст) → «Шагов пока нет», ленты нет', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [] }));
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')!;
    const empty = findNodeByAttr(body, 'data-testid', 'feature-steps-empty');
    expect(empty, 'пустое состояние «Шагов пока нет»').not.toBeNull();
    expect(textNodes(empty!).join('')).toContain('Шагов пока нет');
    expect(findNodeByAttr(body, 'data-testid', 'prod-lane'), 'ленты нет').toBeNull();
  });

  it('stepChains нет в данных (старая фича) → «Шагов пока нет» (честная деградация)', () => {
    const c = renderTab(mkRun(fullStages()));
    const body = findNodeByAttr(c, 'data-testid', 'timeline-stage-body-prod')!;
    const empty = findNodeByAttr(body, 'data-testid', 'feature-steps-empty');
    expect(empty, 'пустое состояние «Шагов пока нет»').not.toBeNull();
    expect(findNodeByAttr(body, 'data-testid', 'prod-lane'), 'ленты нет').toBeNull();
  });

  // === 4. 360: isMobile доходит до ленты — поле касания узла 40 px ===
  it('360: узел ленты — невидимое поле касания 40 px, видимая высота 20 px', () => {
    const c = renderTab(prodRun(), true);
    const node = findNodeByAttr(c, 'data-testid', 'prod-node');
    expect(node, 'узел-кнопка с чатом').not.toBeNull();
    const st = (reactProps(node as never)!.style ?? {}) as Record<string, string>;
    expect(st.lineHeight).toBe('20px');
    // Обёртка-поле: 40 px высоты, отрицательные отступы — раскладка не смещается.
    const wrap = (node as { parentNode: never }).parentNode;
    const ws = (reactProps(wrap)!.style ?? {}) as Record<string, string>;
    expect(ws.height).toBe(40);
    expect(ws.margin).toBe('-10px 0');
  });
});
