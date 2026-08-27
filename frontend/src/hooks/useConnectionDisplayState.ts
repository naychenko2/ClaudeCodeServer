import { useSyncExternalStore } from 'react';
import {
  getConnectionDisplayState,
  subscribeConnectionDisplayState,
  type ConnectionDisplayState,
} from '../lib/offline';

// Подписка на трёхступенчатое display-состояние связи (с гистерезисом):
// 'online' | 'unstable' | 'offline'. useSyncExternalStore сам отсеивает
// неизменившийся снимок, лишних ререндеров нет.
export function useConnectionDisplayState(): ConnectionDisplayState {
  return useSyncExternalStore(
    subscribeConnectionDisplayState,
    getConnectionDisplayState,
    getConnectionDisplayState,
  );
}
