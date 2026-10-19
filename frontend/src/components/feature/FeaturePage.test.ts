// Страница фичи (волна 2 «Обзор фич», шаг Ф2.4): рендер-тесты пяти состояний
// (loading | ok | notFound | disabled | error, макет §5.7) + шапки (глиф, название,
// тег статуса, ветка, возраст, «M задач», ⋯, главное действие) и блока тревоги.
//
// Крошка читает указатель возврата (featureBackNav):
//   • пришли из чата (back.to = эта фича) — «← К чату «…»» (название — из api.chats.get);
//   • указателя нет — «← К списку» (мобила) / «← Назад» (десктоп).
// Клик — onBack(target) + clearBackNav.
//
// Окружение node (без jsdom): fakeDom (mount) + управляемые сторы (featurePage,
// featuresIndex, featureBackNav) и mock api (chats.get, tasks.getFeature).

import { act } from 'react';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePage, type FeaturePageBackTarget } from './FeaturePage';
import { __test as pageTest } from '../../lib/featurePage';
import { __test as indexTest } from '../../lib/featuresIndex';
import { __test as backNavTest, recordBackNav, clearBackNav } from '../../lib/featureBackNav';
import { setAllFlags } from '../../lib/featureFlags';
import { api } from '../../lib/api';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureIndexFeature, FeaturesIndex } from '../../types/featureIndex';
import type { FeaturePage as FeaturePageData } from '../../types/featureIndex';
import type { Session } from '../../types';

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
    },
  },
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

