// Дефекты приёмки ce827f79 (карточки 401e7e56 и 95f557bd):
//
// 1) Пилюля класса на карточке подписки была недостижима: у Claude нет квотной карточки,
//    а buildSubscriptionCard не клал цену — самый дорогой класс («дорого») показать
//    было негде. Теперь карточка подписки получает класс claude с бэка (/api/models,
//    costTier) и рисует пилюлю тем же механизмом, что квотная карточка.
//
// 2) Локальный провайдер с машин-специфичным ключом терял «бесплатный»: класс неизвестен
//    (каталог /api/models не пришёл), а фронт знал локальный движок только по двум ключам.
//    Теперь признак локальности берётся с бэка (isLocal в caps провайдера) — свой список
//    локальных ключей на фронте не заводим.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { ProviderCard, type ProviderCardData } from '../ProviderCard';
import { buildSubscriptionCard } from '../QuotasTab';
import { isFreeProviderKey, providerFreeBadge } from '../../../lib/spendSources';
import type { SubscriptionUsage } from '../../../types';
import { mount, installFakeDom, uninstallFakeDom, textNodes } from '../../../components/chat/__tests__/fakeDom';

const baseSub: SubscriptionUsage = { snapshots: [] };

// Минимальный SubCtx (как в quotaPills.test.ts) + класс claude с бэка.
const ctx = {
  rotationThreshold: 0.8,
  weeklyThreshold: 0.95,
  routingTarget: undefined,
  pollStatuses: {},
  freeAvailable: true,
  subs: {},
  usageError: false,
  costTier: 'expensive',
} as const;

function cardText(data: ProviderCardData): string {
  const { container } = mount(createElement(ProviderCard, { data }));
  return textNodes(container).join('');
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('дефект 401e7e56 — пилюля класса на карточке подписки', () => {
  it('класс claude доезжает до карточки подписки', () => {
    const card = buildSubscriptionCard('claude', baseSub, ctx);
    expect(card.costTier).toBe('expensive');
  });

  it('на карточке подписки рисуется пилюля «дорого»', () => {
    const card = buildSubscriptionCard('claude', baseSub, ctx);
    const text = cardText(card);
    expect(text).toContain('дорого');
    expect(text).not.toContain('бесплатный');
    expect(text).not.toContain('дёшево');
  });

  // Та же гарантия, что у квотной карточки: цена называется один раз.
  it('две пилюли класса одновременно не рисуются', () => {
    for (const costTier of ['free', 'cheap', 'expensive']) {
      const card = buildSubscriptionCard('claude', baseSub, { ...ctx, costTier });
      const text = cardText(card);
      const classPills = ['бесплатный', 'дёшево', 'дорого'].filter(p => text.includes(p));
      expect(classPills.length).toBeLessThanOrEqual(1);
    }
  });

  // Каталог не пришёл — класс неизвестен: пилюли нет, рендер не падает.
  it('неизвестный класс — пилюли нет, карточка рендерится', () => {
    const card = buildSubscriptionCard('claude', baseSub, { ...ctx, costTier: null });
    expect(card.costTier).toBeNull();
    const text = cardText(card);
    expect(text).not.toContain('дорого');
    expect(text).not.toContain('дёшево');
    expect(text).not.toContain('бесплатный');
  });
});

describe('дефект 95f557bd — фолбэк «бесплатный» по признаку локальности с бэка', () => {
  // Локальный провайдер с нестандартным ключом: фронт его по имени не узнает,
  // признак приходит с бэка (isLocal).
  it('неизвестный класс + isLocal с бэка → провайдер бесплатный', () => {
    expect(isFreeProviderKey('local-qwen', null, true)).toBe(true);
  });

  it('без isLocal провайдер с нестандартным ключом бесплатным не считается', () => {
    expect(isFreeProviderKey('local-qwen', null, false)).toBe(false);
    expect(isFreeProviderKey('local-qwen', null)).toBe(false);
  });

  // Известный класс важнее признака локальности: класс решает.
  it('известный класс важнее isLocal', () => {
    expect(isFreeProviderKey('local-qwen', 'cheap', true)).toBe(false);
    expect(isFreeProviderKey('local-qwen', 'free', false)).toBe(true);
  });

  it('бейдж полосы и карточка говорят одно и то же', () => {
    expect(providerFreeBadge('local-qwen', null, true)).toBe('бесплатный');
    expect(providerFreeBadge('local-qwen', null, false)).toBeNull();
  });
});
