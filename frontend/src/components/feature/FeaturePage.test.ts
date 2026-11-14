// Страница фичи (волна 2 «Обзор фич», шаг Ф2.4). «Быстрый трек — страница как файл»:
// рендер-тесты пяти состояний (loading | ok | notFound | disabled | error, макет §5.7)
// + шапки-«файла» (глиф, название, тег статуса, «Закрыть» как у FileViewer) и тела —
// ТОЛЬКО конвейер (PipelineFlow).
//
// Закрытие читает указатель возврата (featureBackNav):
//   • пришли из чата (back.to = эта фича) — «К чату «…»» (название — из api.chats.get);
//   • указателя нет — «К списку» (мобила) / «Назад» (десктоп).
// Клик/Escape — onBack(target) + clearBackNav.
//
// Окружение node (без jsdom): fakeDom (mount) + управляемые сторы (featurePage,
// featuresIndex, featureBackNav) и mock api (chats.get, tasks.getFeature).

import { act, useSyncExternalStore } from 'react';
import { createElement, StrictMode } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { resolveSelectedStage } from './FeatureStageArea';
import { FeaturePage, type FeaturePageBackTarget } from './FeaturePage';
import { featureRunStatusTag, hasWorkflowRoute } from './FeaturePageHeader';
import { __test as pageTest, setFeaturePageTab, setFeatureMainTab, refetchFeaturePage, getFeatureMainTab, type FeatureBrief } from '../../lib/featurePage';
import { __test as indexTest } from '../../lib/featuresIndex';
import { __test as backNavTest, recordBackNav, clearBackNav } from '../../lib/featureBackNav';
import { setAllFlags } from '../../lib/featureFlags';
import { KEY_SESSION_TOOLTIP } from '../../lib/chatGroups';
import { setMeFromServer, clearMe } from '../../lib/defaultPersona';
import { C, SHADOW } from '../../lib/design';
import { api } from '../../lib/api';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureIndexFeature, FeaturesIndex } from '../../types/featureIndex';
import type { FeaturePage as FeaturePageData } from '../../types/featureIndex';
import type { FeatureMergeRisk, FeatureState } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';
import type { Session, Task } from '../../types';

// Управляемый индекс фич (useFeaturesIndex → этот срез).
const mockIndex = vi.hoisted(() => ({
  features: [] as FeatureIndexFeature[],
  chats: {} as Record<string, import('../../types/featureIndex').FeatureChatLink[]>,
}));
// Экран этапа (F10): по умолчанию запрос вечно «в пути»; тест подставляет ответ с executorSessionId.
const mockStage = vi.hoisted(() => ({ fn: (_id: string): Promise<unknown> => new Promise(() => {}) }));
// Управляемые ответы api.chats.get: id чата → Session (name).
const mockChats = vi.hoisted(() => new Map<string, Session>());
// Управляемый ответ api.tasks.getFeature: pending (загрузка) / error (статус) / result (ok).
// calls — журнал вызовов (fid + eventsLimit) для проверки «Показать все».
// perCall — ПОСЛЕДОВАТЕЛЬНЫЕ ответы (по одному на вызов, в порядке вызовов): сценарий
// «догрузка упала → повтор» (call 1 = initial ok, call 2 = expand 500, call 3 = retry ok).
// Пусто — статичный result/error (как раньше).
const mockGetFeature = vi.hoisted(() => ({
  pending: false,
  result: null as FeaturePageData | null,
  error: null as (Error & { status?: number }) | null,
  calls: [] as { fid: string; eventsLimit?: string }[],
  perCall: [] as ({ result?: FeaturePageData; error?: Error & { status?: number } })[],
}));
// Управляемый ответ api.tasks.mergeFeature: calls — журнал (двойной клик → 1 вызов;
// FS-XF1: поле tail — решение о закрытом хвосте в теле запроса, undefined — тела нет),
// pending (запрос в полёте → кнопка заблокирована), error (409/сбой, body.error — причина,
// body.code — машинный код tail_decision_required / tail_withdraw_not_applied),
// result (успех → задача мержа).
const mockMergeFeature = vi.hoisted(() => ({
  pending: false,
  result: null as Task | null,
  error: null as (Error & { status?: number; body?: unknown }) | null,
  calls: [] as { fid: string; tail?: string }[],
}));
// Управляемые ответы api.tasks.setFeatureHold/clearFeatureHold (W4-HOLD-F2): calls —
// журнал (fid + метод + reason), pending (запрос в полёте), error (409/сбой).
// Ответ DELETE hold — {resumed, expired} (вердикт R-HOLD-H): resumed/expired — управления
// для теста тоста снятия паузы (W4-HOLD-F3 хвост).
const mockHold = vi.hoisted(() => ({
  calls: [] as { fid: string; method: 'POST' | 'DELETE'; reason?: string }[],
  pending: false,
  error: null as (Error & { status?: number; body?: unknown }) | null,
  resumed: [] as string[],
  expired: [] as string[],
  // Имитация Ok() без тела: request отдаёт undefined (offline.ts:447) — проверка
  // страховки на пустое тело в doHoldClear (хвост R-HOLD-F3).
  emptyBody: false,
}));
// Журнал тостов (W4-HOLD-F2: текст 409 и успех — проверяются по нему).
const toasts = vi.hoisted(() => [] as [string, string | undefined][]);
// Ответ api.tasks.update (F2 «Закрыть фичу», F3 «Возобновить»): calls — журнал (id +
// dto), pending — запрос в полёте, error — отказ бэка (403/сбой). pending-запрос
// можно разрешить/отклонить вручную (settle/fail) — иначе он навсегда «в полёте» и
// тест не может снять состояние loading (хвост 2 вердикта F2-R).
const mockUpdateTask = vi.hoisted(() => ({
  calls: [] as { id: string; dto: unknown }[],
  pending: false,
  error: null as (Error & { status?: number; body?: unknown }) | null,
  settle: null as ((r: Task) => void) | null,
  fail: null as ((e: Error) => void) | null,
}));

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async (id: string) => mockChats.get(id) ?? null },
    tasks: {
      featuresIndex: async () => mockIndex,
      // Экран этапа (D1c) в этих тестах не предмет: запрос вечно «в пути»
      stage: (id: string) => mockStage.fn(id),
      getFeature: (fid: string, eventsLimit?: string) => {
        mockGetFeature.calls.push({ fid, eventsLimit });
        if (mockGetFeature.pending) return new Promise<FeaturePageData>(() => {});
        const next = mockGetFeature.perCall.shift();
        if (next?.error) return Promise.reject(next.error);
        if (next?.result) return Promise.resolve(next.result);
        if (mockGetFeature.error) return Promise.reject(mockGetFeature.error);
        return Promise.resolve(mockGetFeature.result!);
      },
      mergeFeature: (fid: string, tail?: string) => {
        mockMergeFeature.calls.push({ fid, tail });
        if (mockMergeFeature.pending) return new Promise<Task>(() => {});
        if (mockMergeFeature.error) return Promise.reject(mockMergeFeature.error);
        return Promise.resolve(mockMergeFeature.result!);
      },
      setFeatureHold: (fid: string, reason: string) => {
        mockHold.calls.push({ fid, method: 'POST', reason });
        if (mockHold.pending) return new Promise<{ interrupted: string[] }>(() => {});
        if (mockHold.error) return Promise.reject(mockHold.error);
        return Promise.resolve({ interrupted: [] as string[] });
      },
      clearFeatureHold: (fid: string) => {
        mockHold.calls.push({ fid, method: 'DELETE' });
        if (mockHold.pending) return new Promise<{ resumed: string[]; expired: string[] }>(() => {});
        if (mockHold.error) return Promise.reject(mockHold.error);
        if (mockHold.emptyBody) return Promise.resolve(undefined as unknown as { resumed: string[]; expired: string[] });
        return Promise.resolve({ resumed: mockHold.resumed, expired: mockHold.expired });
      },
      update: (id: string, dto: unknown) => {
        mockUpdateTask.calls.push({ id, dto });
        if (mockUpdateTask.pending) {
          return new Promise<Task>((res, rej) => { mockUpdateTask.settle = res; mockUpdateTask.fail = rej; });
        }
        if (mockUpdateTask.error) return Promise.reject(mockUpdateTask.error);
        return Promise.resolve({} as Task);
      },
    },
  },
}));
// Стор персон (lib/personas) — мок: hoisted-карта id→имя (аватары 18 в строках
// чатов/шагов, таблица §5.5). Компоненту нужен только .name. usePersonasVersion —
// настоящий useSyncExternalStore: тест может «догрузить стор» (добавить персону и
// поднять версию) и проверить, что чип перерисовывается (W4-1F, реактивность).
const personaStore = vi.hoisted(() => ({
  map: new Map<string, string>(),
  version: 0,
  listeners: new Set<() => void>(),
  ensureCalls: 0,
}));
const personaMap = personaStore.map;
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
  usePersonasVersion: () => useSyncExternalStore(
    (fn: () => void) => { personaStore.listeners.add(fn); return () => personaStore.listeners.delete(fn); },
    () => personaStore.version,
    () => personaStore.version,
  ),
  usePersonas: () => [],
  ensurePersonasLoaded: () => { personaStore.ensureCalls++; return Promise.resolve(); },
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
// Тосты пишутся в журнал toasts: тексты успеха и 409 (W4-HOLD-F2) проверяются по нему.
vi.mock('../../lib/toast', () => ({ showToast: (title: string, detail?: string) => { toasts.push([title, detail]); } }));
// Журнал openChatById: тесты чипа «ведёт» (W4-1F) проверяют, куда ведёт клик.
const openChatCalls = vi.hoisted(() => [] as string[]);
// Крюк момента открытия: тест снимает состояние в тот момент, когда openChatById уже
// вызван (FS-FKEY: доказать, что запись точки возврата идёт раньше перехода).
const openChatHook = vi.hoisted(() => ({ fn: null as ((id: string) => void) | null }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { openChatCalls.push(id); openChatHook.fn?.(id); return true; } }));
// Журнал recordFeatureReturn: делегирующий обёртка — настоящий модуль делает работу,
// журнал даёт точное число вызовов и их аргументы (FS-FKEY).
const recFeatureReturnCalls = vi.hoisted(() => [] as { featureId: string; to: { kind: string; id: string }; title: string | null }[]);
vi.mock('../../lib/featureBackNav', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/featureBackNav')>();
  return {
    ...orig,
    recordFeatureReturn: (featureId: string, to: { kind: 'chat' | 'task'; id: string }, title?: string | null, ...rest: unknown[]) => {
      recFeatureReturnCalls.push({ featureId, to, title: title ?? null });
      return (orig.recordFeatureReturn as (...a: unknown[]) => void)(featureId, to, title, ...rest);
    },
  };
});
// Модели: вкладка «Статистика» (Breakdown) читает каталог — в тесте он пуст.
vi.mock('../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/models')>();
  return { ...orig, useModels: () => [], modelLabel: (v: string | null | undefined) => v ?? '' };
});

// Каталог шаблонов для блока подтверждения маршрута (C3b): журнал вызовов, пустой каталог.
const mockTemplates = vi.hoisted(() => ({ calls: 0 }));
vi.mock('../../features/workflowTemplates/api', () => ({
  workflowTemplatesApi: {
    listTemplates: async () => { mockTemplates.calls += 1; return []; },
    confirmWorkflow: async () => {},
    recommendWorkflow: async () => ({}),
  },
}));

// Фиксированное «сейчас» для стабильных подписей возраста/тревоги.
const NOW = new Date('2026-10-19T12:00:00Z').getTime();

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: [
      { key: 'cut', state: 'done', tone: null },
      { key: 'plan', state: 'done', tone: null },
      { key: 'prod', state: 'active', tone: 'run' },
      { key: 'review', state: 'pending', tone: null },
      { key: 'qa', state: 'pending', tone: null },
      { key: 'final', state: 'pending', tone: null },
      { key: 'merge', state: 'pending', tone: null },
    ],
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chat-key', keyArchived: false, keyProjectId: 'p1', chatCount: 2,
    branch: 'feat/my-feature', createdAt: '2026-10-18T06:00:00Z', taskCount: 5,
    ...over,
  };
}

function pageData(over: Partial<FeaturePageData> = {}): FeaturePageData {
  return {
    run: {
      featureId: 'f1',
      epic: { id: 'f1', title: 'Моя фича' },
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    },
    events: [], eventsTotal: 0,
    ...over,
  };
}

// Страница с настоящими данными (чаты c-live/c-arch, шаги t-open/t-done, лента, статистика):
// общая фикстура блока «Ф9b: пометки переходов» и блока про вкладки (FS-FP).
function withData() {
  mockGetFeature.result = pageData({
    run: {
      featureId: 'f1', epic: { id: 'f1', title: 'Моя фича' },
      steps: [
        { id: 't-open', title: 'Открытая', status: 'inProgress', role: 'step', linkedSessionId: 'c-live' },
        { id: 't-done', title: 'Закрытая', status: 'done', role: 'step', linkedSessionId: 'c-arch' },
      ],
      reviews: [], qa: [], final: null, merge: null,
      stepChains: [
        { stepRef: 'A', title: 'Открытая', taskIds: ['t-open'], outcome: 'running', status: 'идёт', durationMinutes: 0, loops: 0, rounds: [], chatId: 'c-live' },
        { stepRef: 'B', title: 'Закрытая', taskIds: ['t-done'], outcome: 'accepted', status: 'принят', durationMinutes: 0, loops: 0, rounds: [], chatId: 'c-arch' },
      ],
      stages: [{ key: 'prod', state: 'active', passes: 1, taskIds: ['t-open', 't-done'], rounds: [] }],
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    } as never,
    chats: [
      { id: 'c-live', kind: 'executor', taskId: 't-open', archived: false, personaId: null, taskRole: null, stepRef: null, lane: null, modelLabel: null, modelFull: null },
      { id: 'c-arch', kind: 'executor', taskId: 't-done', archived: true, personaId: null, taskRole: null, stepRef: null, lane: null, modelLabel: null, modelFull: null },
    ],
    events: [
      // Лента — at desc (как отдаёт бэк): сначала поздняя.
      { at: '2026-10-19T11:00:00Z', kind: 'accepted', taskId: 't-done', role: null, stepRef: null, personaId: null, sessionId: 'c-arch', round: null, reason: null },
      { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't-open', role: null, stepRef: null, personaId: null, sessionId: 'c-live', round: null, reason: null },
    ] as never,
    eventsTotal: 2,
    stats: {
      source: 'journal', truncated: false,
      timeline: {
        from: '2026-10-19T09:00:00Z', to: '2026-10-19T12:00:00Z', stages: [],
        segments: [{ from: '2026-10-19T09:00:00Z', to: '2026-10-19T10:00:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-done' }],
        current: null,
        totals: { elapsedMinutes: 0, workWallMinutes: 0, agentsSumMinutes: 0, notWorkingMinutes: 0, waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null, errorCount: null, errorMinutes: null },
      },
      breakdown: { personas: [], models: [], roles: [] },
      longestNow: { taskId: 't-done', chatId: 'c-arch', personaId: null, stepRef: null, minutes: 5, tokens: null, share: null },
      spend: null,
    } as never,
  });
}

function render(props: { featureId?: string; projectId?: string | null; projectOwnerId?: string | null; isMobile?: boolean; onBack?: (t: FeaturePageBackTarget) => void }) {
  installFakeDom();
  // fakeDom'ов window/document без addEventListener/querySelector — ставим минимальные:
  // Esc-обработчик FeaturePage (как у FileViewer) вешает window.addEventListener('keydown')
  // и читает document.querySelector; без них mount падал. dispatchEvent → реальные слушатели.
  const win = globalThis as any;
  const doc = (globalThis as any).document;
  if (!win.window.addEventListener) {
    const listeners: Record<string, Set<Function>> = {};
    win.window.addEventListener = (t: string, f: Function) => { (listeners[t] ??= new Set()).add(f); };
    win.window.removeEventListener = (t: string, f: Function) => { listeners[t]?.delete(f); };
    win.window.dispatchEvent = (e: { type: string }) => { listeners[e.type]?.forEach(f => f(e)); return true; };
    win.__winListeners = listeners;
  }
  if (!doc.querySelector) doc.querySelector = () => null;
  // ToolbarOverflowMenu при открынении слушает прокручиваемых предков через
  // getComputedStyle — в fakeDom его нет; все зоны «visible» (прокрутки нет).
  if (!(globalThis as any).getComputedStyle) (globalThis as any).getComputedStyle = () => ({ overflowY: 'visible' });
  const pageProps = {
    featureId: props.featureId ?? 'f1',
    projectId: props.projectId ?? 'p1',
    projectOwnerId: props.projectOwnerId ?? null,
    isMobile: props.isMobile ?? false,
    onBack: props.onBack ?? (() => {}),
  };
  const { container, root } = mount(createElement(FeaturePage, pageProps));
  lastRender = { root, pageProps: pageProps as Record<string, unknown> };
  return container;
}

// Корень последнего render: тесты смены фичи перерисовывают тот же компонент с другим featureId
// (состояние страницы остаётся, как при переходе без перемонтирования).
let lastRender: { root: { render: (n: unknown) => void }; pageProps: Record<string, unknown> } | null = null;
function rerenderWithFeature(featureId: string) {
  act(() => { lastRender!.root.render(createElement(FeaturePage, { ...lastRender!.pageProps, featureId } as never)); });
}

// Первый <button> в поддереве узла (главное действие — Button ВНУТРИ div[data-testid]).
type AnyNode = { nodeType: number; nodeName: string; childNodes: AnyNode[] };
function firstButton(n: AnyNode): AnyNode | null {
  if (n.nodeType === 1 && n.nodeName === 'BUTTON') return n;
  for (const c of n.childNodes) {
    const hit = firstButton(c);
    if (hit) return hit;
  }
  return null;
}

