// Пилюля ценового класса на карточке провайдера (S4). Класс приходит с бэка
// (/api/models, costTier): рядом с пилюлей «бесплатный» человек видит, дёшево это
// или дорого. Словарь «какая модель какого класса» на фронте не держим — красим по
// ключу, который прислал бэк.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { ProviderCard, costTierPill, type ProviderCardData } from '../ProviderCard';
import { mount, installFakeDom, uninstallFakeDom, textNodes } from '../../../components/chat/__tests__/fakeDom';

describe('costTierPill: пилюля класса провайдера', () => {
  it('cheap — пилюля «дёшево»', () => {
    expect(costTierPill('cheap')).toEqual({ label: 'дёшево', tone: 'info' });
  });

  it('expensive — пилюля «дорого»', () => {
    expect(costTierPill('expensive')).toEqual({ label: 'дорого', tone: 'expensive' });
  });

  // Бесплатность провайдера уже выражена пилюлей «бесплатный»: вторую не заводим.
  it('free — пилюли класса нет (бесплатный уже назван)', () => {
    expect(costTierPill('free')).toBeNull();
  });

  it('неизвестный класс или его отсутствие — пилюли нет, под класс не выдумываем', () => {
    expect(costTierPill(null)).toBeNull();
    expect(costTierPill(undefined)).toBeNull();
    expect(costTierPill('vip')).toBeNull();
  });
});

function cardData(over: Partial<ProviderCardData> = {}): ProviderCardData {
  return {
    key: 'glm', name: 'GLM', color: 'var(--c-accent)', state: 'ready', isFree: false,
    onRetry: () => {}, windows: [], pills: [], hasExhausted: false, expandable: false,
    ...over,
  };
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('карточка провайдера: пилюля класса рядом с «бесплатным»', () => {
  it('у провайдера с costTier cheap показана пилюля «дёшево»', () => {
    const { container } = mount(createElement(ProviderCard, {
      data: cardData({ costTier: 'cheap', pills: [{ label: 'бесплатный', tone: 'free' }] }),
    }));
    expect(textNodes(container).join('')).toContain('дёшево');
  });

  it('у провайдера с costTier expensive — пилюля «дорого»', () => {
    const { container } = mount(createElement(ProviderCard, {
      data: cardData({ costTier: 'expensive' }),
    }));
    expect(textNodes(container).join('')).toContain('дорого');
  });

  it('у провайдера без класса пилюли нет — карточка не врёт', () => {
    const { container } = mount(createElement(ProviderCard, { data: cardData() }));
    const text = textNodes(container).join('');
    expect(text).not.toContain('дёшево');
    expect(text).not.toContain('дорого');
  });
});
