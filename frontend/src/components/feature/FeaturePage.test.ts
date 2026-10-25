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
import { setMeFromServer, clearMe } from '../../lib/defaultPersona';
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
  const { container } = mount(createElement(FeaturePage, {
    featureId: props.featureId ?? 'f1',
    projectId: props.projectId ?? 'p1',
    projectOwnerId: props.projectOwnerId ?? null,
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
      expect(navFlags(c, 'loss-longest-nav')).toEqual([{ chatArchived: true, taskClosed: true }]);
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
      ['stats', 'loss-longest-nav', 'Статистика', 'nav-target-chat'],
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

  // Клик чипа — переход с записью возврата (плашка Ф9b): recordFeatureReturn пишет
  // указатель {from: фича, to: ключевой чат}. Мутация: убрать вызов recordFeatureReturn
  // в onClick → getBackNav() пуст → тест краснеет (хвост R-1F-T, п.3).
  it('клик по чипу открывает keySessionId и записывает возврат к фиче; на чипе — «ведёт» и имя', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead');
    expect(chip, 'чип «ведёт» в мета-строке').not.toBeNull();
    const text = textNodes(chip!).join('');
    expect(text, 'подпись «ведёт» и имя оркестратора').toContain('ведёт');
    expect(text, 'имя оркестратора').toContain('Софья (локальная)');
    await act(async () => { (reactProps(chip)!.onClick as () => void)(); });
    expect(openChatCalls, 'клик ведёт в keySessionId').toEqual(['chat-key']);
    const nav = backNavTest.getBackNav();
    expect(nav?.from, 'запись возврата — из фичы f1').toBe('f1');
    expect(nav?.fromKind, 'дискриминатор — feature').toBe('feature');
    expect(nav?.to, 'цель возврата — ключевой чат').toEqual({ kind: 'chat', id: 'chat-key' });
    expect(nav?.fromTitle, 'название фичи для плашки').toBe('Моя фича');
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

  // Состояния чипа (хвосты R-1F-R/R-1F-T): контрол — ui.Button, состояния —
  // React-состояние LeadChip, обработчики на обёртке (ui.Button их не форвардит,
  // прецедент TaskCreatedView). Нажатие — pointer-события (тач и мышь дают их
  // одинаково), фокус — :focus-visible (кольцо только с клавиатуры), глиф-стрелка
  // красится только по наведению. Мутации (в отчёте): убрать onPointerDown на
  // обёртке → тест нажатия краснеет; вернуть setFocused(true) без :focus-visible →
  // тест фокуса краснеет; убрать recordFeatureReturn в onClick → тест записи возврата
  // краснеет; убрать textOverflow у спана имени → тест ellipsis краснеет.
  it('наведение: фон и рамка меняются, глиф красится только здесь; с наведения — обратно', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    const icon = findNodeByAttr(c, 'data-testid', 'feature-page-lead-icon')!;
    expect(chip.nodeName, 'контрол — кнопка из ui.Button').toBe('BUTTON');
    expect(chip.style.background, 'базовый фон чипа').toBe(C.bgWhite);
    expect(icon.style.color, 'глиф без наведения — тихий').toBe(C.textMuted);
    const wrap = reactProps(chip!.parentNode as never) ?? {};
    const enter = wrap.onMouseEnter as (() => void) | undefined;
    const leave = wrap.onMouseLeave as (() => void) | undefined;
    expect(enter && leave, 'на обёртке обработчики наведения').toBeTruthy();
    act(() => { enter!(); });
    expect(chip.style.background, 'фон на наведении').toBe(C.bgSelected);
    expect(chip.style.borderColor, 'рамка на наведении').toBe(C.accentMuted);
    expect(icon.style.color, 'глиф на наведении — accent').toBe(C.accent);
    act(() => { leave!(); });
    expect(chip.style.background, 'фон возвращается после наведения').toBe(C.bgWhite);
    expect(chip.style.borderColor, 'рамка возвращается после наведения').toBe(C.border);
    expect(icon.style.color, 'глиф после наведения — тихий').toBe(C.textMuted);
  });

  it('нажатие: фон accentLight; отпускание и pointercancel — снова фон наведения', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    const wrap = reactProps(chip!.parentNode as never) ?? {};
    const enter = wrap.onMouseEnter as (() => void) | undefined;
    const down = wrap.onPointerDown as (() => void) | undefined;
    const up = wrap.onPointerUp as (() => void) | undefined;
    const cancel = wrap.onPointerCancel as (() => void) | undefined;
    expect(down && up && cancel, 'нажатие — по pointer-событиям (не mouse)').toBeTruthy();
    act(() => { enter!(); });
    act(() => { down!(); });
    expect(chip.style.background, 'фон на нажатии').toBe(C.accentLight);
    act(() => { up!(); });
    expect(chip.style.background, 'после отпускания — фон наведения').toBe(C.bgSelected);
    // Сход пальца (pointercancel без pointerup): нажатие должно сниматься.
    act(() => { down!(); });
    act(() => { cancel!(); });
    expect(chip.style.background, 'pointercancel — нажатие снято').toBe(C.bgSelected);
  });

  it('фокус с клавиатуры: кольцо и accent-рамка; фокус после мыши (:focus-visible false) — нет', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    const icon = findNodeByAttr(c, 'data-testid', 'feature-page-lead-icon')!;
    const wrapNode = chip!.parentNode as unknown as { focusVisible: boolean };
    const wrap = reactProps(wrapNode as never) ?? {};
    const focus = wrap.onFocus as ((e: unknown) => void) | undefined;
    const blur = wrap.onBlur as (() => void) | undefined;
    expect(focus && blur, 'на обёртке обработчики фокуса').toBeTruthy();
    // Tab: :focus-visible = true → кольцо + accent-рамка (макет pt1).
    wrapNode.focusVisible = true;
    act(() => { focus!({ currentTarget: wrapNode }); });
    expect(chip.style.boxShadow, 'фокус с клавиатуры — кольцо').toBe(SHADOW.focus);
    expect(chip.style.borderColor, 'фокус с клавиатуры — accent-рамка').toBe(C.accent);
    expect(icon.style.color, 'глиф по фокусу не красится (только по наведению)').toBe(C.textMuted);
    // Мышь/тап: :focus-visible = false → рамка и кольцо не залипают (хвост R-1F-T).
    act(() => { blur!(); });
    wrapNode.focusVisible = false;
    act(() => { focus!({ currentTarget: wrapNode }); });
    expect(chip.style.boxShadow, 'фокус после мыши — без кольца').toBe('none');
    expect(chip.style.borderColor, 'фокус после мыши — базовая рамка').toBe(C.border);
    act(() => { blur!(); });
    expect(chip.style.boxShadow, 'blur — без кольца').toBe('none');
    expect(chip.style.borderColor, 'blur — базовая рамка').toBe(C.border);
  });

  it('длинное имя: спан имени с ellipsis (чип не лезет за 360)', async () => {
    const c = render({});
    await settle();
    const chip = findNodeByAttr(c, 'data-testid', 'feature-page-lead')!;
    const nameSpan = (chip!.childNodes as FNodeLike[]).find((n) => textNodes(n as never).join('') === 'Софья (локальная)');
    expect(nameSpan, 'спан с именем оркестратора').not.toBeUndefined();
    expect(nameSpan!.style.textOverflow, 'имя реется многоточием').toBe('ellipsis');
    expect(nameSpan!.style.overflow, 'overflow скрыт').toBe('hidden');
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
describe('F2: кнопка «Закрыть фичу» в шапке', () => {
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

  // Все пункты шторки ⋯ (их несколько: пауза, ветка, эпик, закрытие).
  function menuItems(): AnyNode[] {
    const out: AnyNode[] = [];
    const walk = (x: AnyNode) => {
      if ((x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('role') === 'menuitem') out.push(x);
      for (const ch of x.childNodes) walk(ch);
    };
    walk((globalThis as any).document.body as unknown as AnyNode);
    return out;
  }

  it('владелец: кнопка есть; клик — подтверждение; «Отмена» — запроса нет', async () => {
    openPage();
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn');
    expect(btn, 'кнопка «Закрыть фичу» в тулбаре').not.toBeNull();
    expect(textNodes(btn!).join('')).toContain('Закрыть фичу');
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
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
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn')!;
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
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
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'не владельцу кнопки нет').toBeNull();
  });

  // Фолбэка «поля нет — владелец по умолчанию» нет: бэк проставляет ownerId всем
  // проектам (миграция ProjectManager), а для разрушительного действия отсутствие
  // данных — не разрешение, а неизвестность.
  it('без ownerId: кнопки нет (нет данных ≠ разрешение)', async () => {
    openPage();
    owner();
    const c = render({});
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'неизвестный владелец — не показываем').toBeNull();
  });

  it('уже закрыта (эпик done): кнопки нет; тег — «закрыта без мержа»', async () => {
    const pd = pageData();
    pd.run = { ...pd.run, state: 'closed', epic: { id: 'f1', title: 'Моя фича', status: 'done' } } as never;
    mockGetFeature.result = pd;
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'у закрытой фичи кнопки нет').toBeNull();
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(textNodes(tag!).join(''), 'состояние страницы — «закрыта»').toContain('закрыта');
  });

  it('360: кнопки в ряду нет — пункт «Закрыть фичу…» в шторке ⋯', async () => {
    openPage();
    owner();
    const c = render({ isMobile: true, projectOwnerId: 'u-own' });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'на мобиле кнопки в ряду нет').toBeNull();
    const trigger = findNodeByAttr(c, 'aria-label', 'Ещё');
    expect(trigger, 'триггер ⋯').not.toBeNull();
    await act(async () => { (reactProps(trigger)!.onClick as () => void)(); });
    const closeItem = menuItems().find(x => textNodes(x as never).join('').includes('Закрыть фичу'));
    expect(closeItem, 'пункт «Закрыть фичу…»').not.toBeNull();
    await act(async () => { (reactProps(closeItem)!.onClick as () => void)(); });
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
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn')!;
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
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
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn')!;
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
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
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn')!;
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
    const warn = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-running');
    expect(warn, 'строка предупреждения').not.toBeNull();
    expect(textNodes(warn!).join(''), 'текст про один шаг').toContain('Идет 1 шаг — он не остановится, фича перейдёт в «Закрытые»');
    const cancel = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-cancel')!;
    await act(async () => { (reactProps(cancel)!.onClick as () => void)(); });
  });

  it('запрос в полёте: кнопка тулбара disabled и «Закрываю…» (loading)', async () => {
    openPage();
    mockUpdateTask.pending = true; // запрос не разрешается — состояние «в полёте» держится
    owner();
    const c = render({ projectOwnerId: 'u-own' });
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn')!;
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
    const confirm = findNodeByAttr((globalThis as any).document.body, 'data-testid', 'feature-page-close-confirm')!;
    await act(async () => { (reactProps(confirm)!.onClick as () => void)(); });
    const props = reactProps(btn) as { disabled?: boolean };
    expect(props.disabled, 'хвост 2: индикация loading на кнопке').toBe(true);
    expect(textNodes(btn).join(''), 'текст в полёте').toContain('Закрываю…');
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
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn')!;
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
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
    await act(async () => { (reactProps(btn)!.onClick as () => void)(); });
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
    setAllFlags({ 'features-overview': true });
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    openChatCalls.length = 0;
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
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), 'у закрытой фичи «Закрыть» нет').toBeNull();
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
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-close-btn'), '«Закрыть» на открытой — есть').not.toBeNull();
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
    const closeBtn = findNodeByAttr(c, 'data-testid', 'feature-page-close-btn');
    expect(closeBtn, 'после возобновления снова «Закрыть фичу»').not.toBeNull();
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
