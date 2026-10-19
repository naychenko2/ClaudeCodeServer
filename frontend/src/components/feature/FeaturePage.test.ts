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
const mockGetFeature = vi.hoisted(() => ({
  pending: false,
  result: null as FeaturePageData | null,
  error: null as (Error & { status?: number }) | null,
}));

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async (id: string) => mockChats.get(id) ?? null },
    tasks: {
      featuresIndex: async () => mockIndex,
      getFeature: () => {
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

function render(props: { featureId?: string; projectId?: string | null; isMobile?: boolean; onBack?: (t: FeaturePageBackTarget) => void }) {
  installFakeDom();
  const { container } = mount(createElement(FeaturePage, {
    featureId: props.featureId ?? 'f1',
    projectId: props.projectId ?? 'p1',
    isMobile: props.isMobile ?? false,
    onBack: props.onBack ?? (() => {}),
  }));
  return container;
}

// Дождаться разрешения fetch (fake timers + microtasks) → финальное состояние страницы.
async function settle() {
  await act(async () => { await vi.runAllTimersAsync(); });
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

  it('ok: ⋯ открывает меню «Скопировать ветку» + «Открыть эпик в «Задачах»» (без «Закрыть без мержа»)', async () => {
    const c = render({});
    await settle();
    const kebab = findNodeByAttr(c, 'aria-label', 'Ещё действия')!;
    act(() => { (reactProps(kebab)!.onClick as (e: unknown) => void)({ currentTarget: { getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }) } }); });
    // Меню — портал в document.body: пункты на месте, «Закрыть без мержа» нет.
    const body = (globalThis as { document: { body: unknown } }).document.body as { childNodes: unknown[] };
    const all = textNodes(body as never).join('');
    expect(all).toContain('Скопировать ветку');
    expect(all).toContain('Открыть эпик в «Задачах»');
    expect(all).not.toContain('Закрыть без мержа');
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
});
