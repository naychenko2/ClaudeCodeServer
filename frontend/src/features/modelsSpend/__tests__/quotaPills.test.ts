import { describe, it, expect } from 'vitest';
import { buildSubscriptionCard, subscriptionPills, subscriptionExpandedPills } from '../QuotasTab';
import type { SubscriptionUsage } from '../../../types';

// Генерация пилюль карточки подписки — третья ось наблюдаемости (тариф + ограничения).
// Покрываем сценарии, иначе регрессия (типа «нет пилюли в раскрытии на мобиле») не
// ловится: RTL в проекте не настроен, поэтому проверяем чистые функции.
const baseSub: SubscriptionUsage = { snapshots: [] };

describe('subscriptionPills', () => {
  it('без тарифа и без флагов — массив пустой', () => {
    expect(subscriptionPills({ ...baseSub })).toEqual([]);
  });

  it('только тариф — одна plain-пилюля', () => {
    const pills = subscriptionPills({ ...baseSub, tier: 'Max' });
    expect(pills).toEqual([{ label: 'Тариф: Max', tone: 'plain' }]);
  });

  it('SupportsOpus=false → plain-пилюля «Без Opus» (ограничение — свойство тарифа, не тревога)', () => {
    const pills = subscriptionPills({ ...baseSub, supportsOpus: false });
    expect(pills).toEqual([{ label: 'Без Opus', tone: 'plain' }]);
  });

  it('Supports1M=false → plain-пилюля «Без 1M»', () => {
    const pills = subscriptionPills({ ...baseSub, supports1M: false });
    expect(pills).toEqual([{ label: 'Без 1M', tone: 'plain' }]);
  });

  // Оба ограничения — одна пилюля «Без Opus и 1M», не две: меньше ширины и одна
  // «ложная тревога» вместо двух янтарных плашек рядом с бейджем ротации.
  it('оба false → тариф + одна объединённая plain-пилюля', () => {
    const pills = subscriptionPills({ ...baseSub, tier: 'Pro', supportsOpus: false, supports1M: false });
    expect(pills).toEqual([
      { label: 'Тариф: Pro', tone: 'plain' },
      { label: 'Без Opus и 1M', tone: 'plain' },
    ]);
  });

  // Поле не пришло со старого бэка (null/undefined) — дефолт true неинформативен,
  // пилюля НЕ рисуется. Иначе карточка засыпала бы «Без Opus» у всех подписок после
  // отката на старый бэкенд.
  it('null/undefined флаги → пилюль ограничений нет', () => {
    expect(subscriptionPills({ ...baseSub, supportsOpus: null, supports1M: null })).toEqual([]);
    expect(subscriptionPills({ ...baseSub, supportsOpus: undefined, supports1M: undefined })).toEqual([]);
  });

  // true — поддержка заявлена, не ограничение, пилюли не рисуем
  it('true → пилюль ограничений нет (только тариф, если есть)', () => {
    expect(subscriptionPills({ ...baseSub, supportsOpus: true, supports1M: true, tier: 'Max' }))
      .toEqual([{ label: 'Тариф: Max', tone: 'plain' }]);
  });

  // У подписки без тарифа (tier не задан) + ограничение: третья ось наблюдаемости должна
  // быть видна на карточке без тарифа. ProviderCard дополнительно гейтит expandedPills
  // условием `data.tier || data.expandedPills?.length` — здесь проверяем, что сами
  // пилюли присутствуют в обоих массивах, чтобы ревью не потеряло их на пустой карточке.
  it('tier пустой + SupportsOpus=false → plain-пилюля ограничения в обоих массивах', () => {
    expect(subscriptionPills({ ...baseSub, supportsOpus: false }))
      .toEqual([{ label: 'Без Opus', tone: 'plain' }]);
    expect(subscriptionExpandedPills({ ...baseSub, supportsOpus: false }))
      .toEqual([{ label: 'Без Opus', tone: 'plain' }]);
  });
});

describe('subscriptionExpandedPills', () => {
  it('без ограничений — массив пустой (тариф рисуется отдельной строкой в раскрытии)', () => {
    expect(subscriptionExpandedPills({ ...baseSub, tier: 'Max' })).toEqual([]);
  });

  it('оба false → одна объединённая plain-пилюля (порядок «Opus и 1M» стабилен)', () => {
    const pills = subscriptionExpandedPills({ ...baseSub, supportsOpus: false, supports1M: false });
    expect(pills).toEqual([{ label: 'Без Opus и 1M', tone: 'plain' }]);
  });

  it('null/undefined флаги → пустой массив', () => {
    expect(subscriptionExpandedPills({ ...baseSub, supportsOpus: null })).toEqual([]);
    expect(subscriptionExpandedPills({ ...baseSub, supports1M: undefined })).toEqual([]);
  });
});

