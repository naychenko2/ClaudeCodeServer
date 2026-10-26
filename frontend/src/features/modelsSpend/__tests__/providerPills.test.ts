// Производитель пилюли цены на карточке провайдера (providerPricePills в QuotasTab).
// Провод правила: раньше мутация «вернуть локальное правило по ключу» в этом месте
// оставалась зелёной — производитель пилюли не был покрыт тестом.
//
// Правило после дельты: признак «бесплатный» ведёт класс с бэка (costTier);
// при неизвестном классе (/api/models не ответил) — откат на локальное правило по ключу,
// иначе карточка теряла бы пилюлю, а бейдж полосы по-прежнему говорил бы «бесплатный».
// Правило freellm* живёт в Core (SpendCostTiers) — фронт его не дублирует.

import { describe, it, expect } from 'vitest';
import { providerPricePills } from '../QuotasTab';
import { isFreeProviderKey, providerFreeBadge } from '../../../lib/spendSources';

describe('providerPricePills — пилюля цены карточки провайдера', () => {
  it('класс free — ровно одна пилюля «бесплатный», никакой «дёшево»', () => {
    const pills = providerPricePills('glm', 'free');
    expect(pills.filter(p => p.label === 'бесплатный')).toHaveLength(1);
    expect(pills.some(p => p.label === 'дёшево')).toBe(false);
    expect(pills.some(p => p.label === 'дорого')).toBe(false);
  });

  // Класс с бэка решает: «cheap» у ключа freellm* означает, что провайдер платный.
  // Локальное правило по ключу в этом месте не применяется — иначе «бесплатный» и
  // «дёшево» оказались бы на карточке одновременно (цена не названа вовсе).
  it('класс cheap у ключа freellm* — пилюли «бесплатный» нет', () => {
    const pills = providerPricePills('freellmapi', 'cheap');
    expect(pills.some(p => p.label === 'бесплатный')).toBe(false);
  });

  it('класс expensive — пилюли цены нет', () => {
    expect(providerPricePills('glm', 'expensive')).toEqual([]);
  });

  // Каталог не пришёл — класс неизвестен. Тогда признак берётся из локального
  // отката: карточка не теряет пилюлю «бесплатный» и не противоречит бейджу полосы.
  it('неизвестный класс — откат на локальное правило по ключу', () => {
    expect(providerPricePills('freellmapi', null)).toEqual([{ label: 'бесплатный', tone: 'free' }]);
    expect(providerPricePills('glm', null)).toEqual([]);
  });

  // Признак локальности с бэка (isLocal): локальный провайдер с машин-специфичным
  // ключом фронту по имени не узнать — свой список ключей на фронте не заводим.
  it('неизвестный класс + isLocal с бэка — пилюля «бесплатный»', () => {
    expect(providerPricePills('local-qwen', null, true)).toEqual([{ label: 'бесплатный', tone: 'free' }]);
    expect(providerPricePills('local-qwen', null, false)).toEqual([]);
  });
});

describe('признак бесплатности — одна точка правды на фронте', () => {
  it('класс с бэка важнее локального правила по ключу', () => {
    expect(isFreeProviderKey('freellmapi', 'free')).toBe(true);
    expect(isFreeProviderKey('freellmapi', 'cheap')).toBe(false);
    expect(isFreeProviderKey('freellmapi', 'expensive')).toBe(false);
  });

  it('неизвестный класс — откат на локальное правило', () => {
    expect(isFreeProviderKey('freellmapi', null)).toBe(true);
    expect(isFreeProviderKey('glm', null)).toBe(false);
  });

  // Платный прямой адаптер не бесплатный; freellm*-direct — бесплатный (решение Андрея, 26.10).
  it('неизвестный класс — платный *-direct не бесплатный, freellm*-direct бесплатный', () => {
    expect(isFreeProviderKey('minimax-direct', null)).toBe(false);
    expect(isFreeProviderKey('freellmapi-direct', null)).toBe(true);
  });

  it('бейдж полосы и карточка говорят одно и то же', () => {
    // Класс известен — бейдж «бесплатный» ровно у бесплатного класса.
    expect(providerFreeBadge('freellmapi', 'free')).toBe('бесплатный');
    expect(providerFreeBadge('freellmapi', 'cheap')).toBeNull();
    // Класс неизвестен — обе поверхности берут локальный откат и не расходятся.
    expect(providerFreeBadge('freellmapi', null)).toBe('бесплатный');
    expect(providerFreeBadge('glm', null)).toBeNull();
    // Слово «локально» — локальное правило, в классе его нет.
    expect(providerFreeBadge('ollama', 'free')).toBe('локально');
  });
});
