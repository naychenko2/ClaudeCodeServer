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

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePage, type FeaturePageBackTarget } from './FeaturePage';
import { featureTaskTotal, taskCounters } from './featurePageBody';
import { __test as pageTest, setFeaturePageTab } from '../../lib/featurePage';
import { __test as indexTest } from '../../lib/featuresIndex';
import { __test as backNavTest, recordBackNav, clearBackNav } from '../../lib/featureBackNav';
import { setAllFlags } from '../../lib/featureFlags';
import { KEY_SESSION_TOOLTIP } from '../../lib/chatGroups';
import { api } from '../../lib/api';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureIndexFeature, FeaturesIndex } from '../../types/featureIndex';
import type { FeaturePage as FeaturePageData } from '../../types/featureIndex';
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
const mockGetFeature = vi.hoisted(() => ({
  pending: false,
  result: null as FeaturePageData | null,
  error: null as (Error & { status?: number }) | null,
  calls: [] as { fid: string; eventsLimit?: string }[],
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

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async (id: string) => mockChats.get(id) ?? null },
    tasks: {
      featuresIndex: async () => mockIndex,
      getFeature: (fid: string, eventsLimit?: string) => {
        mockGetFeature.calls.push({ fid, eventsLimit });
        if (mockGetFeature.pending) return new Promise<FeaturePageData>(() => {});
        if (mockGetFeature.error) return Promise.reject(mockGetFeature.error);
        return Promise.resolve(mockGetFeature.result!);
      },
      mergeFeature: (fid: string) => {
        mockMergeFeature.calls.push(fid);
        if (mockMergeFeature.pending) return new Promise<Task>(() => {});
        if (mockMergeFeature.error) return Promise.reject(mockMergeFeature.error);
        return Promise.resolve(mockMergeFeature.result!);
      },
    },
  },
}));
// Стор персон (lib/personas) — мок: hoisted-карта id→имя (аватары 18 в строках
// чатов/шагов, таблица §5.5). Компоненту нужен только .name.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));

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
    mockGetFeature.pending = false;
    mockGetFeature.result = pageData();
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    mockMergeFeature.pending = false;
    mockMergeFeature.result = null;
    mockMergeFeature.error = null;
    mockMergeFeature.calls = [];
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

  // м.2: повторное раскрытие после сворачивания — без доп. GET (кэш _eventsLimit='all').
  // Мутация (в отчёте): убрать `if (_eventsLimit === 'all') return` → 3-й GET → red.
  it('м.2: «Свернуть» → «Показать все» повторно — без доп. запроса (кэш)', async () => {
    const events: import('../../types/featureIndex').FeatureEvent[] = [];
    for (let i = 0; i < 10; i++) {
      events.push({ at: `2026-10-19T${String(10 + i).padStart(2, '0')}:00:00Z`, kind: 'started', taskId: `t${i}`, personaId: null, sessionId: null, round: null, reason: null });
    }
    mockGetFeature.result = pageData({ events, eventsTotal: 1000 });
    const c = render({});
    await settle();
    act(() => { setFeaturePageTab('events'); });
    const showAll = findNodeByAttr(c, 'data-testid', 'events-show-all')!;
    // 1-й раз: догрузка (GET #2, eventsLimit=all).
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    // «Свернуть» → «Показать все» (повторно): кэш — без нового GET.
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
    act(() => { (reactProps(showAll)!.onClick as () => void)(); });
    await settle();
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

// === п.64: featureTaskTotal — ОДИН фолбэк (индекс → иначе сумма по шагам) ===
// Чистая функция: taskCount из индекса имеет приоритет; фичи нет в индексе (null) —
// фолбэк на taskCounters(run).total. Мутация (в отчёте): фолбэк на 0 → тест краснеет.
describe('п.64: featureTaskTotal (один фолбэк)', () => {
  const run = pageData().run;
  it('индекс (taskCount) имеет приоритет над суммой по шагам', () => {
    expect(featureTaskTotal(feat({ taskCount: 7 }), run)).toBe(7);
  });
  it('фичи нет в индексе (null) — фолбэк на taskCounters(run).total', () => {
    expect(featureTaskTotal(null, run)).toBe(taskCounters(run).total);
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
