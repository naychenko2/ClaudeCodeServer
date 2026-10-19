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

  it('без указателя, есть ключевой чат: крошка «← К ключевому чату», onBack → key', () => {
    const onBack = vi.fn();
    const c = render(onBack);
    const back = findNodeByAttr(c, 'data-testid', 'feature-page-back');
    expect(back).not.toBeNull();
    expect(textNodes(back!).join('')).toContain('К ключевому чату');
    act(() => { reactProps(back!)!.onClick(); });
    expect(onBack).toHaveBeenCalledWith({ kind: 'key', chatId: 'chat-key' });
  });

  it('без указателя и без ключевого: крошка «← К списку», onBack → list', () => {
    mockIndex.features = [feat({ keySessionId: null, keyProjectId: null })];
    const onBack = vi.fn();
    const c = render(onBack);
    const back = findNodeByAttr(c, 'data-testid', 'feature-page-back');
    expect(back).not.toBeNull();
    expect(textNodes(back!).join('')).toContain('К списку');
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
});
