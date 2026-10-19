// «← назад» в плашке ключевого чата (Ф4, «Обзор фич»): откуда пришли, в памяти вкладки.
//
// Записывается, когда из плашки чата фичи (кнопка «Ключевой чат →») перешли в ключевой
// чат. Плашка ключевого чата читает: показывает «← <роль исходного чата>» и возвращает
// в исходный чат одним кликом.
//
// Живёт в памяти вкладки (не localStorage) — как режим/выбор фичи в featureRuns.
// Сбрасывается: (1) по «← назад» (возврат в исходный чат); (2) при обычном открытии
// чата, не из плашки (эффект FeatureBanner: записанный переход ведёт в другой чат).
//
// Один указатель (не per-session): «← назад» — про ТОТ переход, что только что сделали.
// Фронт ничего не сортирует и не считает: порядок и роль исходного чата даёт индекс фич.

import { useSyncExternalStore } from 'react';

export interface BackNav {
  // Исходный чат (id), из которого перешли в ключевой.
  from: string;
  // Куда перешли (id ключевого чата).
  to: string;
}

let _back: BackNav | null = null;
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Записать переход: из чата `from` в ключевой чат `to`. `from === to` — не переход.
export function recordBackNav(from: string, to: string): void {
  if (!from || !to || from === to) return;
  _back = { from, to };
  emit();
}

// Сброс (возврат «← назад» или обычное открытие чата).
export function clearBackNav(): void {
  if (_back) { _back = null; emit(); }
}

// Сырой указатель (для эффекта «сброс при обычном открытии» в FeatureBanner).
export function getBackNav(): BackNav | null {
  return _back;
}

// «← назад» для чата `sessionId`: id исходного чата, если мы сейчас в `to`; иначе null
// (перехода не было или открыли чат не из плашки).
export function backNavFrom(sessionId: string | null): string | null {
  if (!sessionId || !_back || _back.to !== sessionId) return null;
  return _back.from;
}

// Хук: id исходного чата для «← назад» (null, пока перехода не было).
export function useBackNavFrom(sessionId: string | null): string | null {
  return useSyncExternalStore(subscribe, () => backNavFrom(sessionId), () => backNavFrom(sessionId));
}

// Тестовые швы (не публичное API).
export const __test = {
  reset() { _back = null; _listeners.clear(); },
  recordBackNav,
  clearBackNav,
  getBackNav,
  backNavFrom,
};
