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
import { getFeaturePageTab, type FeaturePageTab } from './featurePage';

// Куда перешли: в чат (id чата), на страницу фичи (id фичи) или в задачу (id задачи).
export interface BackNavTarget {
  kind: 'chat' | 'feature' | 'task';
  id: string;
}

export interface BackNav {
  // Исходный id (чат или фича), из которого перешли.
  from: string;
  // Дискриминатор: 'chat' — from = id чата (recordBackNav); 'feature' — from = id фичи
  // (recordFeatureReturn). Ф9b читает этот указатель: без дискриминатора id фичи
  // неотличим от id чата (ревью фронта 1, minor 6).
  fromKind: 'chat' | 'feature';
  // Куда перешли (чат или страница фичи).
  to: BackNavTarget;
  // Название исходной фичи (fromKind='feature'): для «← к фиче {название}» в шапке чата
  // (Ф-pl-b). null — вызывающий не передал (шапка покажет «← к фиче» без имени).
  fromTitle?: string | null;
  // Вкладка страницы фичи, с которой ушли (fromKind='feature'): чип возврата показывает её
  // название («← к фиче · Чаты»), а возврат открывает ту же вкладку (Ф9b).
  fromTab?: FeaturePageTab;
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
  _back = { from, fromKind: 'chat', to: target };
  emit();
}

// Сброс (возврат «← назад» или обычное открытие чата).
export function clearBackNav(): void {
  if (_back) { _back = null; emit(); }
}

// Точка возврата «страница фичи → чат/задача» (Ф9a, «Навигация в один клик»):
// записывает, что из страницы фичи `featureId` открыли чат/задачу `to`. Чип
// «вернуться на страницу фичи» (Ф9b) читает это и предлагает возврат. Использует
// тот же указатель _back, что и «← назад» (chat → chat/feature) — один переход
// за раз; `from` здесь — id фичи (не чата), `fromKind` = 'feature', `to.kind` — 'chat' | 'task'.
// Вкладка берётся из стора страницы в момент перехода (tab — только для тестов/явного
// указания): вызывающим вкладкам её передавать не нужно.
export function recordFeatureReturn(featureId: string, to: { kind: 'chat' | 'task'; id: string }, title?: string | null, tab?: FeaturePageTab): void {
  if (!featureId || !to.id) return;
  _back = {
    from: featureId, fromKind: 'feature', to: { kind: to.kind, id: to.id },
    fromTitle: title ?? null, fromTab: tab ?? getFeaturePageTab(),
  };
  emit();
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

// «← к фиче {название}» (Ф-pl-b): чат открыт переходом ИЗ страницы фичи (recordFeatureReturn
// с fromKind='feature') и текущий чат — цель перехода (to.kind='chat', to.id=sessionId).
// Возвращает id и название исходной фичи; null — перехода из фичи не было. Отличается от
// backNavFrom (он — chat→chat): здесь дискриминатор fromKind='feature' обязателен.
export interface FeatureBackNavInfo { featureId: string; title: string | null; tab: FeaturePageTab }
// Снимок кэшируем по ссылке на запись _back: useSyncExternalStore требует стабильного
// значения между вызовами, иначе новый объект на каждый getSnapshot зациклит рендер.
let _infoSrc: BackNav | null = null;
let _info: FeatureBackNavInfo | null = null;
export function featureBackNavFrom(sessionId: string | null): FeatureBackNavInfo | null {
  if (!sessionId || !_back || _back.fromKind !== 'feature' || _back.to.kind !== 'chat' || _back.to.id !== sessionId) return null;
  if (_infoSrc !== _back) {
    _infoSrc = _back;
    _info = { featureId: _back.from, title: _back.fromTitle ?? null, tab: _back.fromTab ?? 'timeline' };
  }
  return _info;
}

// Хук: «← к фиче {название}» для шапки чата (null, пока перехода из фичи не было).
export function useFeatureBackNav(sessionId: string | null): FeatureBackNavInfo | null {
  return useSyncExternalStore(subscribe, () => featureBackNavFrom(sessionId), () => featureBackNavFrom(sessionId));
}

// Тестовые швы (не публичное API).
export const __test = {
  reset() { _back = null; _listeners.clear(); },
  recordBackNav,
  recordFeatureReturn,
  clearBackNav,
  getBackNav,
  backNavFrom,
  featureBackNavFrom,
  normalizeBackTarget,
};