// Покрытие дыры «на мобиле пилюль нет»: buildSubscriptionCard обязана положить
// expandedPills рядом с pills. После рефакторинга шапки пилюли показываются на
// всех ширинах с переносом, но expandedPills остаётся резервом: на узких вьюпортах
// шапка читается хуже и это единственное место для карточек без тарифа (tier: null).
// Если кто-то случайно уберёт expandedPills из билдера — тест сломается.
describe('buildSubscriptionCard → expandedPills', () => {
  // Минимальный SubCtx, которого хватает buildSubscriptionCard для обеих веток
  // (с lastSnap и без). Ничего не дёргает API и не запускает хуки — чистая функция.
  // weeklyThreshold обязателен по типу SubCtx — без него IDE краснеет (CI не ловит,
  // tsc -b исключает __tests__). Берём дефолт бэка, как в QuotasTab.tsx.
  const ctx = {
    rotationThreshold: 0.8,
    weeklyThreshold: 0.95,
    routingTarget: undefined,
    pollStatuses: {},
    freeAvailable: true,
    subs: {},
    usageError: false,
  } as const;

  it('без ограничений — expandedPills пустой', () => {
    const card = buildSubscriptionCard('claude', { ...baseSub, tier: 'Max' }, ctx);
    expect(card.expandedPills).toEqual([]);
  });

  it('SupportsOpus=false — в expandedPills plain-пилюля «Без Opus»', () => {
    const card = buildSubscriptionCard('claude', { ...baseSub, supportsOpus: false }, ctx);
    expect(card.expandedPills).toEqual([{ label: 'Без Opus', tone: 'plain' }]);
  });

  it('Supports1M=false — в expandedPills plain-пилюля «Без 1M»', () => {
    const card = buildSubscriptionCard('claude', { ...baseSub, supports1M: false }, ctx);
    expect(card.expandedPills).toEqual([{ label: 'Без 1M', tone: 'plain' }]);
  });

  it('оба false — одна объединённая plain-пилюля в expandedPills', () => {
    const card = buildSubscriptionCard('claude',
      { ...baseSub, supportsOpus: false, supports1M: false }, ctx);
    expect(card.expandedPills).toEqual([{ label: 'Без Opus и 1M', tone: 'plain' }]);
  });

  // Дубль с шапкой: pills уже содержит «Тариф: …», expandedPills — нет.
  // Иначе в раскрытии тариф вылезет дважды (строкой `<Pill>Тариф: …</Pill>` + пилюлей
  // на следующей строке).
  it('pills содержит «Тариф: …», а expandedPills — нет (нет дубля в раскрытии)', () => {
    const card = buildSubscriptionCard('claude',
      { ...baseSub, tier: 'Pro', supportsOpus: false, supports1M: false }, ctx);
    expect(card.pills).toEqual([
      { label: 'Тариф: Pro', tone: 'plain' },
      { label: 'Без Opus и 1M', tone: 'plain' },
    ]);
    expect(card.expandedPills).toEqual([{ label: 'Без Opus и 1M', tone: 'plain' }]);
    // И никакого «Тариф: …» в expandedPills — иначе ProviderCard нарисует дубль
    expect(card.expandedPills?.some(p => p.label.startsWith('Тариф:'))).toBe(false);
  });

  // Ветка без lastSnap («пустая» карточка до первого хода): expandedPills должны быть
  // и там — это единственное место, где видна третья ось при tier: null.
  it('без lastSnap — expandedPills всё равно заполнены', () => {
    const card = buildSubscriptionCard('claude', { ...baseSub, supportsOpus: false }, ctx);
    expect(card.expandedPills).toEqual([{ label: 'Без Opus', tone: 'plain' }]);
  });

  // Карточка без тарифа + ограничение: у билдера tier нормализуется в null,
  // expandedPills содержат пилюлю ограничения. Без этого ProviderCard (с гейтом
  // `data.tier || data.expandedPills?.length`) рисовал бы пустой блок в раскрытии.
  it('tier не задан + SupportsOpus=false — expandedPills содержат ограничение, card.tier === null', () => {
    const card = buildSubscriptionCard('claude',
      { ...baseSub, supportsOpus: false }, ctx);
    expect(card.tier).toBeNull();
    expect(card.expandedPills).toEqual([{ label: 'Без Opus', tone: 'plain' }]);
    // Условие ProviderCard должно пройти: tier пуст, но expandedPills есть — блок
    // в раскрытии рендерится, иначе ограничения не видны нигде
    expect(card.tier || (card.expandedPills && card.expandedPills.length)).toBeTruthy();
  });
});