function render(props: { featureId?: string; projectId?: string | null; isMobile?: boolean; onBack?: (t: FeaturePageBackTarget) => void; chatNames?: Record<string, string> }) {
  installFakeDom();
  const { container } = mount(createElement(FeaturePage, {
    featureId: props.featureId ?? 'f1',
    projectId: props.projectId ?? 'p1',
    isMobile: props.isMobile ?? false,
    onBack: props.onBack ?? (() => {}),
    chatNames: props.chatNames,
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

describe('FeaturePage: пять состояний + шапка + тревога', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    setAllFlags({ 'features-overview': true });
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    mockGetFeature.pending = false;
    mockGetFeature.result = pageData();
    mockGetFeature.error = null;
    mockGetFeature.calls = [];
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // === 1. loading: скелет (6 полос + блок 120) + полоса-скелет в шапке вместо названия ===
  it('loading: скелет в теле и полоса-скелет в шапке (крошка рабочая)', async () => {
    mockGetFeature.pending = true; // fetch не разрешается → состояние держится loading
    const c = render({});
    await settle();
    // Тело: скелет.
    const sk = findNodeByAttr(c, 'data-testid', 'feature-page-skeleton');
    expect(sk).not.toBeNull();
    // Шапка: вместо названия — полоса-скелет, крошка на месте.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-title-skeleton')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-back')).not.toBeNull();
    // Название/тег/ветка в загрузке не видны.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-title')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-status')).toBeNull();
  });

  // === 2. ok: шапка (глиф, название, тег, ветка, возраст, «M задач», ⋯, главное действие) + тревога ===
  it('ok: шапка — название, тег «Идёт», ветка, «5 задач», ⋯, главное действие «Ключевой чат»', async () => {
    const c = render({});
    await settle();
    const page = findNodeByAttr(c, 'data-testid', 'feature-page');
    expect(page).not.toBeNull();
    // Название.
    expect(textNodes(findNodeByAttr(c, 'data-testid', 'feature-page-title')!).join('')).toContain('Моя фича');
    // Тег статуса: «идёт» (активная стадия run, без тревоги).
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(tag).not.toBeNull();
    expect(textNodes(tag!).join('')).toContain('Идёт');
    // Ветка.
    const branch = findNodeByAttr(c, 'data-testid', 'feature-page-branch');
    expect(branch).not.toBeNull();
    expect(textNodes(branch!).join('')).toContain('feat/my-feature');
    // «M задач».
    const tc = findNodeByAttr(c, 'data-testid', 'feature-page-taskcount');
    expect(tc).not.toBeNull();
    expect(textNodes(tc!).join('')).toContain('5 задач');
    // ⋯.
    expect(findNodeByAttr(c, 'aria-label', 'Ещё действия')).not.toBeNull();
    // Главное действие (featureQuickAction: нет тревоги, есть ключевой чат → «Ключевой чат»).
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
    expect(main).not.toBeNull();
    expect(textNodes(main!).join('')).toContain('Ключевой чат');
  });

  it('ok: ⋯ открывает меню «Скопировать ветку» (без «Закрыть без мержа» и «Открыть эпик в «Задачах»»)', async () => {
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

  it('ok: тревога «ждёт вас» (infoBg) + кнопка «Ответить»', async () => {
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

  it('ok: тревога «стоит» (dangerBg) + кнопка «Открыть его чат»', async () => {
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

  it('ok: закрытая смержена — итог «Смержена …» (successBg)', async () => {
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

  // === Крошка (сохраняю проверки Ф2.3) ===
  it('крошка: указатель «чат → фича» → «← К чату «…»» (название из api.chats.get), onBack → chat', async () => {
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    mockChats.set('chatA', { id: 'chatA', name: 'Чат Дениса', mode: 'chat', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session);
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    const back = findNodeByAttr(c, 'data-testid', 'feature-page-back')!;
    expect(textNodes(back).join('')).toContain('К чату «Чат Дениса»');
    act(() => { (reactProps(back)!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'chat', chatId: 'chatA' });
    expect(backNavTest.getBackNav()).toBeNull();
  });

  it('крошка: десктоп, без указателя → «← Назад», onBack → list', async () => {
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    const back = findNodeByAttr(c, 'data-testid', 'feature-page-back')!;
    expect(textNodes(back).join('')).toContain('Назад');
    act(() => { (reactProps(back)!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'list' });
  });

  it('крошка: мобила, без указателя → «← К списку»', async () => {
    const c = render({ isMobile: true });
    await settle();
    const back = findNodeByAttr(c, 'data-testid', 'feature-page-back')!;
    expect(textNodes(back).join('')).toContain('К списку');
  });

  // === M2: крошка на 360 — minHeight 40 (тач-цель, §6) ===
  it('M2: крошка на 360 — minHeight 40 (тач-цель)', () => {
    const html = renderToStaticMarkup(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', isMobile: true, onBack: () => {} }));
    expect(html).toContain('min-height:40px');
  });

  // === m2: крошка по таблице §5.1 — поля, gap, вес, maxWidth, многоточие ===
  it('m2: крошка по таблице §5.1 — поля 4 8 4 4, gap 5, вес 400, maxWidth 260, многоточие', () => {
    const html = renderToStaticMarkup(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', onBack: () => {} }));
    expect(html).toContain('padding:4px 8px 4px 4px');
    expect(html).toContain('gap:5px');
    expect(html).toContain('font-weight:400');
    expect(html).toContain('max-width:260px');
    expect(html).toContain('text-overflow:ellipsis');
  });

  // === m3: крошка — фокус SHADOW.focus (класс cc-feature-page-crumb, §0) + шрифт Hanken ===
  it('m3: крошка несёт focus-класс (SHADOW.focus, §0) и шрифт Hanken (ловушка <button>)', () => {
    const html = renderToStaticMarkup(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', onBack: () => {} }));
    expect(html).toContain('cc-feature-page-crumb');
    expect(html).toContain('Hanken Grotesk');
  });

  // === Ф2.5: чаты фичи — ключевой первым ===
  it('Ф2.5: чаты фичи — ключевой чат первым, остальные по порядку индекса', async () => {
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
  it('Ф2.5: архивный ключевой чат — метка «архив»', async () => {
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
  it('Ф2.5: «Показать все» перезапрашивает getFeature с eventsLimit=all', async () => {
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
    // Кнопка «Показать все» есть (лента обрезана).
    const btn = findNodeByAttr(c, 'data-testid', 'feature-page-show-all');
    expect(btn).not.toBeNull();
    // Клик.
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    await settle();
    // Запрос с eventsLimit='all' (последний вызов).
    const lastCall = mockGetFeature.calls[mockGetFeature.calls.length - 1];
    expect(lastCall).toBeDefined();
    expect(lastCall!.eventsLimit).toBe('all');
  });

  // === Ф2.5: событие без sessionId не даёт ссылки на чат ===
  it('Ф2.5: событие без sessionId — нет ссылки «открыть чат»', async () => {
    mockGetFeature.result = pageData({
      events: [
        { at: '2026-10-19T10:00:00Z', kind: 'started', taskId: 't1', personaId: null, sessionId: null, round: null, reason: null },
      ],
      eventsTotal: 1,
    });
    const c = render({});
    await settle();
    // Ссылка на чат — нет (sessionId = null).
    const link = findNodeByAttr(c, 'data-testid', 'feature-event-chat-link-null');
    expect(link).toBeNull();
  });

  // === Ф2.5: событие С sessionId даёт ссылку ===
  it('Ф2.5: событие с sessionId — ссылка «открыть чат» на месте', async () => {
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
  it('Ф2.5: «шагов нет» — пустое состояние в блоке Шаги', async () => {
    // stepChains = [] (нет в pageData по умолчанию).
    const c = render({});
    await settle();
    const all = textNodes(c).join('');
    expect(all).toContain('Шагов пока нет');
  });

  // === M1: главное действие и тревога — в ВЕРНЫЙ чат (не в ключевой) ===
  it('M1: без тревоги, есть ключевой чат → «Ключевой чат» → onBack {kind:key, chat-key}', async () => {
    const onBack = vi.fn();
    const c = render({ onBack });
    await settle();
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action')!;
    expect(textNodes(main).join('')).toContain('Ключевой чат');
    const btn = firstButton(main as never)!;
    act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'key', chatId: 'chat-key' });
  });

  it('M1: тревога про «chat-x» (не ключевой чат) → «Ответить» → onBack {kind:chat, chat-x}', async () => {
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

  it('M1: «Ответить», но чата тревоги нет → кнопки нет (нет мёртвых действий)', async () => {
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
  it('M2: подпись шага — chain.status бэка как есть', async () => {
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

  it('M2: status отсутствует (старый ответ) → фолбэк словами бэка: pending=«ждёт ревью», returned=«закрыт без повторного ревью»', async () => {
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
  it('M3: строка чата — <button>; клик → onBack {kind:chat, chatId}', async () => {
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

  it('M3: «открыть чат» в ленте — <button>; клик → onBack {kind:chat, chatId}', async () => {
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

  // === M4: название чата вместо GUID (без новых запросов) ===
  it('M4: есть chatNames → название; GUID наружу не попадает', async () => {
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

  it('M4: названия нет (чат вне проекта / список не пришёл) → «Чат»', async () => {
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
  it('blocker: минимальный run (без steps/reviews/qa) — страница рендерится, «Задачи» на месте', async () => {
    mockGetFeature.result = {
      run: { featureId: 'f1', epic: { id: 'f1', title: 'Моя фича' } },
      events: [], eventsTotal: 0,
    } as unknown as FeaturePageData;
    const c = render({});
    await settle();
    // Страница и шапка на месте (taskCounters не упал TypeError'ом на spread).
    expect(findNodeByAttr(c, 'data-testid', 'feature-page')).not.toBeNull();
    expect(textNodes(c).join('')).toContain('Моя фича');
    // Блок «Задачи» с счётчиками (1 задача — эпик, впереди).
    expect(textNodes(c).join('')).toContain('впереди 1');
  });
});
