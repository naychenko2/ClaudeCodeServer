// Признак «прямо сейчас идёт выкатка на бой». Модульный стор по паттерну lib/uiInspector.ts.
//
// Нужен, потому что на время публикации продукт останавливают НАМЕРЕННО, и весь интерфейс
// вокруг начинает честно сообщать о беде: индикатор связи уходит в «Офлайн», всплывает плашка
// «Доступно обновление приложения» (новый service worker как раз приехал). Всё это правда, но
// пользователю в этот момент не адресовано — он и так смотрит на заставку выкатки и знает,
// что сервер перезапускается. Флаг позволяет таким сообщениям помолчать, не отключая их логику.
//
// Ставит и снимает DeployModal; после выкатки состояние возвращается само.

import { useSyncExternalStore } from 'react';

let _deploying = false;
const _listeners = new Set<() => void>();

export function setDeployInProgress(value: boolean): void {
  if (_deploying === value) return;
  _deploying = value;
  _listeners.forEach(fn => fn());
}

export function isDeployInProgress(): boolean {
  return _deploying;
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

export function useDeployInProgress(): boolean {
  return useSyncExternalStore(subscribe, () => _deploying, () => false);
}
