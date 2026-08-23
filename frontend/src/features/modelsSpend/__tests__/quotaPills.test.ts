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

  it('SupportsOpus=false → warn-пилюля «Без Opus»', () => {
    const pills = subscriptionPills({ ...baseSub, supportsOpus: false });
    expect(pills).toEqual([{ label: 'Без Opus', tone: 'warn' }]);
  });

  it('Supports1M=false → warn-пилюля «Без 1M»', () => {
    const pills = subscriptionPills({ ...baseSub, supports1M: false });
    expect(pills).toEqual([{ label: 'Без 1M', tone: 'warn' }]);
  });

  it('оба false → тариф + обе warn-пилюли в порядке Opus, 1M', () => {
    const pills = subscriptionPills({ ...baseSub, tier: 'Pro', supportsOpus: false, supports1M: false });
    expect(pills).toEqual([
      { label: 'Тариф: Pro', tone: 'plain' },
      { label: 'Без Opus', tone: 'warn' },
      { label: 'Без 1M', tone: 'warn' },
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
});

describe('subscriptionExpandedPills', () => {
  it('без ограничений — массив пустой (тариф рисуется отдельной строкой в раскрытии)', () => {
    expect(subscriptionExpandedPills({ ...baseSub, tier: 'Max' })).toEqual([]);
  });

  it('оба false → обе warn-пилюли (порядок важен для стабильности теста)', () => {
    const pills = subscriptionExpandedPills({ ...baseSub, supportsOpus: false, supports1M: false });
    expect(pills).toEqual([
      { label: 'Без Opus', tone: 'warn' },
      { label: 'Без 1M', tone: 'warn' },
    ]);
  });

  it('null/undefined флаги → пустой массив', () => {
    expect(subscriptionExpandedPills({ ...baseSub, supportsOpus: null })).toEqual([]);
    expect(subscriptionExpandedPills({ ...baseSub, supports1M: undefined })).toEqual([]);
  });
});

// Покрытие дыры «на мобиле пилюль нет»: buildSubscriptionCard обязана положить
// expandedPills рядом с pills. На мобиле pills в шапке скрыты (!isMobile → false),
// и без expandedPills ограничения тарифа не увидеть вообще — именно это и было в
// исходном ревью Глеба. Если кто-то случайно уберёт expandedPills из билдера —
// тест сломается.
describe('buildSubscriptionCard → expandedPills', () => {
  // Минимальный SubCtx, которого хватает buildSubscriptionCard для обеих веток
  // (с lastSnap и без). Ничего не дёргает API и не запускает хуки — чистая функция.
  const ctx = {
    rotationThreshold: 0.8,
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

  it('SupportsOpus=false — в expandedPills «Без Opus»', () => {
    const card = buildSubscriptionCard('claude', { ...baseSub, supportsOpus: false }, ctx);
    expect(card.expandedPills).toEqual([{ label: 'Без Opus', tone: 'warn' }]);
  });

  it('Supports1M=false — в expandedPills «Без 1M»', () => {
    const card = buildSubscriptionCard('claude', { ...baseSub, supports1M: false }, ctx);
    expect(card.expandedPills).toEqual([{ label: 'Без 1M', tone: 'warn' }]);
  });

  it('оба false — обе warn-пилюли в expandedPills', () => {
    const card = buildSubscriptionCard('claude',
      { ...baseSub, supportsOpus: false, supports1M: false }, ctx);
    expect(card.expandedPills).toEqual([
      { label: 'Без Opus', tone: 'warn' },
      { label: 'Без 1M', tone: 'warn' },
    ]);
  });

  // Дубль с шапкой: pills уже содержит «Тариф: …», expandedPills — нет.
  // Иначе в раскрытии тариф вылезет дважды (строкой `<Pill>Тариф: …</Pill>` + пилюлей
  // на следующей строке). Это и есть главный момент, который «нет пилюли на мобиле»
  // скрывал за собой.
  it('pills содержит «Тариф: …», а expandedPills — нет (нет дубля в раскрытии)', () => {
    const card = buildSubscriptionCard('claude',
      { ...baseSub, tier: 'Pro', supportsOpus: false, supports1M: false }, ctx);
    expect(card.pills).toEqual([
      { label: 'Тариф: Pro', tone: 'plain' },
      { label: 'Без Opus', tone: 'warn' },
      { label: 'Без 1M', tone: 'warn' },
    ]);
    expect(card.expandedPills).toEqual([
      { label: 'Без Opus', tone: 'warn' },
      { label: 'Без 1M', tone: 'warn' },
    ]);
    // И никакого «Тариф: …» в expandedPills — иначе ProviderCard нарисует дубль
    expect(card.expandedPills?.some(p => p.label.startsWith('Тариф:'))).toBe(false);
  });

  // Тот же набор проверок для ветки без lastSnap («пустая» карточка до первого хода):
  // expandedPills должны быть и там, иначе подписка без свежих снимков потеряет
  // пилюли ограничений на мобиле дважды.
  it('без lastSnap — expandedPills всё равно заполнены', () => {
    const card = buildSubscriptionCard('claude', { ...baseSub, supportsOpus: false }, ctx);
    expect(card.expandedPills).toEqual([{ label: 'Без Opus', tone: 'warn' }]);
  });
});
