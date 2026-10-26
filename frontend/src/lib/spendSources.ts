// Признак «бесплатный» провайдера на фронте — одна точка правды.
//
// Класс с бэка (/api/models, costTier) решает, когда он известен: правило freellm*
// живёт в Core (SpendCostTiers.IsFreeProviderKey), и фронт его НЕ дублирует —
// бэк freellm* уже считает бесплатным.
//
// Когда класс неизвестен (каталог не пришёл: /api/models не ответил или ответил
// без моделей), остаётся откат на локальное правило по ключу. Без отката карточка
// бесплатного провайдера теряла бы пилюлю «бесплатный», а бейдж в полосе
// «Все провайдеры» по-прежнему говорил бы «бесплатный» — две поверхности
// противоречили бы друг другу.
//
// Слово «локально» (локальный движок) в ценовом классе отсутствует, поэтому
// остаётся здесь — по ключу провайдера.

import { isFreeCostTier } from './costTiers';
import { isLocalEngineKey } from './localEngine';

// Признак «бесплатный» провайдера: класс с бэка, а при неизвестном классе —
// откат на локальное правило по ключу (см. шапку модуля).
export function isFreeProviderKey(key: string, costTier?: string | null): boolean {
  if (costTier != null) return isFreeCostTier(costTier);
  return isLocalEngineKey(key) || key.startsWith('freellm') || key.endsWith('-direct');
}

// Бейдж полосы «Все провайдеры»: «локально» (локальный движок), «бесплатный»
// (признак выше) или null — бейджа нет.
export function providerFreeBadge(key: string, costTier?: string | null): string | null {
  if (isLocalEngineKey(key)) return 'локально';
  return isFreeProviderKey(key, costTier) ? 'бесплатный' : null;
}
