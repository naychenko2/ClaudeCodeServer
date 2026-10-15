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

// Лента для индикатора кэша, замороженная по смыслу. Состояние кэша меняет только result
// хода, а стрим-дельты обновляют ленту на каждый токен — без заморозки шапка пересчитывала
// бы кэш (обход всей ленты) на каждую дельту впустую. Ключ — чат и ссылка на последний
// result: дельты его не трогают, а смена чата и подгрузка истории (F5 посреди хода) — трогают.
export interface PromptCacheFeed {
  sessionId: string | undefined;
  lastResult: ChatItem | undefined;
  items: ChatItem[];
}

function lastResultItem(items: ChatItem[]): ChatItem | undefined {
  for (let i = items.length - 1; i >= 0; i--) if (items[i].kind === 'result') return items[i];
  return undefined;
}

// Чистый шаг выбора ленты: тот же ключ — прежняя лента (та же ссылка), иначе новая
export function nextPromptCacheFeed(prev: PromptCacheFeed | null, sessionId: string | undefined, items: ChatItem[]): PromptCacheFeed {
  const lastResult = lastResultItem(items);
  if (prev && prev.sessionId === sessionId && prev.lastResult === lastResult) return prev;
  return { sessionId, lastResult, items };
}

export function usePromptCacheItems(items: ChatItem[], sessionId: string | undefined): ChatItem[] {
  const [feed, setFeed] = useState<PromptCacheFeed>(() => nextPromptCacheFeed(null, sessionId, items));
  const next = nextPromptCacheFeed(feed, sessionId, items);
  // Хранение значения прошлых рендеров — штатный приём React (setState во время рендера)
  if (next !== feed) setFeed(next);
  return next.items;
}
