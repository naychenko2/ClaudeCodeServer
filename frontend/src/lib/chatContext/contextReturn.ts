// «Назад» к прежнему основному объекту (замена returnTo панелей, ADR-023 §Д1): состояние вида на
// фронте, ключ — сессия. Пишут вклады вертикалей («Сочинить под фильм», меню кадра), читает
// секция «С чем» панели; новым основным объектом «Работать с этой» ссылка снимается.

import { useSyncExternalStore } from 'react';
import type { ContextReturn } from './types';

const _returns = new Map<string, ContextReturn>();
const _listeners = new Set<() => void>();
let _version = 0;

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}
const subscribe = (fn: () => void) => { _listeners.add(fn); return () => { _listeners.delete(fn); }; };
const getVersion = () => _version;

export function setContextReturn(sessionId: string, r: ContextReturn) {
  _returns.set(sessionId, r);
  emit();
}

export function clearContextReturn(sessionId: string) {
  if (_returns.delete(sessionId)) emit();
}

export const getContextReturn = (sessionId: string | null): ContextReturn | null =>
  (sessionId && _returns.get(sessionId)) || null;

export function useContextReturn(sessionId: string | null): ContextReturn | null {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  return getContextReturn(sessionId);
}

// Выход из аккаунта: ссылки прежнего владельца вкладка не держит
export function resetContextReturns() {
  if (!_returns.size) return;
  _returns.clear();
  emit();
}
