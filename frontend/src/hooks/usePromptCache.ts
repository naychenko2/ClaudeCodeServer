import { useEffect, useMemo, useState } from 'react';
import type { ChatItem } from '../types';
import { promptCacheState, type PromptCacheState } from '../lib/promptCache';

// Состояние prompt cache чата со своим таймером (фича «Индикатор prompt cache», шаг F-1a).
// Пересчёт — только на смену ленты (её двигает result хода) и по таймеру: стрим-дельты
// кэш не меняют, их отсекает заморозка usePromptCacheItems. Тикает ровно на смену
// показываемой минуты (остаток округляется вверх, порог «остывает» — целые минуты),
// поэтому перерисовок раз в минуту и не чаще; у остывшего и пустого состояния таймера нет.
// warnThresholdMs — точка расширения личного порога «остывает» (B-3): пока не подключена,
// подключается на шаге диалога порогов.
export function usePromptCache(items: ChatItem[], warnThresholdMs?: number): PromptCacheState | null {
  const [now, setNow] = useState(() => Date.now());
  const state = useMemo(() => {
    const s = promptCacheState(items, now, warnThresholdMs);
    // «Сейчас» из стейта отстаёт до ближайшего тика. Новый ход приносит якорь позже
    // этого «сейчас» — без зажима остаток вышел бы больше TTL
    return s && s.anchorAtMs > now ? promptCacheState(items, s.anchorAtMs, warnThresholdMs) : s;
  }, [items, now, warnThresholdMs]);

  // Новая лента (result хода, загрузка истории) — сверяем часы сразу, не дожидаясь тика
  // eslint-disable-next-line react-hooks/set-state-in-effect -- часы живут вне React
  useEffect(() => { setNow(Date.now()); }, [items]);

  // Следующий тик — на ближайшую смену минуты остатка; дальше остатка нет — не тикаем
  useEffect(() => {
    if (!state || state.remainingMs <= 0) return;
    const t = window.setTimeout(() => setNow(Date.now()), (state.remainingMs % 60_000 || 60_000) + 50);
    return () => window.clearTimeout(t);
  }, [state]);

  // Фоновая вкладка душит таймеры: вернулись — пересчитываем сразу
  useEffect(() => {
    const onVisible = () => { if (document.visibilityState === 'visible') setNow(Date.now()); };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  }, []);

  return state;
}

// Лента для индикатора кэша: пока идёт ход, держим ту, что была до него. Состояние кэша
// меняет только result хода, а стрим-дельты обновляют ленту на каждый токен — без заморозки
// шапка пересчитывала бы кэш (обход всей ленты) на каждую дельту впустую.
export function usePromptCacheItems(items: ChatItem[], isWaiting: boolean): ChatItem[] {
  const [held, setHeld] = useState(items);
  // Хранение значения прошлых рендеров — штатный приём React (setState во время рендера)
  if (!isWaiting && held !== items) setHeld(items);
  return isWaiting ? held : items;
}
