// Страница фичи (волна 2 «Обзор фич», шаг Ф2.3): рендер-тест шапки-заглушки и крошки.
//
// Крошка читает указатель возврата (featureBackNav):
//   • пришли из чата (back.to = эта фича) — «← К чату «…»» (название — из api.chats.get);
//   • указателя нет, есть ключевой чат — «← К ключевому чату»;
//   • указателя и ключевого нет — «← К списку».
// Клик — onBack(target) + clearBackNav.
//
// Окружение node (без jsdom): fakeDom (mount) + управляемые сторы (featurePage,
// featuresIndex, featureBackNav) и mock api.chats.get.

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

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async (id: string) => mockChats.get(id) ?? null },
    tasks: { featuresIndex: async () => mockIndex, getFeature: async () => ({ run: { epic: { title: 'x' } }, events: [], eventsTotal: 0 } as FeaturePageData) },
  },
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});

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
    ...over,
  };
}

function render(onBack: (t: FeaturePageBackTarget) => void) {
  installFakeDom();
  const { container } = mount(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', onBack }));
  return container;
}

describe('FeaturePage: крошка и шапка', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    pageTest.reset();
    indexTest.reset();
    backNavTest.reset();
    setAllFlags({ 'features-overview': true });
    mockIndex.features = [feat()];
    mockIndex.chats = {};
    mockChats.clear();
    indexTest.setIndex('p1', mockIndex as FeaturesIndex);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('указатель «чат → фича»: крошка «← К чату «…»» (название из api.chats.get), onBack → chat', async () => {
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    mockChats.set('chatA', { id: 'chatA', name: 'Чат Дениса', mode: 'chat', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session);
    const onBack = vi.fn();
    const c = render(onBack);
    // до ответа api.chats.get — нейтральная подпись «К чату»
    let back = findNodeByAttr(c, 'data-testid', 'feature-page-back');
    expect(back).not.toBeNull();
    // после ответа — с названием чата
    await act(async () => { await vi.runAllTimersAsync(); });
    back = findNodeByAttr(c, 'data-testid', 'feature-page-back');
    const text = textNodes(back!).join('');
    expect(text).toContain('К чату «Чат Дениса»');
    // клик → onBack({ kind: 'chat', chatId: 'chatA' }) + clearBackNav
    act(() => { reactProps(back!)!.onClick(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'chat', chatId: 'chatA' });
    expect(backNavTest.getBackNav()).toBeNull();
  });

  // m4: без указателя (вход из списка/диплинк) крошка НЕ ведёт в ключевой чат, а в список:
  // десктоп — «Назад», мобила — «К списку» (макет §6). Ключевой чат — отдельная кнопка в теле.
  it('m4: десктоп, без указателя: крошка «← Назад», onBack → list (не в ключевой чат)', () => {
    const onBack = vi.fn();
    const c = render(onBack);
    const back = findNodeByAttr(c, 'data-testid', 'feature-page-back');
    expect(back).not.toBeNull();
    expect(textNodes(back!).join('')).toContain('Назад');
    expect(textNodes(back!).join('')).not.toContain('К ключевому чату');
    act(() => { reactProps(back!)!.onClick(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'list' });
  });

  it('m4: мобила, без указателя: крошка «← К списку» (макет §6)', () => {
    installFakeDom();
    const { container } = mount(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', isMobile: true, onBack: () => {} }));
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back).not.toBeNull();
    expect(textNodes(back!).join('')).toContain('К списку');
  });

  it('без указателя и без ключевого: крошка «← Назад» (десктоп), onBack → list', () => {
    mockIndex.features = [feat({ keySessionId: null, keyProjectId: null })];
    const onBack = vi.fn();
    const c = render(onBack);
    const back = findNodeByAttr(c, 'data-testid', 'feature-page-back');
    expect(back).not.toBeNull();
    expect(textNodes(back!).join('')).toContain('Назад');
    act(() => { reactProps(back!)!.onClick(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'list' });
  });

  it('шапка: название фичи из индекса', () => {
    const onBack = vi.fn();
    const c = render(onBack);
    const page = findNodeByAttr(c, 'data-testid', 'feature-page');
    expect(page).not.toBeNull();
    expect(textNodes(page!).join('')).toContain('Моя фича');
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

  // === m3: крошка — фокус SHADOW.focus (класс cc-feature-crumb, §0) + шрифт Hanken ===
  it('m3: крошка несёт focus-класс (SHADOW.focus, §0) и шрифт Hanken (ловушка <button>)', () => {
    const html = renderToStaticMarkup(createElement(FeaturePage, { featureId: 'f1', projectId: 'p1', onBack: () => {} }));
    // Фокус: кнопка несёт focus-класс (инжект :focus-visible → outline:none + SHADOW.focus).
    expect(html).toContain('cc-feature-crumb');
    // §0: нативная <button> не наследует шрифт — задаём Hanken явно.
    expect(html).toContain('Hanken Grotesk');
  });
});
