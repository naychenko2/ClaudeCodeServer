// Пилюля ценового класса на карточке провайдера (S4). Класс приходит с бэка
// (/api/models, costTier) — по нему карточка называет цену: «бесплатный» /
// «дёшево» / «дорого». Цена на карточке называется ОДИН раз: бесплатность уже
// названа пилюлей «бесплатный» — второй пилюли класса не рисуем. Раньше гард стоял
// на значении класса (free → пилюли нет), а бесплатность вешало локальное правило по
// ключу (lib/spendSources, freellm* — сервер про них не знает) — карточка показывала
// «бесплатный» и «дёшево» вместе.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { ProviderCard, costTierPill, type ProviderCardData } from '../ProviderCard';
import { isFreeCostTier } from '../../../lib/costTiers';
import { mount, installFakeDom, uninstallFakeDom, textNodes } from '../../../components/chat/__tests__/fakeDom';

describe('costTierPill: пилюля класса провайдера', () => {
  it('free — пилюля «бесплатный»', () => {
    expect(costTierPill('free')).toEqual({ label: 'бесплатный', tone: 'free' });
  });

  it('cheap — пилюля «дёшево»', () => {
    expect(costTierPill('cheap')).toEqual({ label: 'дёшево', tone: 'info' });
  });

  it('expensive — пилюля «дорого»', () => {
    expect(costTierPill('expensive')).toEqual({ label: 'дорого', tone: 'expensive' });
  });

  it('неизвестный класс или его отсутствие — пилюли нет, под класс не выдумываем', () => {
    expect(costTierPill(null)).toBeNull();
    expect(costTierPill(undefined)).toBeNull();
    expect(costTierPill('vip')).toBeNull();
  });
});

// Признак бесплатности на карточке определяется полем с бэка (costTier), а не локальным
// правилом по ключу провайдера: локальное знает freellm*, сервер — нет.
describe('признак бесплатности карточки — по классу с бэка', () => {
  it('бесплатным делает только класс free', () => {
    expect(isFreeCostTier('free')).toBe(true);
    expect(isFreeCostTier('cheap')).toBe(false);
    expect(isFreeCostTier('expensive')).toBe(false);
    expect(isFreeCostTier(null)).toBe(false);
    expect(isFreeCostTier(undefined)).toBe(false);
  });
});

function cardData(over: Partial<ProviderCardData> = {}): ProviderCardData {
  return {
    key: 'glm', name: 'GLM', color: 'var(--c-accent)', state: 'ready', isFree: false,
    onRetry: () => {}, windows: [], pills: [], hasExhausted: false, expandable: false,
    ...over,
  };
}

function cardText(over: Partial<ProviderCardData> = {}): string {
  const { container } = mount(createElement(ProviderCard, { data: cardData(over) }));
  return textNodes(container).join('');
}

beforeEach(() => { installFakeDom(); });
afterEach(() => { uninstallFakeDom(); });

describe('карточка провайдера: цену называем один раз', () => {
  it('класс free — только «бесплатный»', () => {
    const text = cardText({ costTier: 'free' });
    expect(text).toContain('бесплатный');
    expect(text).not.toContain('дёшево');
    expect(text).not.toContain('дорого');
  });

  it('класс cheap — только «дёшево»', () => {
    const text = cardText({ costTier: 'cheap' });
    expect(text).toContain('дёшево');
    expect(text).not.toContain('бесплатный');
    expect(text).not.toContain('дорого');
  });

  it('класс expensive — только «дорого»', () => {
    const text = cardText({ costTier: 'expensive' });
    expect(text).toContain('дорого');
    expect(text).not.toContain('бесплатный');
    expect(text).not.toContain('дёшево');
  });

  it('без класса пилюли нет — карточка не врёт', () => {
    const text = cardText();
    expect(text).not.toContain('дёшево');
    expect(text).not.toContain('дорого');
    expect(text).not.toContain('бесплатный');
  });

  // Запрещённое сочетание: бесплатность уже названа пилюлей «бесплатный», а класс
  // приехал «cheap». Второй пилюли быть НЕ должно — цена называется один раз.
  it('бесплатность уже названа — пилюли класса нет', () => {
    const text = cardText({ costTier: 'cheap', pills: [{ label: 'бесплатный', tone: 'free' }] });
    expect(text).toContain('бесплатный');
    expect(text).not.toContain('дёшево');
  });

  // freellmapi: локальное правило вешало «бесплатный», сервер отдавал «cheap».
  // Ни при каком классе обе пилюли на карточке не стоят.
  it('freellmapi: «бесплатный» и «дёшево» одновременно не рисуются', () => {
    for (const costTier of ['free', 'cheap', 'expensive']) {
      const text = cardText({ key: 'freellmapi', costTier, pills: [{ label: 'бесплатный', tone: 'free' }] });
      expect(text).toContain('бесплатный');
      expect(text).not.toContain('дёшево');
      expect(text).not.toContain('дорого');
    }
  });
});
