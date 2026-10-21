// Плашка фичи в чате (FeatureBanner) — СТРОКА с точками этапов (макет feature-banner-1,
// вариант «А · Строка» + кликабельные этапы из «Г»). Три вопроса строки:
//   • ГДЕ МЫ     — 7 точек этапов + подпись «{этап} · {состояние}» (слова 1:1 с pillModel/попапом);
//   • ЧТО ОТ МЕНЯ — одно главное действие (waiting → «Ответить», stopped → «Чат»,
//                    merge_pending → «Открыть», иначе → «Открыть страницу фичи»);
//   • КТО РАБОТАЕТ — чип роли этого чата («этот чат — {роль}» / «ключевой»).
//
// Два уровня:
//   1 — ЧИСТАЯ модель featureBannerRow (where/действие/тон/роль) — без рендера,
//      табличный прогон по 9 состояниям FeatureState.
//   2 — РЕНДЕР панели с НАСТОЯЩИМИ данными списка (стор индекса через __test.setIndex,
//      не прямой проп): SSR (подпись/действие/тон по каждому состоянию) + mount
//      (клик по точке → cc-open-feature, «впереди» не кликабельно).
//
// Окружение node (без jsdom): SSR — renderToStaticMarkup; mount — fakeDom.
// Стор индекса и флаги заполняются напрямую (setIndex / setAllFlags) — useEffect-фетч
// в SSR не идёт, компонент читает уже заполненный стор.

import { createElement, act } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, beforeEach, afterEach, vi } from 'vitest';
import { FeatureBanner, featureBannerRow } from './FeatureBanner';
import { __test } from '../../../lib/featuresIndex';
import { setAllFlags } from '../../../lib/featureFlags';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps } from '../__tests__/fakeDom';
import { __test as runsTest } from '../../../lib/featureRuns';
import type { FeatureAttention, FeatureChatLink, FeatureIndexFeature, FeaturesIndex } from '../../../types/featureIndex';
import type { FeatureState } from '../../../types/featureRun';

// m7: управляемый индекс фич — mock `api.tasks.featuresIndex` возвращает этот объект,
// чтобы live-перезапрос (useFeaturesIndex) не затирал синхронный сид стора.
const mockIdx = vi.hoisted(() => ({
  features: [] as FeatureIndexFeature[],
  chats: {} as Record<string, FeatureChatLink[]>,
}));
// Персоны: управляемый список для getPersonaById (имя исполнителя в «Ответить {имя}»).
const mockPersonas = vi.hoisted(() => [] as { id: string; name: string }[]);
// === Мок lib/api: featuresIndex → mockIdx ===
vi.mock('../../../lib/api', () => ({
  api: {
    sessions: { list: async () => [] },
    chats: { list: async () => [] },
    tasks: {
      execute: async () => {},
      featuresIndex: async () => mockIdx,
      getFeatures: async () => [],
    },
    personas: { list: async () => [] },
  },
}));
// === Мок signalr: onMessage/onPreviewMessage — no-op (иначе getConnection() строит
// реальное соединение '/hubs/session', которое в node не резолвится) ===
vi.mock('../../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
// === Мок lib/personas: ensurePersonasLoaded — no-op; getPersonaById — из mockPersonas ===
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, ensurePersonasLoaded: async () => {}, getPersonaById: (id: string) => mockPersonas.find(p => p.id === id) };
});

// === Фикстуры ===

// Рельса 7 стадий: cut/plan пройдены, prod — активная (run), остальное — впереди.
function rail(): FeatureIndexFeature['rail'] {
  return [
    { key: 'cut', state: 'done', tone: null },
    { key: 'plan', state: 'done', tone: null },
    { key: 'prod', state: 'active', tone: 'run' },
    { key: 'review', state: 'pending', tone: null },
    { key: 'qa', state: 'pending', tone: null },
    { key: 'final', state: 'pending', tone: null },
    { key: 'merge', state: 'pending', tone: null },
  ];
}

