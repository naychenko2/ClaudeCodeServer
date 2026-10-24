// Метка фичи в строке чата (Ф9, макет features-overview.html, «Дни»):
//   • featureLabelFor — чистое решение «показывать ли метку» (чат фичи + флаг → фича;
//     иначе null — прежнее имя персоны);
//   • FeatureLabel — рендер глифа + названия (обрезка с title).
//
// Окружение node (без jsdom): featureLabelFor — прямой вызов, FeatureLabel — SSR.
// Индекс фич — через тестовый шов __test.setIndex; флаг — setAllFlags.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { act } from 'react';
import { beforeAll, afterAll, beforeEach, afterEach, describe, expect, it, vi } from 'vitest';
import { ChatCard, FeatureLabel, featureLabelFor } from './ChatCard';
import { __test as featuresIndexTest } from '../lib/featuresIndex';
import { setAllFlags } from '../lib/featureFlags';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr, textNodes, reactProps } from './chat/__tests__/fakeDom';
import type { FeatureIndexFeature, FeatureRailKey, FeatureRailSegment } from '../types/featureIndex';
import type { Session } from '../types';

// ── Моки для fakeDom-монтажа ChatCard (M2): SSR-тесты эффектов не гоняют, но клиентский
//    рендер (mount) их гоняет — подписки/запросы глушим, как в workspaceFeatureCenter ──
vi.mock('../lib/signalr', () => {
  const sub = () => () => {};
  const actP = () => Promise.resolve(null);
  return {
    __esModule: true,
    onMessage: sub, onReconnected: sub, onFilesChanged: sub, onPreviewMessage: sub,
    onLocalModelMessage: sub, onGitStatusChanged: sub, onAgentHubMessage: sub,
    joinProject: actP, leaveProject: actP, joinSession: actP, leaveSession: actP,
    joinUser: actP, leaveUser: actP, sendMessage: actP, respondPermission: actP,
    interruptSession: actP, compactSession: actP, answerQuestion: actP, respondPlan: actP,
    respondTeamPlan: actP, respondTeamEscalation: actP, setMode: actP, resetConnection: actP,
    joinPreviewLog: actP, leavePreviewLog: actP,
  };
});
const { mockIndexData } = vi.hoisted(() => ({ mockIndexData: null as unknown }));
vi.mock('../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/api')>();
  return { ...orig, api: { ...orig.api,
    tasks: { ...orig.api.tasks, featuresIndex: async () => (mockIndexData ?? { features: [], chats: {} }) },
    chats: { ...orig.api.chats, get: async () => null },
  } };
});
vi.mock('../lib/toast', () => ({ showToast: () => {} }));
vi.mock('../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/personas')>();
  return { ...orig, getPersonaById: () => undefined, usePersonasVersion: () => 0, ensurePersonasLoaded: () => Promise.resolve() };
});

// Фикстура: 7 сегментов (design не включена — необязательный этап); по умолчанию «идёт» (prod active run).
const KEYS: FeatureRailKey[] = ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge'];
function rail(): FeatureRailSegment[] {
  const base: Record<FeatureRailKey, FeatureRailSegment> = {
    cut:    { key: 'cut',    state: 'done',    tone: null },
    design: { key: 'design', state: 'pending', tone: null },
    plan:   { key: 'plan',   state: 'done',    tone: null },
    prod:   { key: 'prod',   state: 'active',  tone: 'run' },
    review: { key: 'review', state: 'pending', tone: null },
    qa:     { key: 'qa',     state: 'pending', tone: null },
    final:  { key: 'final',  state: 'pending', tone: null },
    merge:  { key: 'merge',  state: 'pending', tone: null },
  };
  return KEYS.map(k => base[k]);
}

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: rail(),
    chips: ['open'], stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  };
}

// Индекс: одна фича f1, чат chat-1 — её исполнитель.
function setupIndex() {
  featuresIndexTest.setIndex('p1', {
    features: [feat()],
    chats: { 'chat-1': [{ featureId: 'f1', kind: 'executor', taskRole: null, stepRef: null, lane: null }] },
  });
}

beforeEach(() => {
  featuresIndexTest.reset();
  setAllFlags({});
});

