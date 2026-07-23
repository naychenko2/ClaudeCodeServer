import { useSyncExternalStore } from 'react';

// Закреплённые и недавние проекты для быстрого переключения в шапке (зона «Проекты»
// + палитра). Храним только id — сами проекты берём из api.projects.list(). Клиентский
// стор по образцу lib/sidebarWidth.ts: модульное состояние + подписчики + localStorage +
// синхронизация между вкладками. Серверную модель Project не трогаем.

const PINNED_KEY = 'cc_pinned_projects';   // порядок = порядок закрепления
const RECENT_KEY = 'cc_recent_projects';   // MRU-список последних открытых
const SWITCHER_KEY = 'cc_switcher_order';  // СТАБИЛЬНЫЙ порядок незакреплённых в плашке
// Сколько значков максимум показывать в зоне таббара (остальные закреплённые — в палитре)
export const PINNED_ZONE_MAX = 4;
const RECENT_MAX = 8;

function readList(key: string): string[] {
  try {
    const raw = localStorage.getItem(key);
    if (!raw) return [];
    const arr = JSON.parse(raw);
    return Array.isArray(arr) ? arr.filter((x): x is string => typeof x === 'string') : [];
  } catch { return []; }
}

let pinned = readList(PINNED_KEY);
let recent = readList(RECENT_KEY);
// Стабильный порядок незакреплённых значков плашки. Сеем из MRU-recent при первом
// запуске (чтобы у существующих юзеров сразу были иконки), дальше — append-only.
let switcherOrder = readList(SWITCHER_KEY);
if (switcherOrder.length === 0 && recent.length > 0) {
  switcherOrder = [...recent];
  try { localStorage.setItem(SWITCHER_KEY, JSON.stringify(switcherOrder)); } catch { /* приватный режим */ }
}

const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());

function persist(key: string, list: string[]) {
  try { localStorage.setItem(key, JSON.stringify(list)); } catch { /* приватный режим */ }
}

// === Закреплённые ===

export function isPinned(id: string): boolean { return pinned.includes(id); }

export function pinProject(id: string) {
  if (pinned.includes(id)) return;
  pinned = [...pinned, id];
  persist(PINNED_KEY, pinned);
  emit();
}

export function unpinProject(id: string) {
  if (!pinned.includes(id)) return;
  pinned = pinned.filter(x => x !== id);
  persist(PINNED_KEY, pinned);
  emit();
}

export function togglePin(id: string) {
  if (pinned.includes(id)) unpinProject(id); else pinProject(id);
}

// Переставить закреплённый проект на позицию другого (drag-and-drop сортировка в зоне)
export function movePinned(dragId: string, targetId: string) {
  const from = pinned.indexOf(dragId);
  const to = pinned.indexOf(targetId);
  if (from < 0 || to < 0 || from === to) return;
  const next = [...pinned];
  next.splice(from, 1);
  next.splice(to, 0, dragId);
  pinned = next;
  persist(PINNED_KEY, pinned);
  emit();
}

// Закрепить проект, вставив его на позицию targetId среди закреплённых. Используется
// при перетаскивании недавнего проекта в зону пинов: он и закрепляется, и встаёт на
// нужное место за один жест. Уже закреплённый — просто переставляем (movePinned).
export function pinAndPlace(id: string, targetId: string) {
  if (pinned.includes(id)) { movePinned(id, targetId); return; }
  const to = pinned.indexOf(targetId);
  if (to < 0) { pinProject(id); return; }
  const next = [...pinned];
  next.splice(to, 0, id);
  pinned = next;
  persist(PINNED_KEY, pinned);
  emit();
}

// Вставить проект в закреплённые на позицию index: закрепляет, если не был, и/или
// переставляет. Используется pointer-drag'ом при дропе в зону пинов.
export function pinInsertAt(id: string, index: number) {
  const next = pinned.filter(x => x !== id);
  const at = Math.max(0, Math.min(index, next.length));
  next.splice(at, 0, id);
  pinned = next;
  persist(PINNED_KEY, pinned);
  emit();
}

// === Недавние (MRU) ===

// Отметить проект открытым: поднимаем в начало списка, схлопываем дубли, режем хвост.
export function recordRecentProject(id: string) {
  const next = [id, ...recent.filter(x => x !== id)].slice(0, RECENT_MAX);
  if (next.length === recent.length && next.every((x, i) => x === recent[i])) return;
  recent = next;
  persist(RECENT_KEY, recent);
  emit();
}

// === Стабильный порядок плашки (append-only) ===

// Отметить проект в плашке: если его там ещё нет — добавить В КОНЕЦ (существующие НЕ
// двигаем, поэтому выбор проекта не меняет порядок иконок). Хвост режем по лимиту.
export function recordSwitcherProject(id: string) {
  if (switcherOrder.includes(id)) return;
  switcherOrder = [...switcherOrder, id].slice(-RECENT_MAX);
  persist(SWITCHER_KEY, switcherOrder);
  emit();
}

// Переставить незакреплённый проект в порядке плашки: перед beforeId (или в конец при
// null). Используется pointer-drag'ом при дропе в зону недавних (реордер / после открепа).
export function switcherInsertBefore(id: string, beforeId: string | null) {
  const next = switcherOrder.filter(x => x !== id);
  if (beforeId && beforeId !== id) {
    const at = next.indexOf(beforeId);
    if (at >= 0) next.splice(at, 0, id); else next.push(id);
  } else {
    next.push(id);
  }
  switcherOrder = next;
  persist(SWITCHER_KEY, switcherOrder);
  emit();
}

// === Подписка ===

function subscribe(cb: () => void) { listeners.add(cb); return () => { listeners.delete(cb); }; }
function getPinnedSnapshot() { return pinned; }
function getRecentSnapshot() { return recent; }

function getSwitcherSnapshot() { return switcherOrder; }

export function usePinnedIds(): string[] {
  return useSyncExternalStore(subscribe, getPinnedSnapshot, getPinnedSnapshot);
}
export function useRecentIds(): string[] {
  return useSyncExternalStore(subscribe, getRecentSnapshot, getRecentSnapshot);
}
export function useSwitcherOrder(): string[] {
  return useSyncExternalStore(subscribe, getSwitcherSnapshot, getSwitcherSnapshot);
}

// Синхронизация между вкладками
if (typeof window !== 'undefined') {
  window.addEventListener('storage', e => {
    if (e.key === PINNED_KEY) { pinned = readList(PINNED_KEY); emit(); }
    else if (e.key === RECENT_KEY) { recent = readList(RECENT_KEY); emit(); }
    else if (e.key === SWITCHER_KEY) { switcherOrder = readList(SWITCHER_KEY); emit(); }
  });
}
