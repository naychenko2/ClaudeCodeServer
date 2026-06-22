import { useSyncExternalStore } from 'react';
import { isOnline, subscribeOnline } from '../lib/offline';

// Подписка на глобальное состояние связи. Возвращает true, если онлайн.
export function useOnline(): boolean {
  return useSyncExternalStore(subscribeOnline, isOnline, isOnline);
}