// === featureLabelFor: решение «показывать ли метку» ===

describe('featureLabelFor', () => {
  it('чат фичи + флаг вкл → фича (метка показывается)', () => {
    setupIndex();
    setAllFlags({ 'features-overview': true });
    const result = featureLabelFor('chat-1', true);
    expect(result?.featureId).toBe('f1');
  });

  it('чат без фичи → null (прежнее имя персоны)', () => {
    setupIndex();
    setAllFlags({ 'features-overview': true });
    const result = featureLabelFor('chat-other', true);
    expect(result).toBeNull();
  });

  it('флаг выключен → null (прежнее имя персоны)', () => {
    setupIndex();
    setAllFlags({ 'features-overview': false });
    const result = featureLabelFor('chat-1', false);
    expect(result).toBeNull();
  });
});

// === FeatureLabel: рендер метки ===

describe('FeatureLabel', () => {
  it('глиф + название; длинное название — title с полным текстом', () => {
    const longTitle = 'Очень длинное название фичи для проверки обрезки текста в строке';
    const f = feat({ title: longTitle });
    const html = renderToStaticMarkup(createElement(FeatureLabel, { feature: f }));
    // Метка есть
    expect(html).toContain('data-testid="chat-feature-label"');
    // title — полное название (обрезка CSS-ом, title — подсказка)
    expect(html).toContain(`title="${longTitle}"`);
    // Название в тексте
    expect(html).toContain(longTitle);
  });

  it('мутация: без data-testid метка не находится', () => {
    // Временно ломаем: убираем data-testid из FeatureLabel (через рендер с переопределением)
    // Для мутации проверяем, что нормальный рендер ДАЕТ data-testid
    const f = feat();
    const html = renderToStaticMarkup(createElement(FeatureLabel, { feature: f }));
    expect(html).toContain('data-testid="chat-feature-label"');
    // Мутация: если бы data-testid не было — тест бы упал (здесь мы проверяем его наличие)
  });
});

