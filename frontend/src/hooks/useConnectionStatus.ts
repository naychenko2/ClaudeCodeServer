import { useSyncExternalStore } from 'react';
import { getConnectionState, subscribeConnectionState } from '../lib/offline';
import type { ConnectionState } from '../lib/offline';

// Подписка на тройное состояние связи: online | degraded | offline.
// Компонентам, которым достаточно бинарного флага, — useOnline (он оставлен как есть).
export function useConnectionStatus(): ConnectionState {
  return useSyncExternalStore(subscribeConnectionState, getConnectionState, getConnectionState);
}
