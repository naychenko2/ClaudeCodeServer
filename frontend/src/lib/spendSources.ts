// Признак «бесплатный» провайдера на фронте — одна точка правды.
//
// Класс с бэка (/api/models, costTier) решает, когда он известен: правило freellm*
// живёт в Core (SpendCostTiers.IsFreeProviderKey) — источник правды там.
//
// Когда класс неизвестен (каталог не пришёл: /api/models не ответил или ответил
// без моделей), остаётся откат на локальное правило по ключу. Без отката карточка
// бесплатного провайдера теряла бы пилюлю «бесплатный», а бейдж в полосе
// «Все провайдеры» по-прежнему говорил бы «бесплатный» — две поверхности
// противоречили бы друг другу.
//
// Копия правила freellm* ниже — ТОЛЬКО этот откат (не второй источник правды):
// при расширении правила в Core откат надо продублировать здесь.
//
// Слово «локально» (локальный движок) в ценовом классе отсутствует, поэтому
// остаётся здесь — по ключу провайдера.
//
// Локальность провайдера — признак С БЭКА (isLocal в /api/models): ключи
// машин-специфичных локальных провайдеров фронту неизвестны, поэтому свой список
// локальных ключей на фронте не заводим (LOCAL_ENGINE_KEYS в lib/localEngine —
// это подписи движка, а не признак бесплатности).

import { isFreeCostTier } from './costTiers';
import { isLocalEngineKey } from './localEngine';

// Признак «бесплатный» провайдера: класс с бэка, а при неизвестном классе —
// признак локальности с бэка (isLocal) и затем откат на локальное правило
// по ключу (см. шапку модуля).
export function isFreeProviderKey(key: string, costTier?: string | null, isLocal?: boolean): boolean {
  if (costTier != null) return isFreeCostTier(costTier);
  if (isLocal) return true;
  return isLocalEngineKey(key) || key.startsWith('freellm');
}

// Бейдж полосы «Все провайдеры»: «локально» (локальный движок), «бесплатный»
// (признак выше) или null — бейджа нет.
export function providerFreeBadge(key: string, costTier?: string | null, isLocal?: boolean): string | null {
  if (isLocalEngineKey(key)) return 'локально';
  return isFreeProviderKey(key, costTier, isLocal) ? 'бесплатный' : null;
}