// Строка фичи: по умолчанию running (prod активна). `state` — переопределяемый (9 значений).
function feat(state: FeatureState = 'running', over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', attention: null, state,
    rail: rail(),
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 1,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chat-key', keyArchived: false, keyProjectId: 'p1', chatCount: 2,
    ...over,
  };
}

function attention(kind: FeatureAttention['kind'], over: Partial<FeatureAttention> = {}): FeatureAttention {
  return { kind, since: null, reason: null, personaId: null, sessionId: null, taskId: null, ...over };
}

// Связь «этот чат × фича»: по умолчанию исполнитель Ш2 · бэк.
function link(over: Partial<FeatureChatLink> = {}): FeatureChatLink {
  return { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк', ...over };
}

// Заполнить стор индекса одним проектом: одна фича + связь «чат × фича».
function seedIndex(f: FeatureIndexFeature, l: FeatureChatLink, chatId: string, projectKey: string) {
  const index: FeaturesIndex = { features: [f], chats: { [chatId]: [l] } };
  __test.setIndex(projectKey, index);
}

// === 1: чистая модель featureBannerRow — подпись/действие/тон/роль по 9 состояниям ===

describe('featureBannerRow: «где мы», действие, тон, роль', () => {
  // Таблица: state → (подпись, действие, тон). Активная стадия фикстуры — prod.
  const cases: { state: FeatureState; where: string; action: string; kind: 'reply' | 'chat' | 'open' | 'page'; tone: 'run' | 'wait' | 'stop' | 'closed' }[] = [
    { state: 'not_started', where: 'Не начата', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
    { state: 'running', where: 'Производство · идёт', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
    { state: 'waiting', where: 'Производство · ждёт вас', action: 'Ответить', kind: 'reply', tone: 'wait' },
    { state: 'stopped', where: 'Производство · стоит', action: 'Чат', kind: 'chat', tone: 'stop' },
    { state: 'merge_pending', where: 'ждёт согласия на мерж', action: 'Открыть', kind: 'open', tone: 'wait' },
    { state: 'merging', where: 'Мерж · идёт', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
    { state: 'merged', where: 'Смержена', action: 'Открыть страницу фичи', kind: 'page', tone: 'closed' },
    { state: 'closed', where: 'Закрыта без мержа', action: 'Открыть страницу фичи', kind: 'page', tone: 'closed' },
    { state: 'idle', where: 'Простаивает', action: 'Открыть страницу фичи', kind: 'page', tone: 'run' },
  ];

  it.each(cases)('state=$state → «$where», действие «$action» ($kind), тон $tone', c => {
    const m = featureBannerRow(feat(c.state), link(), 'chat-exec');
    expect(m.where).toBe(c.where);
    expect(m.action.label).toBe(c.action);
    expect(m.action.kind).toBe(c.kind);
    expect(m.tone).toBe(c.tone);
  });

  it('merged с датой: «Смержена {дд.мм}» (локальным геттером от той же Date)', () => {
    const f = feat('merged', { summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } });
    const d = new Date('2026-10-18T12:00:00Z');
    const date = `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}`;
    expect(featureBannerRow(f, link(), 'chat-exec').where).toBe(`Смержена ${date}`);
  });

  it('waiting с персоной: «Ответить {имя}» (имя — из стора персон)', () => {
    mockPersonas.length = 0;
    mockPersonas.push({ id: 'p-kira', name: 'Кира' });
    const f = feat('waiting', { attention: attention('wait', { personaId: 'p-kira' }) });
    expect(featureBannerRow(f, link(), 'chat-exec').action.label).toBe('Ответить Кира');
  });

  it('stopped с персоной: «Чат {имя}»', () => {
    mockPersonas.length = 0;
    mockPersonas.push({ id: 'p-denis', name: 'Денис' });
    const f = feat('stopped', { attention: attention('stopped', { personaId: 'p-denis' }) });
    expect(featureBannerRow(f, link(), 'chat-exec').action.label).toBe('Чат Денис');
  });

  it('роль: исполнитель step → «Ш2 · бэк»; lane «back» (англ.) нормализуется в «бэк»', () => {
    expect(featureBannerRow(feat('running'), link(), 'chat-exec').role).toBe('Ш2 · бэк');
    expect(featureBannerRow(feat('running'), link({ lane: 'back' }), 'chat-exec').role).toBe('Ш2 · бэк');
  });

  it('роль: куратор без stepRef/lane → «куратор» (фолбэк на вид связи)', () => {
    expect(featureBannerRow(feat('running'), link({ kind: 'curator', stepRef: null, lane: null }), 'chat-cur').role).toBe('куратор');
  });

  it('этот чат — ключевой (sessionId === keySessionId): роль «ключевой», isKey', () => {
    const m = featureBannerRow(feat('running', { keySessionId: 'chat-key' }), link(), 'chat-key');
    expect(m.isKey).toBe(true);
    expect(m.role).toBe('ключевой');
  });

  it('точки: done/active кликабельны, pending/absent — нет (7 точек, порядок — от бэка)', () => {
    const m = featureBannerRow(feat('running'), link(), 'chat-exec');
    expect(m.dots).toHaveLength(7);
    expect(m.dots.map(d => d.key)).toEqual(['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge']);
    expect(m.dots.filter(d => d.clickable).map(d => d.key)).toEqual(['cut', 'plan', 'prod']);
  });
});

// === 2: рендер панели с настоящими данными списка (SSR) ===

function renderBanner(sessionId: string, projectId: string | null, isMobile = false): string {
  return renderToStaticMarkup(createElement(FeatureBanner, { sessionId, projectId, isMobile }));
}

describe('FeatureBanner: рендер с данными списка (SSR)', () => {
  beforeEach(() => {
    __test.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    setAllFlags({});
  });

  it('идёт: тон run, «Производство · идёт», действие «Открыть страницу фичи», роль «Ш2 · бэк»', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-banner"');
    expect(html).toContain('data-banner-tone="run"');
    expect(html).toContain('Производство · идёт');
    expect(html).toContain('Открыть страницу фичи');
    expect(html).toContain('Ш2 · бэк');
  });

  it('ждёт вас: тон wait (C.infoBg), «Производство · ждёт вас», «Ответить»', () => {
    seedIndex(feat('waiting', { attention: attention('wait') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="wait"');
    expect(html).toContain('var(--c-info-bg)');
    expect(html).toContain('Производство · ждёт вас');
    expect(html).toContain('Ответить');
  });

  it('стоит: тон stop (C.dangerBg), «Производство · стоит», «Чат»', () => {
    seedIndex(feat('stopped', { attention: attention('stopped') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="stop"');
    expect(html).toContain('var(--c-danger-bg)');
    expect(html).toContain('Производство · стоит');
    expect(html).toContain('Чат');
  });

  it('к мержу: тон wait, «ждёт согласия на мерж», действие «Открыть» (НЕ «Мерж…»)', () => {
    seedIndex(feat('merge_pending', { attention: attention('merge') }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="wait"');
    expect(html).toContain('ждёт согласия на мерж');
    expect(html).toContain('Открыть');
    // «Мерж…» — только в попапе и на странице, НЕ на панели (задача, п.2).
    expect(html).not.toContain('Мерж…');
  });

  it('смержена: серая (C.bgInset), «Смержена {дата}»', () => {
    seedIndex(feat('merged', { summary: { mergedAt: '2026-10-18T12:00:00Z', durationMinutes: 30 } }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="closed"');
    expect(html).toContain('var(--c-bg-inset)');
    expect(html).toContain('Смержена ');
  });

  it('закрыта без мержа: «Закрыта без мержа», тон closed', () => {
    seedIndex(feat('closed', { summary: null }), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-banner-tone="closed"');
    expect(html).toContain('Закрыта без мержа');
  });

  it('не начата: «Не начата»; простаивает: «Простаивает»; merging: «Мерж · идёт»', () => {
    seedIndex(feat('not_started'), link(), 'chat-exec', 'p1');
    expect(renderBanner('chat-exec', 'p1')).toContain('Не начата');
    seedIndex(feat('idle'), link(), 'chat-exec', 'p1');
    expect(renderBanner('chat-exec', 'p1')).toContain('Простаивает');
    seedIndex(feat('merging'), link(), 'chat-exec', 'p1');
    expect(renderBanner('chat-exec', 'p1')).toContain('Мерж · идёт');
  });

  it('чат без фичи: плашки нет (null)', () => {
    seedIndex(feat('running'), link(), 'other-chat', 'p1');
    expect(renderBanner('nope', 'p1')).toBe('');
  });

  it('оба флага выключены: плашки нет (null), даже если фича в сторе', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    setAllFlags({ 'turn-plan-feature-mode': false, 'features-overview': false });
    expect(renderBanner('chat-exec', 'p1')).toBe('');
  });

  it('точки: 7 data-testid feature-dot-*, «впереди» (pending) — <i>, не <button>', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    // Все 7 точек на месте (data-testid у каждой).
    for (const k of ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge']) {
      expect(html).toContain(`data-testid="feature-dot-${k}"`);
    }
    // «Впереди» (pending) — <i>, не <button>: тег-открывашка сразу перед data-testid.
    const i = html.indexOf('data-testid="feature-dot-review"');
    expect(i, 'нет точки review').not.toBe(-1);
    expect(html.slice(Math.max(0, i - 20), i), 'pending review должен быть <i>').toContain('<i');
    // Текущий (active) — <button> (type="button" перед data-testid).
    const b = html.indexOf('data-testid="feature-dot-prod"');
    expect(html.slice(Math.max(0, b - 40), b), 'active prod должен быть <button>').toContain('<button');
  });

  it('«· ещё N»: чат с двумя фичами → «· ещё 1» (порядок — от бэка)', () => {
    const f1 = feat('running', { keySessionId: 'chat-key' });
    const f2 = feat('running', { featureId: 'f2', title: 'Вторая фича', keySessionId: 'chat-key2' });
    const index: FeaturesIndex = {
      features: [f1, f2],
      chats: { 'chat-exec': [
        { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' },
        { featureId: 'f2', kind: 'curator', taskRole: null, stepRef: null, lane: null },
      ] },
    };
    __test.setIndex('p1', index);
    const html = renderBanner('chat-exec', 'p1');
    expect(html).toContain('data-testid="feature-more"');
    expect(html).toContain('· ещё 1');
  });

  it('чип «ключевой»: font-weight 700 + accent/accentLight (макет .role.key)', () => {
    seedIndex(feat('running', { keySessionId: 'chat-key' }), link({ kind: 'orchestrator', stepRef: null, lane: null }), 'chat-key', 'p1');
    const html = renderBanner('chat-key', 'p1');
    expect(html).toContain('font-weight:700;color:var(--c-accent);background:var(--c-accent-light)');
  });

  it('центрирование по колонке чата: обёртка — CSS-переменные useChatGutter (fallback 950px) + margin-left:auto', () => {
    seedIndex(feat('running'), link(), 'chat-exec', 'p1');
    const html = renderBanner('chat-exec', 'p1');
    // Обёртка (data-testid=feature-banner-wrap) читает ширину/сдвиг из CSS-переменных,
    // которые ставит useChatGutter; в SSR (хук не идёт) виден только fallback 950px.
    expect(html).toContain('data-testid="feature-banner-wrap"');
    expect(html).toContain('max-width:var(--cc-chat-box-w, 950px)');
    expect(html).toContain('margin-left:auto');
  });
});

// === 3: клик по точке → cc-open-feature (mount, реальный DOM) ===

describe('FeatureBanner: клик по точке → переход (mount)', () => {
  beforeEach(() => {
    installFakeDom();
    // useChatGutter (обёртка баннера) создаёт ResizeObserver — в node/fakeDom его нет:
    // заглушка, иначе mount упал бы на `new ResizeObserver`.
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} };
    __test.reset();
    runsTest.reset();
    setAllFlags({ 'turn-plan-feature-mode': true, 'features-overview': true });
  });
  afterEach(() => {
    __test.reset();
    runsTest.reset();
    setAllFlags({});
    uninstallFakeDom();
  });

  it('клик по текущему этапу (prod) → cc-open-feature { featureId, fromChatId, fromChat, stage: prod }', async () => {
    const f = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    // Перехват window.dispatchEvent (fake window — no-op): записать cc-open-feature.
    const dispatched: { type: string; detail: unknown }[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
      (e: Event) => { dispatched.push({ type: e.type, detail: (e as CustomEvent).detail }); return true; };

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const dot = findNodeByAttr(container, 'data-testid', 'feature-dot-prod');
    expect(dot, 'нет точки prod').not.toBeNull();
    const props = reactProps(dot!);
    expect(props?.onClick, 'у точки prod нет onClick').toBeDefined();
    act(() => { (props!.onClick as () => void)(); });

    const ev = dispatched.find(d => d.type === 'cc-open-feature');
    expect(ev, 'cc-open-feature не диспатчен').toBeDefined();
    expect(ev!.detail).toMatchObject({ featureId: 'f1', fromChatId: 'chat-exec', fromChat: true, stage: 'prod' });
    root.unmount();
  });

  it('«впереди» (pending) не кликабельно: <i> без onClick (клик не диспатчит cc-open-feature)', async () => {
    const f = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    const dispatched: { type: string }[] = [];
    (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent =
      (e: Event) => { dispatched.push({ type: e.type }); return true; };

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // review — pending: тег <i>, без onClick (в отличие от done/active — <button> + onClick).
    const pending = findNodeByAttr(container, 'data-testid', 'feature-dot-review');
    expect(pending, 'нет точки review').not.toBeNull();
    expect(pending!.nodeName, 'pending должен быть <i>, не <button>').toBe('I');
    expect(reactProps(pending!)?.onClick, 'у pending не должно быть onClick').toBeUndefined();
    // Клик по pending (если бы onClick был) не диспатчит cc-open-feature.
    const onClick = reactProps(pending!)?.onClick as (() => void) | undefined;
    if (onClick) act(() => { onClick(); });
    expect(dispatched.some(d => d.type === 'cc-open-feature'), 'pending не должен диспатчить переход').toBe(false);
    root.unmount();
  });

  it('ширина = колонка чата: useChatGutter ставит --cc-chat-box-w на обёртку (mode=center, десктоп)', async () => {
    const f = feat('running', { keySessionId: 'chat-key' });
    const index: FeaturesIndex = {
      features: [f],
      chats: { 'chat-exec': [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк' }] },
    };
    __test.setIndex('p1', index);
    mockIdx.features = index.features;
    mockIdx.chats = index.chats;

    const { root, container } = mount(createElement(FeatureBanner, { sessionId: 'chat-exec', projectId: 'p1', isMobile: false }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const wrap = findNodeByAttr(container, 'data-testid', 'feature-banner-wrap');
    expect(wrap, 'нет обёртки feature-banner-wrap').not.toBeNull();
    // useChatGutter (mode=center) пишет --cc-chat-box-w на обёртку. Мутация «убрать
    // useChatGutter» → переменная не стоит → тест краснеет (SSR-тест выше её не видит).
    const w = (wrap! as unknown as { style: Record<string, string> }).style['--cc-chat-box-w'];
    expect(w, 'useChatGutter не поставил --cc-chat-box-w').toBeTruthy();
    root.unmount();
  });
});