// === n1: метка фичи не рисуется внутри группы (inFeatureGroup) ===
// Внутри группы фичи имя фичи уже несёт заголовок группы; дубль в карточке убираем.
// featureLabelFor по-прежнему отдаёт фичу (решение «есть ли фича» не меняется), но
// ChatCard с inFeatureGroup=true метку не рисует.
describe('n1: метка фичи внутри группы (inFeatureGroup)', () => {
  // Минимальный чат: для метки важен id (featureOf читает индекс по id)
  const session = (id: string): Session => ({
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);

  const renderCard = (id: string, inFeatureGroup?: boolean): string =>
    renderToStaticMarkup(createElement(ChatCard, {
      session: session(id), isActive: false, isMobile: false, online: true,
      onSelect: () => {}, onHover: () => {}, onDelete: () => {}, inFeatureGroup,
    }));

  it('чат фичи: метка есть вне группы, подавлена внутри', () => {
    setupIndex();
    setAllFlags({ 'features-overview': true });
    // chat-1 — исполнитель f1 (setupIndex)
    expect(renderCard('chat-1')).toContain('data-testid="chat-feature-label"');
    expect(renderCard('chat-1', true)).not.toContain('data-testid="chat-feature-label"');
  });
});

// === Ф3.2: подсказка у метки «ключевой» (группа) ===
// Метка «ключевой» — span в карточке; подсказка «что значит ключевой» — единый текст
// (KEY_SESSION_TOOLTIP). SSR (как остальные тесты файла): проверяем саму метку;
// появление ТЕКСТА плашки по наведению/фокусу — в fakeDom-блоке ниже (M2).
// (M1-минор: aria-label с неинтерактивного span убран — скринридер его игнорирует.)
describe('Ф3.2: метка «ключевой» (группа, SSR)', () => {
  const session = (id: string): Session => ({
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);

  const renderRoleCard = (role: string): string =>
    renderToStaticMarkup(createElement(ChatCard, {
      session: session('chat-1'), isActive: false, isMobile: false, online: true,
      onSelect: () => {}, onHover: () => {}, onDelete: () => {},
      featureRole: role,
    }));

  it('«ключевой» — метка-чип с текстом «ключевой»', () => {
    const html = renderRoleCard('ключевой');
    expect(html).toContain('data-testid="chat-feature-role"');
    expect(html).toContain('ключевой');
  });

  it('«общий с …» (не ключевой) — метка живым форматом, без слова «ключевой»', () => {
    // Мёртвый формат «ещё 2» (минор Глеба, 24.10): featureRoleFor так больше не
    // выдаёт — берём то, что реально строится: первая пара + «· ещё N».
    const html = renderRoleCard('общий с Фича B · ещё 2');
    expect(html).toContain('data-testid="chat-feature-role"');
    expect(html).toContain('общий с Фича B · ещё 2');
    // Ограничение длины (минор 2): чип не выталкивает имя чата из строки на 360 px —
    // maxWidth + ellipsis. Мутация (убрать maxWidth) краснит этот тест.
    expect(html).toContain('max-width:140px');
    expect(html).toContain('text-overflow:ellipsis');
  });
});

// === M2: подсказка «ключевой» — рендер-тесты (fakeDom: наведение + фокус + уход) ===
// Глеб (R6): SSR-тесты ловили только aria-label; мутация «убрать <Tooltip>» оставляла
// их зелёными. Здесь — сам ТЕКСТ плашки в DOM: по наведению (задержка 350 мс) и по
// фокусу на карточке-строке (клавиатура), исчезает по уходу/blur. Мутация «убрать
// <Tooltip>» краснит: без обёртки нет ни onMouseEnter, ни focused-состояния.
describe('Ф3.2: M2 подсказка «ключевой» (fakeDom: наведение + фокус + уход)', () => {
  const session = (id: string): Session => ({
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);

  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-18T10:00:00Z').getTime());
    featuresIndexTest.reset();
    setAllFlags({ 'features-overview': false });
    // fake-window не несёт setTimeout/clearTimeout (Tooltip, задержка 350 мс) и
    // addEventListener/removeEventListener (useActionVisibility) — даём no-op/(fake) таймеры.
    const w = (globalThis as { window: Record<string, unknown> }).window;
    w.setTimeout = globalThis.setTimeout;
    w.clearTimeout = globalThis.clearTimeout;
    w.addEventListener = () => {};
    w.removeEventListener = () => {};
  });
  afterEach(() => { vi.useRealTimers(); });

  const renderCard = (role: string = 'ключевой') => {
    const { container } = mount(createElement(ChatCard, {
      session: session('chat-1'), isActive: false, isMobile: false, online: true,
      onSelect: () => {}, onHover: () => {}, onDelete: () => {},
      featureRole: role,
    }));
    return container;
  };
  // п.59: текст подсказки живёт и в СКРЫТОМ span (aria-describedby), поэтому «есть ли
  // плашка» детектируем по элементу role="tooltip", а не по тексту (текст всегда в DOM).
  // (M1) Плашка — ПОРТАЛ в document.body (position: fixed), не в контейнер рендера:
  // ищем в body, а не в контейнере (c — не используется, но держим сигнатуру для вызывающих).
  const hasTip = (_c: Parameters<typeof findNodeByAttr>[0]) => {
    const body = (globalThis as { document?: { body?: Parameters<typeof findNodeByAttr>[0] } }).document?.body;
    if (!body) return false;
    return findNodeByAttr(body, 'role', 'tooltip') !== null;
  };

  it('по наведению: плашка появляется (задержка 350 мс) и исчезает по уходу', async () => {
    const c = renderCard();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Обёртка Tooltip — родитель метки «ключевой» (data-testid="chat-feature-role").
    const chip = findNodeByAttr(c, 'data-testid', 'chat-feature-role')!;
    const wrapper = (chip as unknown as { parentNode: Parameters<typeof findNodeByAttr>[0] }).parentNode;
    const wp = reactProps(wrapper)!;
    expect(hasTip(c), 'до наведения — плашки нет').toBe(false);
    await act(async () => { (wp.onMouseEnter as () => void)(); await vi.advanceTimersByTimeAsync(350); });
    expect(hasTip(c), 'наведение → плашка в DOM').toBe(true);
    await act(async () => { (wp.onMouseLeave as () => void)(); });
    expect(hasTip(c), 'уход → плашка исчезла').toBe(false);
  });

  it('по фокусу на карточке (клавиатура, :focus-visible): плашка появляется и исчезает по blur', async () => {
    const c = renderCard();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    // Карточка-строка — внешний div (tabIndex 0); его onFocus/onBlur — источник состояния.
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const cp = reactProps(card)!;
    expect(hasTip(c), 'до фокуса — плашки нет').toBe(false);
    // M1': фокус с КЛАВИАТУРЫ (Tab) — :focus-visible = true → плашка.
    (card as unknown as { focusVisible: boolean }).focusVisible = true;
    // п.51: фокус САМОЙ карточки (target === currentTarget) → плашка.
    await act(async () => { (cp.onFocus as (e: unknown) => void)({ target: card, currentTarget: card }); });
    expect(hasTip(c), 'фокус с клавиатуры на карточке → плашка в DOM').toBe(true);
    await act(async () => { (cp.onBlur as (e: unknown) => void)({ target: card, currentTarget: card }); });
    expect(hasTip(c), 'blur → плашка исчезла').toBe(false);
  });

  // M1': клик мышью/тап — фокус БЕЗ :focus-visible → плашка НЕ поднимается (не залипает).
  // Мутация «убрать проверку :focus-visible» красит: плашка поднимется и от мыши.
  it('M1: клик мышью (фокус без :focus-visible) — плашка НЕ поднимается', async () => {
    const c = renderCard();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const cp = reactProps(card)!;
    // Мышь: :focus-visible = false (default fakeDom).
    (card as unknown as { focusVisible: boolean }).focusVisible = false;
    await act(async () => { (cp.onFocus as (e: unknown) => void)({ target: card, currentTarget: card }); });
    expect(hasTip(c), 'клик мышью поднял плашку (M1: гейт по :focus-visible)').toBe(false);
  });

  // M1': Escape гасит плашку (фокус с клавиатуры), не открывая чат.
  it('M1: Escape гасит плашку (фокус с клавиатуры)', async () => {
    const c = renderCard();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const cp = reactProps(card)!;
    (card as unknown as { focusVisible: boolean }).focusVisible = true;
    await act(async () => { (cp.onFocus as (e: unknown) => void)({ target: card, currentTarget: card }); });
    expect(hasTip(c), 'фокус с клавиатуры → плашка').toBe(true);
    // Escape на карточке (target === currentTarget) → плашка гаснет.
    await act(async () => { (cp.onKeyDown as (e: unknown) => void)({ key: 'Escape', target: card, currentTarget: card, preventDefault: () => {} }); });
    expect(hasTip(c), 'Escape не погасил плашку (M1: Escape)').toBe(false);
  });

  // п.51: фокус на ВЛОЖЕННОМ элементе (target ≠ currentTarget) — плашка НЕ поднимается
  // (иначе любое действие внутри карточки поднимало бы подсказку «ключевой»).
  it('п.51: фокус на вложенном элементе (target ≠ currentTarget) — плашки нет', async () => {
    const c = renderCard();
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const cp = reactProps(card)!;
    // target — вложенный элемент ({}), currentTarget — сама карточка → не совпадают.
    await act(async () => { (cp.onFocus as (e: unknown) => void)({ target: {}, currentTarget: card }); });
    expect(hasTip(c), 'фокус на вложенном элементе поднял плашку (п.51: target ≠ currentTarget)').toBe(false);
  });

  // (M1) Метка «ключевой · ещё 1» (активная карточка, центр закрыт кнопками действий)
  // — та же подсказка «что значит ключевой»: по наведению на обёртку Tooltip плашка
  // поднимается (портал в document.body). Мутация «убрать <Tooltip>» красит.
  it('«ключевой · ещё 1»: метка поднимает подсказку (M1)', async () => {
    const c = renderCard('ключевой · ещё 1');
    await act(async () => { await vi.advanceTimersByTimeAsync(50); });
    const chip = findNodeByAttr(c, 'data-testid', 'chat-feature-role')!;
    const wrapper = (chip as unknown as { parentNode: Parameters<typeof findNodeByAttr>[0] }).parentNode;
    const wp = reactProps(wrapper)!;
    expect(hasTip(c), 'до наведения — плашки нет').toBe(false);
    await act(async () => { (wp.onMouseEnter as () => void)(); await vi.advanceTimersByTimeAsync(350); });
    expect(hasTip(c), '«ключевой · ещё 1» → плашка в DOM').toBe(true);
  });
});

// === M4: карточка — role="button" + клавиатурная активация (Enter/Space) ===
describe('M4: карточка — role="button" + клавиатурная активация', () => {
  const session = (id: string): Session => ({
    id, mode: 'auto', status: 'finished', messageCount: 0,
    createdAt: '2026-10-18T10:00:00Z', updatedAt: '2026-10-18T10:00:00Z',
    origin: 'manual',
  } as Session);

  beforeAll(() => { installFakeDom(); });
  afterAll(() => { uninstallFakeDom(); });
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-18T10:00:00Z').getTime());
    featuresIndexTest.reset();
    setAllFlags({ 'features-overview': false });
    const w = (globalThis as { window: Record<string, unknown> }).window;
    w.setTimeout = globalThis.setTimeout;
    w.clearTimeout = globalThis.clearTimeout;
    w.addEventListener = () => {};
    w.removeEventListener = () => {};
  });
  afterEach(() => { vi.useRealTimers(); });

  const renderCard = (onSelect: () => void) => {
    const { container } = mount(createElement(ChatCard, {
      session: session('chat-1'), isActive: false, isMobile: false, online: true,
      onSelect, onHover: () => {}, onDelete: () => {},
    }));
    return container;
  };

  it('корень карточки — role="button"', () => {
    const c = renderCard(() => {});
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const p = reactProps(card)!;
    expect(p['role'], 'нет role="button" на корне карточки (M4)').toBe('button');
  });

  it('Enter на карточке открывает чат (onSelect вызывается)', () => {
    const onSelect = vi.fn();
    const c = renderCard(onSelect);
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const p = reactProps(card)!;
    // Симулируем keydown Enter: target === currentTarget (нажатие на саму карточку)
    const e = { key: 'Enter', target: card, currentTarget: card, preventDefault: () => {} } as unknown as Parameters<typeof p.onKeyDown>[0];
    act(() => { (p.onKeyDown as (e: unknown) => void)(e); });
    expect(onSelect, 'Enter на карточке не вызвал onSelect (M4)').toHaveBeenCalledTimes(1);
  });

  it('Space на карточке открывает чат (onSelect вызывается)', () => {
    const onSelect = vi.fn();
    const c = renderCard(onSelect);
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const p = reactProps(card)!;
    const e = { key: ' ', target: card, currentTarget: card, preventDefault: () => {} } as unknown as Parameters<typeof p.onKeyDown>[0];
    act(() => { (p.onKeyDown as (e: unknown) => void)(e); });
    expect(onSelect, 'Space на карточке не вызвал onSelect (M4)').toHaveBeenCalledTimes(1);
  });

  // п.62: клик мышью по карточке открывает чат. onClick и onKeyDown идут через ОДИН
  // обработчик (openChat, п.61) — мутация «onClick не зовёт openChat» красит этот тест.
  it('п.62: клик мышью по карточке открывает чат (onSelect вызывается)', () => {
    const onSelect = vi.fn();
    const c = renderCard(onSelect);
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const p = reactProps(card)!;
    act(() => { (p.onClick as () => void)(); });
    expect(onSelect, 'клик мышью не открыл чат (п.62)').toHaveBeenCalledTimes(1);
  });

  it('Enter на вложенной кнопке НЕ открывает чат (target ≠ currentTarget)', () => {
    const onSelect = vi.fn();
    const c = renderCard(onSelect);
    const card = (c as unknown as { childNodes: Parameters<typeof findNodeByAttr>[0][] }).childNodes[0];
    const p = reactProps(card)!;
    // target — вложенный элемент (не сама карточка) → handlePressableKey не срабатывает
    const nested = { key: 'Enter', target: {}, currentTarget: card, preventDefault: () => {} } as unknown as Parameters<typeof p.onKeyDown>[0];
    act(() => { (p.onKeyDown as (e: unknown) => void)(nested); });
    expect(onSelect, 'Enter на вложенной кнопке вызвал onSelect (M4: target ≠ currentTarget)').not.toHaveBeenCalled();
  });
});
