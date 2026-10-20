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
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePage, type FeaturePageBackTarget } from './FeaturePage';
import { stripStepCode, featureTaskTotal, taskCounters } from './featurePageBody';
import { __test as pageTest } from '../../lib/featurePage';
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

function render(props: { featureId?: string; projectId?: string | null; isMobile?: boolean; onBack?: (t: FeaturePageBackTarget) => void; chatNames?: Record<string, string>; chatPersonas?: Record<string, string> }) {
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

// Дождаться разрешения fetch (fake timers + microtasks) → финальное состояние страницы.
// advanceTimersByTime(2000): прогоняет debounce 1,5 с и microtasks, но НЕ interval 60 с
// (useMinuteTick в PipelineFlow — иначе 10000 ререндеров = infinite loop).
async function settle() {
  await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
}

// Быстрый трек «страница как файл»: активны пять состояний + шапка-«файл» (глиф,
// название, тег, «Закрыть» как у FileViewer) + закрытие (кнопка/Esc) + back-nav.
// ЗАМОРОЖЕНЫ (it.skip, «вернём позже»): блоки, убранные из рендера — тревога, шаги,
// чаты фичи, задачи, лента событий, «Мерж», меню ⋯, главное действие, M2-подсказка.
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

  // === 2b. «как файл»: белый фон C.bgCard, Toolbar-шапка, убранных блоков нет в DOM ===
  it('as a file: фон C.bgCard, Toolbar-шапка; нет тревоги/чатов/задач/ленты/⋯/главного действия', async () => {
    const c = render({});
    await settle();
    // Белый фон страницы — по токену C.bgCard (var(--c-bg-card)), как у просмотра файла.
    const page = findNodeByAttr(c, 'data-testid', 'feature-page')!;
    expect((page as unknown as { style: Record<string, string> }).style.background, 'фон страницы не C.bgCard (as a file)').toBe('var(--c-bg-card)');
    // Убранные блоки/элементы шапки — в DOM нет (мёртвое действие/контент).
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-alarm'), 'тревога на месте (as a file: убрана)').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-main-action'), 'главное действие на месте (as a file: убрано)').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-branch'), 'ветка на месте (as a file: убрана)').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-taskcount'), '«M задач» на месте (as a file: убрано)').toBeNull();
    expect(findNodeByAttr(c, 'aria-label', 'Ещё действия'), 'меню ⋯ на месте (as a file: убрано)').toBeNull();
    // Строки чатов/шагов/событий фичи — нет. («Мерж»-стадия конвейера — легитимна, не проверяем по тексту.)
    expect(findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key'), 'строка чата на месте (as a file: убрана)').toBeNull();
  });

  it.skip('ok: ⋯ открывает меню «Скопировать ветку» (без «Закрыть без мержа» и «Открыть эпик в «Задачах»»)', async () => {
    const c = render({});
    await settle();
    const kebab = findNodeByAttr(c, 'aria-label', 'Ещё действия')!;
    act(() => { (reactProps(kebab)!.onClick as (e: unknown) => void)({ currentTarget: { getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }) } }); });
    // Меню — портал в document.body: «Скопировать ветку» на месте; «Закрыть без мержа»
    // и «Открыть эпик в «Задачах»» убраны (мёртвое действие — переход будет в Ф4.1).
    const body = (globalThis as { document: { body: unknown } }).document.body as { childNodes: unknown[] };
    const all = textNodes(body as never).join('');
    expect(all).toContain('Скопировать ветку');
    expect(all).not.toContain('Закрыть без мержа');
    expect(all).not.toContain('Открыть эпик в «Задачах»');
  });

  it.skip('ok: тревога «ждёт вас» (infoBg) + кнопка «Ответить»', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'wait', since: '2026-10-19T10:00:00Z', reason: 'выбрать вариант', personaId: 'p', sessionId: 'chat-key' },
    })];
    const c = render({});
    await settle();
    // Тег «Ждёт вас».
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(textNodes(tag!).join('')).toContain('Ждёт вас');
    // Блок тревоги + кнопка «Ответить».
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(alarm).not.toBeNull();
    expect(textNodes(alarm!).join('')).toContain('Ждёт вашего решения');
    expect(textNodes(alarm!).join('')).toContain('Ответить');
    // Главное действие — «Ответить» (attention.kind wait).
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
    expect(textNodes(main!).join('')).toContain('Ответить');
  });

  it.skip('ok: тревога «стоит» (dangerBg) + кнопка «Открыть его чат»', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'stopped', since: '2026-10-19T09:00:00Z', reason: 'ход упал', personaId: 'p', sessionId: 'chat-x' },
    })];
    const c = render({});
    await settle();
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(textNodes(tag!).join('')).toContain('Стоит');
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(alarm).not.toBeNull();
    expect(textNodes(alarm!).join('')).toContain('Исполнитель молчит');
    expect(textNodes(alarm!).join('')).toContain('Открыть его чат');
  });

  // === m1: пробел перед датой (не «решения18.10») + единый формат даты с лентой ===
  it.skip('m1: тревога — пробел перед датой (не «решения18.10»), формат даты как в ленте', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      // since — НЕ сегодня (18.10): дата выводится, и перед ней обязателен пробел.
      attention: { kind: 'wait', since: '2026-10-18T10:00:00Z', reason: 'выбрать вариант', personaId: 'p', sessionId: 'chat-key' },
    })];
    const c = render({});
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm')!;
    const text = textNodes(alarm).join('');
    expect(text, 'нет пробела перед датой (m1: «решения18.10»)').toContain('решения 18.10');
    expect(text, 'дата склеена с «решения» (m1)').not.toContain('решения18.10');
  });

  // === m2: кнопка тревоги на 360 — тач-цель ≥ 40 ===
  it.skip('m2: кнопка тревоги на 360 — minHeight 40 (тач-цель)', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'wait', since: '2026-10-19T10:00:00Z', reason: 'выбрать вариант', personaId: 'p', sessionId: 'chat-key' },
    })];
    const c = render({ isMobile: true });
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm')!;
    const btn = firstButton(alarm as never);
    expect(btn, 'кнопка тревоги не найдена (m2)').not.toBeNull();
    expect((btn as unknown as { style: Record<string, string> }).style.minHeight, 'на 360 нет minHeight 40 (m2: тач-цель < 40)').toBe('40px');
  });

  // === m3: тревога «к мержу» (kind=merge) — текст «К мержу», не «Ждёт вашего решения» ===
  it.skip('m3: тревога kind=merge — текст «К мержу» (не «Ждёт вашего решения»)', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'merge', since: '2026-10-19T10:00:00Z', reason: null, personaId: 'p', sessionId: 'chat-key' },
    })];
    const c = render({});
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm')!;
    const text = textNodes(alarm).join('');
    expect(text, 'нет «К мержу» (m3: тревога merge показывает «Ждёт вашего решения»)').toContain('К мержу');
    expect(text, '«Ждёт вашего решения» вместо «К мержу» (m3)').not.toContain('Ждёт вашего решения');
  });

  // === Ф2.6: главное действие «Мерж» (состояние «к мержу») ===

  it.skip('Ф2.6: тревога kind=merge → главное действие «Мерж» (не «К мержу»/«Ответить»)', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'merge', since: '2026-10-19T10:00:00Z', reason: null, personaId: 'p', sessionId: 'chat-key' },
    })];
    const c = render({});
    await settle();
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    const text = textNodes(main).join('');
    expect(text, 'нет кнопки «Мерж» (Ф2.6: главное действие merge)').toContain('Мерж');
    expect(text, '«К мержу»/«Ответить» вместо «Мерж» (Ф2.6)').not.toContain('К мержу');
    expect(text).not.toContain('Ответить');
  });

  it.skip('Ф2.6: кнопка «Мерж» есть ТОЛЬКО в состоянии «к мержу» (идущая фича → «Ключевой чат»)', async () => {
    // Идущая фича (нет тревоги, есть ключевой чат) → «Ключевой чат», НЕ «Мерж» (мёртвая кнопка).
    const c = render({});
    await settle();
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    const text = textNodes(main).join('');
    expect(text, 'идущая фича: «Мерж» вместо «Ключевой чат» (Ф2.6: мёртвая кнопка)').toContain('Ключевой чат');
    expect(text).not.toContain('Мерж');
  });

  it.skip('Ф2.6: 409 → причина из ответа (body.error) под кнопкой', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'merge', since: '2026-10-19T10:00:00Z', reason: null, personaId: 'p', sessionId: 'chat-key' },
    })];
    mockMergeFeature.error = { status: 409, body: { error: 'Финал не принят: мерж преждевременно' } } as Error & { status?: number; body?: unknown };
    const c = render({});
    await settle();
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    const btn = firstButton(main as never)!;
    act(() => { (reactProps(btn)!.onClick as () => void)(); });
    await settle();
    const err = findNodeByAttr(c, 'data-testid', 'feature-page-merge-error');
    expect(err, 'нет причины 409 (Ф2.6: 409 не показывает причину)').not.toBeNull();
    expect(textNodes(err!).join('')).toContain('Финал не принят: мерж преждевременно');
  });

  it.skip('Ф2.6: двойной клик → один запрос (кнопка заблокирована на время запроса)', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'merge', since: '2026-10-19T10:00:00Z', reason: null, personaId: 'p', sessionId: 'chat-key' },
    })];
    mockMergeFeature.pending = true; // запрос в полёте → кнопка заблокирована
    const c = render({});
    await settle();
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    const btn = firstButton(main as never)!;
    // ДВА клика в ОДНОМ act() = один тик: setState(merging) ещё не сменился (батчинг),
    // защита от второго запроса — синхронный mergingRef (state-флаг тут не спасает).
    act(() => {
      (reactProps(btn)!.onClick as () => void)();
      (reactProps(btn)!.onClick as () => void)();
    });
    expect(mockMergeFeature.calls, 'двойной клик дал >1 запроса (Ф2.6: кнопка не заблокирована)').toHaveLength(1);
  });

  it.skip('Ф2.6: успех → название задачи мержа + статус (без мёртвой ссылки)', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'merge', since: '2026-10-19T10:00:00Z', reason: null, personaId: 'p', sessionId: 'chat-key' },
    })];
    mockMergeFeature.result = { id: 'merge-1', title: 'Мерж фичи: Моя фича', status: 'inProgress', description: '', priority: 'medium' } as Task;
    const c = render({});
    await settle();
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    const btn = firstButton(main as never)!;
    act(() => { (reactProps(btn)!.onClick as () => void)(); });
    await settle();
    const result = findNodeByAttr(c, 'data-testid', 'feature-page-merge-result');
    expect(result, 'нет результата мержа (Ф2.6: успех не показывает задачу)').not.toBeNull();
    const text = textNodes(result!).join('');
    expect(text).toContain('Мерж фичи: Моя фича');
    expect(text, 'нет статуса задачи мержа (Ф2.6)').toContain('в работе');
  });

  // === Ф2.6: ключи ленты уникальны при равном at (taskId в ключе) ===
  it.skip('Ф2.6: два события одного вида в одном чате с равным at → ключи уникальны (taskId в ключе)', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: 'chat-x', round: null, reason: null },
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't2', personaId: null, sessionId: 'chat-x', round: null, reason: null },
      ],
      eventsTotal: 2,
    });
    installFakeDom();
    const { root, container } = mount(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', isMobile: false, onBack: () => {} }));
    await settle();
    // Перерендер = reconciliation списка: дубль ключа (at|kind|sessionId без taskId) дал бы
    // warning «two children with the same key». С taskId в ключе — уникальны, warning нет.
    const errSpy = vi.spyOn(console, 'error');
    act(() => { root.render(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', isMobile: false, onBack: () => {} })); });
    await settle();
    const dupKey = errSpy.mock.calls.some(args =>
      args.some(a => typeof a === 'string' && a.includes('same key')));
    errSpy.mockRestore();
    // Обе строки на месте (data-testid повторяется) + нет warning о дубле ключа.
    const all = textNodes(container).join('');
    expect(all.split('открыть чат').length - 1, 'не две строки с равным at (Ф2.6: дубль ключа)').toBe(2);
    expect(dupKey, 'дубль ключа ленты (Ф2.6: taskId не в ключе)').toBe(false);
  });

  it.skip('ok: закрытая смержена — итог «Смержена …» (successBg)', async () => {
    mockIndex.features = [feat({
      bucket: 'closed',
      summary: { mergedAt: '2026-10-15T12:00:00Z', durationMinutes: 2000 },
      taskCount: 7,
    })];
    const c = render({});
    await settle();
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(textNodes(tag!).join('')).toContain('Смержена');
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(alarm).not.toBeNull();
    expect(textNodes(alarm!).join('')).toContain('Смержена');
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
  it('back-nav: указатель «чат → фича» → «К чату «…»» (название из api.chats.get), onBack → chat', async () => {
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    mockChats.set('chatA', { id: 'chatA', name: 'Чат Дениса', mode: 'chat', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session);
    const onBack = vi.fn();
    const c = render({ onBack, isMobile: true });
    await settle();
    const close = findNodeByAttr(c, 'data-testid', 'feature-page-close')!;
    // Мобила: BackButton с подписью «К чату «…»» (название из api.chats.get).
    expect(textNodes(close).join('')).toContain('К чату «Чат Дениса»');
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

  // === M2: крошка на 360 — minHeight 40 (тач-цель, §6) ===
  it.skip('M2: крошка на 360 — minHeight 40 (тач-цель)', () => {
    const html = renderToStaticMarkup(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', isMobile: true, onBack: () => {} }));
    expect(html).toContain('min-height:40px');
  });

  // === m2: крошка по таблице §5.1 — поля, gap, вес, maxWidth, многоточие ===
  it.skip('m2: крошка по таблице §5.1 — поля 4 8 4 4, gap 5, вес 400, maxWidth 260, многоточие', () => {
    const html = renderToStaticMarkup(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', onBack: () => {} }));
    expect(html).toContain('padding:4px 8px 4px 4px');
    expect(html).toContain('gap:5px');
    expect(html).toContain('font-weight:400');
    expect(html).toContain('max-width:260px');
    expect(html).toContain('text-overflow:ellipsis');
  });

  // === m3: крошка — фокус SHADOW.focus (класс cc-feature-page-crumb, §0) + шрифт Hanken ===
  it.skip('m3: крошка несёт focus-класс (SHADOW.focus, §0) и шрифт Hanken (ловушка <button>)', () => {
    const html = renderToStaticMarkup(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', onBack: () => {} }));
    expect(html).toContain('cc-feature-page-crumb');
    expect(html).toContain('Hanken Grotesk');
  });

  // === Ф2.5: чаты фичи — ключевой первым ===
  it.skip('Ф2.5: чаты фичи — ключевой чат первым, остальные по порядку индекса', async () => {
    // Два чата: 'chat-key' (ключевой) и 'chat-other'.
    // В индексе 'chat-other' идёт ПЕРЕД 'chat-key' (Object.entries порядок).
    mockIndex.chats = {
      'chat-other': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'back' }],
      'chat-key':   [{ featureId: 'f1', kind: 'curator',  taskRole: null,  stepRef: null,  lane: null }],
    };
    mockIndex.features = [feat({ keySessionId: 'chat-key', keyArchived: false })];
    const c = render({});
    await settle();
    // Ключевой чат — первым.
    const keyRow = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key');
    const otherRow = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-other');
    expect(keyRow).not.toBeNull();
    expect(otherRow).not.toBeNull();
    // Ключевой — раньше в DOM: рекурсивный обход childNodes, собираем data-testid.
    const order: string[] = [];
    const walk = (n: { childNodes: unknown[]; attributes?: Record<string, string> }) => {
      const at = (n as { attributes?: Record<string, string> }).attributes;
      if (at && at['data-testid'] && at['data-testid'].startsWith('feature-chat-')) {
        order.push(at['data-testid']);
      }
      for (const ch of (n.childNodes as { childNodes: unknown[]; attributes?: Record<string, string> }[])) {
        walk(ch as never);
      }
    };
    walk(c as never);
    expect(order.indexOf('feature-chat-chat-key')).toBeGreaterThanOrEqual(0);
    expect(order.indexOf('feature-chat-chat-key')).toBeLessThan(order.indexOf('feature-chat-chat-other'));
  });

  // === Ф2.5: архивный ключевой чат помечен ===
  it.skip('Ф2.5: архивный ключевой чат — метка «архив»', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    mockIndex.features = [feat({ keySessionId: 'chat-key', keyArchived: true })];
    const c = render({});
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    expect(textNodes(row).join('')).toContain('архив');
  });

  // === Ф2.5: «Показать все» — перезапрос без лимита ===
  it.skip('Ф2.5: «Показать все» перезапрашивает getFeature с eventsLimit=all', async () => {
    // Лента обрезана: 2 события, всего 5.
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: null, round: null, reason: null },
        { at: '2026-10-19T11:00:00Z', kind: 'completed', taskId: 't2', personaId: null, sessionId: null, round: null, reason: null },
      ],
      eventsTotal: 5,
    });
    const c = render({});
    await settle();
    // Кнопка «Показать все» есть (лента обрезана): ui/Button xs ghost внутри span-обёртки.
    const wrap = findNodeByAttr(c, 'data-testid', 'feature-page-show-all');
    expect(wrap).not.toBeNull();
    const btn = firstButton(wrap as never);
    expect(btn).not.toBeNull();
    // Клик.
    act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
    await settle();
    // Запрос с eventsLimit='all' (последний вызов).
    const lastCall = mockGetFeature.calls[mockGetFeature.calls.length - 1];
    expect(lastCall).toBeDefined();
    expect(lastCall!.eventsLimit).toBe('all');
  });

  // === Ф2.5: событие без sessionId не даёт ссылки на чат ===
  it.skip('Ф2.5: событие без sessionId — нет ссылки «открыть чат»', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: null, round: null, reason: null },
      ],
      eventsTotal: 1,
    });
    const c = render({});
    await settle();
    // p.19: проверка по отсутствию ТЕКСТА «открыть чат», а не по testid с хвостом null
    // (testid = feature-event-chat-link-${sessionId} — хрупко: зависит от формата id).
    // Событие без sessionId → ссылки на чат нет вообще (fail-closed).
    expect(textNodes(c).join(''), 'ссылка «открыть чат» у события без sessionId').not.toContain('открыть чат');
  });

  // === Ф2.5: событие С sessionId даёт ссылку ===
  it.skip('Ф2.5: событие с sessionId — ссылка «открыть чат» на месте', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: 'chat-x', round: null, reason: null },
      ],
      eventsTotal: 1,
    });
    const c = render({});
    await settle();
    const link = findNodeByAttr(c, 'data-testid', 'feature-event-chat-link-chat-x');
    expect(link).not.toBeNull();
  });

  // === Ф2.5: «шагов нет» — пустое состояние ===
  it.skip('Ф2.5: «шагов нет» — пустое состояние в блоке Шаги', async () => {
    // stepChains = [] (нет в pageData по умолчанию).
    const c = render({});
    await settle();
    const all = textNodes(c).join('');
    expect(all).toContain('Шагов пока нет');
  });

  // === M1: главное действие и тревога — в ВЕРНЫЙ чат (не в ключевой) ===
  it.skip('M1: без тревоги, есть ключевой чат → «Ключевой чат» → onBack {kind:key, chat-key}', async () => {
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    expect(textNodes(main).join('')).toContain('Ключевой чат');
    const btn = firstButton(main as never)!;
    act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'key', chatId: 'chat-key' });
  });

  it.skip('M1: тревога про «chat-x» (не ключевой чат) → «Ответить» → onBack {kind:chat, chat-x}', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'wait', since: '2026-10-19T10:00:00Z', reason: 'выбрать вариант', personaId: 'p', sessionId: 'chat-x' },
    })];
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    // Главное действие — «Ответить» (не «Ключевой чат»).
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    expect(textNodes(main).join('')).toContain('Ответить');
    const btn = firstButton(main as never)!;
    act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
    // Ведёт в чат ТРЕВОГИ, не в ключевой чат.
    expect(onBack).toHaveBeenCalledWith({ kind: 'chat', chatId: 'chat-x' });
    expect(onBack).not.toHaveBeenCalledWith({ kind: 'key', chatId: 'chat-key' });
  });

  it.skip('M1: «Ответить», но чата тревоги нет → кнопки нет (нет мёртвых действий)', async () => {
    mockIndex.features = [feat({
      bucket: 'attention',
      attention: { kind: 'wait', since: '2026-10-19T10:00:00Z', reason: 'выбрать вариант', personaId: 'p', sessionId: null },
    })];
    const c = render({});
    await settle();
    // В шапке кнопки нет (dead action), в блоке тревоги — тоже.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-main-action')).toBeNull();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm')!;
    expect(textNodes(alarm).join('')).not.toContain('Ответить');
  });

  // === M2: подпись шага — готовая подпись бэка chain.status (одна точка правды) ===
  it.skip('M2: подпись шага — chain.status бэка как есть', async () => {
    mockGetFeature.result = pageData({
      run: {
        ...pageData().run,
        stepChains: [
          { stepRef: 'Ш1', title: 'Шаг один', lane: 'back', taskIds: [], outcome: 'pending', status: 'ждёт ревью', durationMinutes: 0, loops: 0, rounds: [] },
        ],
      },
    });
    const c = render({});
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-step-Ш1')!;
    expect(textNodes(row).join('')).toContain('ждёт ревью');
  });

  it.skip('M2: status отсутствует (старый ответ) → фолбэк словами бэка: pending=«ждёт ревью», returned=«закрыт без повторного ревью»', async () => {
    mockGetFeature.result = pageData({
      run: {
        ...pageData().run,
        // status нет в ответах (старый бэк) → null после нормализации stepRows.
        stepChains: [
          { stepRef: 'Ш2', title: 'Шаг два', lane: 'front', taskIds: [], outcome: 'pending', durationMinutes: 0, loops: 0, rounds: [] },
          { stepRef: 'Ш3', title: 'Шаг три', lane: 'back', taskIds: [], outcome: 'returned', durationMinutes: 0, loops: 1, rounds: [] },
        ] as unknown as import('../../types/featureRun').FeatureStepChain[],
      },
    });
    const c = render({});
    await settle();
    const row2 = findNodeByAttr(c, 'data-testid', 'feature-step-Ш2')!;
    expect(textNodes(row2).join('')).toContain('ждёт ревью');
    const row3 = findNodeByAttr(c, 'data-testid', 'feature-step-Ш3')!;
    expect(textNodes(row3).join('')).toContain('закрыт без повторного ревью');
  });

  // === M3: строка чата и «открыть чат» — настоящие <button> (ловушки §0) ===
  it.skip('M3: строка чата — <button>; клик → onBack {kind:chat, chatId}', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    // Настоящая кнопка, а не div.
    expect((row as unknown as { nodeName: string }).nodeName).toBe('BUTTON');
    act(() => { (reactProps(row)!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'chat', chatId: 'chat-key' });
  });

  it.skip('M3: «открыть чат» в ленте — <button>; клик → onBack {kind:chat, chatId}', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: 'chat-x', round: null, reason: null },
      ],
      eventsTotal: 1,
    });
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    const link = findNodeByAttr(c, 'data-testid', 'feature-event-chat-link-chat-x')!;
    expect((link as unknown as { nodeName: string }).nodeName).toBe('BUTTON');
    act(() => { (reactProps(link)!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'chat', chatId: 'chat-x' });
  });

  // === m5: «открыть чат» — блок display:flex + alignItems:flex-start, прижат к тексту;
  // на 360 тач-цель ≥ 40 через minHeight (не большими полями). ===
  it.skip('m5: «открыть чат» — display:flex + alignItems:flex-start (прижат к тексту), на 360 minHeight 40', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: 'chat-x', round: null, reason: null },
      ],
      eventsTotal: 1,
    });
    const c = render({ isMobile: true });
    await settle();
    const link = findNodeByAttr(c, 'data-testid', 'feature-event-chat-link-chat-x')!;
    const st = (link as unknown as { style: Record<string, string> }).style;
    // m5: flex-блок, прижатый к тексту события (не detached full-width display:block).
    expect(st.display, '«открыть чат» не flex-блок (m5: прижат к тексту)').toBe('flex');
    expect(st.alignItems, '«открыть чат» не alignItems:flex-start (m5)').toBe('flex-start');
    // На 360 тач-цель ≥ 40 через minHeight (псевдо-область), а не большими полями (старое '12px 0').
    expect(st.minHeight, 'на 360 нет minHeight 40 (m5: тач-цель < 40)').toBe('40px');
    expect(st.padding, 'на 360 большие поля (m5: тач-цель через minHeight, не padding)').not.toBe('12px 0px');
  });

  // === M4: название чата вместо GUID (без новых запросов) ===
  it.skip('M4: есть chatNames → название; GUID наружу не попадает', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    const c = render({ chatNames: { 'chat-key': 'Чат Дениса' } });
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    expect(textNodes(row).join('')).toContain('Чат Дениса');
    // GUID ('chat-key') — только в data-testid (атрибут), в ТЕКСТ не попадает.
    expect(textNodes(c).join('')).not.toContain('chat-key');
  });

  it.skip('M4: названия нет (чат вне проекта / список не пришёл) → «Чат»', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    const c = render({});
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    expect(textNodes(row).join('')).toContain('Чат');
    expect(textNodes(c).join('')).not.toContain('chat-key');
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

  // === R6-1: чаты — роль русским словом, аватар персоны, «ключевой» ===

  it.skip('R6-1: роль чата — русское слово (curator→«куратор», executor→«исполнитель»), не сырой kind', async () => {
    mockIndex.chats = {
      'chat-c': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
      'chat-x': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'back' }],
    };
    const c = render({});
    await settle();
    const cRow = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-c')!;
    const cText = textNodes(cRow).join('');
    expect(cText).toContain('куратор');
    expect(cText).not.toContain('curator');
    const xRow = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-x')!;
    const xText = textNodes(xRow).join('');
    expect(xText).toContain('исполнитель');
    expect(xText).not.toContain('executor');
  });

  it.skip('R6-1: ключевой чат — метка «ключевой» (accentLight), без кода роли', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    const c = render({});
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    const t = textNodes(row).join('');
    expect(t).toContain('ключевой');
    expect(t).not.toContain('curator');
    // Фон строки — accentLight (не рамка/фон-карточки).
    expect((row as unknown as { style: Record<string, string> }).style.background).toBe('var(--c-accent-light)');
  });

  it.skip('Ф3.2: метка «ключевой» — aria-describedby ведёт на скрытый span (минор 46: id всегда в DOM)', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    const c = render({});
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    // Минор 46: aria-describedby → скрытый span с постоянным id (не на Tooltip,
    // который в DOM только когда открыт; на тач Tooltip не поднимается).
    // Префикс id — по МЕСТУ (п.58): feat-key-desc- (строка страницы фичи), не key-desc- (карточка
    // списка): оба места могут быть в DOM одновременно, id не коллидируют.
    expect(row.getAttribute('aria-describedby'), 'кнопка-строка ссылается на скрытое описание').toBe('feat-key-desc-chat-key');
    // Скрытый span с описанием существует в DOM; описание — ТЕКСТОМ (п.59), не aria-label.
    const desc = findNodeByAttr(row, 'id', 'feat-key-desc-chat-key');
    expect(desc, 'скрытый span с описанием подсказки (минор 46)').not.toBeNull();
    expect(textNodes(desc as never).join(''), 'текст описания в скрытом span (п.59)').toBe(KEY_SESSION_TOOLTIP);
    const chip = findNodeByAttr(row, 'data-testid', 'feature-role-key');
    expect(chip, 'метка «ключевой» (data-testid) есть').not.toBeNull();
  });

  it.skip('R6-1: аватар 18 — персона из chatPersonas (инициалы); без персоны — нейтральный круг', async () => {
    personaMap.set('p1', 'Денис');
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    const c = render({ chatPersonas: { 'chat-key': 'p1' } });
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    // Аватар персоны: инициал (mock personaInitials → первая буква).
    expect(textNodes(row).join('')).toContain('Д');
  });

  it.skip('R6-1: персоны нет — инициалов в строке чата нет (нейтральный круг)', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    const c = render({});
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-chat-chat-key')!;
    expect(textNodes(row).join('')).not.toContain('Д');
  });

  // === R6-2: лента — исполнитель, дата+время, сетка 44·16 ===

  it.skip('R6-2: лента — исполнитель по personaId (имя), без персоны — без прочерка', async () => {
    personaMap.set('p1', 'Денис');
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: 'p1', sessionId: null, round: null, reason: null },
        { at: '2026-10-19T11:00:00Z', kind: 'completed', taskId: 't2', personaId: null, sessionId: null, round: null, reason: null },
      ],
      eventsTotal: 2,
    });
    const c = render({});
    await settle();
    const withPersona = findNodeByAttr(c, 'data-testid', 'feature-event-2026-10-19T10:00:00Z-started')!;
    const t1 = textNodes(withPersona).join('');
    expect(t1).toContain('Денис');
    expect(t1).not.toContain('—');
    // Без персоны — только фраза, прочерка нет.
    const noPersona = findNodeByAttr(c, 'data-testid', 'feature-event-2026-10-19T11:00:00Z-completed')!;
    const t2 = textNodes(noPersona).join('');
    expect(t2).toContain('закрыта');
    expect(t2).not.toContain('—');
  });

  it.skip('R6-2: время — сегодня «ЧЧ:ММ» (одна строка); другой день — ДВЕ строки: дата сверху, время снизу (N2)', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: null, round: null, reason: null },
        { at: '2026-10-18T10:00:00Z', kind: 'completed', taskId: 't2', personaId: null, sessionId: null, round: null, reason: null },
      ],
      eventsTotal: 2,
    });
    const c = render({});
    await settle();
    // Сегодня (NOW = 19.10): только время, ОДНА строка; даты нет.
    const today = findNodeByAttr(c, 'data-testid', 'feature-event-2026-10-19T10:00:00Z-started')!;
    const t1 = textNodes(today).find(t => /^\d{2}:\d{2}$/.test(t));
    expect(t1).toBeDefined();
    expect(textNodes(today).some(t => /^\d{2}\.\d{2}$/.test(t)), 'сегодняшний день: дата не должна выводиться (N2)').toBe(false);
    // Другой день (N2): ДВЕ отдельные строки — дата «18.10» сверху, время «10:00» снизу.
    const other = findNodeByAttr(c, 'data-testid', 'feature-event-2026-10-18T10:00:00Z-completed')!;
    const nodes = textNodes(other);
    expect(nodes.some(t => /^\d{2}\.\d{2}$/.test(t)), 'нет отдельной строки даты (N2: дата сверху)').toBe(true);
    expect(nodes.some(t => /^\d{2}:\d{2}$/.test(t)), 'нет отдельной строки времени (N2: время снизу)').toBe(true);
    // НЕ одна склеенная строка «18.10 10:00» (она не влезала в колонку 44 — минуты под аватар).
    expect(nodes.some(t => /^\d{2}\.\d{2} \d{2}:\d{2}$/.test(t)), 'дата+время склеены в одну строку (N2: должны быть в две строки)').toBe(false);
  });

  it.skip('R6-2: строка события — сетка 44px 16px minmax(0,1fr), column-gap 8', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: null, round: null, reason: null },
      ],
      eventsTotal: 1,
    });
    const c = render({});
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'feature-event-2026-10-19T10:00:00Z-started')!;
    const st = (row as unknown as { style: Record<string, string> }).style;
    expect(st.gridTemplateColumns).toBe('44px 16px minmax(0,1fr)');
    expect(st.columnGap).toBe('8px');
  });

  // === R6-3: шаги — код не повторяется, число отдельно, «Показать все» Button, строка-кнопка ===

  it.skip('R6-3: код шага в названии не повторяется (stripStepCode, чистая функция)', () => {
    expect(stripStepCode('Ш1 · индекс поиска', 'Ш1')).toBe('индекс поиска');
    expect(stripStepCode('Ш10 · индекс', 'Ш1')).toBe('Ш10 · индекс'); // «Ш10» для кода «Ш1» не режется
    expect(stripStepCode('Ш10 · индекс', 'Ш10')).toBe('индекс');
    expect(stripStepCode('Индекс без кода', 'Ш1')).toBe('Индекс без кода');
  });

  it.skip('R6-3: строка шага — код один раз; с чатом — <button>, без — <div>', async () => {
    mockGetFeature.result = pageData({
      run: {
        ...pageData().run,
        stepChains: [
          { stepRef: 'Ш1', title: 'Ш1 · индекс поиска', lane: 'back', taskIds: [], outcome: 'pending', status: 'ждёт ревью', durationMinutes: 0, loops: 0, rounds: [] },
          { stepRef: 'Ш2', title: 'Шаг два', lane: 'front', taskIds: [], outcome: 'pending', status: 'ждёт ревью', durationMinutes: 0, loops: 0, rounds: [] },
        ],
      },
    });
    // Чат исполнителя только для Ш1.
    mockIndex.chats = {
      'chat-x': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш1', lane: 'back' }],
    };
    const c = render({});
    await settle();
    const row1 = findNodeByAttr(c, 'data-testid', 'feature-step-Ш1')!;
    const t1 = textNodes(row1).join('');
    // Код «Ш1» — один раз (отдельный span), название без повторённого кода.
    expect(t1.match(/Ш1/g)?.length).toBe(1);
    expect(t1).toContain('индекс поиска');
    // Ведёт в чат → настоящая кнопка.
    expect(row1.nodeName).toBe('BUTTON');
    // Ш2 — чата нет → текст (div).
    const row2 = findNodeByAttr(c, 'data-testid', 'feature-step-Ш2')!;
    expect(row2.nodeName).toBe('DIV');
  });

  it.skip('R6-3: число шагов отделено от подписи (отдельный span, не «ШАГИ2»)', async () => {
    mockGetFeature.result = pageData({
      run: {
        ...pageData().run,
        stepChains: [
          { stepRef: 'Ш1', title: 'Шаг один', lane: 'back', taskIds: [], outcome: 'pending', status: 'ждёт ревью', durationMinutes: 0, loops: 0, rounds: [] },
          { stepRef: 'Ш2', title: 'Шаг два', lane: 'front', taskIds: [], outcome: 'pending', status: 'ждёт ревью', durationMinutes: 0, loops: 0, rounds: [] },
        ],
      },
    });
    const c = render({});
    await settle();
    // Подпись блока: caption «Шаги» и число «2» — разные текстовые ноды (right —
    // отдельный span с marginLeft auto; подписываю его testid'ом, caption — его родитель).
    const right = findNodeByAttr(c, 'data-testid', 'feature-steps-cap');
    expect(right).not.toBeNull();
    const capDiv = (right as unknown as { parentNode: FNodeLike }).parentNode as FNodeLike;
    const nodes = textNodes(capDiv as never);
    expect(nodes).toContain('Шаги');
    expect(nodes).toContain('2');
    // Ни одна нода не склеивает подпись и число.
    for (const t of nodes) expect(t).not.toMatch(/Шаги\s*2|2\s*Шаги/);
  });

  // === R6-4: каркас §5.4 — колонки, открытые блоки, порядок на 360 ===

  it.skip('R6-4: десктоп — grid minmax(0,1fr) 300px, gap 18, align start; блоки открытые (marginBottom 18, без фона)', async () => {
    const c = render({});
    await settle();
    const body = findNodeByAttr(c, 'data-testid', 'feature-page-body')!;
    const st = (body as unknown as { style: Record<string, string> }).style;
    expect(st.gridTemplateColumns).toBe('minmax(0,1fr) 300px');
    expect(st.gap).toBe('18px');
    expect(st.alignItems).toBe('start');
    // Левая колонка → первый блок (конвейер): открытый — отступ 18, фона/рамки/полей нет.
    const leftCol = (body as unknown as { childNodes: FNodeLike[] }).childNodes[0] as FNodeLike;
    const firstBlock = leftCol.childNodes[0] as FNodeLike;
    const bst = (firstBlock as unknown as { style: Record<string, string> }).style;
    expect(bst.marginBottom).toBe('18px');
    expect(bst.background).toBeUndefined();
    expect(bst.border).toBeUndefined();
  });

  it.skip('R6-4: 360 — одна колонка, порядок «чаты → шаги» (R-11)', async () => {
    mockIndex.chats = {
      'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }],
    };
    mockGetFeature.result = pageData({
      run: {
        ...pageData().run,
        stepChains: [
          { stepRef: 'Ш1', title: 'Шаг один', lane: 'back', taskIds: [], outcome: 'pending', status: 'ждёт ревью', durationMinutes: 0, loops: 0, rounds: [] },
        ],
      },
    });
    const c = render({ isMobile: true });
    await settle();
    const body = findNodeByAttr(c, 'data-testid', 'feature-page-body')!;
    const bst = (body as unknown as { style: Record<string, string> }).style;
    expect(bst.display).toBe('flex');
    expect(bst.flexDirection).toBe('column');
    // Порядок блоков: чаты раньше шагов.
    const order: string[] = [];
    const walk = (n: { childNodes: unknown[]; attributes?: Record<string, string> }) => {
      const at = (n as { attributes?: Record<string, string> }).attributes;
      if (at && at['data-testid']) {
        if (at['data-testid'].startsWith('feature-chat-')) order.push('chat');
        if (at['data-testid'].startsWith('feature-step-')) order.push('step');
      }
      for (const ch of (n.childNodes as { childNodes: unknown[]; attributes?: Record<string, string> }[])) walk(ch as never);
    };
    walk(body as never);
    expect(order.indexOf('chat')).toBeGreaterThanOrEqual(0);
    expect(order.indexOf('step')).toBeGreaterThanOrEqual(0);
    expect(order.indexOf('chat')).toBeLessThan(order.indexOf('step'));
  });

  // === R6-5: склонение, тач-цель «Повторить», счётчики-теги ===

  it.skip('R6-5: шапка — «1 задача», «2 задачи», «5 задач» (lib/plural, не «3 задач»)', async () => {
    for (const [count, expected] of [[1, '1 задача'], [2, '2 задачи'], [5, '5 задач']] as const) {
      mockIndex.features = [feat({ taskCount: count })];
      const c = render({});
      await settle();
      const tc = findNodeByAttr(c, 'data-testid', 'feature-page-taskcount')!;
      expect(textNodes(tc).join('')).toContain(expected);
    }
  });

  it('R6-5: «Повторить» на 360 — minHeight 40 (тач-цель)', async () => {
    mockGetFeature.error = { name: 'HttpError', status: 500 } as Error & { status?: number };
    const c = render({ isMobile: true });
    await settle();
    const btn = findButtonWithText(c, 'Повторить');
    expect(btn).not.toBeNull();
    expect((btn as unknown as { style: Record<string, string> }).style.minHeight).toBe('40px');
  });

  it.skip('R6-5: счётчики задач — теги §5.1 (11/600, R 6, padding 1 7), ряд gap 6, marginBottom 6', async () => {
    const c = render({});
    await settle();
    const body = findNodeByAttr(c, 'data-testid', 'feature-page-body')!;
    // Правая колонка (десктоп): чаты, задачи, события → блок «Задачи» — третий.
    const rightCol = (body as unknown as { childNodes: FNodeLike[] }).childNodes[1] as FNodeLike;
    const tasksBlock = rightCol.childNodes[1] as FNodeLike;
    const rows = (tasksBlock as unknown as { childNodes: FNodeLike[] }).childNodes;
    // Block: [BlockCap, TaskCountersView].
    const counters = rows[1] as FNodeLike;
    const cst = (counters as unknown as { style: Record<string, string> }).style;
    expect(cst.gap).toBe('6px');
    expect(cst.marginBottom).toBe('6px');
    // Тег: 600, padding 1 7, R 6.
    const tag = (counters as unknown as { childNodes: FNodeLike[] }).childNodes[0] as FNodeLike;
    const tst = (tag as unknown as { style: Record<string, string> }).style;
    expect(tst.fontWeight).toBe('600');
    expect(tst.padding).toBe('1px 7px');
    expect(tst.borderRadius).toBe('6px');
  });

  // === п.63: «Всего N» + «прочее K» — итог (из индекса, включает consult) > сумма счётчиков ===
  // beforeEach: index.taskCount=5; run.epic есть (taskCounters: pending=1, total=1) →
  // «Всего 5» + «прочее 4» (5 − 1: consult не виден в счётчиках по статусам).
  // Мутация (в отчёте): убрать тег «прочее» из TaskCountersView → тест краснеет.
  it.skip('п.63: «Всего N» + «прочее K» (итог из индекса > сумма счётчиков)', async () => {
    const c = render({});
    await settle();
    const text = textNodes(c).join('');
    expect(text, '«Всего 5» (итог из индекса, не «Задачи · N»)').toContain('Всего 5');
    expect(text, '«прочее 4» (5 − 1: эпик в счётчиках, consult не виден)').toContain('прочее 4');
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

// === M2: подсказка «ключевой» — рендер-тесты (наведение + фокус + уход) ===
// Глеб (R6): тесты ловили только aria-label; мутация «убрать <Tooltip>» оставляла их
// зелёными. Здесь — сам ТЕКСТ плашки в DOM: по наведению (задержка 350 мс) и по фокусу
// на кнопке-строке (клавиатура), исчезает по уходу/blur. Мутация «убрать <Tooltip>»
// краснит: без обёртки нет ни onMouseEnter, ни focused-состояния → текст не появляется.
// Заморожено (быстрый трек «страница как файл»): блок «Чаты фичи» убран из рендера —
// строки чатов (feature-chat-*) и метка «ключевой» больше не на странице. Вернём вместе.
describe.skip('FeaturePage: M2 подсказка «ключевой» (наведение + фокус + уход)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    personaMap.clear();
    setAllFlags({ 'features-overview': true });
    mockIndex.features = [feat()];
    mockIndex.chats = { 'chat-key': [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }] };
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

  // Tooltip использует window.setTimeout (задержка 350 мс); fake-window его не несёт —
  // даём (fake) таймер глобала, чтобы advanceTimersByTime прогнал задержку.
  const withWindowTimers = () => {
    (globalThis as { window: Record<string, unknown> }).window.setTimeout = globalThis.setTimeout;
    (globalThis as { window: Record<string, unknown> }).window.clearTimeout = globalThis.clearTimeout;
  };
  const keyWrapper = (c: FNodeLike) => {
    const chip = findNodeByAttr(c as never, 'data-testid', 'feature-role-key')!;
    return (chip as unknown as { parentNode: FNodeLike }).parentNode;
  };
  // Плашка = элемент role="tooltip" (не по тексту: скрытый span-описание всегда
  // содержит текст подсказки — aria-describedby, минор 59).
  // (M1) Плашка — ПОРТАЛ в document.body (position: fixed), не в контейнер: ищем в body.
  const hasTip = (_c: FNodeLike) => {
    const body = (globalThis as { document?: { body?: unknown } }).document?.body;
    if (!body) return false;
    return findNodeByAttr(body as never, 'role', 'tooltip') !== null;
  };

  it('по наведению: плашка появляется (задержка 350 мс) и исчезает по уходу', async () => {
    const c = render({});
    withWindowTimers();
    await settle();
    const wp = reactProps(keyWrapper(c))!;
    expect(hasTip(c), 'до наведения — плашки нет').toBe(false);
    await act(async () => { (wp.onMouseEnter as () => void)(); await vi.advanceTimersByTimeAsync(350); });
    expect(hasTip(c), 'наведение → плашка в DOM').toBe(true);
    await act(async () => { (wp.onMouseLeave as () => void)(); });
    expect(hasTip(c), 'уход → плашка исчезла').toBe(false);
  });

  it('по фокусу на кнопке-строке (клавиатура, :focus-visible): плашка появляется и исчезает по blur', async () => {
    const c = render({});
    await settle();
    const button = findNodeByAttr(c as never, 'data-testid', 'feature-chat-chat-key')!;
    expect(hasTip(c), 'до фокуса — плашки нет').toBe(false);
    // M1': фокус с КЛАВИАТУРЫ (Tab) — :focus-visible = true → плашка.
    (button as unknown as { focusVisible: boolean }).focusVisible = true;
    await act(async () => { (reactProps(button)!.onFocus as (e: unknown) => void)({ currentTarget: button }); });
    expect(hasTip(c), 'фокус с клавиатуры на кнопке-строке → плашка в DOM').toBe(true);
    await act(async () => { (reactProps(button)!.onBlur as () => void)(); });
    expect(hasTip(c), 'blur → плашка исчезла').toBe(false);
  });

  // M1': клик мышью/тап — фокус БЕЗ :focus-visible → плашка НЕ поднимается (не залипает).
  // Мутация «убрать проверку :focus-visible» красит: плашка поднимется и от мыши.
  it('M1: клик мышью (фокус без :focus-visible) — плашка НЕ поднимается', async () => {
    const c = render({});
    await settle();
    const button = findNodeByAttr(c as never, 'data-testid', 'feature-chat-chat-key')!;
    (button as unknown as { focusVisible: boolean }).focusVisible = false; // мышь: не :focus-visible
    await act(async () => { (reactProps(button)!.onFocus as (e: unknown) => void)({ currentTarget: button }); });
    expect(hasTip(c), 'клик мышью поднял плашку (M1: гейт по :focus-visible)').toBe(false);
  });

  // M1': Escape гасит плашку (фокус с клавиатуры), не открывая чат.
  it('M1: Escape гасит плашку (фокус с клавиатуры)', async () => {
    const c = render({});
    await settle();
    const button = findNodeByAttr(c as never, 'data-testid', 'feature-chat-chat-key')!;
    (button as unknown as { focusVisible: boolean }).focusVisible = true;
    await act(async () => { (reactProps(button)!.onFocus as (e: unknown) => void)({ currentTarget: button }); });
    expect(hasTip(c), 'фокус с клавиатуры → плашка').toBe(true);
    await act(async () => { (reactProps(button)!.onKeyDown as (e: unknown) => void)({ key: 'Escape', currentTarget: button, preventDefault: () => {} }); });
    expect(hasTip(c), 'Escape не погасил плашку (M1: Escape)').toBe(false);
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
