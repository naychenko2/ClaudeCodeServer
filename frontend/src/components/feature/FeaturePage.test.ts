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
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePage, type FeaturePageBackTarget } from './FeaturePage';
import { pluralTasks, featureRunStatusTag } from './FeaturePageHeader';
import { __test as pageTest, setFeaturePageTab } from '../../lib/featurePage';
import { __test as indexTest } from '../../lib/featuresIndex';
import { __test as backNavTest, recordBackNav, clearBackNav } from '../../lib/featureBackNav';
import { setAllFlags } from '../../lib/featureFlags';
import { KEY_SESSION_TOOLTIP } from '../../lib/chatGroups';
import { C, SHADOW } from '../../lib/design';
import { api } from '../../lib/api';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureIndexFeature, FeaturesIndex } from '../../types/featureIndex';
import type { FeaturePage as FeaturePageData } from '../../types/featureIndex';
import type { FeatureState } from '../../types/featureRun';
import type { Session, Task } from '../../types';

// Управляемый индекс фич (useFeaturesIndex → этот срез).
const mockIndex = vi.hoisted(() => ({
  features: [] as FeatureIndexFeature[],
  chats: {} as Record<string, import('../../types/featureIndex').FeatureChatLink[]>,
}));
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
// Управляемый ответ api.tasks.mergeFeature: calls — журнал (двойной клик → 1 вызов),
// pending (запрос в полёте → кнопка заблокирована), error (409/сбой, body.error — причина),
// result (успех → задача мержа).
const mockMergeFeature = vi.hoisted(() => ({
  pending: false,
  result: null as Task | null,
  error: null as (Error & { status?: number; body?: unknown }) | null,
  calls: [] as string[],
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
}));
// Журнал тостов (W4-HOLD-F2: текст 409 и успех — проверяются по нему).
const toasts = vi.hoisted(() => [] as [string, string | undefined][]);

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async (id: string) => mockChats.get(id) ?? null },
    tasks: {
      featuresIndex: async () => mockIndex,
      getFeature: (fid: string, eventsLimit?: string) => {
        mockGetFeature.calls.push({ fid, eventsLimit });
        if (mockGetFeature.pending) return new Promise<FeaturePageData>(() => {});
        const next = mockGetFeature.perCall.shift();
        if (next?.error) return Promise.reject(next.error);
        if (next?.result) return Promise.resolve(next.result);
        if (mockGetFeature.error) return Promise.reject(mockGetFeature.error);
        return Promise.resolve(mockGetFeature.result!);
      },
      mergeFeature: (fid: string) => {
        mockMergeFeature.calls.push(fid);
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
        return Promise.resolve({ resumed: mockHold.resumed, expired: mockHold.expired });
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
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { openChatCalls.push(id); return true; } }));
// Модели: вкладка «Статистика» (Breakdown) читает каталог — в тесте он пуст.
vi.mock('../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/models')>();
  return { ...orig, useModels: () => [], modelLabel: (v: string | null | undefined) => v ?? '' };
});

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

