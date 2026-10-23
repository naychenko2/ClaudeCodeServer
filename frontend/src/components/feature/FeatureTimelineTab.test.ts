// Вкладка «Хронология» (волна 3, Ф2a/Ф2b, макет features-overview-3-final §5).
//
// Чистые функции (имя/состояние/границы/«≈»/точка/линия/задачи/минуты/статус/плашка;
// Ф2b: вердикт витка, статус/тон/модель шага, пустое состояние витков) — в node, без DOM.
// Рендер-тесты (fakeDom): порядок 7 этапов, раскрытие одного этапа, исполнители
// (roleLabel + чип модели), «чата нет», пустые состояния, тач-цель 40 на 360;
// Ф2b: шаги (шапка, «чат · задача»), раскрытие одного шага, витки (кто/вердикт/находки,
// «чат · задача» ревью), пустые состояния («ещё не ревьюили», «нет исполнителя»,
// «ревью идёт», «Шагов пока нет»), честная деградация без stepRef.
//
// Мутации (в отчёте): (1) переставить этапы в run.stages → «порядок этапов» краснеет;
// (2) убрать «≈» из stageDurationLabel → «длительность ≈» краснеет;
// (3) раскрытие двух шагов одновременно (Set вместо одного ключа) → «один шаг за раз» краснеет;
// (4) убрать подпись вердикта из roundVerdictView → «виток: вердикт» краснеет;
// (5) игнорировать reviewChatId (всегда рисовать «чат») → «виток без чата» краснеет.

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

  // === 5. Исполнители: roleLabel + чип модели + «чата нет» ===
  it('исполнитель: имя персоны, роль (roleLabel: step+back → «Ш1 · бэк»), чип модели', () => {
    const c = renderTab(mkRun(fullStages()));
    const row = findNodeByAttr(c, 'data-testid', 'timeline-executor');
    expect(row).not.toBeNull();
    const t = textNodes(row!).join('');
    expect(t).toContain('Денис');
    expect(t).toContain('Ш1 · бэк');
    // Чип модели.
    const chip = findNodeByAttr(c, 'data-testid', 'timeline-model-chip');
    expect(chip).not.toBeNull();
    expect(textNodes(chip!).join('')).toBe('Opus');
  });

  it('исполнитель без чата (chatId == null) → «чата нет» (без мёртвой ссылки)', () => {
    const stages = fullStages();
    // prod: исполнитель без chatId; задача t2 в run со статусом done.
    stages[2]!.executors = [mkExec({ personaId: 'p1', taskId: 't2', taskRole: 'step', stepRef: 'Ш2', lane: 'front', status: 'done', chatId: null })];
    const c = renderTab(mkRun(stages, { steps: [{ id: 't2', title: 'Шаг 2', status: 'done' }] }));
    const noChat = findNodeByAttr(c, 'data-testid', 'nav-target-no-chat');
    expect(noChat).not.toBeNull();
    expect(textNodes(noChat!).join('')).toBe('чата нет');
    // R1 major: taskClosed={task?.status === 'done'} (один источник: статус задачи) →
    // кнопка задачи показывает «закрыта».
    const taskBtn = findNodeByAttr(c, 'data-testid', 'nav-target-task');
    expect(taskBtn, 'кнопка задачи при taskId != null').not.toBeNull();
    expect(textNodes(taskBtn!).join('')).toContain('закрыта');
  });

  it('исполнитель с чатом → кнопка «чат» (NavTargetButton)', () => {
    const c = renderTab(mkRun(fullStages()));
    // prod-исполнитель с chatId → кнопка «чат» в конкретной строке (не подстрока по всему дереву).
    const pair = findNodeByAttr(c, 'data-testid', 'nav-target-pair');
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

// === Ф2b: рендер — шаги и витки ===

describe('FeatureTimelineTab: Ф2b — шаги и витки', () => {
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

  // === 1. Строка шага: код, название, исполнитель, roleLabel, статус, «чат · задача» ===
  it('шаг: код stepRef, название, исполнитель, roleLabel «Ш1 · бэк», статус, пара «чат · задача»', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain()] }));
    const row = findNodeByAttr(c, 'data-testid', 'feature-step-Ш1');
    expect(row, 'строка шага Ш1').not.toBeNull();
    const t = textNodes(row!).join('');
    expect(t).toContain('Ш1');
    expect(t).toContain('Модель шаблона');
    expect(t).toContain('Денис');
    expect(t).toContain('Ш1 · бэк');
    expect(t).toContain('принят');
    // «чат · задача» шага: чат (chatId есть) + задача (taskIds[0]).
    const pair = findNodeByAttr(row!, 'data-testid', 'nav-target-pair');
    expect(pair, 'пара «чат · задача» шага').not.toBeNull();
    expect(textNodes(pair!).join('')).toContain('чат');
    expect(textNodes(pair!).join('')).toContain('задача');
  });

  // === 2. Чип модели шага: из исполнителя стадии с тем же stepRef ===
  it('чип модели шага — из исполнителя стадии с тем же stepRef', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain()] }));
    // fullStages: prod-исполнитель stepRef 'Ш1', modelLabel 'Opus' → чип на строке шага.
    const row = findNodeByAttr(c, 'data-testid', 'feature-step-Ш1')!;
    const chip = findNodeByAttr(row, 'data-testid', 'timeline-model-chip');
    expect(chip, 'чип модели на строке шага').not.toBeNull();
    expect(textNodes(chip!).join('')).toBe('Opus');
  });

  it('шаг без совпадения по stepRef → чипа модели нет (честная деградация)', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ stepRef: 'Ш9', title: 'Шаг девять' })] }));
    const row = findNodeByAttr(c, 'data-testid', 'feature-step-Ш9')!;
    expect(findNodeByAttr(row, 'data-testid', 'timeline-model-chip'), 'чипа нет').toBeNull();
  });

  // === 3. Раскрытие: один шаг за раз ===
  it('один шаг раскрыт за раз: клик по второму схлопывает первый; повторный клик — схлопывает', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [
      mkChain(),
      mkChain({ stepRef: 'Ш2', title: 'Шаг два', taskIds: ['t-s2'], chatId: 'chat-s2' }),
    ] }));
    // Сначала ни один шаг не раскрыт.
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-tours-Ш1'), 'витков Ш1 нет').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-tours-Ш2'), 'витков Ш2 нет').toBeNull();
    // Раскрываем Ш1.
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-tours-Ш1'), 'Ш1 раскрыт').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-tours-Ш2'), 'Ш2 ещё закрыт').toBeNull();
    // Раскрываем Ш2: Ш1 схлопывается, Ш2 раскрывается (один за раз).
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш2')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-tours-Ш2'), 'Ш2 раскрыт').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-tours-Ш1'), 'Ш1 схлопнут').toBeNull();
    // aria-expanded: Ш2 — true, Ш1 — false.
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш2')!.getAttribute('aria-expanded')).toBe('true');
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!.getAttribute('aria-expanded')).toBe('false');
    // Повторный клик по Ш2 — схлопывает (все закрыты).
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш2')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'feature-step-tours-Ш2'), 'Ш2 схлопнут').toBeNull();
  });

  // === 4. Виток: номер, кто, вердикт, находки, «чат · задача» ревью ===
  it('виток: «виток 1», ревьюер, вердикт «принят», находки (reason), «чат · задача» ревью', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ rounds: [
      mkRound({ n: 1, verdict: 'accepted', personaId: 'p2', reason: 'нет проверки прав' }),
    ] })] }));
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    const round = findNodeByAttr(c, 'data-testid', 'feature-round-1');
    expect(round, 'строка витка 1').not.toBeNull();
    const t = textNodes(round!).join('');
    expect(t).toContain('виток 1');
    expect(t).toContain('Глеб');
    expect(t).toContain('принят');
    expect(t).toContain('нет проверки прав');
    // «чат · задача» ревью этого витка.
    const pair = findNodeByAttr(round!, 'data-testid', 'nav-target-pair');
    expect(pair, 'пара «чат · задача» витка').not.toBeNull();
    expect(textNodes(pair!).join('')).toContain('чат');
    expect(textNodes(pair!).join('')).toContain('задача');
  });

  it('виток: вердикты «вернул» (warning) и «принят с замечаниями» — подписи по-русски', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ rounds: [
      mkRound({ n: 1, verdict: 'returned', reason: 'сырой hex' }),
      mkRound({ n: 2, verdict: 'accepted_with_remarks' }),
    ] })] }));
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    const r1 = findNodeByAttr(c, 'data-testid', 'feature-round-1')!;
    expect(textNodes(r1).join('')).toContain('вернул');
    const r2 = findNodeByAttr(c, 'data-testid', 'feature-round-2')!;
    expect(textNodes(r2).join('')).toContain('принят с замечаниями');
  });

  // === 4а. Цели перехода: шаг (chatId, taskIds[0]) и виток (reviewChatId, reviewTaskId) ===
  // Мутация: подменить id (чат ← taskIds[0], задача ← chatId) → openChat/openTask
  // получают чужой id → краснеет.
  it('шаг: клик «чат» → openChatById(chain.chatId); клик «задача» → openTaskInSection(taskIds[0])', async () => {
    const chain = mkChain({ chatId: 'chat-s1', taskIds: ['t-s1', 't-s1b'] });
    const c = renderTab(mkRun(fullStages(), { stepChains: [chain] }));
    const row = findNodeByAttr(c, 'data-testid', 'feature-step-Ш1')!;
    const pair = findNodeByAttr(row, 'data-testid', 'nav-target-pair')!;
    // «чат» → openChatById('chat-s1') (не taskIds[0]).
    const chatBtn = findNodeByAttr(pair, 'data-testid', 'nav-target-chat')!;
    await act(async () => { (reactProps(chatBtn)!.onClick as () => void)(); });
    expect(mockOpenChat.calls, 'цель «чат» = chain.chatId').toEqual(['chat-s1']);
    // «задача» → openTaskInSection({ id: 't-s1' }) (первая задача цепочки, не вторая).
    const taskBtn = findNodeByAttr(pair, 'data-testid', 'nav-target-task')!;
    await act(async () => { (reactProps(taskBtn)!.onClick as () => void)(); });
    expect(mockOpenTask.calls, 'цель «задача» = taskIds[0]').toEqual([{ id: 't-s1' }]);
  });

  it('виток: клик «чат» → openChatById(reviewChatId); клик «задача» → openTaskInSection(reviewTaskId)', async () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ rounds: [
      mkRound({ n: 1, verdict: 'accepted', reviewChatId: 'chat-r1', reviewTaskId: 't-r1' }),
    ] })] }));
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    const round = findNodeByAttr(c, 'data-testid', 'feature-round-1')!;
    const pair = findNodeByAttr(round, 'data-testid', 'nav-target-pair')!;
    // «чат» → openChatById('chat-r1') (reviewChatId).
    const chatBtn = findNodeByAttr(pair, 'data-testid', 'nav-target-chat')!;
    await act(async () => { (reactProps(chatBtn)!.onClick as () => void)(); });
    expect(mockOpenChat.calls, 'цель «чат» = reviewChatId').toEqual(['chat-r1']);
    // «задача» → openTaskInSection({ id: 't-r1' }) (reviewTaskId).
    const taskBtn = findNodeByAttr(pair, 'data-testid', 'nav-target-task')!;
    await act(async () => { (reactProps(taskBtn)!.onClick as () => void)(); });
    expect(mockOpenTask.calls, 'цель «задача» = reviewTaskId').toEqual([{ id: 't-r1' }]);
  });

  // Мутация (5): игнорировать reviewChatId (всегда рисовать «чат») → кнопка «чат»
  // появляется у витка без чата → краснеет.
  it('виток без чата (reviewChatId = null): кнопки «чат» нет, «чата нет», задача есть', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ rounds: [
      mkRound({ n: 1, verdict: 'running', reviewChatId: null }),
    ] })] }));
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    const round = findNodeByAttr(c, 'data-testid', 'feature-round-1')!;
    const pair = findNodeByAttr(round, 'data-testid', 'nav-target-pair')!;
    expect(findNodeByAttr(pair, 'data-testid', 'nav-target-chat'), 'кнопки «чат» нет').toBeNull();
    expect(textNodes(pair).join('')).toContain('чата нет');
    expect(findNodeByAttr(pair, 'data-testid', 'nav-target-task'), 'кнопка «задача» есть').not.toBeNull();
  });

  // === 5. Пустые состояния ===
  it('шагов нет (stepChains пуст) → «Шагов пока нет»', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [] }));
    const empty = findNodeByAttr(c, 'data-testid', 'feature-steps-empty');
    expect(empty, 'пустое состояние «Шагов пока нет»').not.toBeNull();
    expect(textNodes(empty!).join('')).toContain('Шагов пока нет');
  });

  it('stepChains нет в данных (старая фича) → «Шагов пока нет» (честная деградация)', () => {
    const c = renderTab(mkRun(fullStages()));
    const empty = findNodeByAttr(c, 'data-testid', 'feature-steps-empty');
    expect(empty, 'пустое состояние «Шагов пока нет»').not.toBeNull();
  });

  it('шаг без исполнителя и без витков → «нет исполнителя»', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ stepRef: 'Ш1', personaId: null })] }));
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    const empty = findNodeByAttr(c, 'data-testid', 'feature-step-rounds-empty');
    expect(empty, '«нет исполнителя»').not.toBeNull();
    expect(textNodes(empty!).join('')).toBe('нет исполнителя');
  });

  it('шаг с исполнителем, витков нет → «ещё не ревьюили»', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ stepRef: 'Ш1', personaId: 'p1' })] }));
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    const empty = findNodeByAttr(c, 'data-testid', 'feature-step-rounds-empty');
    expect(empty, '«ещё не ревьюили»').not.toBeNull();
    expect(textNodes(empty!).join('')).toBe('ещё не ревьюили');
  });

  it('последний виток идёт → «ревью идёт»', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ rounds: [
      mkRound({ n: 1, verdict: 'returned' }),
      mkRound({ n: 2, verdict: 'running' }),
    ] })] }));
    act(() => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!)!.onClick as () => void)(); });
    const running = findNodeByAttr(c, 'data-testid', 'feature-step-review-running');
    expect(running, '«ревью идёт»').not.toBeNull();
    expect(textNodes(running!).join('')).toBe('ревью идёт');
  });

  // === 6. Честная деградация: старый шаг без stepRef ===
  it('шаг без stepRef (старая фича): кода нет, название и «чат · задача» — как есть', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain({ stepRef: '', title: 'Старый шаг' })] }));
    const row = findNodeByAttr(c, 'data-testid', 'feature-step-no-ref');
    expect(row, 'строка шага без stepRef').not.toBeNull();
    const t = textNodes(row!).join('');
    expect(t).toContain('Старый шаг');
    // Код-чип не нарисован (нет stepRef — не выдумываем).
    expect(findNodeByAttr(row!, 'data-testid', 'feature-step-code'), 'кода нет').toBeNull();
  });

  // === 7. 360: тач-цель шага ≥ 44 ===
  it('на 360 кнопка-шаг — minHeight 44', () => {
    const c = renderTab(mkRun(fullStages(), { stepChains: [mkChain()] }), true);
    const header = findNodeByAttr(c, 'data-testid', 'feature-step-header-Ш1')!;
    const st = (header as unknown as { style: Record<string, string> }).style;
    expect(st.minHeight).toBe('44px');
  });
});
