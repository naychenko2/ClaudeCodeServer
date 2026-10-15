import { useEffect, useMemo, useState } from 'react';
import type { ChatItem } from '../types';
import { promptCacheState, type PromptCacheState } from '../lib/promptCache';

// Состояние prompt cache чата со своим таймером. Тикает только компонент-подписчик (чип,
// точка на мобильной пилюле, секция шторки), а не весь чат: ChatPanel о времени не знает.
// Следующий тик — ровно на смене показываемой минуты (остаток округляется вверх), поэтому
// перерисовок раз в минуту и не чаще; у остывшего и неизвестного кэша таймера нет вовсе.
export function usePromptCache(items: ChatItem[]): PromptCacheState {
  const [now, setNow] = useState(() => Date.now());
  const state = useMemo(() => {
    const s = promptCacheState(items, now);
    // «Сейчас» из стейта отстаёт до ближайшего тика, а у остывшего кэша застывает совсем.
    // Новый ход приносит якорь позже этого «сейчас» — без зажима остаток вышел бы больше TTL
    return s.anchorAtMs !== undefined && s.anchorAtMs > now ? promptCacheState(items, s.anchorAtMs) : s;
  }, [items, now]);
  const remaining = state.remainingMs;

  // Новая лента (ход, загрузка истории) — сверяем часы сразу, не дожидаясь тика
  // eslint-disable-next-line react-hooks/set-state-in-effect -- часы живут вне React
  useEffect(() => { setNow(Date.now()); }, [items]);

  useEffect(() => {
    if (remaining === undefined || remaining <= 0) return;
    const t = window.setTimeout(() => setNow(Date.now()), (remaining % 60_000 || 60_000) + 50);
    return () => window.clearTimeout(t);
  }, [remaining]);

  // Фоновая вкладка душит таймеры: вернулись — пересчитываем сразу
  useEffect(() => {
    const onVisible = () => { if (document.visibilityState === 'visible') setNow(Date.now()); };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  }, []);

  return state;
}