function render(props: { featureId?: string; projectId?: string | null; isMobile?: boolean; onBack?: (t: FeaturePageBackTarget) => void }) {
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
  const { container } = mount(createElement(FeaturePage, {
    featureId: props.featureId ?? 'f1',
    projectId: props.projectId ?? 'p1',
    isMobile: props.isMobile ?? false,
    onBack: props.onBack ?? (() => {}),
  }));
  return container;
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
    setAllFlags({ 'features-overview': true });
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
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
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-branch'), 'ветка в мета-строке (волна 3)').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-taskcount'), '«M задач» в мета-строке (волна 3)').not.toBeNull();
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
    const withData = () => {
      mockGetFeature.result = pageData({
        run: {
          featureId: 'f1', epic: { id: 'f1', title: 'Моя фича' },
          steps: [
            { id: 't-open', title: 'Открытая', status: 'inProgress', role: 'step', linkedSessionId: 'c-live' },
            { id: 't-done', title: 'Закрытая', status: 'done', role: 'step', linkedSessionId: 'c-arch' },
          ],
          reviews: [], qa: [], final: null, merge: null,
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
    };

    it('вкладка «События»: страница передаёт marks — чат c-arch «в архиве», задача t-done «закрыта»', async () => {
      withData();
      const c = render({});
      await settle();
      act(() => { setFeaturePageTab('events'); });
      expect(navFlags(c, 'event-row-')).toEqual([
        { chatArchived: true, taskClosed: true },   // accepted · t-done · c-arch (лента at desc)
        { chatArchived: false, taskClosed: false }, // started · t-open · c-live
      ]);
    });

    it('вкладка «Статистика»: «Дольше всех» помечен по данным страницы', async () => {
      withData();
      const c = render({});
      await settle();
      act(() => { setFeaturePageTab('stats'); });
      expect(navFlags(c, 'stats-longest')).toEqual([{ chatArchived: true, taskClosed: true }]);
    });

    it('вкладка «Задачи»: закрытая и архивная строки помечены', async () => {
      withData();
      const c = render({});
      await settle();
      act(() => { setFeaturePageTab('tasks'); });
      expect(navFlags(c, 'task-row-t-done')).toEqual([{ chatArchived: true, taskClosed: true }]);
      expect(navFlags(c, 'task-row-t-open')).toEqual([{ chatArchived: false, taskClosed: false }]);
    });

    // Вкладка запоминается при реальном клике из вкладки: чип в чате покажет её название.
    for (const [tab, rowPrefix, label, btnId] of [
      ['events', 'event-row-', 'События', 'nav-target-chat'],
      ['tasks', 'task-row-t-done', 'Задачи', 'nav-target-chat'],
      ['stats', 'stats-longest', 'Статистика', 'nav-target-chat'],
      // «Статистика»: кнопки задач под лентой времени (TaskButtons) — отдельный путь записи.
      ['stats', 'stats-tasks', 'Статистика', 'nav-target-task'],
    ] as const) {
      it(`клик по «${btnId === 'nav-target-chat' ? 'чат' : 'задача'}» на вкладке «${label}» (${rowPrefix}) → точка возврата помнит вкладку «${tab}»`, async () => {
        withData();
        const c = render({});
        await settle();
        act(() => { setFeaturePageTab(tab); });
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
        expect(back?.fromTab, `вкладка перехода (${label})`).toBe(tab);
        expect(back?.fromTitle).toBe('Моя фича');
      });
    }
  });

  // === 2в. Переключение вкладок Чаты/Задачи/События → свои компоненты (мутация: ComingSoon → red) ===
  it('переключение вкладок Чаты/Задачи/События показывает свои компоненты (мутация → ComingSoon → red)', async () => {
    mockGetFeature.result = pageData({
      run: {
        featureId: 'f1',
        epic: { id: 'f1', title: 'Моя фича' },
        steps: [{ id: 's1', title: 'Шаг 1', status: 'inProgress' }],
        reviews: [], qa: [], final: null, merge: null,
        verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
        progress: 0, ready: false,
      },
      chats: [
        { id: 'c1', kind: 'curator', taskId: null, archived: false, personaId: 'p-kira', taskRole: null, stepRef: null, lane: null, modelLabel: null, modelFull: null },
      ],
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 's1', role: null, stepRef: null, personaId: null, sessionId: null, round: null, reason: null },
      ],
      eventsTotal: 1,
    });
    const c = render({});
    await settle();

    // «Чаты» → FeatureChatsTab (data-testid="chats"), заглушка «Скоро» не рисуется.
    act(() => { setFeaturePageTab('chats'); });
    expect(findNodeByAttr(c, 'data-testid', 'chats'), 'вкладка «Чаты» → FeatureChatsTab').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-tab-stub'), '«Чаты»: не ComingSoon').toBeNull();

    // «Задачи» → FeatureTasksTab (data-testid="tasks").
    act(() => { setFeaturePageTab('tasks'); });
    expect(findNodeByAttr(c, 'data-testid', 'tasks'), 'вкладка «Задачи» → FeatureTasksTab').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-tab-stub'), '«Задачи»: не ComingSoon').toBeNull();

    // «События» → FeatureEventsTab (data-testid="events").
    act(() => { setFeaturePageTab('events'); });
    expect(findNodeByAttr(c, 'data-testid', 'events'), 'вкладка «События» → FeatureEventsTab').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-tab-stub'), '«События»: не ComingSoon').toBeNull();
  });

  // === 2г. Счётчики вкладок = честные числа (мутация: обнулить → red) ===
  // «События» (major 2): счётчик = ИСТОЧНИК «что реально доступно» = events.length
  // (загружено), НЕ eventsTotal (общее до обрезки бэком). В фикстуре значения
  // РАЗВЕДЕНЫ (eventsTotal=1000 ≠ events.length=3): мутация events.length→eventsTotal
  // (и обратная) красит — счётчик стал бы 1000, а не 3.
  it('счётчики вкладок Чаты/Задачи/События = честные числа (мутация: обнулить → red)', async () => {
    mockGetFeature.result = pageData({
      run: {
        featureId: 'f1',
        epic: { id: 'f1', title: 'Моя фича' },
        steps: [{ id: 's1', title: 'Шаг 1', status: 'inProgress' }],
        reviews: [], qa: [], final: null, merge: null,
        verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
        progress: 0, ready: false,
      },
      chats: [
        { id: 'c1', kind: 'curator', taskId: null, archived: false, personaId: 'p-kira', taskRole: null, stepRef: null, lane: null, modelLabel: null, modelFull: null },
        { id: 'c2', kind: 'executor', taskId: null, archived: false, personaId: 'p-denis', taskRole: null, stepRef: null, lane: null, modelLabel: null, modelFull: null },
      ],
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 's1', role: null, stepRef: null, personaId: null, sessionId: null, round: null, reason: null },
        { at: '2026-10-19T11:00:00Z', kind: 'started', taskId: 's1', role: null, stepRef: null, personaId: null, sessionId: null, round: null, reason: null },
        { at: '2026-10-19T12:00:00Z', kind: 'started', taskId: 's1', role: null, stepRef: null, personaId: null, sessionId: null, round: null, reason: null },
      ],
      // eventsTotal ≠ events.length: счётчик «События» обязан брать events.length (3),
      // не eventsTotal (1000) — иначе мутация источника была бы незаметна.
      eventsTotal: 1000,
    });
    const c = render({});
    await settle();

    // «Чаты» = 2 (только живые, не архивные).
    const chatsTab = findTab(c, 'Чаты');
    expect(chatsTab, 'вкладка «Чаты»').not.toBeNull();
    expect(textNodes(chatsTab!).join(''), 'счётчик «Чаты» = 2').toContain('2');
    // «Задачи» = 2 (эпик + шаг).
    const tasksTab = findTab(c, 'Задачи');
    expect(tasksTab, 'вкладка «Задачи»').not.toBeNull();
    expect(textNodes(tasksTab!).join(''), 'счётчик «Задачи» = 2 (эпик + шаг)').toContain('2');
    // «События» = 3 = events.length (загружено), НЕ eventsTotal=1000 (общее до обрезки).
    // Мутация events.length→eventsTotal → счётчик 1000 → «3» не найдётся → red.
    const eventsTab = findTab(c, 'События');
    expect(eventsTab, 'вкладка «События»').not.toBeNull();
    const eventsText = textNodes(eventsTab!).join('');
    expect(eventsText, 'счётчик «События» = events.length = 3').toContain('3');
    expect(eventsText, 'счётчик «События» ≠ eventsTotal=1000').not.toContain('1000');
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

  // === 5. error: Notice «Не удалось загрузить фичу.» + «Повторить» ===
  it('error: Notice «Не удалось загрузить фичу.» + кнопка «Повторить»', async () => {
    mockGetFeature.error = { name: 'HttpError', status: 500 } as Error & { status?: number };
    const c = render({});
    await settle();
    const all = textNodes(c).join('');
    expect(all).toContain('Не удалось загрузить фичу.');
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



  // === Ф2.5: «Показать все» — догрузка ВСЕЙ ленты (eventsLimit=all) ===
  // Связка «кнопка «Показать все» → стор → запрос»: БЕЗ мока expandFeaturePageEvents
  // (реальный стор lib/featurePage). Клик по кнопке во вкладке «События» →
  // expandFeaturePageEvents → fetchPage → getFeature(fid, 'all').
  // Мутация (в отчёте): getFeature(fid, undefined, signal) (догрузка eventsLimit=all
  // отключена) → последний вызов без eventsLimit='all' → red.
  it('Ф2.5: «Показать все» перезапрашивает getFeature с eventsLimit=all', async () => {
    // Лента обрезана: 10 событий (порог «Показать все» = 8), всего 1000.
    const events: import('../../types/featureIndex').FeatureEvent[] = [];
    for (let i = 0; i < 10; i++) {
      events.push({ at: `2026-10-19T${String(10 + i).padStart(2, '0')}:00:00Z`, kind: 'started', taskId: `t${i}`, personaId: null, sessionId: null, round: null, reason: null });
    }
    mockGetFeature.result = pageData({ events, eventsTotal: 1000 });
    const c = render({});
    await settle();
    // Вкладка «События» (кнопка «Показать все» — только там).
    act(() => { setFeaturePageTab('events'); });
    // Кнопка «Показать все · 1000» есть (лента > 8).
    const showAll = findNodeByAttr(c, 'data-testid', 'events-show-all');
    expect(showAll, 'кнопка «Показать все»').not.toBeNull();
    // Клик → догрузка всей ленты (eventsLimit=all).
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    // Запрос с eventsLimit='all' (последний вызов).
    const lastCall = mockGetFeature.calls[mockGetFeature.calls.length - 1];
    expect(lastCall, 'догрузка = запрос с eventsLimit').toBeDefined();
    expect(lastCall!.eventsLimit, 'догрузка = eventsLimit=all').toBe('all');
  });

  // м.2: повторное раскрытие после сворачивания — лента раскрывается (все строки) БЕЗ
  // доп. GET (кэш УСПЕШНОЙ догрузки, _eventsLoaded). Мутации (в отчёте):
  //   (a) вернуть гард `if (_eventsLimit === 'all') return` (выход ДО `_eventsExpanded=true`)
  //       → лента остаётся свёрнутой (8 строк) → «все 10» краснеет;
  //   (b) убрать `_eventsExpanded = true` из ветки «уже загружено» → то же (8 строк) → red.
  it('м.2: «Свернуть» → «Показать все» повторно — лента раскрыта (все строки), без доп. GET', async () => {
    const events: import('../../types/featureIndex').FeatureEvent[] = [];
    for (let i = 0; i < 10; i++) {
      events.push({ at: `2026-10-19T${String(10 + i).padStart(2, '0')}:00:00Z`, kind: 'started', taskId: `t${i}`, personaId: null, sessionId: null, round: null, reason: null });
    }
    mockGetFeature.result = pageData({ events, eventsTotal: 1000 });
    const c = render({});
    await settle();
    act(() => { setFeaturePageTab('events'); });
    const showAll = findNodeByAttr(c, 'data-testid', 'events-show-all')!;
    // Свёрнуто: 8 последних (порог «Показать все» = 8).
    expect(countEventRows(c), 'свёрнутая лента = 8').toBe(8);
    // 1-й раз: догрузка (GET #2, eventsLimit=all) → лента раскрыта (все 10).
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    expect(countEventRows(c), 'после «Показать все» — все 10').toBe(10);
    // «Свернуть» → 8; «Показать все» (повторно) → кэш: все 10 БЕЗ нового GET.
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    expect(countEventRows(c), 'после «Свернуть» — 8').toBe(8);
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    // Повторное раскрытие: ВСЕ 10 строк видны (не 8) — мутация (a)/(b) красит.
    expect(countEventRows(c), 'повторное «Показать все» — все 10 (не 8)').toBe(10);
    // GET: #1 (initial) + #2 (expand) = 2; повторный expand — без #3.
    expect(mockGetFeature.calls.length, 'повторное раскрытие без доп. GET (кэш)').toBe(2);
  });

  // м.3: раскрытие в сторе — переживает смену вкладки (локальный useState сбрасывался).
  // Мутация (в отчёте): вернуть expanded в локальный useState → red.
  it('м.3: раскрытие ленты переживает смену вкладки (стор, не локальный state)', async () => {
    const events: import('../../types/featureIndex').FeatureEvent[] = [];
    for (let i = 0; i < 10; i++) {
      events.push({ at: `2026-10-19T${String(10 + i).padStart(2, '0')}:00:00Z`, kind: 'started', taskId: `t${i}`, personaId: null, sessionId: null, round: null, reason: null });
    }
    mockGetFeature.result = pageData({ events, eventsTotal: 1000 });
    const c = render({});
    await settle();
    act(() => { setFeaturePageTab('events'); });
    const showAll = findNodeByAttr(c, 'data-testid', 'events-show-all')!;
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    // Смена вкладки → возврат: раскрытие сохранилось (кнопка «Свернуть»).
    act(() => { setFeaturePageTab('timeline'); });
    act(() => { setFeaturePageTab('events'); });
    const btn = findNodeByAttr(c, 'data-testid', 'events-show-all')!;
    expect(textNodes(btn).join(''), 'раскрытие переживает смену вкладки').toContain('Свернуть');
  });

  // Сценарий на живом сторе (не мок вкладки): упавшая догрузка → страница жива, вкладки
  // на месте, локальная ошибка «Не удалось догрузить» → клик «Повторить» → ВТОРОЙ запрос
  // с eventsLimit='all' → лента раскрылась, ошибки нет. perCall: call1=initial ok,
  // call2=expand 500, call3=retry ok. Мутации (в отчёте):
  //   (a) вернуть гард `if (_eventsLimit === 'all') return` → «Повторить» не уходит в GET
  //       (calls=2, не 3) → red;
  //   (b) убрать очистку ошибки (не сбрасывать _expandError успешной догрузкой) →
  //       «events-expand-error» остаётся → red.
  it('сценарий: догрузка упала → «Повторить» → 2-й запрос eventsLimit=all → лента раскрыта, ошибки нет', async () => {
    const events: import('../../types/featureIndex').FeatureEvent[] = [];
    for (let i = 0; i < 10; i++) {
      events.push({ at: `2026-10-19T${String(10 + i).padStart(2, '0')}:00:00Z`, kind: 'started', taskId: `t${i}`, personaId: null, sessionId: null, round: null, reason: null });
    }
    const full = pageData({ events, eventsTotal: 1000 });
    mockGetFeature.perCall = [
      { result: full },                                        // call 1: initial load (ok)
      { error: { name: 'HttpError', status: 500 } as Error & { status?: number } }, // call 2: expand (500)
      { result: full },                                        // call 3: retry (ok)
    ];
    const c = render({});
    await settle();
    act(() => { setFeaturePageTab('events'); });
    const showAll = findNodeByAttr(c, 'data-testid', 'events-show-all')!;
    // Клик «Показать все» → догрузка (call 2) → 500 → локальная ошибка (страница не роняется).
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    // Страница жива, вкладки на месте, локальная ошибка «Не удалось догрузить».
    expect(findNodeByAttr(c, 'data-testid', 'feature-page'), 'страница жива после упавшей догрузки').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-tabs'), 'вкладки на месте').not.toBeNull();
    const err = findNodeByAttr(c, 'data-testid', 'events-expand-error');
    expect(err, 'локальная ошибка догрузки').not.toBeNull();
    expect(textNodes(err!).join('')).toContain('Не удалось догрузить');
    // Клик «Повторить» → call 3 (eventsLimit=all) → успех → лента раскрыта, ошибки нет.
    const retry = findNodeByAttr(c, 'data-testid', 'events-expand-retry')!;
    act(() => { (reactProps(retry)!.onClick as () => void)(); });
    await settle();
    // Второй запрос — с eventsLimit='all' (call 3).
    const lastCall = mockGetFeature.calls[mockGetFeature.calls.length - 1];
    expect(lastCall.eventsLimit, 'повтор = eventsLimit=all').toBe('all');
    // Лента раскрыта (все 10) и локальной ошибки нет.
    expect(countEventRows(c), 'после повтора лента раскрыта (все 10)').toBe(10);
    expect(findNodeByAttr(c, 'data-testid', 'events-expand-error'), 'ошибка снята после повтора').toBeNull();
  });

  // Минор 1 (уровень страницы): метка «Показать все · 1000» = eventsTotal (общее до
  // обрезки), НЕ events.length (загружено). Мутация (в отчёте): FeaturePage.tsx
  // `eventsTotal={page.page.events.length}` → метка «Показать все · 10» → red.
  it('минор 1: метка «Показать все · 1000» = eventsTotal (не events.length)', async () => {
    const events: import('../../types/featureIndex').FeatureEvent[] = [];
    for (let i = 0; i < 10; i++) {
      events.push({ at: `2026-10-19T${String(10 + i).padStart(2, '0')}:00:00Z`, kind: 'started', taskId: `t${i}`, personaId: null, sessionId: null, round: null, reason: null });
    }
    mockGetFeature.result = pageData({ events, eventsTotal: 1000 });
    const c = render({});
    await settle();
    act(() => { setFeaturePageTab('events'); });
    const showAll = findNodeByAttr(c, 'data-testid', 'events-show-all')!;
    // events.length=10, eventsTotal=1000: метка — ОБЩЕЕ число (1000), не длина ленты (10).
    expect(textNodes(showAll).join(''), 'метка «Показать все · 1000»').toBe('Показать все · 1000');
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


// === W4-1F: чип «ведёт ·» в мета-строке шапки (макет pt1) ===
// Чип — сам вход в ключевой чат: клик открывает keySessionId. Мутация (в отчёте):
// убрать openChatById в обработчике чипа → вызов не попадёт в журнал → тест краснеет.
describe('W4-1F: чип «ведёт ·» в шапке фичи', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    setAllFlags({ 'features-overview': true });
    personaMap.set('p-sofya', 'Софья (локальная)');
    mockIndex.features = [feat({ orchestratorPersonaId: 'p-sofya' })];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
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

  it('клик по чипу открывает keySessionId; на чипе — «ведёт» и имя', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead');
    expect(chip, 'чип «ведёт» в мета-строке').not.toBeNull();
    const text = textNodes(chip!).join('');
    expect(text, 'подпись «ведёт» и имя оркестратора').toContain('ведёт');
    expect(text, 'имя оркестратора').toContain('Софья (локальная)');
    await act(async () => { (reactProps(chip)!.onClick as () => void)(); });
    expect(openChatCalls, 'клик ведёт в keySessionId').toEqual(['chat-key']);
  });

  it('keyArchived: чип помечен «в архиве»', async () => {
    reindex({ keyArchived: true });
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    expect(textNodes(chip).join(''), 'пометка «в архиве»').toContain('в архиве');
  });

  it('чата нет, персона есть: чип без перехода (не кнопка, нет onClick)', async () => {
    reindex({ keySessionId: null });
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead');
    expect(chip, 'статичный чип (без ключевого чата)').not.toBeNull();
    expect(chip!.nodeName, 'не кнопка — перехода нет').not.toBe('BUTTON');
    expect(reactProps(chip)?.onClick, 'у статичного чипа нет обработчика клика').toBeUndefined();
  });

  it('персоны нет: элемента нет', async () => {
    mockIndex.features = [feat()]; // дефолт без orchestratorPersonaId
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
    const c = render({});
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-lead'), 'без персоны — чипа нет').toBeNull();
  });

  // Состояния чипа (хвосты R-1F-R): контрол — ui.Button, состояния — React-состояние
  // LeadChip, обработчики на обёртке (ui.Button их не форвардит, прецедент
  // TaskCreatedView). Мутации (в отчёте): убрать onMouseDown на обёртке → тест
  // нажатия краснеет; убрать boxShadow/рамку фокуса в стиле → тест фокуса краснеет.
  it('наведение: фон и рамка меняются, с наведения — обратно', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    expect(chip.nodeName, 'контрол — кнопка из ui.Button').toBe('BUTTON');
    expect(chip.style.background, 'базовый фон чипа').toBe(C.bgWhite);
    const wrap = reactProps(chip!.parentNode as never) ?? {};
    const enter = wrap.onMouseEnter as (() => void) | undefined;
    const leave = wrap.onMouseLeave as (() => void) | undefined;
    expect(enter && leave, 'на обёртке обработчики наведения').toBeTruthy();
    act(() => { enter!(); });
    expect(chip.style.background, 'фон на наведении').toBe(C.bgSelected);
    expect(chip.style.borderColor, 'рамка на наведении').toBe(C.accentMuted);
    act(() => { leave!(); });
    expect(chip.style.background, 'фон возвращается после наведения').toBe(C.bgWhite);
    expect(chip.style.borderColor, 'рамка возвращается после наведения').toBe(C.border);
  });

  it('нажатие: фон accentLight, после отпускания — снова фон наведения', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    const wrap = reactProps(chip!.parentNode as never) ?? {};
    const enter = wrap.onMouseEnter as (() => void) | undefined;
    const down = wrap.onMouseDown as (() => void) | undefined;
    const up = wrap.onMouseUp as (() => void) | undefined;
    expect(down && up, 'на обёртке обработчики нажатия').toBeTruthy();
    act(() => { enter!(); });
    act(() => { down!(); });
    expect(chip.style.background, 'фон на нажатии').toBe(C.accentLight);
    act(() => { up!(); });
    expect(chip.style.background, 'после отпускания — фон наведения').toBe(C.bgSelected);
  });

  it('фокус: кольцо SHADOW.focus + accent-рамка (макет pt1); blur — обратно', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    const wrap = reactProps(chip!.parentNode as never) ?? {};
    const focus = wrap.onFocus as (() => void) | undefined;
    const blur = wrap.onBlur as (() => void) | undefined;
    expect(focus && blur, 'на обёртке обработчики фокуса').toBeTruthy();
    act(() => { focus!(); });
    expect(chip.style.boxShadow, 'фокус — кольцо').toBe(SHADOW.focus);
    expect(chip.style.borderColor, 'фокус — accent-рамка').toBe(C.accent);
    act(() => { blur!(); });
    expect(chip.style.boxShadow, 'blur — без кольца').toBe('none');
    expect(chip.style.borderColor, 'blur — базовая рамка').toBe(C.border);
  });

  // Реактивность стора персон (возврат R-2): getPersonaById сам по себе не реактивен
  // (lib/personas.ts:75) — чип подписывается на версию стора и просит загрузку.
  // Мутация (в отчёте): убрать usePersonasVersion() в LeadChip → подъём версии не
  // перерисовывает чип → тест краснеет.
  it('стор персон не догружен: чипа нет; после догрузки — появляется', async () => {
    personaMap.clear(); // оркестратор в индексе есть, в сторе — пока нет
    const c = render({});
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-lead'), 'пока стор не догружен — чипа нет').toBeNull();
    expect(personaStore.ensureCalls, 'чип запросил загрузку стора персон').toBeGreaterThan(0);
    act(() => {
      personaMap.set('p-sofya', 'Софья (локальная)');
      personaStore.version++;
      for (const fn of [...personaStore.listeners]) fn();
    });
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead');
    expect(chip, 'чип появляется после догрузки стора').not.toBeNull();
    expect(textNodes(chip!).join(''), 'имя оркестратора после догрузки').toContain('Софья (локальная)');
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

// Минор 5: склонение «задача/задачи/задач» (FeaturePageHeader.pluralTasks).
// Мутация (в отчёте): сломать любое правило (11→задача, 21→задачи) → red.
describe('pluralTasks: склонение «задача/задачи/задач»', () => {
  it('1 задача / 2 задачи / 5 задач / 11 задач / 21 задача', () => {
    expect(pluralTasks(1)).toBe('задача');
    expect(pluralTasks(2)).toBe('задачи');
    expect(pluralTasks(5)).toBe('задач');
    expect(pluralTasks(11)).toBe('задач');
    expect(pluralTasks(21)).toBe('задача');
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
    setAllFlags({ 'features-overview': true });
    personaMap.set('p-kira', 'Кира');
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
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

  it('360: кнопки «Пауза» в ряду нет — первый пункт шторки ⋯ с подсказкой', async () => {
    holdPage('running', { now: [{ taskId: 't1', stage: 'prod', personaId: 'p-kira', stepRef: 'W2-A', title: 'Шаг' }] });
    const c = render({ isMobile: true });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-pause'), 'на мобиле кнопки в ряду нет').toBeNull();
    const trigger = findNodeByAttr(c, 'aria-label', 'Ещё');
    expect(trigger, 'триггер ⋯').not.toBeNull();
    await act(async () => { (reactProps(trigger)!.onClick as () => void)(); });
    const item = findNodeByAttr((globalThis as any).document.body, 'role', 'menuitem');
    expect(item, 'пункт шторки').not.toBeNull();
    const itemText = textNodes(item!).join('');
    expect(itemText).toContain('Поставить на паузу');
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
  });
});
