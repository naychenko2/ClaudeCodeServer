import { useEffect, useMemo, useState } from 'react';
import type { ChatItem } from '../types';
import { promptCacheState, type CacheWarnThresholds, type PromptCacheState } from '../lib/promptCache';

// Стабильная пустая лента для параметра-дефолта: новый [] каждый рендер заморозка
// ловила бы в бесконечный цикл «Too many re-renders» — ссылка между рендерами
// должна быть одна (F-1b-r).
export const EMPTY_ITEMS: ChatItem[] = [];

// Состояние prompt cache чата (фича «Индикатор prompt cache и перенос в новый чат»,
// шаг F-1a). Пересчёт — только на смену ленты (её двигает result хода): остаток и
// статус держит живыми сам циферблат своим таймером, а не вся пилюля (F-1b-r).
// warnThresholds — личный порог «остывает» (B-3), применяется по ttl; не задан —
// дефолты EXPIRING_THRESHOLD_MS.
export function usePromptCache(
  items: ChatItem[] = EMPTY_ITEMS,
  warnThresholds?: CacheWarnThresholds,
): PromptCacheState | null {
  const [now, setNow] = useState(() => Date.now());
  // Новая лента (result хода, загрузка истории) — сверяем часы сразу: остаток в state
  // нужен только для aria-label, живые минуты считает циферблат своими таймером
  // eslint-disable-next-line react-hooks/set-state-in-effect -- часы живут вне React
  useEffect(() => { setNow(Date.now()); }, [items]);
  return useMemo(() => {
    const s = promptCacheState(items, now, warnThresholds);
    // «Сейчас» отстаёт от якоря нового хода: без зажима остаток вышел бы больше TTL
    return s && s.anchorAtMs > now ? promptCacheState(items, s.anchorAtMs, warnThresholds) : s;
  }, [items, now, warnThresholds]);
}

// Лента для индикатора кэша: пока идёт ход, держим ту, что была до него. Состояние кэша
// меняет только result хода, а стрим-дельты обновляют ленту на каждый токен — без заморозки
// шапка пересчитывала бы кэш (обход всей ленты) на каждую дельту впустую.
export function usePromptCacheItems(items: ChatItem[] = EMPTY_ITEMS, isWaiting: boolean): ChatItem[] {
  const [held, setHeld] = useState(items);
  // Хранение значения прошлых рендеров — штатный приём React (setState во время рендера)
  if (!isWaiting && held !== items) setHeld(items);
  return isWaiting ? held : items;
}
