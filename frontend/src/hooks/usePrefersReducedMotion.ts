import { useSyncExternalStore } from 'react';

// Настройка ОС «уменьшить движение» с подпиской: смена настройки перерисует компонент
// сразу, а не при следующем случайном рендере (как было бы с matchMedia прямо в рендере)
const QUERY = '(prefers-reduced-motion: reduce)';

function subscribe(onChange: () => void): () => void {
  const mq = typeof window !== 'undefined' ? window.matchMedia?.(QUERY) : undefined;
  if (!mq) return () => {};
  mq.addEventListener('change', onChange);
  return () => mq.removeEventListener('change', onChange);
}

const getSnapshot = () => typeof window !== 'undefined' && !!window.matchMedia?.(QUERY).matches;

export function usePrefersReducedMotion(): boolean {
  return useSyncExternalStore(subscribe, getSnapshot, () => false);
}
