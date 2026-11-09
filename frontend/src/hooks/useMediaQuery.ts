import { useCallback, useSyncExternalStore } from 'react';

// Общий примитив media-query: SSR-безопасен (на сервере — false), подписывается
// на `change` и отписывается при размонтировании. Паттерн — usePrefersReducedMotion.
// query — стабильная строка (константа модуля), поэтому subscribe/getSnapshot не
// меняют идентичность между рендерами — повторной подписки нет.
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback((onChange: () => void) => {
    const mq = typeof window !== 'undefined' ? window.matchMedia?.(query) : undefined;
    if (!mq) return () => {};
    if (mq.addEventListener) mq.addEventListener('change', onChange);
    else mq.addListener(onChange);
    return () => {
      if (mq.removeEventListener) mq.removeEventListener('change', onChange);
      else mq.removeListener(onChange);
    };
  }, [query]);
  const getSnapshot = useCallback(
    () => typeof window !== 'undefined' && !!window.matchMedia?.(query)?.matches,
    [query]
  );
  return useSyncExternalStore(subscribe, getSnapshot, () => false);
}
