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

// Куда перешли: в чат (id чата) или на страницу фичи (id фичи).
export interface BackNavTarget {
  kind: 'chat' | 'feature';
  id: string;
}

export interface BackNav {
  // Исходный чат (id), из которого перешли.
  from: string;
  // Куда перешли (чат или страница фичи).
  to: BackNavTarget;
}

// Старая строка в хранилище (id чата) читается как переход в чат; объект — как есть.
export function normalizeBackTarget(raw: string | BackNavTarget): BackNavTarget {
  if (typeof raw === 'string') return { kind: 'chat', id: raw };
  return raw;
}

let _back: BackNav | null = null;
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => { _listeners.delete(fn); };
}

// Записать переход: из чата `from` в `to` (чат или страницу фичи).
// `to` — строка (id чата, старый формат) или объект { kind, id }.
// Переход «чат → тот же чат» (to.kind==='chat' && to.id===from) — не переход.
export function recordBackNav(from: string, to: string | BackNavTarget): void {
  const target = normalizeBackTarget(to);
  if (!from || !target.id || (target.kind === 'chat' && target.id === from)) return;
  _back = { from, to: target };
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

// «← назад» для чата `sessionId`: id исходного чата, если мы сейчас в `to`-ЧАТЕ;
// иначе null (перехода не было, открыли чат не из плашки, или `to` — страница фичи).
export function backNavFrom(sessionId: string | null): string | null {
  if (!sessionId || !_back || _back.to.kind !== 'chat' || _back.to.id !== sessionId) return null;
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
  normalizeBackTarget,
};