// Кнопка вкладки (role="tab") по её подписи; null — нет. Счётчик — в тексте кнопки.
function findTab(c: ReturnType<typeof render>, label: string): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && (x as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'tab') {
      if (textNodes(x as never).join('').includes(label)) { hit = x; return; }
    }
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c as unknown as AnyNode);
  return hit;
}

// Число строк событий в ПОКАЗАННОЙ ленте (data-testid="event-row-…"): 8 — свёрнуто,
// все (10) — раскрыто. findNodeByAttr даёт только первый узел, поэтому обход дерева.
function countEventRows(c: ReturnType<typeof render>): number {
  let n = 0;
  const walk = (x: AnyNode) => {
    const id = (x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('data-testid') ?? '';
    if (id.startsWith('event-row-')) { n++; return; }
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c as unknown as AnyNode);
  return n;
}

// Дождаться разрешения fetch (fake timers + microtasks) → финальное состояние страницы.
// advanceTimersByTime(2000): прогоняет debounce 1,5 с и microtasks, но НЕ interval 60 с
// (useMinuteTick в PipelineFlow — иначе 10000 ререндеров = infinite loop).
async function settle() {
  await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
}

// Быстрый трек «страница как файл»: активны пять состояний + шапка-«файл» (глиф,
// название, тег, «Закрыть» как у FileViewer) + закрытие (кнопка/Esc) + back-nav.
// Блоки вкладок (тревога, шаги, чаты, задачи, лента, «Мерж», ⋯, M2-подсказка)
// покрыты отдельными тестами: FeaturePageFrame / FeatureTimelineTab / FeatureChatsTab /
// FeatureTasksTab / FeatureEventsTab.
describe('FeaturePage: пять состояний + шапка-«файл» + закрытие', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = pageData();
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockMergeFeature.pending = false;
    mockMergeFeature.result = null;
    mockMergeFeature.error = null;
    mockMergeFeature.calls = [];
    mockHold.calls = [];
    mockHold.pending = false;
    mockHold.error = null;
    toasts.length = 0;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. loading: скелет (6 полос + блок 120) в теле; шапка-«файл» — глиф/название/тег/«Закрыть» ===
  it('loading: скелет в теле; шапка-«файл» — глиф, название, тег, «Закрыть»', async () => {
    mockGetFeature.pending = true; // fetch не разрешается → состояние держится loading
    const c = render({});
    await settle();
    // Тело: скелет.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-skeleton')).not.toBeNull();
    // Шапка-«файл»: название (из индекса), тег, глиф, «Закрыть» — на месте (как у файла).
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'feature-page-title')!).join('')).toContain('Моя фича');
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-status')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-glyph')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close')).not.toBeNull();
  });

  // === 2. ok: шапка-«файл» (глиф, название, тег, «Закрыть») + тело — ТОЛЬКО конвейер ===
  it('ok: шапка-«файл» — глиф, название, тег «Идёт», «Закрыть»; тело — только конвейер', async () => {
    const c = render({});
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page')).not.toBeNull();
    // Название (mono, как «имя файла»).
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'feature-page-title')!).join('')).toContain('Моя фича');
    // Тег статуса: «Идёт» (активная стадия run, без тревоги).
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(tag).not.toBeNull();
    expect(textNodes(tag!).join('')).toContain('Идёт');
    // Глиф состояния + «Закрыть» (как у файла).
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-glyph')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close')).not.toBeNull();
    // Тело — только конвейер (PipelineFlow); убранных блоков нет.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-body')).not.toBeNull();
  });

  // п.1: тултип НАЗВАНИЯ фичи = title (nameTitle={title}), а не label «К чату «…»».
  // Мутация (в отчёте): вернуть nameTitle={label} → title ≠ название → тест краснеет.
  it('п.1: title имени фичи = название (не «К чату «…»»)', async () => {
    const c = render({});
    await settle();
    const name = findNodeByAttr(c, 'data-testid', 'feature-page-title')!;
    const title = (name as unknown as { attributes: Record<string, string> }).attributes?.title;
    expect(title, 'title имени = название фичи (не label возврата)').toBe('Моя фича');
  });

  // === 2b. «как файл»: белый фон C.bgCard, Toolbar-шапка, убранных блоков нет в DOM ===
  it('as a file: фон C.bgCard, Toolbar-шапка; нет тревоги/чатов/задач/ленты/⋯/главного действия', async () => {
    const c = render({});
    await settle();
    // Белый фон страницы — по токену C.bgCard (var(--c-bg-card)), как у просмотра файла.
    const page = findNodeByAttr(c, 'data-testid', 'feature-page')!;
    expect((page as unknown as { style: Record<string, string> }).style.background, 'фон страницы не C.bgCard (as a file)').toBe('var(--c-bg-card)');
    // Волна 3 (Ф1): шапка стала полной — мета-строка (ветка, «M задач») и меню ⋯
    // теперь НА МЕСТЕ; тревога и главное действие — только при attention/«не начата»
    // (в дефолте running/без тревоги их нет).
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-alarm'), 'тревога (running, без attention — нет)').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-main-action'), 'главное действие (running, без attention — нет)').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-meta'), 'мета-строки нет (Ш5)').toBeNull();
    // Ш5: возраст, «M задач» и сегменты ушли из шапки в сводку — в DOM их нет.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-taskcount'), '«M задач» в мета-строке убрано (Ш5)').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-steps'), 'сегменты прогресса убраны (Ш5)').toBeNull();
    expect(findNodeByAttr(c, 'aria-label', 'Ещё'), 'меню ⋯ (волна 3) — триггер на месте').not.toBeNull();
    // Строки чатов/шагов/событий фичи — нет. («Мерж»-стадия конвейера — легитимна, не проверяем по тексту.)
    expect(findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key'), 'строка чата на месте (as a file: убрана)').toBeNull();
  });

  // === 2б'. Ф9b: пометки и вкладка возврата — СКВОЗНО через страницу (проводка marks) ===
  // Данные настоящие: t-open (открыта, чат c-live), t-done (done, чат c-arch в архиве).
  // Мутации: FeaturePage не передаёт marks во вкладку → пометки пропадают; recordFeatureReturn
  // не запоминает вкладку → fromTab != вкладке клика.
  describe('Ф9b: пометки переходов и вкладка возврата через страницу', () => {
    const navFlags = (c: ReturnType<typeof render>, rowPrefix: string) => {
      const rows: AnyNode[] = [];
      const walk = (x: AnyNode) => {
        const id = (x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('data-testid') ?? '';
        if (id.startsWith(rowPrefix)) { rows.push(x); return; }
        for (const ch of x.childNodes) walk(ch);
      };
      walk(c as unknown as AnyNode);
      return rows.map(r => {
        const chat = findNodeByAttr(r as never, 'data-testid', 'nav-target-chat');
        const task = findNodeByAttr(r as never, 'data-testid', 'nav-target-task');
        return {
          chatArchived: !!chat && (chat.getAttribute('aria-label') ?? '').includes('(чат в архиве)'),
          taskClosed: !!task && (task.getAttribute('aria-label') ?? '').includes('(задача закрыта)'),
        };
      });
    };
    // Вкладка запоминается при реальном клике из вкладки: чип в чате покажет её название.
    // FS-FBACK: точка возврата хранит мейн-вкладку — старая вкладка страницы переводится
    // по таблице (events/stats → review, tasks → progress).
    for (const [tab, mainTab, rowPrefix, label, btnId] of [
      ['events', 'review', 'event-row-', 'Разбор · хронология', 'nav-target-chat'],
      ['tasks', 'progress', 'step-row-B', 'Ход · шаги', 'nav-target-chat'],
    ] as const) {
      it(`клик по «${btnId === 'nav-target-chat' ? 'чат' : 'задача'}» на вкладке «${label}» (${rowPrefix}) → точка возврата помнит мейн-вкладку «${mainTab}»`, async () => {
        withData();
        const c = render({});
        await settle();
        act(() => { setFeaturePageTab(tab); });
        // Шаги живут внутри «Производства»: раскрываем их кнопкой «Все N шагов этапа ›».
        if (tab === 'tasks') {
          const toggle = findNodeByAttr(c, 'data-testid', 'progress-steps-toggle')!;
          await act(async () => { (reactProps(toggle)!.onClick as () => void)(); });
        }
        const rows: AnyNode[] = [];
        const walk = (x: AnyNode) => {
          const id = (x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('data-testid') ?? '';
          if (id.startsWith(rowPrefix)) { rows.push(x); return; }
          for (const ch of x.childNodes) walk(ch);
        };
        walk(c as unknown as AnyNode);
        const btn = findNodeByAttr(rows[0] as never, 'data-testid', btnId)!;
        await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
        const back = backNavTest.getBackNav();
        expect(back?.fromKind).toBe('feature');
        expect(back?.fromTab, `мейн-вкладка перехода (${label})`).toBe(mainTab);
        expect(back?.fromTitle).toBe('Моя фича');
      });
    }

    // === FS-FKEY: общее пустое «Материалы» — «Открыть ключевой чат» пишет возврат, как остальные места ===
    // Мутации: убрать recordFeatureReturn из onOpenKeyChat → atOpen.records = 0 (записи в момент
    // открытия нет) и пустой recFeatureReturnCalls → тест красный; порядок «открыл, потом записал»
    // даёт тот же atOpen.records = 0.
    it('пустое «Материалы»: «Открыть ключевой чат» → ровно одна запись возврата, раньше openChatById', async () => {
      const c = render({});
      await settle();
      act(() => { setFeaturePageTab('plans'); });
      const btn = findNodeByAttr(c, 'data-testid', 'plans-open-key-chat');
      expect(btn, 'кнопка «Открыть ключевой чат» в пустом состоянии').not.toBeNull();
      // Снимок в момент openChatById: запись уже сделана → порядок «запись → переход».
      const atOpen: { records: number; from: string | null; toId: string | null } = { records: -1, from: null, toId: null };
      openChatHook.fn = () => {
        atOpen.records = recFeatureReturnCalls.length;
        const nav = backNavTest.getBackNav();
        atOpen.from = nav?.from ?? null;
        atOpen.toId = nav?.to?.id ?? null;
      };
      try {
        await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
      } finally {
        openChatHook.fn = null;
      }
      expect(openChatCalls, 'переход в ключевой чат').toEqual(['chat-key']);
      expect(recFeatureReturnCalls.length, 'ровно одна запись возврата').toBe(1);
      expect(recFeatureReturnCalls[0], 'аргументы записи: фича → её чат, с названием').toEqual({ featureId: 'f1', to: { kind: 'chat', id: 'chat-key' }, title: 'Моя фича' });
      expect(atOpen, 'в момент открытия чата запись уже стоит').toEqual({ records: 1, from: 'f1', toId: 'chat-key' });
    });
  });

  // === FS-FP: вкладки «Ход · Материалы · Разбор» ===
  // Сами вкладки: состав и порядок, внешние переходы старой вкладки, клики и счётчик
  // «Материалов». Пометки переходов из этих вкладок — в блоке выше (Ф9b), они про
  // точку возврата, а не про вкладки (хвост ревью, карточка b7488536, п.4).
  describe('FeaturePage: вкладки «Ход · Материалы · Разбор» (FS-FP)', () => {
    it('вкладки: ровно три в порядке Ход, Материалы, Разбор; по умолчанию «Ход»', async () => {
      withData();
      const c = render({});
      await settle();
      const tabs = findNodeByAttr(c, 'data-testid', 'feature-page-tabs')!;
      const labels: string[] = [];
      const walk = (x: AnyNode) => {
        if ((x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'tab') labels.push(textNodes(x).join(''));
        for (const ch of x.childNodes) walk(ch);
      };
      walk(tabs as unknown as AnyNode);
      expect(labels.map(l => l.replace(/\d+$/, '')), 'порядок и состав вкладок').toEqual(['Ход', 'Материалы', 'Разбор']);
      expect(findNodeByAttr(c, 'data-testid', 'progress-tab'), '«Ход» открыт по умолчанию').not.toBeNull();
      expect(findNodeByAttr(c, 'data-testid', 'feature-stats-tab'), 'старой «Статистики» в DOM нет').toBeNull();
      expect(labels.length, 'role=tab ровно три').toBe(3);
    });

    it('внешние setFeaturePageTab: events/stats/chats → «Разбор», plans → «Материалы», timeline/tasks → «Ход»', async () => {
      withData();
      const c = render({});
      await settle();
      const open = (old: Parameters<typeof setFeaturePageTab>[0]) => { act(() => { setFeaturePageTab(old); }); return getFeatureMainTab(); };
      expect(open('events')).toBe('review');
      expect(findNodeByAttr(c, 'data-testid', 'review-history'), 'тело «Разбора»').not.toBeNull();
      expect(open('timeline')).toBe('progress');
      expect(open('plans')).toBe('materials');
      expect(open('stats')).toBe('review');
      expect(open('tasks')).toBe('progress');
      expect(open('chats')).toBe('review');
    });

    // Пользователь переключил вкладку руками, потом внешний переход на то же старое значение
    // обязан её открыть (раннего выхода по прошлому значению нет).
    it('повторный setFeaturePageTab(\'events\') после ручного переключения снова открывает «Разбор»', async () => {
      withData();
      render({});
      await settle();
      act(() => { setFeaturePageTab('events'); });
      act(() => { setFeatureMainTab('progress'); });
      act(() => { setFeaturePageTab('events'); });
      expect(getFeatureMainTab()).toBe('review');
    });

    // Конкурентность: вкладка — в общем сторе; смена вкладки данные страницы не перезагружает.
    it('внешний setFeaturePageTab(\'events\') при открытой странице: «Разбор» активен, getFeature не вызывается повторно', async () => {
      withData();
      const c = render({});
      await settle();
      const before = mockGetFeature.calls.length;
      act(() => { setFeaturePageTab('events'); });
      await settle();
      const selected = findNodeByAttr(c, 'aria-selected', 'true')!;
      expect(textNodes(selected).join(''), 'активная вкладка').toContain('Разбор');
      expect(findNodeByAttr(c, 'data-testid', 'feature-review-tab'), 'смонтирован FeatureReviewTab').not.toBeNull();
      expect(findNodeByAttr(c, 'data-testid', 'progress-tab'), '«Ход» размонтирован').toBeNull();
      expect(mockGetFeature.calls.length, 'загрузки страницы не выросли').toBe(before);
    });

    // Клик по role="tab" доходит до стора вкладки и меняет тело страницы. Хвост ревью
    // (карточка b7488536, п.2): тот же инвариант, что на пути стора, — и для клика:
    // смена вкладки НЕ перечитывает страницу (число вызовов getFeature не растёт).
    it('клик по вкладкам: «Материалы» → тело материалов, «Разбор» → тело разбора, «Ход» → ход; getFeature не зовётся', async () => {
      withData();
      const c = render({});
      await settle();
      const calls = mockGetFeature.calls.length;
      expect(findNodeByAttr(c, 'data-testid', 'progress-tab'), 'старт на «Ход»').not.toBeNull();
      await act(async () => { (reactProps(findTab(c, 'Материалы')! as never)!.onClick as () => void)(); });
      expect(getFeatureMainTab()).toBe('materials');
      expect(findNodeByAttr(c, 'data-testid', 'progress-tab'), '«Ход» размонтирован').toBeNull();
      expect(findNodeByAttr(c, 'data-testid', 'plans-all-empty') ?? findNodeByAttr(c, 'data-testid', 'plans-grid'), 'тело «Материалов»').not.toBeNull();
      await act(async () => { (reactProps(findTab(c, 'Разбор')! as never)!.onClick as () => void)(); });
      expect(getFeatureMainTab()).toBe('review');
      expect(findNodeByAttr(c, 'data-testid', 'feature-review-tab'), 'тело «Разбора»').not.toBeNull();
      await act(async () => { (reactProps(findTab(c, 'Ход')! as never)!.onClick as () => void)(); });
      expect(getFeatureMainTab()).toBe('progress');
      expect(findNodeByAttr(c, 'data-testid', 'progress-tab')).not.toBeNull();
      expect(mockGetFeature.calls.length, 'смена вкладки перечитала страницу').toBe(calls);
    });

    // Счётчик «Материалов» = plansTabCount (FeaturePlansTab.tsx:340-346): видимые документы
    // + живые макеты + «прочие документы». Случай только из планов — ниже; случай со всеми
    // тремя составляющими — следом (хвост ревью, карточка b7488536, п.1: правка в части
    // макетов снимала цифру с вкладки, тест оставался зелёным).
    it('счётчик у «Материалов» равен числу карточек (2), у «Хода» и «Разбора» цифры нет', async () => {
      withData();
      const d = mockGetFeature.result!;
      d.plans = [
        { taskId: 'p1', role: 'cut', title: 'Разрез', authorPersonaId: null, createdAt: '2026-10-18T07:00:00Z', completedAt: null, hasText: true, mockupPaths: [] },
        { taskId: 'p2', role: 'consult', title: 'Консультация', authorPersonaId: null, createdAt: '2026-10-18T08:00:00Z', completedAt: null, hasText: true, mockupPaths: [] },
      ];
      const c = render({});
      await settle();
      expect(textNodes(findTab(c, 'Материалы')! as never).join('')).toBe('Материалы2');
      expect(textNodes(findTab(c, 'Ход')! as never).join('')).toBe('Ход');
      expect(textNodes(findTab(c, 'Разбор')! as never).join('')).toBe('Разбор');
    });

    // Все три составляющие plansTabCount сразу, с точной суммой и правилами каждой:
    // дедуп планов (показан последний по createdAt), неизвестная роль не считается,
    // путь, уже покрытый каталогом макетов, — не второй документ.
    it('счётчик «Материалы» = планы + макеты + прочие документы (4), у соседей цифры нет', async () => {
      withData();
      const d = mockGetFeature.result!;
      d.plans = [
        { taskId: 'p-cut', role: 'cut', title: 'Разрез', authorPersonaId: null, createdAt: '2026-10-18T07:00:00Z', completedAt: null, hasText: true, mockupPaths: [] },
        { taskId: 'p-old', role: 'plan', title: 'Старый план', authorPersonaId: null, createdAt: '2026-10-18T07:10:00Z', completedAt: null, hasText: true, mockupPaths: [] },
        { taskId: 'p-new', role: 'plan', title: 'Новый план', authorPersonaId: null, createdAt: '2026-10-18T08:00:00Z', completedAt: null, hasText: true, mockupPaths: ['docs/mockups/panel.dc.html', 'docs/adr/031.md'] },
        { taskId: 'p-step', role: 'step', title: 'Шаг (не документ)', authorPersonaId: null, createdAt: '2026-10-18T09:00:00Z', completedAt: null, hasText: true, mockupPaths: [] } as never,
      ];
      // Живой макет: группа из одного файла без суффикса версии, не отклонён.
      d.mockups = [{
        path: 'docs/mockups/panel.dc.html', title: 'Панель', status: null,
        files: [{ path: 'docs/mockups/panel.dc.html', exists: true }],
        source: 'header', taskId: null, authorPersonaId: null,
      } as never];
      const c = render({});
      await settle();
      // 1 разрез + 1 последний план («p-old» дедупнут) + 1 живой макет + 1 прочий документ
      // («docs/adr/031.md» нет в каталоге макетов; «panel.dc.html» уже макет — не второй).
      expect(textNodes(findTab(c, 'Материалы')! as never).join('')).toBe('Материалы4');
      expect(textNodes(findTab(c, 'Ход')! as never).join('')).toBe('Ход');
      expect(textNodes(findTab(c, 'Разбор')! as never).join('')).toBe('Разбор');
      // Путь materials[] (FS-F1e): «прочие документы» берутся из них, а не из mockupPaths.
      d.materials = [
        { path: 'docs/adr/031.md', taskId: 'p-new', at: '2026-10-18T09:30:00Z' },
        { path: 'docs/spec.md', taskId: 'p-cut', at: '2026-10-18T09:31:00Z' },
      ];
      await act(async () => { refetchFeaturePage('f1'); });
      await settle();
      // 2 документа + 1 макет + 2 материала = 5.
      expect(textNodes(findTab(c, 'Материалы')! as never).join('')).toBe('Материалы5');
    });
  });

  // === 3. notFound (404): EmptyState «Фичи больше нет» + «К списку чатов» ===
  it('notFound (404): EmptyState «Фичи больше нет» + кнопка «К списку чатов»', async () => {
    mockGetFeature.error = { name: 'HttpError', status: 404 } as Error & { status?: number };
    const c = render({});
    await settle();
    const all = textNodes(c).join('');
    expect(all).toContain('Фичи больше нет');
    expect(all).toContain('К списку чатов');
  });

  // === 4. disabled (503): Notice «Подсистема задач выключена» ===
  it('disabled (503): Notice «Подсистема задач выключена»', async () => {
    mockGetFeature.error = { name: 'HttpError', status: 503 } as Error & { status?: number };
    const c = render({});
    await settle();
    const all = textNodes(c).join('');
    expect(all).toContain('Подсистема задач выключена');
    expect(all).toContain('Страница фичи недоступна');
  });

  // === 5. error: «Не загрузилось» + «Повторить» ===
  it('error: «Не загрузилось» + кнопка «Повторить»', async () => {
    mockGetFeature.error = { name: 'HttpError', status: 500 } as Error & { status?: number };
    const c = render({});
    await settle();
    const all = textNodes(c).join('');
    expect(all).toContain('Не загрузилось');
    expect(all).toContain('Повторить');
  });

  // === Back-nav: закрытие (кнопка/Esc) ведёт туда, откуда открыли ===
  it('back-nav: указатель «чат → фича» → короткая «Чат», полный «К чату «…»» в title, onBack → chat', async () => {
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    mockChats.set('chatA', { id: 'chatA', name: 'Чат Дениса', mode: 'chat', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session);
    const onBack = vi.fn();
    const c = render({ onBack, isMobile: true });
    await settle();
    const close = findNodeByAttr(c, 'data-testid', 'feature-page-close')!;
    // Майя M2: на 360 длинное «К чату «…»» ломало шапку. Видимая подпись — КОРОТКАЯ
    // (одна строка, «Чат»), полный текст — только в title/aria-label.
    expect(textNodes(close).join(''), 'видимая подпись не короткая').toBe('Чат');
    expect(close.childNodes[0].getAttribute('title'), 'полный текст не в title').toBe('К чату «Чат Дениса»');
    act(() => { (reactProps(close.childNodes[0])!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'chat', chatId: 'chatA' });
    expect(backNavTest.getBackNav()).toBeNull();
  });

  it('back-nav: десктоп, без указателя → «Закрыть» (X), onBack → list', async () => {
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    const close = findNodeByAttr(c, 'data-testid', 'feature-page-close')!;
    // Десктоп: X-иконка (ToolbarIconButton), без текстовой подписи.
    act(() => { (reactProps(close.childNodes[0])!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'list' });
  });

  it('back-nav: мобила, без указателя → «К списку», onBack → list', async () => {
    const onBack = vi.fn();
    const c = render({ onBack, isMobile: true });
    await settle();
    const close = findNodeByAttr(c, 'data-testid', 'feature-page-close')!;
    expect(textNodes(close).join('')).toContain('К списку');
    act(() => { (reactProps(close.childNodes[0])!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'list' });
  });

  // === m2 (Майя): aria-label с полным текстом возврата; видимая подпись — короткая ===
  // Полный текст («К чату «…»»/«К списку») раньше был ТОЛЬКО в title; добавлен aria-label
  // (скринридеры читают aria-label, не title). Видимая подпись остаётся короткой.
  it('m2: aria-label «К чату «…»» (полный текст), видимая подпись короткая «Чат»', async () => {
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    mockChats.set('chatA', { id: 'chatA', name: 'Чат Дениса', mode: 'chat', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session);
    const c = render({ isMobile: true });
    await settle();
    // aria-label — полный текст возврата (мутация «убрать aria-label» красит).
    const back = findNodeByAttr(c, 'aria-label', 'К чату «Чат Дениса»');
    expect(back, 'aria-label с полным текстом «К чату «…»»').not.toBeNull();
    // Видимая подпись — короткая «Чат» (не длинный «К чату «…»»).
    const close = findNodeByAttr(c, 'data-testid', 'feature-page-close')!;
    expect(textNodes(close).join(''), 'видимая подпись короткая').toBe('Чат');
  });

  it('m2: aria-label «К списку» (мобила, без указателя), видимая подпись «К списку»', async () => {
    const c = render({ isMobile: true });
    await settle();
    const back = findNodeByAttr(c, 'aria-label', 'К списку');
    expect(back, 'aria-label «К списку» (мобила, без указателя)').not.toBeNull();
  });

  // === Esc закрывает страницу (ровно как файл: FileViewer) — onBack(target) ===
  it('Esc: закрывает → onBack (target по back-nav); в поле — не перехватывает', async () => {
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    mockChats.set('chatA', { id: 'chatA', name: 'Чат Дениса', mode: 'chat', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session);
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    // Esc вне поля/диалога → закрытие (target: chat).
    act(() => { (globalThis as any).window.dispatchEvent({ type: 'keydown', key: 'Escape', defaultPrevented: false, target: null }); });
    expect(onBack, 'Esc не закрыл страницу').toHaveBeenCalledWith({ kind: 'chat', chatId: 'chatA' });
    // Esc, когда печатают в поле (INPUT) — не перехватываем (Escape нужнее полю).
    onBack.mockClear();
    act(() => { (globalThis as any).window.dispatchEvent({ type: 'keydown', key: 'Escape', defaultPrevented: false, target: { tagName: 'INPUT' } }); });
    expect(onBack, 'Esc в поле перехватил закрытие (должен уступить полю)').not.toHaveBeenCalled();
  });

  // п.3 (Глеб, желательно): правило «скрытый центр не перехватывает Esc» — и для страницы.
  // Страница передаёт rootRef в оболочку (useEscapeClose): скрытая (display:none →
  // offsetParent=null) — Esc её не закрывает. Мутация «не передавать rootRef» → проверка
  // offsetParent не идёт (el=null) → Esc закрывает скрытую → red.
  it('п.3: скрытая страница (offsetParent=null) — Esc не перехватывает', async () => {
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    // Корень страницы (data-testid=feature-page) — тот, что useEscapeClose проверяет (rootRef).
    const root = findNodeByAttr(c, 'data-testid', 'feature-page') as unknown as { offsetParent?: unknown } | null;
    expect(root, 'корень страницы (rootRef оболочки)').not.toBeNull();
    // Скрытый центр (display:none → offsetParent=null) — Esc не закрывает.
    root!.offsetParent = null;
    act(() => { (globalThis as any).window.dispatchEvent({ type: 'keydown', key: 'Escape', defaultPrevented: false, target: null }); });
    expect(onBack, 'скрытая страница — Esc не закрывает').not.toHaveBeenCalled();
    // Контроль: видимая страница (offsetParent ≠ null) — Esc закрывает.
    root!.offsetParent = { tagName: 'BODY' };
    act(() => { (globalThis as any).window.dispatchEvent({ type: 'keydown', key: 'Escape', defaultPrevented: false, target: null }); });
    expect(onBack, 'видимая страница — Esc закрывает').toHaveBeenCalledTimes(1);
  });










  // === Blocker: минимальный run (бэк не отдал steps/reviews/qa) — страница не падает ===
  it('blocker: минимальный run (без steps/reviews/qa) — страница рендерится', async () => {
    mockGetFeature.result = {
      run: { featureId: 'f1', epic: { id: 'f1', title: 'Моя фича' } },
      events: [], eventsTotal: 0,
    } as unknown as FeaturePageData;
    const c = render({});
    await settle();
    // Страница и шапка-«файл» на месте (минимальный run не роняет).
    expect(findNodeByAttr(c, 'data-testid', 'feature-page')).not.toBeNull();
    expect(textNodes(c).join('')).toContain('Моя фича');
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close')).not.toBeNull();
  });

  // === Глеб major: run БЕЗ epic — страница не падает (фолбэк названия, run.epic?.title) ===
  it('epic: минимальный run БЕЗ epic — страница не падает', async () => {
    mockGetFeature.result = {
      run: { featureId: 'f1' },
      events: [], eventsTotal: 0,
    } as unknown as FeaturePageData;
    const c = render({});
    await settle();
    // Страница на месте: run без epic не роняет (название — из индекса, run.epic?.title — safe).
    // Мутация «убрать ?. у run.epic» красит: TypeError → белый экран (feature-page null).
    expect(findNodeByAttr(c, 'data-testid', 'feature-page'), 'страница упала (epic: run без epic)').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-title'), 'название не на месте (epic: run без epic)').not.toBeNull();
  });






});


// === Ш5: ветка и «ведёт» — в сводке, мета-строки шапки нет ===
describe('Ш5: ветка и ведущий в сводке', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    personaMap.set('p-sofya', 'Софья (локальная)');
    mockIndex.features = [feat({ orchestratorPersonaId: 'p-sofya' })];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = pageData();
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  const reindex = (over: Partial<FeatureIndexFeature>) => {
    mockIndex.features = [feat({ orchestratorPersonaId: 'p-sofya', ...over })];
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  };

  const brief = (over: Partial<FeatureBrief> = {}): FeatureBrief => ({
    kind: 'running', now: null, reason: null,
    done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' },
    next: [], elapsedMinutes: null, estimate: null, queue: null, note: null,
    computedAt: '2026-10-19T11:50:00Z',
    ...over,
  });
  const fbStats = (): FeatureStats => ({
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-19T10:00:00Z',
      to: '2026-10-19T11:50:00Z',
      stages: [{ key: 'prod', startedAt: '2026-10-19T10:00:00Z', barEnd: null }],
      segments: [
        { from: '2026-10-19T10:00:00Z', to: '2026-10-19T10:30:00Z', kind: 'work', track: 'main', stage: 'prod', taskId: 't-prod' },
      ],
      current: { kind: 'work', since: '2026-10-19T11:40:00Z', taskId: 't-prod' },
      totals: {
        elapsedMinutes: 110, workWallMinutes: 30, agentsSumMinutes: 30, notWorkingMinutes: 80,
        waitingMinutes: null, idleMinutes: null, waitingCount: null, longestWaitMinutes: null,
        errorCount: null, errorMinutes: null,
      },
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
  });
  const note = (byPersonaId: string | null): FeatureBrief['note'] =>
    ({ text: 'Идём по плану.', at: '2026-10-19T11:30:00Z', byPersonaId, byUser: false, stageAtWrite: 'prod', stale: false });

  it('мета-строки страницы нет ни на 1280, ни на 360; тег статуса — в тулбаре на обеих ширинах', async () => {
    for (const isMobile of [false, true]) {
      mockGetFeature.result = pageData({ brief: brief() });
      const c = render({ isMobile });
      await settle();
      expect(findNodeByAttr(c, 'data-testid', 'feature-page-meta'), `мета-строка (mobile=${isMobile})`).toBeNull();
      expect(findNodeByAttr(c, 'data-testid', 'feature-page-lead'), `чип «ведёт» (mobile=${isMobile})`).toBeNull();
      expect(findNodeByAttr(c, 'data-testid', 'feature-page-status'), `тег в тулбаре (mobile=${isMobile})`).not.toBeNull();
    }
  });

});

// Тип узла fakeDom для обходов в тестах (childNodes/attributes).
type FNodeLike = { nodeType: number; nodeName: string; childNodes: FNodeLike[]; attributes?: Record<string, string> };

// <button> с текстом (обход дерева; null — не найден).
function findButtonWithText(n: FNodeLike, text: string): FNodeLike | null {
  if (n.nodeType === 1 && n.nodeName === 'BUTTON') {
    const t = textNodes(n as never).join('');
    if (t.includes(text)) return n;
  }
  for (const c of n.childNodes) {
    const hit = findButtonWithText(c, text);
    if (hit) return hit;
  }
  return null;
}

// === Esc: общий хук useEscapeClose (в CenterViewShell) — FileViewer и FeaturePage ===
// Правило: не перехватываем, если печатают в поле/редакторе или открыт [role=dialog]/[role=menu].
// Мутации: (1) убрать removeEventListener в cleanup → «слушатель снят при размонтировании» краснеет;
// (2) убрать проверку [role=dialog] → «Esc при открытом диалоге» краснеет.
describe('Esc: useEscapeClose (общий хук, CenterViewShell)', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  function mountPage(onBack: (t: FeaturePageBackTarget) => void) {
    installFakeDom();
    const win = globalThis as any;
    const doc = (globalThis as any).document;
    if (!win.window.addEventListener) {
      const listeners: Record<string, Set<Function>> = {};
      win.window.addEventListener = (t: string, f: Function) => { (listeners[t] ??= new Set()).add(f); };
      win.window.removeEventListener = (t: string, f: Function) => { listeners[t]?.delete(f); };
      win.window.dispatchEvent = (e: { type: string }) => { listeners[e.type]?.forEach(f => f(e)); return true; };
      win.__winListeners = listeners;
    }
    if (!doc.querySelector) doc.querySelector = () => null;
    const { root, container } = mount(createElement(FeaturePage, {
      featureId: 'f1', projectId: 'p1', isMobile: false, onBack,
    }));
    return { root, container, win, doc };
  }
  function esc(win: any) {
    win.window.dispatchEvent({ type: 'keydown', key: 'Escape', target: { tagName: 'DIV', isContentEditable: false } });
  }
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('Esc: закрывает (onBack) — как у файла', async () => {
    const onBack = vi.fn();
    const { win } = mountPage(onBack);
    await settle();
    esc(win);
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it('Esc при открытом [role=dialog]/[role=menu]: не перехватывает (Escape нужнее им)', async () => {
    const onBack = vi.fn();
    const { win, doc } = mountPage(onBack);
    await settle();
    doc.querySelector = () => ({ role: 'dialog' });
    esc(win);
    expect(onBack, 'Esc перехвачен при открытом диалоге').not.toHaveBeenCalled();
  });

  it('Esc-слушатель снят при размонтировании (мутация: убрать removeEventListener → red)', async () => {
    const onBack = vi.fn();
    const { root, win } = mountPage(onBack);
    await settle();
    act(() => { root.unmount(); });
    esc(win);
    expect(onBack, 'слушатель Esc не снят при размонтировании (stale listener)').not.toHaveBeenCalled();
  });
});

// === W4-HOLD-F2: кнопка «Пауза» и блок состояния «на паузе»/«черновик» (feature-hold-1.md) ===
// Мутации (в отчёте): (1) убрать ветку 'paused' в featureRunStatusTag → тест тега
// краснеет; (2) убрать api.tasks.setFeatureHold в doHoldPause → «клик Паузы» краснеет;
// (3) убрать ранний return paused в FeaturePageAlert → блок «на паузе» не появится;
// (4) убрать разбор ответа в doHoldClear (всегда «Пауза снята») → тест «снятие с resumed»
// краснеет: заголовок не «Фича снята с паузы».
describe('W4-HOLD-F2: пауза и черновик в шапке и блоке состояния', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    personaMap.set('p-kira', 'Кира');
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockMergeFeature.pending = false;
    mockMergeFeature.result = null;
    mockMergeFeature.error = null;
    mockMergeFeature.calls = [];
    mockHold.calls = [];
    mockHold.pending = false;
    mockHold.error = null;
    mockHold.resumed = [];
    mockHold.expired = [];
    mockHold.emptyBody = false;
    toasts.length = 0;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // run страницы с состоянием: та же форма, что pageData, плюс state/hold/now.
  const holdPage = (state: FeatureState, over: Record<string, unknown> = {}) => {
    const pd = pageData();
    pd.run = { ...pd.run, state, ...over } as never;
    mockGetFeature.result = pd;
  };

  it('тег: paused → «на паузе» (тон hold), draft → «черновик» — даже с stageName', () => {
    expect(featureRunStatusTag('paused', 'Продукт')).toEqual({ label: 'на паузе', tone: 'hold' });
    expect(featureRunStatusTag('draft', 'Продукт')).toEqual({ label: 'черновик', tone: 'draft' });
  });

  it('paused на десктопе: тег «на паузе» без «идёт» и без тревожного тона; блок — «Фича на паузе»', async () => {
    holdPage('paused', {
      hold: { reason: 'manual', since: '2026-10-19T10:00:00Z' },
      now: [{ taskId: 't1', stage: 'prod', personaId: 'p-kira', stepRef: 'W2-A', title: 'Шаг' }],
    });
    const c = render({});
    await settle();
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(tag, 'тег на месте').not.toBeNull();
    const tagText = textNodes(tag!).join('');
    expect(tagText, 'тег «на паузе»').toContain('на паузе');
    expect(tagText, 'нет «идёт» (тон активной стадии на бэке — run)').not.toContain('идёт');
    expect((tag as unknown as { style: Record<string, string> }).style.background, 'тон не тревожный').toBe('var(--c-bg-inset)');
    const block = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(block, 'блок состояния на месте').not.toBeNull();
    expect((block as unknown as { attributes: Record<string, string> }).attributes['data-tone'], 'тон hold').toBe('hold');
    const blockText = textNodes(block!).join('');
    expect(blockText).toContain('Фича на паузе');
    expect(blockText).toContain('Прервано 1 шаг');
    expect(blockText).toContain('Кира · W2-A');
    expect(blockText, 'нет тревоги «ждёт вас»').not.toContain('ждёт вас');
    expect((block as unknown as { style: Record<string, string> }).style.background, 'фон блока — bgInset, не dangerBg').toBe('var(--c-bg-inset)');
  });

  // Хвост RV-K-Q5I-2, п.1: возраст паузы живой (useNow), а не замерший на последнем ренере.
  // Мутация: убрать useNow из блока (замершее «сейчас») → второй тезис остаёт «1 ч» → красный.
  it('длительность живая: возраст паузы обновляется через минуту', async () => {
    holdPage('paused', { hold: { reason: 'manual', since: '2026-10-19T11:00:00Z' }, now: [] });
    const c = render({});
    await settle();
    const block = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(block, 'блок состояния').not.toBeNull();
    expect(textNodes(block!).join(''), 'исходная длительность — 1 ч').toContain('Фича на паузе 1 ч.');
    await act(async () => { vi.advanceTimersByTime(60_000); });
    // Минуты после часов — двумя цифрами (формат Майи, Ш1a): «1 ч 01 мин», не «1 ч 1 мин».
    expect(textNodes(block!).join(''), 'после интервала — 1 ч 01 мин, не замерло').toContain('Фича на паузе 1 ч 01 мин.');
  });

  it('клик «Пауза» с идущими ходами: модалка; подтверждение — POST hold', async () => {
    holdPage('running', { now: [{ taskId: 't1', stage: 'prod', personaId: 'p-kira', stepRef: 'W2-A', title: 'Шаг' }] });
    const c = render({});
    await settle();
    const pause = findNodeByAttr(c, 'data-testid', 'feature-page-pause');
    expect(pause, 'кнопка «Пауза» в тулбаре').not.toBeNull();
    await act(async () => { (reactProps(pause)!.onClick as () => void)(); });
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-hold-confirm');
    expect(confirm, 'модалка подтверждения').not.toBeNull();
    const modalText = textNodes((globalThis as any).document.body).join(' ');
    expect(modalText).toContain('Поставить фичу на паузу?');
    expect(modalText).toContain('Кира · W2-A');
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    expect(mockHold.calls, 'POST hold reason manual').toEqual([{ fid: 'f1', method: 'POST', reason: 'manual' }]);
  });

  it('идущих ходов нет: сразу POST без модалки', async () => {
    holdPage('running', { now: [] });
    const c = render({});
    await settle();
    const pause = findNodeByAttr(c, 'data-testid', 'feature-page-pause')!;
    await act(async () => { (reactProps(pause)!.onClick as () => void)(); });
    expect(mockHold.calls).toEqual([{ fid: 'f1', method: 'POST', reason: 'manual' }]);
    expect(findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-hold-confirm'), 'модалки нет').toBeNull();
  });

  it('при paused: «Снять паузу» — DELETE hold', async () => {
    holdPage('paused', { hold: { reason: 'manual', since: '2026-10-19T11:00:00Z' }, now: [] });
    const c = render({});
    await settle();
    const unpause = findNodeByAttr(c, 'data-testid', 'feature-page-unpause');
    expect(unpause, 'главное действие «Снять паузу»').not.toBeNull();
    expect(textNodes(unpause!).join('')).toContain('Снять паузу');
    await act(async () => { (reactProps(unpause)!.onClick as () => void)(); });
    expect(mockHold.calls, 'DELETE hold').toEqual([{ fid: 'f1', method: 'DELETE' }]);
  });

  // Хвост R-HOLD-H2: ответ DELETE hold — {resumed, expired}. resumed не пустой → тост
  // «Фича снята с паузы» без числа; пустой → просто «Пауза снята». expired не показывается.
  it('снятие паузы с resumed: тост «Фича снята с паузы», без числа; expired не упоминается', async () => {
    holdPage('paused', { hold: { reason: 'manual', since: '2026-10-19T11:00:00Z' }, now: [] });
    mockHold.resumed = ['t1', 't2'];
    mockHold.expired = ['t3'];
    const c = render({});
    await settle();
    const unpause = findNodeByAttr(c, 'data-testid', 'feature-page-unpause')!;
    await act(async () => { (reactProps(unpause)!.onClick as () => void)(); });
    const last = toasts[toasts.length - 1];
    expect(last[0], 'заголовок — «Фича снята с паузы»').toBe('Фича снята с паузы');
    expect(last[1], 'текст без обещания работы (R-HOLD-F3)').toBe('Задачи, которым пауза мешала, снова разрешены.');
    expect((last[1] ?? ''), 'в тексте нет числа').not.toMatch(/\d/);
    expect((last[1] ?? ''), 'expired не показывается').not.toMatch(/просрочен|expired/i);
    expect(findNodeByAttr(c, 'data-testid', 'feature-page'), 'страница жива').not.toBeNull();
  });

  it('снятие паузы с пустым resumed: просто «Пауза снята», без «продолжено N»', async () => {
    holdPage('paused', { hold: { reason: 'manual', since: '2026-10-19T11:00:00Z' }, now: [{ taskId: 't1', stage: 'prod', personaId: 'p-kira', stepRef: 'W2-A', title: 'Шаг' }] });
    const c = render({});
    await settle();
    const unpause = findNodeByAttr(c, 'data-testid', 'feature-page-unpause')!;
    await act(async () => { (reactProps(unpause)!.onClick as () => void)(); });
    const last = toasts[toasts.length - 1];
    expect(last[0], 'заголовок — «Пауза снята»').toBe('Пауза снята');
    expect(last[1] ?? '', 'деталей про продолженные шаги нет').toBe('');
  });

  // Хвост R-HOLD-F3: Ok() без тела — request отдаёт undefined (offline.ts:447).
  // Без страховки `(await ...) ?? {}` деструктуризация бросит TypeError, попадёт в
  // catch — тост стал бы «Не получилось». Мутация: убрать `?? {}` — тест краснеет.
  it('пустое тело ответа: не падает, «Пауза снята», а не «Не получилось»', async () => {
    holdPage('paused', { hold: { reason: 'manual', since: '2026-10-19T11:00:00Z' }, now: [] });
    mockHold.emptyBody = true;
    const c = render({});
    await settle();
    const unpause = findNodeByAttr(c, 'data-testid', 'feature-page-unpause')!;
    await act(async () => { (reactProps(unpause)!.onClick as () => void)(); });
    const last = toasts[toasts.length - 1];
    expect(last[0], 'страховка сработала — заголовок «Пауза снята»').toBe('Пауза снята');
    expect(findNodeByAttr(c, 'data-testid', 'feature-page'), 'страница жива').not.toBeNull();
  });

  it('360: кнопки «Пауза» в ряду нет — первый пункт шторки ⋯ с подсказкой', async () => {
    holdPage('running', { now: [{ taskId: 't1', stage: 'prod', personaId: 'p-kira', stepRef: 'W2-A', title: 'Шаг' }] });
    const c = render({ isMobile: true });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-pause'), 'на мобиле кнопки в ряду нет').toBeNull();
    const trigger = findNodeByAttr(c, 'aria-label', 'Ещё');
    expect(trigger, 'триггер ⋯').not.toBeNull();
    await act(async () => { (reactProps(trigger)!.onClick as () => void)(); });
    // Порядок шторки (макет feature-queue-1): «В очередь…», затем «Поставить на паузу…».
    const items: string[] = [];
    const walk = (x: any) => {
      if (x.getAttribute?.('role') === 'menuitem') items.push(textNodes(x).join(''));
      for (const ch of x.childNodes) walk(ch);
    };
    walk((globalThis as any).document.body);
    expect(items[0], 'первый пункт — «В очередь…»').toContain('В очередь');
    const itemText = items[1];
    expect(itemText, 'второй пункт шторки').toContain('Поставить на паузу');
    expect(itemText).toContain('Прервёт 1 идущий шаг');
  });

  it('409: тост с причиной из ответа, страница не падает', async () => {
    holdPage('running', { now: [] });
    mockHold.error = Object.assign(new Error('Conflict'), { status: 409, body: { error: 'Мерж уже идёт — его не останавливают посреди' } });
    const c = render({});
    await settle();
    const pause = findNodeByAttr(c, 'data-testid', 'feature-page-pause')!;
    await act(async () => { (reactProps(pause)!.onClick as () => void)(); });
    expect(mockHold.calls.length, 'POST был попытан').toBe(1);
    const last = toasts[toasts.length - 1];
    expect(last[1], 'тост — причина из ответа').toContain('Мерж уже идёт');
    expect(findNodeByAttr(c, 'data-testid', 'feature-page'), 'страница жива после 409').not.toBeNull();
  });

  it('draft: блок «Черновик», кнопки «Пауза» нет; «Запустить фичу» — DELETE', async () => {
    holdPage('draft', { hold: { reason: 'draft', since: '2026-10-19T11:00:00Z' } });
    const c = render({});
    await settle();
    const block = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(block, 'блок черновика').not.toBeNull();
    expect((block as unknown as { attributes: Record<string, string> }).attributes['data-tone'], 'тон draft').toBe('draft');
    expect(textNodes(block!).join('')).toContain('Черновик.');
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-pause'), 'в черновике паузы нет').toBeNull();
    const start = findNodeByAttr(c, 'data-testid', 'feature-page-unpause');
    expect(start, 'главное действие «Запустить фичу»').not.toBeNull();
    expect(textNodes(start!).join('')).toContain('Запустить фичу');
    await act(async () => { (reactProps(start)!.onClick as () => void)(); });
    expect(mockHold.calls).toEqual([{ fid: 'f1', method: 'DELETE' }]);
    const last = toasts[toasts.length - 1];
    // Текст соответствует фактическому поведению бэка до решения по дефекту
    // bec00347: снятие draft-удержания не утверждает, что конвейер уже начался.
    expect(last[0], 'заголовок тоста — «Черновик снят»').toBe('Черновик снят');
    expect(last[1] ?? '', 'деталь про куратора').toContain('куратор');
  });
});

// Ш4: видимой кнопки «Закрыть фичу» нет — закрытие только пунктом ⋯ (и на 1280, и на 360).
function menuItemsNow(): AnyNode[] {
  const out: AnyNode[] = [];
  const walk = (x: AnyNode) => {
    if ((x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'menuitem') out.push(x);
    for (const ch of x.childNodes) walk(ch);
  };
  walk((globalThis as any).document.body as unknown as AnyNode);
  return out;
}
async function openMoreMenu(c: AnyNode) {
  const trigger = findNodeByAttr(c, 'aria-label', 'Ещё');
  expect(trigger, 'триггер ⋯').not.toBeNull();
  await act(async () => { (reactProps(trigger)!.onClick as () => void)(); });
}
async function hasCloseItem(c: AnyNode): Promise<boolean> {
  await openMoreMenu(c);
  return menuItemsNow().some(x => textNodes(x as never).join('').includes('Закрыть фичу'));
}
async function clickClose(c: AnyNode) {
  await openMoreMenu(c);
  const item = menuItemsNow().find(x => textNodes(x as never).join('').includes('Закрыть фичу'));
  expect(item, 'пункт «Закрыть фичу…» в ⋯').toBeDefined();
  await act(async () => { (reactProps(item)!.onClick as () => void)(); });
}

// === F2 (статусной модели): кнопка «Закрыть фичу» — только владелец, с подтверждением ===
// Контракт бэка (гард B2, TasksHttpDelegationGateTests): PUT /api/tasks/{epicId}
// {status:'done'} с пользовательским JWT проходит; сервисный токен — 403. Фронт
// показывает кнопку только владельцу проекта (FeaturePage, гейт по projectOwnerId)
// и только пока эпик не «Готово». «Возобновить» (F3) — отдельный describe ниже.
// Мутации (в отчёте): (1) убрать гейт canClose в FeaturePage → тест «не владелец»
// красный; (2) убрать api.tasks.update в doCloseFeature → тест подтверждения красный;
// (3) убрать проверку epic.status !== 'done' → тест «уже закрыта» красный;
// (4) не renderить CloseConfirmModal (закрывать сразу) → тест «Отмена» красный;
// (5) закрывать модалку и при отказе (без ветки err) → тест «отказ бэка» красный;
// (6) снять loading/текст на кнопке тулбара → тест «запрос в полёте» красный;
// (7) убрать строку предупреждения (nowCount > 0) → тест «идущие шаги» красный.
describe('F2: «Закрыть фичу» — пункт ⋯ (Ш4: кнопки нет)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockUpdateTask.calls = [];
    mockUpdateTask.pending = false;
    mockUpdateTask.error = null;
    toasts.length = 0;
    clearMe();
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Идущая фича с открытым эпиком (status 'todo').
  const openPage = () => {
    const pd = pageData();
    pd.run = { ...pd.run, state: 'running', epic: { id: 'f1', title: 'Моя фича', status: 'todo' } } as never;
    mockGetFeature.result = pd;
  };
  const owner = () => setMeFromServer({ userId: 'u-own', username: 'andrey', role: 'user' });

  it('владелец: кнопки в ряду нет, пункт в ⋯; клик — подтверждение; «Отмена» — запроса нет', async () => {
    openPage();
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'видимой кнопки «Закрыть фичу» нет').toBeNull();
    await clickClose(c);
    const modalText = textNodes((globalThis as any).document.body).join(' ');
    expect(modalText, 'заголовок с названием фичи').toContain('Закрыть «Моя фича»?');
    expect(modalText, 'пояснение про группу «Закрытые»').toContain('Закрытые');
    expect(modalText, 'идущих шагов нет — предупреждения нет').not.toContain('Идут');
    const cancel = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel');
    expect(cancel, 'кнопка «Отмена»').not.toBeNull();
    await act(async () => { (reactProps(cancel)!.onClick as () => void)(); });
    expect(mockUpdateTask.calls, 'без подтверждения — не закрыто').toEqual([]);
    expect(findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm'), 'модалка закрыта').toBeNull();
  });

  it('подтверждение: PUT status done, тост, страница перезапрошена', async () => {
    openPage();
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    await clickClose(c);
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm')!;
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    expect(mockUpdateTask.calls, 'PUT done по эпику').toEqual([{ id: 'f1', dto: { status: 'done' } }]);
    const last = toasts[toasts.length - 1];
    expect(last[0], 'тост «Фича закрыта»').toBe('Фича закрыта');
    expect(mockGetFeature.calls.length, 'после закрытия — перезапрос страницы').toBeGreaterThanOrEqual(2);
  });

  it('не владелец: кнопки нет', async () => {
    openPage();
    setMeFromServer({ userId: 'u-other', username: 'other', role: 'user' });
    // Честная формулировка (хвост 1 вердикта F2-R, MAJOR-1): проп подётся напрямую —
    // здесь проверяется сам гейт FeaturePage (projectOwnerId ≠ me.userId → кнопки нет).
    // Источник пропа — project.ownerId из ответа бэка (дефект 0dee7033) — forwarding
    // без изменений: WorkspacePage.tsx:2446 → FeatureCenter.tsx:42 → FeaturePage.tsx:127.
    // Сквозного теста через WorkspacePage/FeatureCenter в этом файле нет: там рендер
    // всей стены, а ломается в этой цепочке только значение пропа.
    const project = { id: 'p1', ownerId: 'u-own' };
    const c = render({ projectOwnerId: project.ownerId });
    await settle();
    expect(await hasCloseItem(c), 'не владельцу пункта нет').toBe(false);
  });

  // Фолбэка «поля нет — владелец по умолчанию» нет: бэк проставляет ownerId всем
  // проектам (миграция ProjectManager), а для разрушительного действия отсутствие
  // данных — не разрешение, а неизвестность.
  it('без ownerId: кнопки нет (нет данных ≠ разрешение)', async () => {
    openPage();
    owner();
    const c = render({});
    await settle();
    expect(await hasCloseItem(c), 'неизвестный владелец — не показываем').toBe(false);
  });

  it('уже закрыта (эпик done): кнопки нет; тег — «закрыта без мержа»', async () => {
    const pd = pageData();
    pd.run = { ...pd.run, state: 'closed', epic: { id: 'f1', title: 'Моя фича', status: 'done' } } as never;
    mockGetFeature.result = pd;
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    expect(await hasCloseItem(c), 'у закрытой фичи пункта нет').toBe(false);
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(textNodes(tag!).join(''), 'состояние страницы — «закрыта»').toContain('закрыта');
  });

  it('360: кнопки в ряду нет — пункт «Закрыть фичу…» в шторке ⋯', async () => {
    openPage();
    owner();
    const c = render({ isMobile: true, projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'на мобиле кнопки в ряду нет').toBeNull();
    await clickClose(c);
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm');
    expect(confirm, 'модалка подтверждения').not.toBeNull();
    // Закрыть модалку здесь же: незакрытая остаётся в DOM через портал и её
    // рендер старого компонента попадает в body следующего теста (респондеры
    // не анмонтированы) — следующий тест ловил бы чужую модалку.
    const cancel = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel')!;
    await act(async () => { (reactProps(cancel)!.onClick as () => void)(); });
  });

  it('отказ бэка: модалка остаётся открытой с причиной внутри; тост тоже есть', async () => {
    openPage();
    mockUpdateTask.error = Object.assign(new Error('Forbidden'), { status: 403, body: { error: 'Фичу закрывает только человек' } });
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    await clickClose(c);
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm')!;
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    const last = toasts[toasts.length - 1];
    expect(last[0], 'отказ не молчание').toBe('Не получилось');
    expect(last[1] ?? '', 'причина из ответа бэка').toContain('только человек');
    // Хвост 1: модалка НЕ закрыта, причина — в ней, а не только в тосте.
    const errNode = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-error');
    expect(errNode, 'причина в модалке').not.toBeNull();
    expect(textNodes(errNode!).join(''), 'текст причины').toContain('Фичу закрывает только человек');
    expect(findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm'), 'модалка открыта').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page'), 'страница жива').not.toBeNull();
    // Закрыть модалку здесь же: иначе её рендер утекает в следующий тест (портал не
    // анмонтирован — тот же приём, что в тесте «360»).
    const cancel = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel')!;
    await act(async () => { (reactProps(cancel)!.onClick as () => void)(); });
  });

  it('идущие шаги: предупреждение «Идут N … — они не остановятся» в модалке', async () => {
    const pd = pageData();
    pd.run = {
      ...pd.run,
      state: 'running',
      epic: { id: 'f1', title: 'Моя фича', status: 'todo' },
      now: [
        { taskId: 't1', stage: 'prod', personaId: null, stepRef: 'S1', title: 'Реализация' },
        { taskId: 't2', stage: 'review', personaId: null, stepRef: null, title: 'Ревью' },
      ],
    } as never;
    mockGetFeature.result = pd;
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    await clickClose(c);
    const warn = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-running');
    expect(warn, 'строка предупреждения').not.toBeNull();
    expect(textNodes(warn!).join(''), 'текст про идущие шаги').toContain('Идут 2 шага — они не остановятся, фича перейдёт в «Закрытые»');
    const cancel = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel')!;
    await act(async () => { (reactProps(cancel)!.onClick as () => void)(); });
  });

  it('один идущий шаг: «Идет 1 шаг — он не остановится» (дефект a1067076)', async () => {
    const pd = pageData();
    pd.run = {
      ...pd.run,
      state: 'running',
      epic: { id: 'f1', title: 'Моя фича', status: 'todo' },
      now: [
        { taskId: 't1', stage: 'prod', personaId: null, stepRef: 'S1', title: 'Реализация' },
      ],
    } as never;
    mockGetFeature.result = pd;
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    await clickClose(c);
    const warn = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-running');
    expect(warn, 'строка предупреждения').not.toBeNull();
    expect(textNodes(warn!).join(''), 'текст про один шаг').toContain('Идет 1 шаг — он не остановится, фича перейдёт в «Закрытые»');
    const cancel = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel')!;
    await act(async () => { (reactProps(cancel)!.onClick as () => void)(); });
  });

  it('запрос в полёте: «Закрыть» в модалке disabled и «Закрываю…» (loading)', async () => {
    openPage();
    mockUpdateTask.pending = true; // запрос не разрешается — состояние «в полёте» держится
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    await clickClose(c);
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm')!;
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    const props = reactProps(confirm) as { disabled?: boolean };
    expect(props.disabled, 'хвост 2: индикация loading на кнопке модалки').toBe(true);
    expect(textNodes(confirm).join(''), 'текст в полёте').toContain('Закрываю…');
    // Разрешить запрос успехом: модалка закрывается сама (cleanup — незакрытая
    // модалка утекала бы в следующий тест, портал не анмонтирован).
    await act(async () => { mockUpdateTask.settle?.({} as Task); await Promise.resolve(); });
    expect(findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm'), 'успех — модалка закрыта').toBeNull();
  });

  it('хвост 2: в полёте модалка не закрывается («Отмена» и Esc — no-op); после отказа — закрывается', async () => {
    openPage();
    mockUpdateTask.pending = true; // запрос «в полёте» до явного разрешения
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    await clickClose(c);
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm')!;
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    const stillOpen = () => findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm');
    const cancel = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel')!;
    await act(async () => { (reactProps(cancel)!.onClick as () => void)(); });
    expect(stillOpen(), '«Отмена» в полёте не закрывает').not.toBeNull();
    // Esc: обработчик модалки висит на document (fakeDom — настоящая подписка).
    // Фиктивному событию даём preventDefault: обработчик Modal зовёт его (ui/Modal.tsx:58),
    // без метода на фиктиве он бросил бы TypeError.
    const pressEsc = () => {
      act(() => {
        for (const f of ((globalThis as any).document.listeners?.keydown ?? new Set<Function>())) {
          f({ key: 'Escape', defaultPrevented: false, preventDefault: () => {} });
        }
      });
    };
    pressEsc();
    expect(stillOpen(), 'Esc в полёте не закрывает').not.toBeNull();
    // Отказ бэка снимает busy (try/finally, хвост 3): «Отмена» снова работает.
    await act(async () => { mockUpdateTask.fail?.(Object.assign(new Error('offline'), { status: 500 })); await Promise.resolve(); });
    expect(stillOpen(), 'при отказе модалка остаётся открытой с причиной').not.toBeNull();
    // Парное утверждение (хвост вердикта bb0a70c7): вне полёта Esc закрывает модалку —
    // доказательство, что обработчик действительно висит и работает; без этого «в полёте
    // не закрывает» было бы вакуумным. Мутация: убрать регистрацию Esc в ui/Modal.tsx —
    // эта проверка краснеет.
    pressEsc();
    expect(stillOpen(), 'Esc вне полёта закрывает модалку').toBeNull();
    // «Отмена» тоже работает: открыть снова и закрыть кнопкой.
    await clickClose(c);
    const cancel2 = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel')!;
    await act(async () => { (reactProps(cancel2)!.onClick as () => void)(); });
    expect(stillOpen(), 'после отказа «Отмена» закрывает').toBeNull();
  });
});

// === F3 (статусной модели): «Возобновить» на закрытой фиче — только владелец, без подтверждения ===
// Та же PUT-ручка /api/tasks/{epicId}, status:'todo'. Гейт «Возобновить» — по состоянию
// фичи (run.state === 'closed', хвост вердикта bb0a70c7), а не по epic.status: у
// смерженной фичи с закрытым эпиком кнопки нет. Подтверждения нет: возобновление не
// разрушительное (решение продукта). Состояние пересчитывает проектор после refetch.
// Мутации (в отчёте): (1) убрать гейт canClose → тест «не владелец» красный;
// (2) заменить гейт run.state === 'closed' на epic.status === 'done' → тест «смерженная
// фича с закрытым эпиком» красный;
// (3) убрать api.tasks.update в doResumeFeature → тест «клик — PUT todo» красный;
// (4) не делать refetchFeaturePage → тест «после возобновления снова «Закрыть»» красный.
describe('F3: кнопка «Возобновить» на закрытой фиче', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockUpdateTask.calls = [];
    mockUpdateTask.pending = false;
    mockUpdateTask.error = null;
    mockUpdateTask.settle = null;
    mockUpdateTask.fail = null;
    toasts.length = 0;
    clearMe();
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // Закрытая фича: состояние «closed», эпик «Готово».
  const closedPage = () => {
    const pd = pageData();
    pd.run = { ...pd.run, state: 'closed', epic: { id: 'f1', title: 'Моя фича', status: 'done' } } as never;
    mockGetFeature.result = pd;
  };
  const owner = () => setMeFromServer({ userId: 'u-own', username: 'andrey', role: 'user' });

  // Все пункты шторки ⋯.
  function menuItems(): AnyNode[] {
    const out: AnyNode[] = [];
    const walk = (x: AnyNode) => {
      if ((x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'menuitem') out.push(x);
      for (const ch of x.childNodes) walk(ch);
    };
    walk((globalThis as any).document.body as unknown as AnyNode);
    return out;
  }

  it('владелец: «Возобновить» есть, «Закрыть» нет; клик — PUT todo, тост, перезапрос страницы', async () => {
    closedPage();
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-resume-btn');
    expect(btn, 'кнопка «Возобновить» в тулбаре').not.toBeNull();
    expect(textNodes(btn!).join('')).toContain('Возобновить');
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'кнопки «Закрыть» нет нигде').toBeNull();
    expect(await hasCloseItem(c), 'у закрытой фичи «Закрыть» нет').toBe(false);
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
    expect(mockUpdateTask.calls, 'PUT todo по эпику').toEqual([{ id: 'f1', dto: { status: 'todo' } }]);
    const last = toasts[toasts.length - 1];
    expect(last[0], 'тост «Фича возобновлена»').toBe('Фича возобновлена');
    expect(mockGetFeature.calls.length, 'после возобновления — перезапрос страницы').toBeGreaterThanOrEqual(2);
  });

  it('не владелец: «Возобновить» нет', async () => {
    closedPage();
    setMeFromServer({ userId: 'u-other', username: 'other', role: 'user' });
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-resume-btn'), 'не владельцу кнопки нет').toBeNull();
  });

  // Хвост вердикта bb0a70c7: гейт по состоянию фичи (run.state), а не по epic.status.
  // Смерженная фича с закрытым эпиком — «Возобновить» нет: возобновление эпика не
  // вернуло бы её в активные (проектор считает её merged). Мутация гейта (заменить
  // run.state === 'closed' на epic.status === 'done') красит этот тест.
  it('смерженная фича с закрытым эпиком: «Возобновить» нет (гейт по run.state, не по epic)', async () => {
    const pd = pageData();
    pd.run = { ...pd.run, state: 'merged', epic: { id: 'f1', title: 'Моя фича', status: 'done' } } as never;
    mockGetFeature.result = pd;
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-resume-btn'), 'у смерженной фичи «Возобновить» нет').toBeNull();
  });

  it('открытая фича: «Возобновить» нет — кнопки взаимоисключающие', async () => {
    const pd = pageData();
    pd.run = { ...pd.run, state: 'running', epic: { id: 'f1', title: 'Моя фича', status: 'todo' } } as never;
    mockGetFeature.result = pd;
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-resume-btn'), 'на открытой фиче «Возобновить» нет').toBeNull();
    expect(await hasCloseItem(c), '«Закрыть» на открытой — есть').toBe(true);
  });

  it('после возобновления: refetch отдаёт открытый эпик — снова «Закрыть», «Возобновить» нет', async () => {
    const closed = pageData();
    closed.run = { ...closed.run, state: 'closed', epic: { id: 'f1', title: 'Моя фича', status: 'done' } } as never;
    const open = pageData();
    open.run = { ...open.run, state: 'running', epic: { id: 'f1', title: 'Моя фича', status: 'todo' } } as never;
    // Первый вызов — закрытая страница, второй (после возобновления) — открытая.
    mockGetFeature.perCall = [{ result: closed }, { result: open }];
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-resume-btn')!;
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
    await settle();
    expect(await hasCloseItem(c), 'после возобновления снова «Закрыть фичу»').toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-resume-btn'), '«Возобновить» исчезла').toBeNull();
  });

  it('360: кнопки в ряду нет — пункт «Возобновить фичу» в шторке ⋯', async () => {
    closedPage();
    owner();
    const c = render({ isMobile: true, projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-resume-btn'), 'на мобиле кнопки в ряду нет').toBeNull();
    const trigger = findNodeByAttr(c, 'aria-label', 'Ещё');
    expect(trigger, 'триггер ⋯').not.toBeNull();
    await act(async () => { (reactProps(trigger)!.onClick as () => void)(); });
    const item = menuItems().find(x => textNodes(x as never).join('').includes('Возобновить'));
    expect(item, 'пункт «Возобновить фичу»').not.toBeNull();
    await act(async () => { (reactProps(item)!.onClick as () => void)(); });
    expect(mockUpdateTask.calls, 'клик в шторке — PUT todo').toEqual([{ id: 'f1', dto: { status: 'todo' } }]);
  });
});

// === Q5B: очередь фич — действия в шторке ⋯ (макет feature-queue-1) ===
// Мутации (в отчёте): (1) 'queue' → 'manual' в doQueueIn → тест «В очередь» красный;
// (2) 'manual' → 'queue' в doQueueOut → тест «Вынуть» красный;
// (3) clearFeatureHold → setFeatureHold в doRunOutOfQueue → тест «вне очереди» красный;
// (4) убрать merge_pending из QUEUE_DENIED → тест недоступности красный.
describe('Q5B: очередь фич в шапке', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    personaMap.set('p-kira', 'Кира');
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockMergeFeature.pending = false;
    mockMergeFeature.result = null;
    mockMergeFeature.error = null;
    mockMergeFeature.calls = [];
    mockHold.calls = [];
    mockHold.pending = false;
    mockHold.error = null;
    mockHold.resumed = [];
    mockHold.expired = [];
    mockHold.emptyBody = false;
    toasts.length = 0;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });
  const queuePage = (state: FeatureState, over: Record<string, unknown> = {}) => {
    const pd = pageData();
    pd.run = { ...pd.run, state, ...over } as never;
    mockGetFeature.result = pd;
  };
  const menuLabels = (): string[] => {
    const out: string[] = [];
    const walk = (x: AnyNode) => {
      if ((x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'menuitem') out.push(textNodes(x).join(''));
      for (const ch of x.childNodes) walk(ch);
    };
    walk((globalThis as any).document.body as unknown as AnyNode);
    return out;
  };
  const menuItem = (label: string): AnyNode | null => {
    let found: AnyNode | null = null;
    const walk = (x: AnyNode) => {
      if (!found && (x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'menuitem'
        && textNodes(x).join('').includes(label)) found = x;
      for (const ch of x.childNodes) walk(ch);
    };
    walk((globalThis as any).document.body as unknown as AnyNode);
    return found;
  };
  const openMenu = async (c: AnyNode) => {
    const trigger = findNodeByAttr(c, 'aria-label', 'Ещё');
    expect(trigger, 'триггер ⋯').not.toBeNull();
    await act(async () => { (reactProps(trigger)!.onClick as () => void)(); });
  };
  const QUEUED = { hold: { reason: 'queue', since: '2026-10-19T11:00:00Z' }, queue: { position: 2, length: 3 }, now: [] };

  it('«В очередь…» шлёт POST hold {queue}; идущих ходов нет — без модалки', async () => {
    queuePage('running', { now: [] });
    const c = render({});
    await settle();
    await openMenu(c);
    const item = menuItem('В очередь');
    expect(item, 'пункт «В очередь…»').not.toBeNull();
    await act(async () => { (reactProps(item)!.onClick as () => void)(); });
    expect(mockHold.calls).toEqual([{ fid: 'f1', method: 'POST', reason: 'queue' }]);
  });

  it('«В очередь…» при идущих ходах: подсказка, модалка, POST hold {queue} после подтверждения', async () => {
    queuePage('running', { now: [{ taskId: 't1', stage: 'prod', personaId: 'p-kira', stepRef: 'W2-A', title: 'Шаг' }] });
    const c = render({});
    await settle();
    await openMenu(c);
    const item = menuItem('В очередь')!;
    expect(textNodes(item).join('')).toContain('Прервёт 1 идущий шаг — продолжатся при запуске из очереди');
    await act(async () => { (reactProps(item)!.onClick as () => void)(); });
    expect(mockHold.calls, 'до подтверждения запроса нет').toEqual([]);
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-queue-confirm');
    expect(confirm, 'модалка подтверждения').not.toBeNull();
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    expect(mockHold.calls).toEqual([{ fid: 'f1', method: 'POST', reason: 'queue' }]);
  });

  it('«Вынуть из очереди» шлёт POST hold {manual}, не DELETE', async () => {
    queuePage('paused', QUEUED);
    const c = render({});
    await settle();
    await openMenu(c);
    expect(menuItem('В очередь…'), 'в очереди «В очередь…» нет').toBeNull();
    const item = menuItem('Вынуть из очереди');
    expect(item, 'пункт «Вынуть из очереди»').not.toBeNull();
    await act(async () => { (reactProps(item)!.onClick as () => void)(); });
    expect(mockHold.calls).toEqual([{ fid: 'f1', method: 'POST', reason: 'manual' }]);
  });

  it('«Запустить вне очереди…» — всегда подтверждение, затем DELETE hold', async () => {
    queuePage('paused', QUEUED);
    const c = render({});
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-unpause'), 'главной кнопки «Снять паузу» у очереди нет').toBeNull();
    await openMenu(c);
    const item = menuItem('Запустить вне очереди');
    expect(item, 'пункт «Запустить вне очереди…»').not.toBeNull();
    await act(async () => { (reactProps(item)!.onClick as () => void)(); });
    expect(mockHold.calls, 'до подтверждения запроса нет').toEqual([]);
    expect(textNodes((globalThis as any).document.body).join(' ')).toContain('вторая');
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-bypass-confirm');
    expect(confirm, 'модалка подтверждения').not.toBeNull();
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    expect(mockHold.calls).toEqual([{ fid: 'f1', method: 'DELETE' }]);
  });

  it.each([
    ['draft', { hold: { reason: 'draft', since: '2026-10-19T11:00:00Z' } }, true],
    ['paused', { hold: { reason: 'manual', since: '2026-10-19T11:00:00Z' } }, true],
    ['merge_pending', {}, false],
    ['merging', {}, false],
    ['merged', {}, false],
    ['closed', {}, false],
  ] as [FeatureState, Record<string, unknown>, boolean][])('«В очередь…»: состояние %s, hold %j → доступно %s', async (state, over, expected) => {
    queuePage(state, { now: [], ...over });
    const c = render({});
    await settle();
    const trigger = findNodeByAttr(c, 'aria-label', 'Ещё');
    if (trigger) await act(async () => { (reactProps(trigger)!.onClick as () => void)(); });
    expect(menuLabels().some(l => l.includes('В очередь'))).toBe(expected);
  });
});

// === Q5I-2: блок «Фича в очереди» на странице фичи (макет feature-queue-1 §«Состояния») ===
// Очередь (paused + hold.reason='queue') — отдельный блок вместо паузного: тон паузы
// (bgInset/рамка border), глиф queue 14, позиция — готовая фраза из queuePhraseParts (Q5I-1).
// Мутации (в отчёте): (1) убрать ветку hold.reason==='queue' в FeaturePageAlert → рендерится
// пауза, «Фича в очереди» отсутствует → тест 1 красный; (2) собрать позицию из
// run.queue.position/length вместо phrase хелпера → «третья из трёх» нет → тест позиции-3
// красный; (3) нижняя граница 1 мин живёт в formatDuration (chatGroups.ts:338): снять
// Math.max(1,…) здесь — вывод тот же, тест свежей постановки зелёный (хвост RV-K-Q5I-2, п.2);
// живость длительности проверяет тест тикания (мутация: убрать useNow из блока — длительность
// замирает);
// (4) снять гейт position===1 → фраза первой позиции появляется при position 2 → тест 1
// красный; (5) сменить глиф/размер (kind≠'queue' или size≠14) → тест глифа красный.
describe('Q5I-2: блок «Фича в очереди» на странице фичи', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockHold.calls = [];
    mockHold.pending = false;
    mockHold.error = null;
    toasts.length = 0;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  const queuePage = (state: FeatureState, over: Record<string, unknown> = {}) => {
    const pd = pageData();
    pd.run = { ...pd.run, state, ...over } as never;
    mockGetFeature.result = pd;
  };
  // position 2 при длине 3 → «вторая из трёх»; since 11:00 при NOW 12:00 → 60 мин → «1 ч».
  const QUEUED = { hold: { reason: 'queue', since: '2026-10-19T11:00:00Z' }, queue: { position: 2, length: 3 }, now: [] };
  const MAIN_SUB = 'Запустится сама, когда одна из идущих фич проекта закончится или уйдёт на мерж. Прерванные шаги продолжатся с того же места.';
  const FIRST_POS = 'Если место освободилось, а фича не запустилась за минуту, запустите её вручную: ⋯ → «Запустить вне очереди».';

  // Внутренний узел, чей текст ровно text (сам текст — в одном статичном узле).
  function findExactText(n: AnyNode, text: string): AnyNode | null {
    if (textNodes(n as never).join('') === text) return n;
    for (const c of n.childNodes) { const hit = findExactText(c, text); if (hit) return hit; }
    return null;
  }
  const blockOf = (c: ReturnType<typeof render>): AnyNode | null => {
    const b = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    return b as unknown as AnyNode | null;
  };

  it('блок: «Фича в очереди: вторая из трёх. Поставили вы 1 ч назад. Шаги не запускается.» + sub', async () => {
    queuePage('paused', QUEUED);
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    const attrs = (block as unknown as { attributes: Record<string, string> }).attributes ?? {};
    expect(attrs['data-tone'], 'тон блока — queue').toBe('queue');
    expect(attrs['role'], 'role=status').toBe('status');
    expect(attrs['aria-live'], 'aria-live=polite').toBe('polite');
    const st = (block as unknown as { style: Record<string, string> }).style;
    expect(st.background, 'фон C.bgInset').toBe('var(--c-bg-inset)');
    expect(st.border, 'рамка 1px solid C.border').toBe('1px solid var(--c-border)');
    expect(st.borderRadius, 'R.lg').toBe('10px');
    const text = textNodes(block as never).join('');
    // Главная фраза — ровно phrase хелпера целиком (не собрана потребителем).
    expect(findExactText(block!, 'Фича в очереди: вторая из трёх.'), 'жирная фраза с позицией').not.toBeNull();
    expect(text).toContain('Поставили вы 1 ч назад. Шаги не запускаются.');
    // sub — FS.sm, C.textSecondary.
    const sub = findExactText(block!, MAIN_SUB);
    expect(sub, 'строка sub').not.toBeNull();
    const subStyle = (sub as unknown as { style: Record<string, string> }).style;
    expect(subStyle.fontSize, 'sub FS.sm').toBe('12px');
    expect(subStyle.color, 'sub C.textSecondary').toBe('var(--c-text-secondary)');
    // Это не пауза: паузных слов и кнопки нет; фразы первой позиции при position 2 нет.
    expect(text, 'нет паузной формулировки').not.toContain('Фича на паузе');
    expect(text, 'нет фразы первой позиции (position 2)').not.toContain('Если место освободилось');
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-unpause'), 'главной кнопки нет').toBeNull();
  });

  it('позиция — из хелпера, не константа: position 3 → «третья из трёх»', async () => {
    queuePage('paused', { ...QUEUED, queue: { position: 3, length: 3 } });
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    const text = textNodes(block as never).join('');
    expect(text, 'позиция третья').toContain('Фича в очереди: третья из трёх.');
    expect(text, 'нет фразы первой позиции').not.toContain('Если место освободилось');
  });

  it('первая позиция: к sub добавляется «Если место освободилось…»', async () => {
    queuePage('paused', { ...QUEUED, queue: { position: 1, length: 3 } });
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    const text = textNodes(block as never).join('');
    expect(text).toContain('Фича в очереди: первая из трёх.');
    expect(text, 'вторая фраза sub').toContain(FIRST_POS);
  });

  it('свежая постановка (<1 мин): «Поставили вы 1 мин назад», не «0 мин»', async () => {
    queuePage('paused', { hold: { reason: 'queue', since: '2026-10-19T12:00:00Z' }, queue: { position: 2, length: 3 }, now: [] });
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    const text = textNodes(block as never).join('');
    expect(text).toContain('Поставили вы 1 мин назад');
    expect(text, 'нуля нет').not.toContain('0 мин');
  });

  // Хвост RV-K-Q5I-2, п.1: у стоящей в очереди фичи событий может не быть вовсе — блок
  // перерисовывает только интервал useNow. Мутация: убрать useNow из блока (замершее
  // «сейчас») → второй тезис остаёт «1 ч» → красный.
  it('длительность живая: «1 ч назад» → «1 ч 01 мин назад» через минуту', async () => {
    queuePage('paused', QUEUED);
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    expect(textNodes(block as never).join(''), 'исходная длительность — 1 ч').toContain('Поставили вы 1 ч назад');
    await act(async () => { vi.advanceTimersByTime(60_000); });
    expect(textNodes(block as never).join(''), 'после интервала — 1 ч 01 мин, не замерло').toContain('Поставили вы 1 ч 01 мин назад');
  });

  it('нет queue (бэк не отдал): блок без позиции, нулей и слов позиции нет', async () => {
    queuePage('paused', { hold: { reason: 'queue', since: '2026-10-19T11:00:00Z' }, now: [] });
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    const text = textNodes(block as never).join('');
    expect(findExactText(block!, 'Фича в очереди.'), 'фраза без позиции').not.toBeNull();
    expect(text, 'слов позиции нет').not.toContain('вторая');
    expect(text, 'чисел «N из M» нет').not.toMatch(/\d+\s*из\s*\d+/);
    expect(text, 'фразы первой позиции нет (позиция неизвестна)').not.toContain('Если место освободилось');
  });

  it('нет hold.since (бэк не отдал): длительности нет, «0 мин» и «undefined» нет', async () => {
    queuePage('paused', { hold: { reason: 'queue' }, queue: { position: 2, length: 3 }, now: [] });
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    const text = textNodes(block as never).join('');
    expect(text).toContain('Поставили вы.');
    expect(text, 'без данных нет «назад»').not.toContain('назад');
    expect(text).not.toContain('undefined');
  });

  it('глиф блока — queue 14 (круг + три полосы), не глиф паузы', async () => {
    queuePage('paused', QUEUED);
    const c = render({});
    await settle();
    const block = blockOf(c);
    expect(block, 'блок очереди').not.toBeNull();
    let svg: AnyNode | null = null;
    const walk = (x: AnyNode) => {
      if (!svg && x.nodeName === 'SVG') svg = x;
      for (const ch of x.childNodes) walk(ch);
    };
    walk(block!);
    expect(svg, 'svg глифа в блоке').not.toBeNull();
    const attrs = (svg as unknown as { attributes: Record<string, string> }).attributes ?? {};
    expect(attrs['width'], 'глиф 14').toBe('14');
    expect(attrs['height'], 'глиф 14').toBe('14');
    let rects = 0;
    const walkRects = (x: AnyNode) => {
      if (x.nodeName === 'RECT') rects++;
      for (const ch of x.childNodes) walkRects(ch);
    };
    walkRects(svg!);
    // Три полосы — отличие от hold (две) и других глифов.
    expect(rects, 'три полосы глифа очереди').toBe(3);
  });
});

// === Ш8-wire: риски мержа в диалоге подтверждения ===
// FeaturePage передаёт attention.mergeRisks ПОСЛЕДНЕГО снимка страницы в FeatureMergeDialog
// (FeaturePage.tsx:429). Диалог открывается главным действием «Мерж…» (state merge_pending +
// attention.kind='merge', FeaturePageHeader.tsx:883-896). Конкурентность: диалог берёт риски
// из последнего page, а не из момента открытия — второй тест перезапрашивает страницу
// (refetchFeaturePage, lib/featurePage.ts:381) при открытом диалоге: блок обязан уйти.
// Мутации (в отчёте): (1) не передавать risks в FeaturePage.tsx → первый тест красный
// (блока нет); второй остаётся зелёным — там блока нет по данным.
describe('Ш8-wire: риски мержа в диалоге подтверждения', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockMergeFeature.pending = false;
    mockMergeFeature.result = null;
    mockMergeFeature.error = null;
    mockMergeFeature.calls = [];
    toasts.length = 0;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  const bodyNode = () => (globalThis as { document: { body: unknown } }).document.body as unknown as AnyNode;

  // Страница фичи, ждущей согласия на мерж: state merge_pending + тревога kind='merge'.
  // Риски — attention.mergeRisks (бэк при отсутствии рисков поля не даёт — undefined).
  function mergePage(risks?: FeatureMergeRisk[]): FeaturePageData {
    const pd = pageData();
    pd.run = {
      ...pd.run,
      state: 'merge_pending',
      attention: {
        kind: 'merge', taskId: 'f-fin', since: '2026-10-19T11:00:00Z', reason: 'мерж',
        mergeRisks: risks,
      },
    } as never;
    return pd;
  }

  // Открыть диалог главным действием шапки: span[data-testid] → Button внутри.
  async function openMerge(c: ReturnType<typeof render>) {
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
    expect(main, 'главное действие «Мерж…»').not.toBeNull();
    const btn = firstButton(main as unknown as AnyNode);
    expect(btn, 'кнопка внутри главного действия').not.toBeNull();
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
  }

  it('attention.kind=merge + риски: «Мерж…» открывает диалог, в нём feature-merge-risks', async () => {
    mockGetFeature.result = mergePage([
      { kind: 'not_created', count: 59 },
      { kind: 'open', count: 13 },
      { kind: 'tracker', count: 1 },
    ]);
    const c = render({});
    await settle();
    await openMerge(c);
    const body = bodyNode();
    expect(textNodes(body).join(' '), 'модалка мержа открыта').toContain('Смержить «Моя фича» в master?');
    const risks = findNodeByAttr(body as never, 'data-testid', 'feature-merge-risks');
    expect(risks, 'блок рисков мержа').not.toBeNull();
    expect(textNodes(risks!).join(''), 'текст — список бэка, фронт ничего не считает')
      .toContain('Не заведено шагов: 59 · Заведены, но не приняты: 13 · Трекер расходится: 1');
    // Закрыть модалку здесь же: незакрытая утекает в следующий тест (портал не анмонтирован).
    const cancel = findButtonWithText(body, 'Отмена');
    expect(cancel, '«Отмена» в модалке').not.toBeNull();
    await act(async () => { (reactProps(cancel!)!.onClick as () => void)(); });
  });

  it('перезапрос страницы без рисков: блок убран из открытого диалога (диалог берёт последний page)', async () => {
    mockGetFeature.perCall = [
      { result: mergePage([{ kind: 'not_created', count: 59 }]) }, // call 1: с рисками
      { result: mergePage(undefined) }, // call 2: бэк пересчитал — рисков больше нет
    ];
    const c = render({});
    await settle();
    await openMerge(c);
    expect(findNodeByAttr(bodyNode() as never, 'data-testid', 'feature-merge-risks'), 'блок первого снимка').not.toBeNull();
    // Перезапрос страницы при открытом диалоге: модалка не закрывается, данные — новые.
    refetchFeaturePage('f1');
    await settle();
    const body = bodyNode();
    expect(textNodes(body).join(' '), 'диалог остался открыт').toContain('Смержить «Моя фича» в master?');
    expect(findNodeByAttr(body as never, 'data-testid', 'feature-merge-risks'), 'устаревший снимок рисков не показан').toBeNull();
    const cancel = findButtonWithText(body, 'Отмена');
    expect(cancel, '«Отмена» в модалке').not.toBeNull();
    await act(async () => { (reactProps(cancel!)!.onClick as () => void)(); });
  });
});

// === FS-XF1: закрытый хвост в диалоге мержа (§8.3, после FS-XT1) ===
// Страница держит решение о хвосте (tailChoice) и передаёт его в тело POST merge;
// состав — только из данных бэка (attention.mergeRisks tail + run.closedTailKeys).
// Без выбора «Мерж» неактивна; без хвоста диалог и запрос — прежние. 409 с
// машинным кодом tail_* — не закрытие диалога и не красная ошибка: выбор
// сбрасывается, риски перечитываются перезапросом страницы.
// Мутации (в отчёте): (1) убрать гард mergeInFlight в FeaturePage.tsx → «двойной
// клик — один запрос» краснеет (2 вызова); (2) не передавать tail в mergeFeature →
// «выбор уходит в тело» краснеет (tail undefined); (3) не различать 409 по коду →
// «показан выбор» краснеет (диалог закрыт, блока нет).
describe('FS-XF1: закрытый хвост в диалоге мержа', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockMergeFeature.pending = false;
    mockMergeFeature.result = null;
    mockMergeFeature.error = null;
    mockMergeFeature.calls = [];
    toasts.length = 0;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  const bodyNode = () => (globalThis as { document: { body: unknown } }).document.body as unknown as FNodeLike;

  // Кнопка в body по ТОЧНОЙ подписи («Мерж» не должен ловить сегмент
  // «Мержить, хвост оставить» — отсюда точное сравнение, не includes).
  function exactButton(text: string): FNodeLike | null {
    let hit: FNodeLike | null = null;
    const walk = (x: FNodeLike) => {
      if (hit) return;
      if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textNodes(x as never).join('') === text) { hit = x; return; }
      for (const c of x.childNodes) walk(c);
    };
    walk(bodyNode());
    return hit;
  }

  const isDisabled = (btn: FNodeLike | null): boolean => {
    if (!btn) return true; // кнопки нет — действие невозможно в любом случае
    return Boolean(reactProps(btn)?.disabled);
  };

  // Страница merge_pending с тревогой «мерж»: риски — attention.mergeRisks,
  // состав хвоста — run.closedTailKeys (тот же учёт бэка).
  function mergePage(risks?: FeatureMergeRisk[], tailKeys?: string[]): FeaturePageData {
    const pd = pageData();
    pd.run = {
      ...pd.run,
      state: 'merge_pending',
      attention: {
        kind: 'merge', taskId: 'f-fin', since: '2026-10-19T11:00:00Z', reason: 'мерж',
        mergeRisks: risks,
      },
      closedTailKeys: tailKeys,
    } as never;
    return pd;
  }

  // Открыть диалог главным действием «Мерж…» (тот же путь, что в Ш8-wire).
  async function openMerge(c: ReturnType<typeof render>) {
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
    expect(main, 'главное действие «Мерж…»').not.toBeNull();
    let btn: FNodeLike | null = null;
    const walk = (x: FNodeLike) => {
      if (btn) return;
      if (x.nodeType === 1 && x.nodeName === 'BUTTON') { btn = x; return; }
      for (const c2 of x.childNodes) walk(c2);
    };
    walk(main as unknown as FNodeLike);
    expect(btn, 'кнопка внутри главного действия').not.toBeNull();
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
  }

  const closeMerge = async () => {
    const cancel = exactButton('Отмена');
    expect(cancel, '«Отмена» в модалке').not.toBeNull();
    await act(async () => { (reactProps(cancel!)!.onClick as () => void)(); });
  };

  it('хвост: диалог показывает его поимённо и требует выбора; без выбора «Мерж» неактивна', async () => {
    mockGetFeature.result = mergePage([{ kind: 'tail', count: 2 }], ['Ф1', 'Ф2']);
    const c = render({});
    await settle();
    await openMerge(c);
    const body = bodyNode();
    expect(textNodes(body).join(' '), 'модалка открыта').toContain('Смержить «Моя фича» в master?');
    const tailBlock = findNodeByAttr(body as never, 'data-testid', 'feature-merge-tail');
    expect(tailBlock, 'блок хвоста').not.toBeNull();
    const keys = textNodes(findNodeByAttr(body as never, 'data-testid', 'feature-merge-tail-keys')!).join(' ');
    expect(keys, 'хвост поимённо').toContain('Ф1');
    expect(keys, 'хвост поимённо').toContain('Ф2');
    expect(exactButton('Мерж'), 'кнопка «Мерж» на месте').not.toBeNull();
    expect(isDisabled(exactButton('Мерж')), '«Мерж» активна без решения о хвосте').toBe(true);
    await closeMerge();
  });

  it('выбор «keep» → «Мерж» активна, решение уходит в тело запроса', async () => {
    mockGetFeature.result = mergePage([{ kind: 'tail', count: 2 }], ['Ф1', 'Ф2']);
    const c = render({});
    await settle();
    await openMerge(c);
    const seg = exactButton('Мержить, хвост оставить');
    expect(seg, 'вариант «keep»').not.toBeNull();
    await act(async () => { (reactProps(seg!)!.onClick as () => void)(); });
    const btn = exactButton('Мерж');
    expect(isDisabled(btn), '«Мерж» неактивна после выбора').toBe(false);
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(mockMergeFeature.calls, 'тело запроса — { tail: keep }').toEqual([{ fid: 'f1', tail: 'keep' }]);
  });

  it('хвоста нет — блока выбора нет, запрос без тела (диалог прежний)', async () => {
    mockGetFeature.result = mergePage();
    const c = render({});
    await settle();
    await openMerge(c);
    expect(findNodeByAttr(bodyNode() as never, 'data-testid', 'feature-merge-tail'), 'блока выбора нет').toBeNull();
    const btn = exactButton('Мерж');
    expect(isDisabled(btn), '«Мерж» без хвоста').toBe(false);
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(mockMergeFeature.calls, 'без хвоста — тело не шлётся').toEqual([{ fid: 'f1', tail: undefined }]);
  });

  it('двойной клик «Мерж» — один запрос (мутация: убрать гард mergeInFlight → red)', async () => {
    mockGetFeature.result = mergePage();
    mockMergeFeature.pending = true; // запрос в полёте не разрешается
    const c = render({});
    await settle();
    await openMerge(c);
    const btn = exactButton('Мерж');
    expect(btn, 'кнопка «Мерж»').not.toBeNull();
    // Два клика в одном тике: state ещё не обновился, гард — ref (FeaturePage.tsx).
    await act(async () => {
      (reactProps(btn!)!.onClick as () => void)();
      (reactProps(btn!)!.onClick as () => void)();
    });
    expect(mockMergeFeature.calls.length, 'двойной клик дал больше одного запроса').toBe(1);
    await closeMerge();
  });

  it('409 tail_decision_required — показан выбор, не ошибка (диалог открыт, risks перечитаны)', async () => {
    // Снимок устарел: хвоста в нём нет, а бэк его видит → 409 с кодом.
    mockGetFeature.perCall = [
      { result: mergePage() }, // call 1: хвоста не видно
      { result: mergePage([{ kind: 'tail', count: 2 }], ['Ф1', 'Ф2']) }, // call 2: перечитано
    ];
    mockMergeFeature.error = { name: 'HttpError', status: 409, body: { error: 'Нужно решение о закрытом хвосте', code: 'tail_decision_required', tailSteps: 2 } } as Error & { status?: number; body?: unknown };
    const c = render({});
    await settle();
    await openMerge(c);
    const btn = exactButton('Мерж');
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
    await settle();
    const body = bodyNode();
    expect(textNodes(body).join(' '), 'диалог закрылся (должен ждать новый выбор)').toContain('Смержить «Моя фича» в master?');
    expect(findNodeByAttr(body as never, 'data-testid', 'feature-merge-tail'), 'выбор показан после перечитывания').not.toBeNull();
    expect(findNodeByAttr(body as never, 'data-testid', 'feature-merge-error'), 'красной ошибки нет').toBeNull();
    await closeMerge();
  });

  it('409 tail_withdraw_not_applied — «хвост изменился», выбор сброшен, риски перечитаны', async () => {
    // Хвост изменился, пока шёл запрос: часть шагов перестала быть хвостом.
    mockGetFeature.perCall = [
      { result: mergePage([{ kind: 'tail', count: 2 }], ['Ф1', 'Ф2']) }, // call 1: два шага
      { result: mergePage([{ kind: 'tail', count: 1 }], ['Ф2']) }, // call 2: один остался
    ];
    mockMergeFeature.error = { name: 'HttpError', status: 409, body: { error: 'Снятие не применено', code: 'tail_withdraw_not_applied', tailSteps: 1 } } as Error & { status?: number; body?: unknown };
    const c = render({});
    await settle();
    await openMerge(c);
    const seg = exactButton('Снять хвост и мержить');
    expect(seg, 'вариант «withdraw»').not.toBeNull();
    await act(async () => { (reactProps(seg!)!.onClick as () => void)(); });
    const btn = exactButton('Мерж');
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
    await settle();
    const body = bodyNode();
    expect(textNodes(body).join(' '), 'диалог закрылся (должен ждать новый выбор)').toContain('Смержить «Моя фича» в master?');
    const notice = findNodeByAttr(body as never, 'data-testid', 'feature-merge-tail-notice');
    expect(notice, 'предупреждение об изменении хвоста').not.toBeNull();
    expect(textNodes(notice!).join(''), 'текст — «хвост изменился, проверь список»').toContain('Хвост изменился, проверь список');
    expect(findNodeByAttr(body as never, 'data-testid', 'feature-merge-error'), 'красной ошибки нет').toBeNull();
    // Выбор сброшен: без нового решения «Мерж» снова неактивна.
    expect(isDisabled(exactButton('Мерж')), 'выбор не сброшен (кнопка активна)').toBe(true);
    // Риски перечитаны: второй GET страницы был.
    expect(mockGetFeature.calls.length, 'перечитывания не было').toBeGreaterThanOrEqual(2);
    await closeMerge();
  });
});

// === Ш2: тег этапа в тулбаре — из brief.now.stage (тот же источник, что сводка) ===
// Раньше: первая active-стадия run (stages.find(state==='active')) — тег тулбара и сводка
// (brief.now.stage, FeatureBriefBlock.tsx:75) могли показывать РАЗНЫЕ этапы. Теперь тег
// берёт brief.now.stage; бэк и семантика FeatureStage.State не тронуты.
// Мутации (в отчёте): (1) вернуть activeStageName на stages.find(active) → тест 1 краснеет
// (тег стал бы «идёт · Разрез»); (2) убрать терпимость к отсутствию brief (brief!.now.stage)
// → тест без brief падает.
describe('Ш2: тег этапа в тулбаре — из brief.now.stage', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    openChatHook.fn = null;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.result = null;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockMergeFeature.pending = false;
    mockMergeFeature.result = null;
    mockMergeFeature.error = null;
    mockMergeFeature.calls = [];
    toasts.length = 0;
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // brief той же формы, что в F6-блоке выше (now null по умолчанию).
  const brief = (over: Partial<FeatureBrief> = {}): FeatureBrief => ({
    kind: 'running',
    now: null,
    reason: null,
    done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' },
    next: [],
    elapsedMinutes: null,
    estimate: null,
    queue: null,
    note: null,
    computedAt: '2026-10-19T11:50:00Z',
    ...over,
  });

  // run идущей фичи с stages: cut — active, qa — pending. Если тег строился по stages,
  // он показал бы «Разрез»; по brief.now.stage — «Проверка». Это и различает тесты.
  const runningPage = (over: Partial<FeaturePageData> = {}) => {
    const pd = pageData(over);
    pd.run = { ...pd.run, state: 'running', stages: [
      { key: 'cut', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [], executors: [] },
      { key: 'qa', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
    ] } as never;
    mockGetFeature.result = pd;
  };

  const tagText = async (c: ReturnType<typeof render>) => {
    await settle();
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(tag, 'тег на месте').not.toBeNull();
    return textNodes(tag!).join('');
  };

  it('stages=[cut active, qa pending] + brief.now.stage=qa → тег «идёт · Проверка»', async () => {
    runningPage({ brief: brief({ now: { stage: 'qa', taskId: null, personaId: null, stepRef: null, lane: null, since: null } }) });
    const t = await tagText(render({}));
    expect(t, 'этап тега — из brief.now.stage (тот же источник, что сводка)').toContain('идёт · Проверка');
    expect(t, 'этап тега — не из первой active-стадии run').not.toContain('Разрез');
  });

  it('brief есть, brief.now null → тег «идёт» без этапа, без падения', async () => {
    runningPage({ brief: brief() });
    const t = await tagText(render({}));
    expect(t).toContain('идёт');
    expect(t, 'этапа в теге нет').not.toContain('·');
  });

  it('brief нет вовсе (старый бэк) → тег «идёт» без этапа, без падения', async () => {
    runningPage({});
    const t = await tagText(render({}));
    expect(t).toContain('идёт');
    expect(t, 'этапа в теге нет').not.toContain('·');
  });
});

// FS-FS: первый экран — плашка «от тебя», «Объём | Срок» (только на «Ходе»), «Итоги»
// закрытой фичи, скелет и сбой с «Повторить».
describe('FeaturePage: первый экран (FS-FS)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
    recFeatureReturnCalls.length = 0;
    mockGetFeature.pending = false;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    mockMergeFeature.calls = [];
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  const brief = (over: Partial<FeatureBrief> = {}): FeatureBrief => ({
    kind: 'running', now: null, reason: null,
    done: { stepsAccepted: 2, stepsTotal: 5, loops: 1, planState: 'cut' },
    next: [], elapsedMinutes: null, estimate: null, queue: null, note: null,
    computedAt: '2026-10-19T11:50:00Z',
    ...over,
  });
  const withProgress = (over: Partial<FeaturePageData> = {}): FeaturePageData => {
    const d = pageData(over);
    d.run = { ...d.run, state: 'running', stepProgress: { total: 5, accepted: 2, review: 1, work: 1, notCreated: 1, planState: 'cut' } as never };
    return d;
  };
  const has = (c: ReturnType<typeof render>, id: string) => findNodeByAttr(c, 'data-testid', id) !== null;

  it('открытая фича на «Ходе»: плашка, потом «Объём» и «Срок»; на «Материалах» — только плашка', async () => {
    mockGetFeature.result = withProgress({ brief: brief() });
    const c = render({});
    await settle();
    expect(has(c, 'feature-ask')).toBe(true);
    expect(has(c, 'volume-card')).toBe(true);
    expect(has(c, 'eta-card')).toBe(true);
    await act(async () => { setFeaturePageTab('plans'); });
    expect(has(c, 'feature-ask')).toBe(true);
    expect(has(c, 'volume-card')).toBe(false);
    expect(has(c, 'eta-card')).toBe(false);
  });

  it('закрытая фича: только «Итоги», плашки и карточек нет; «Разбор ›» → вкладка «Разбор»', async () => {
    const d = withProgress({ brief: brief({ kind: 'merged' }) });
    d.run = { ...d.run, summary: { steps: 5, loops: 1, durationMinutes: 200, mergedAt: '2026-10-18T10:00:00Z' } as never };
    mockGetFeature.result = d;
    const c = render({});
    await settle();
    expect(has(c, 'closed-summary')).toBe(true);
    expect(has(c, 'feature-ask')).toBe(false);
    expect(has(c, 'volume-card')).toBe(false);
    const btn = findNodeByAttr(c, 'data-testid', 'closed-summary-review');
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(getFeatureMainTab()).toBe('review');
  });

  it('«Ответить» плашки: чат тревоги — запись возврата и ровно один переход; мерж — диалог, чат не открывается', async () => {
    personaMap.set('p-den', 'Денис');
    const ask = { text: 'Нужен ответ', source: 'wait' as const, personaId: 'p-den', sessionId: 'chat-ask' };
    mockGetFeature.result = withProgress({ brief: brief({ kind: 'waiting_you', ask }) });
    const c = render({});
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-ask-answer');
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(openChatCalls).toEqual(['chat-ask']);
    expect(recFeatureReturnCalls).toHaveLength(1);
    expect(recFeatureReturnCalls[0].to).toEqual({ kind: 'chat', id: 'chat-ask' });
  });

  it('«Ответить» при source=merge открывает диалог мержа, а не чат', async () => {
    const ask = { text: 'Принять мерж', source: 'merge' as const, personaId: null, sessionId: 'chat-ask' };
    mockGetFeature.result = withProgress({ brief: brief({ kind: 'waiting_you', ask }) });
    const c = render({});
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-ask-answer');
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(openChatCalls).toEqual([]);
    expect(textNodes((globalThis as { document: { body: unknown } }).document.body as never).join(' ')).toContain('Смержить «Моя фича» в master?');
  });

  it('нет sessionId у плашки — кнопки «Ответить» нет', async () => {
    const ask = { text: 'Нужен ответ', source: 'wait' as const, personaId: null, sessionId: null };
    mockGetFeature.result = withProgress({ brief: brief({ kind: 'waiting_you', ask }) });
    const c = render({});
    await settle();
    expect(has(c, 'feature-ask')).toBe(true);
    expect(has(c, 'feature-ask-answer')).toBe(false);
  });

  it('загрузка: скелет первого экрана; сбой: «Не загрузилось · Повторить», два клика подряд — один перезапрос', async () => {
    mockGetFeature.pending = true;
    const c0 = render({});
    await settle();
    expect(has(c0, 'feature-page-skeleton')).toBe(true);
    uninstallFakeDom();

    pageTest.reset();
    mockGetFeature.pending = false;
    mockGetFeature.error = { name: 'HttpError', status: 500 } as Error & { status?: number };
    mockGetFeature.calls = [];
    const c = render({});
    await settle();
    expect(textNodes(c).join('')).toContain('Не загрузилось');
    const before = mockGetFeature.calls.length;
    const btn = findNodeByAttr(c, 'data-testid', 'first-screen-retry');
    await act(async () => {
      const onClick = reactProps(btn!)!.onClick as () => void;
      onClick(); onClick();
    });
    expect(mockGetFeature.calls.length - before).toBe(1);
  });
});

// C3d (вариант A, решение Андрея 13.11): вкладки «Процесс · Материалы · Разбор» под шапкой.
// Граф и выбранный этап — содержимое «Процесса» (конвейер прежнего «Хода» у фичи с маршрутом
// не рисуется); «Материалы» и «Разбор» открываются без графа, содержимое прежнее. Легаси-фича:
// вкладки прежние «Ход · Материалы · Разбор», графа и блока подтверждения нет. Выбор этапа
// живёт в состоянии страницы и переживает уход с вкладки (resolveSelectedStage — чужой фиче
// не принадлежит). Подтверждение маршрута в шапке — как в C3b.
describe('FeaturePage: вариант A — граф и этап внутри «Процесса» (C3d)', () => {
  const stage = (id: string, state: 'done' | 'active' | 'pending', after: string[], column: number, taskIds: string[] = []) =>
    ({ key: 'prod', id, role: 'step', title: `Этап ${id}`, after, column, state, passes: 1, taskIds, rounds: [] });
  const routed = (over: Record<string, unknown> = {}) => {
    const run = {
      featureId: 'f1',
      epic: { id: 'f1', title: 'Моя фича', projectId: 'p1', workflow: { templateId: 't', scope: 'global', stages: [], skipped: [], confirmedVersion: 1 }, ...over },
      steps: [
        { id: 't-b', title: 'Задача B', status: 'inProgress', role: 'step' },
        { id: 't-c', title: 'Задача C', status: 'todo', role: 'step' },
      ],
      reviews: [], qa: [], final: null, merge: null,
      stages: [stage('a', 'done', [], 0), stage('b', 'active', ['a'], 1, ['t-b']), stage('c', 'pending', ['b'], 2, ['t-c'])],
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    };
    mockGetFeature.result = pageData({ run: run as never });
  };
  const areaId = (c: ReturnType<typeof render>) => findNodeByAttr(c, 'data-testid', 'feature-stage-area')?.getAttribute('data-stage-id');

  // Подписи вкладок полосы (role=tab внутри feature-page-tabs), без счётчиков.
  const tabLabels = (c: ReturnType<typeof render>): string[] => {
    const tabs = findNodeByAttr(c, 'data-testid', 'feature-page-tabs');
    if (!tabs) return [];
    const labels: string[] = [];
    const walk = (x: AnyNode) => {
      if ((x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'tab') labels.push(textNodes(x).join(''));
      for (const ch of x.childNodes) walk(ch);
    };
    walk(tabs as unknown as AnyNode);
    return labels.map(l => l.replace(/\d+$/, ''));
  };
  // Есть ли в поддереве узел с testid (граф ВНУТРИ содержимого вкладки).
  const hasIn = (root: AnyNode, testid: string): boolean => {
    if ((root as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('data-testid') === testid) return true;
    return root.childNodes.some(ch => hasIn(ch, testid));
  };
  // data-testid в порядке документа: положение блоков (полоса вкладок выше графа).
  const testidOrder = (c: ReturnType<typeof render>): string[] => {
    const ids: string[] = [];
    const walk = (x: AnyNode) => {
      const id = (x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('data-testid');
      if (id) ids.push(id);
      for (const ch of x.childNodes) walk(ch);
    };
    walk(c as unknown as AnyNode);
    return ids;
  };

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    mockTemplates.calls = 0;
    mockGetFeature.pending = false;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('resolveSelectedStage: чужая фича и пропавшая стадия — defaultSelected', () => {
    routed();
    const run = (mockGetFeature.result as FeaturePageData).run;
    expect(resolveSelectedStage(null, 'f1', run)).toBe('b');
    expect(resolveSelectedStage({ featureId: 'f1', id: 'c' }, 'f1', run)).toBe('c');
    expect(resolveSelectedStage({ featureId: 'f1', id: 'c' }, 'f2', run)).toBe('b');
    expect(resolveSelectedStage({ featureId: 'f1', id: 'нет' }, 'f1', run)).toBe('b');
  });

  // Мутации (в отчёте): вернуть граф над FeaturePageTabs → порядок и hasIn краснеют;
  // снять featureMainTabLabel('progress', true) → «Процесс» → «Ход» → labels краснеют;
  // вернуть конвейер рядом с графом → progress-tab ≠ null → красный.
  it('фича с маршрутом: вкладки «Процесс · Материалы · Разбор», «Процесс» по умолчанию, граф и этап внутри него', async () => {
    routed();
    const c = render({});
    await settle();
    expect(tabLabels(c), 'вкладки фичи с маршрутом').toEqual(['Процесс', 'Материалы', 'Разбор']);
    const selected = findNodeByAttr(c, 'aria-selected', 'true')!;
    expect(textNodes(selected).join(''), 'дефолтная вкладка').toBe('Процесс');
    // Порядок варианта A: полоса вкладок выше содержимого, граф — внутри содержимого.
    const order = testidOrder(c);
    expect(order.indexOf('feature-page-tabs') < order.indexOf('feature-page-process'), 'полоса вкладок выше графа').toBe(true);
    const body = findNodeByAttr(c, 'data-testid', 'feature-page-body')!;
    expect(hasIn(body as unknown as AnyNode, 'feature-process-graph'), 'граф внутри содержимого вкладки').toBe(true);
    expect(areaId(c)).toBe('b');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stage-task-t-b')!).join('')).toContain('Задача B');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stage-task-status-t-b')!).join('')).toBe('в работе');
    // Конвейер прежнего «Хода» у фичи с маршрутом не рисуется: его делают граф и этап.
    expect(findNodeByAttr(c, 'data-testid', 'progress-tab'), 'конвейер на фиче с маршрутом').toBeNull();
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'pgraph-node-c')!)!.onClick as () => void)(); });
    expect(areaId(c)).toBe('c');
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stage-task-status-t-c')!).join('')).toBe('не начата');
  });

  it('«Материалы» и «Разбор» открываются без графа, содержимое прежнее', async () => {
    routed();
    const c = render({});
    await settle();
    await act(async () => { (reactProps(findTab(c, 'Материалы')! as never)!.onClick as () => void)(); });
    expect(getFeatureMainTab()).toBe('materials');
    expect(findNodeByAttr(c, 'data-testid', 'feature-process-graph'), 'граф на «Материалах»').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'plans-all-empty') ?? findNodeByAttr(c, 'data-testid', 'plans-grid'), 'тело «Материалов»').not.toBeNull();
    await act(async () => { (reactProps(findTab(c, 'Разбор')! as never)!.onClick as () => void)(); });
    expect(getFeatureMainTab()).toBe('review');
    expect(findNodeByAttr(c, 'data-testid', 'feature-process-graph'), 'граф на «Разборе»').toBeNull();
    // Строка итога — часть «Разбора» и в пустом состоянии (FeatureReviewTab.tsx:61-70):
    // фикстура routed() без stats — полный корень feature-review-tab не рисуется (ранний return).
    expect(findNodeByAttr(c, 'data-testid', 'feature-analysis-line'), 'тело «Разбора»').not.toBeNull();
  });

  // F10: «Открыть этап →» заменяет центр экраном этапа; «‹ Фича» и Esc возвращают на страницу с тем же узлом
  // и вкладкой; встроенного экрана под графом нет; «Чат исполнителя» — из stageSummary.executorSessionId.
  // Мутации (в отчёте): (1) вернуть встроенный экран в FeatureStageArea → «нет под графом» красный;
  // (2) onClose={goBack} вместо closeStageScreen → Esc-тест красный (onBack вызван); (3) показывать «Чат
  // исполнителя» без проверки sessionId → тест null красный.
  const stageResp = (taskId: string, sid: string | null) => Promise.resolve({
    taskId, featureId: 'f1', role: 'step', stepRef: null, title: `задача ${taskId}`, status: 'inProgress', stage: null,
    artifacts: { commits: [], commitsAvailable: true, linkedFiles: [], resultMarkdown: null },
    input: { stage: null, sessionId: null, transcriptAvailable: false, transcript: null },
    review: { own: null, reviews: [], awaitingHuman: null }, metrics: null, questions: [], stageSummary: { executorSessionId: sid },
  });
  const openStage = async (c: ReturnType<typeof render>) => {
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-stage-area-open')!)!.onClick as () => void)(); });
    await settle();
  };

  it('«Открыть этап →»: экран этапа вместо страницы, встроенного под графом нет, «‹ Фича» возвращает к тому же узлу и вкладке', async () => {
    routed();
    mockStage.fn = (id) => stageResp(id, 'sess-x');
    const c = render({});
    await settle();
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'pgraph-node-c')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'stage-screen'), 'экрана под графом нет').toBeNull();
    await openStage(c);
    expect(findNodeByAttr(c, 'data-testid', 'stage-screen-toolbar'), 'тулбар экрана этапа').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-process-graph'), 'граф скрыт').toBeNull();
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'stage-screen-feature-title')!).join('')).toBe('Моя фича');
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'stage-screen-back')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'stage-screen'), 'вернулись на страницу').toBeNull();
    expect(areaId(c), 'тот же выбранный узел').toBe('c');
    expect(getFeatureMainTab()).toBe('progress');
  });

  it('экран этапа: Esc возвращает на страницу, а не закрывает её; «Чат исполнителя» ведёт в чат, при null кнопки нет', async () => {
    routed();
    const onBack = vi.fn();
    mockStage.fn = (id) => stageResp(id, 'sess-x');
    const c = render({ onBack });
    await settle();
    await openStage(c);
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'stage-screen-executor-chat')!)!.onClick as () => void)(); });
    expect(openChatCalls.at(-1)).toBe('sess-x');
    await act(async () => { (globalThis as any).window.dispatchEvent({ type: 'keydown', key: 'Escape', defaultPrevented: false, target: null }); });
    expect(findNodeByAttr(c, 'data-testid', 'stage-screen'), 'Esc вернул на страницу').toBeNull();
    expect(onBack).not.toHaveBeenCalled();
    mockStage.fn = (id) => stageResp(id, null);
    await openStage(c);
    expect(findNodeByAttr(c, 'data-testid', 'stage-screen-executor-chat'), 'executorSessionId = null — кнопки нет').toBeNull();
    mockStage.fn = () => new Promise(() => {});
  });

  it('выбранный этап сохраняется при уходе на «Материалы» и возврате в «Процесс»', async () => {
    routed();
    const c = render({});
    await settle();
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'pgraph-node-c')!)!.onClick as () => void)(); });
    expect(areaId(c)).toBe('c');
    await act(async () => { (reactProps(findTab(c, 'Материалы')! as never)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'feature-stage-area'), 'область этапа на «Материалах»').toBeNull();
    await act(async () => { (reactProps(findTab(c, 'Процесс')! as never)!.onClick as () => void)(); });
    expect(getFeatureMainTab()).toBe('progress');
    expect(areaId(c), 'выбор после возврата в «Процесс»').toBe('c');
  });

  it('смена фичи сбрасывает выбор этапа на defaultSelected', async () => {
    routed();
    const c = render({});
    await settle();
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'pgraph-node-c')!)!.onClick as () => void)(); });
    expect(areaId(c)).toBe('c');
    rerenderWithFeature('f2');
    await settle();
    expect(areaId(c)).toBe('b');
  });

  it('легаси-эпик (ни предложения, ни маршрута): вкладки прежние «Ход · Материалы · Разбор», графа и блока подтверждения нет, каталог не грузится', async () => {
    mockGetFeature.result = pageData();
    const c = render({});
    await settle();
    expect(tabLabels(c), 'вкладки легаси-фичи').toEqual(['Ход', 'Материалы', 'Разбор']);
    expect(findNodeByAttr(c, 'data-testid', 'feature-process-graph')).toBeNull();
    // Легаси (F08): обёртка несёт только строку «старая фича», сам блок и каталог — после клика.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-route-legacy')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm')).toBeNull();
    expect(mockTemplates.calls).toBe(0);
    // Содержимое «Хода» не меняется: конвейер на месте.
    expect(findNodeByAttr(c, 'data-testid', 'progress-tab'), 'конвейер легаси-фичи').not.toBeNull();
  });

  it('предложение без подтверждения — блок подтверждения в шапке', async () => {
    routed({ workflow: null, workflowProposal: { templateId: 't', scope: 'global', skipped: [], rationale: 'потому что' } });
    const c = render({});
    await settle();
    // Предложение свёрнуто в строку шапки (F08): блок с обоснованием — после «Посмотреть и подтвердить».
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm')).toBeNull();
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', 'feature-page-route-proposal-action')!)!.onClick as () => void)(); });
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm')).not.toBeNull();
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm')!).join('')).toContain('потому что');
  });

  it('подтверждённый маршрут без перемен: блок молчит (тихий режим)', async () => {
    routed();
    const c = render({});
    await settle();
    expect(mockTemplates.calls).toBeGreaterThan(0);
    expect(findNodeByAttr(c, 'data-testid', 'feature-workflow-confirm')).toBeNull();
  });
});

