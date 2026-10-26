// Пилюля ценового класса на карточке провайдера (S4). Класс приходит с бэка
// (/api/models, costTier) — по нему карточка называет цену: «бесплатный» /
// «дёшево» / «дорого». Цена на карточке называется ОДИН раз: бесплатность уже
// названа пилюлей «бесплатный» — второй пилюли класса не рисуем. Правило freellm*
// живёт в Core (SpendCostTiers) — бэк freellm* уже считает бесплатным, и фронт это
// правило как источник правды не дублирует.

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { createElement } from 'react';
import { C } from 'aihome_shell/kit';
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

// Признак бесплатности на карточке определяется полем с бэка (costTier). Локальное
// правило по ключу — только откат на случай, когда каталог /api/models не пришёл
// (см. lib/spendSources и providerPricePills).
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

// Узел-элемент с ровно таким текстом (пилюля — span с одной подписью)
interface StyleNode {
  nodeType: number;
  textContent: string;
  childNodes: StyleNode[];
  style: Record<string, string>;
}

function findByText(container: unknown, text: string): StyleNode | null {
  const walk = (x: StyleNode): StyleNode | null => {
    if (x.nodeType === 1 && x.textContent === text) return x;
    for (const c of x.childNodes) {
      const hit = walk(c);
      if (hit) return hit;
    }
    return null;
  };
  return walk(container as StyleNode);
}

function styleOf(node: unknown): Record<string, string> {
  return (node as { style: Record<string, string> }).style;
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

// Контраст пилюли «дёшево»: у info нет парного текстового токена, а C.info мелким
// текстом (FS.xs) на C.infoBg светлой темы не дотягивает до 4.5:1 — текст основной,
// тон держат подложка и рамка (как в Notice).
describe('пилюля «дёшево»: контраст текста', () => {
  it('текст пилюли — основной, тон держат подложка и рамка', () => {
    const { container } = mount(createElement(ProviderCard, { data: cardData({ costTier: 'cheap' }) }));
    const pill = findByText(container, 'дёшево');
    expect(pill).toBeTruthy();
    expect(styleOf(pill).color).toBe(C.textPrimary);
    expect(styleOf(pill).background).toBe(C.infoBg);
  });
});