// F08: страница без подтверждённого маршрута — граф предложения, строка «ждёт вас»,
// Notice «Разрез готов…», легаси-строка «Выбрать шаблон» и пустой граф.
describe('FeaturePage: состояния без подтверждённого маршрута (F08)', () => {
  const stage = (id: string, after: string[], column: number, proposed: boolean) =>
    ({ key: 'prod', id, role: 'step', title: `Этап ${id}`, after, column, state: 'pending', passes: 0, taskIds: [], rounds: [], proposed });
  const setRun = (epic: Record<string, unknown>, stages: unknown[], state = 'running') => {
    const run = {
      featureId: 'f1', state,
      epic: { id: 'f1', title: 'Моя фича', projectId: 'p1', ...epic },
      steps: [], reviews: [], qa: [], final: null, merge: null, stages,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    };
    mockGetFeature.result = pageData({ run: run as never });
  };
  const proposal = { templateId: 't', scope: 'global', skipped: [], rationale: 'потому что' };
  const proposedStages = () => [stage('a', [], 0, true), stage('b', ['a'], 1, true)];
  const has = (c: ReturnType<typeof render>, id: string) => findNodeByAttr(c, 'data-testid', id) !== null;
  const click = async (c: ReturnType<typeof render>, id: string) => {
    await act(async () => { (reactProps(findNodeByAttr(c, 'data-testid', id)!)!.onClick as () => void)(); });
  };

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    mockTemplates.calls = 0;
    mockGetFeature.pending = false;
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockGetFeature.perCall = [];
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('предложение: граф пунктиром, строка «ждёт вас», блок свёрнут; «Посмотреть и подтвердить» раскрывает его', async () => {
    setRun({ workflowProposal: proposal }, proposedStages());
    const c = render({});
    await settle();
    expect(has(c, 'feature-process-graph'), 'граф предложения').toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'pgraph-node-a')!.getAttribute('data-proposed')).toBe('true');
    const edge = findNodeByAttr(c, 'data-testid', 'pgraph-edge-a-b')!;
    expect(edge.getAttribute('stroke')).toBe(C.planBorder);
    expect(edge.getAttribute('stroke-dasharray')).toBe('5 4');
    const row = textNodes(findNodeByAttr(c, 'data-testid', 'feature-page-route-proposal')!).join('');
    expect(row).toContain('ждёт вас');
    expect(row).toContain('Модель предлагает «t»');
    expect(row).toContain('исполнители не стартуют');
    expect(has(c, 'feature-workflow-confirm'), 'блок свёрнут').toBe(false);
    await click(c, 'feature-page-route-proposal-action');
    expect(has(c, 'feature-workflow-confirm'), 'блок раскрыт').toBe(true);
    expect(has(c, 'feature-page-route-proposal'), 'строка заменена блоком').toBe(false);
  });

  it('предложение: вместо области этапа — Notice «Разрез готов…» и подсказка; «Подтвердить» раскрывает блок', async () => {
    setRun({ workflowProposal: proposal }, proposedStages());
    const c = render({});
    await settle();
    expect(has(c, 'feature-stage-area'), 'области этапа нет').toBe(false);
    const box = textNodes(findNodeByAttr(c, 'data-testid', 'feature-page-unconfirmed')!).join('');
    expect(box).toContain('Разрез готов, дальше работа стоит: исполнители не стартуют, пока маршрут не подтверждён.');
    expect(box).toContain('Детали этапов появятся после подтверждения маршрута.');
    await click(c, 'feature-page-unconfirmed-confirm');
    expect(has(c, 'feature-workflow-confirm')).toBe(true);
  });

  // R-F05: «Подтвердить» открывает шторку КАЖДЫЙ раз. Прежде флаг запроса оставался
  // поднятым, и второе нажатие не открывало шторку снова (мёртвая кнопка).
  // Тест — поведенческий (страница целиком), поэтому переживает любой способ починки.
  it('мобила: «Подтвердить» открывает шторку, и после её закрытия — снова', async () => {
    setRun({ workflowProposal: proposal }, proposedStages());
    const c = render({ isMobile: true });
    await settle();
    const body = () => (globalThis as any).document.body;
    const sheet = () => findNodeByAttr(body(), 'data-testid', 'workflow-route-sheet');
    expect(sheet(), 'шторка закрыта').toBeNull();
    await click(c, 'feature-page-unconfirmed-confirm');
    expect(sheet(), 'шторка открылась').not.toBeNull();
    // Закрываем шторку крестиком модалки.
    const close = findNodeByAttr(body(), 'aria-label', 'Закрыть');
    expect(close, 'крестик шторки').not.toBeNull();
    await act(async () => { (reactProps(close!)!.onClick as () => void)(); });
    expect(sheet(), 'шторка закрылась').toBeNull();
    // Повторное «Подтвердить» открывает шторку снова.
    await click(c, 'feature-page-unconfirmed-confirm');
    expect(sheet(), 'шторка открылась снова').not.toBeNull();
  });

  it('подтверждённый маршрут: Notice и граф предложения не рисуются, область этапа на месте', async () => {
    setRun({ workflow: { templateId: 't', scope: 'global', stages: [], skipped: [], confirmedVersion: 1 }, workflowProposal: proposal }, proposedStages().map(s => ({ ...s, proposed: false })));
    const c = render({});
    await settle();
    expect(has(c, 'feature-page-unconfirmed')).toBe(false);
    expect(has(c, 'feature-page-route-proposal')).toBe(false);
    expect(has(c, 'feature-stage-area')).toBe(true);
  });

  it('легаси: строка «старая фича» + «Выбрать шаблон», графа нет, каталог не грузится до клика', async () => {
    setRun({}, [stage('a', [], 0, false)]);
    const c = render({});
    await settle();
    const row = textNodes(findNodeByAttr(c, 'data-testid', 'feature-page-route-legacy')!).join('');
    expect(row).toContain('старая фича');
    expect(row).toContain('Создана до шаблонов: маршрута нет, замеры приблизительные');
    expect(has(c, 'feature-process-graph'), 'графа у легаси нет').toBe(false);
    expect(has(c, 'progress-tab'), 'прежний «Ход»').toBe(true);
    expect(mockTemplates.calls).toBe(0);
    await click(c, 'feature-page-route-legacy-action');
    expect(has(c, 'feature-workflow-confirm')).toBe(true);
    expect(mockTemplates.calls).toBeGreaterThan(0);
  });

  it('легаси влитой фичи: строки «Выбрать шаблон» нет', async () => {
    setRun({}, [stage('a', [], 0, false)], 'merged');
    const c = render({});
    await settle();
    expect(has(c, 'feature-page-route-legacy')).toBe(false);
  });

  it('режим маршрута без этапов: EmptyState «Этапов пока нет», ни графа, ни конвейера', async () => {
    setRun({ routeRequired: true }, []);
    const c = render({});
    await settle();
    const empty = textNodes(findNodeByAttr(c, 'data-testid', 'feature-page-empty-graph')!).join('');
    expect(empty).toContain('Этапов пока нет');
    expect(empty).toContain('Детали этапов появятся после подтверждения маршрута');
    expect(has(c, 'feature-process-graph')).toBe(false);
    expect(has(c, 'progress-tab')).toBe(false);
  });

  it('«модель не предложила» (state=none): тот же пустой граф, а не граф предложения', async () => {
    setRun({ workflowProposal: { ...proposal, state: 'none' } }, []);
    const c = render({});
    await settle();
    expect(has(c, 'feature-page-empty-graph')).toBe(true);
    expect(has(c, 'feature-page-unconfirmed')).toBe(false);
  });

  it('state=none, но этапы из задач есть (без after): не граф предложения, нет Notice «Разрез готов», «Ход» виден', async () => {
    setRun({ workflowProposal: { ...proposal, state: 'none' } }, [stage('a', [], 0, false)]);
    const c = render({});
    await settle();
    expect(has(c, 'feature-process-graph'), 'графа предложения нет').toBe(false);
    expect(has(c, 'feature-page-unconfirmed'), 'Notice «Разрез готов…» нет').toBe(false);
    expect(has(c, 'feature-page-unconfirmed-hint')).toBe(false);
    expect(has(c, 'progress-tab'), '«Ход» виден').toBe(true);
  });

  it('режим маршрута без предложения, но этапы из задач есть: «Ход» виден, «Этапов пока нет» не показываем', async () => {
    setRun({ routeRequired: true }, [stage('a', [], 0, false)]);
    const c = render({});
    await settle();
    expect(has(c, 'feature-page-empty-graph'), 'утверждение «Этапов пока нет» ложно').toBe(false);
    expect(has(c, 'progress-tab')).toBe(true);
  });
});

describe('hasWorkflowRoute: монтирование блока маршрута по маркеру режима (M5)', () => {
  const run = (epic: Record<string, unknown>) => ({ epic } as unknown as Parameters<typeof hasWorkflowRoute>[0]);

  it('routeRequired без предложения и маршрута — блок есть', () => {
    expect(hasWorkflowRoute(run({ routeRequired: true }))).toBe(true);
  });

  it('легаси-эпик (ни маркера, ни предложения, ни маршрута) — блока нет', () => {
    expect(hasWorkflowRoute(run({}))).toBe(false);
    expect(hasWorkflowRoute(run({ routeRequired: false }))).toBe(false);
    expect(hasWorkflowRoute(null)).toBe(false);
  });
});
