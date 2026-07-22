import { useSyncExternalStore } from 'react';

// Закреплённые и недавние проекты для быстрого переключения в шапке (зона «Проекты»
// + палитра). Храним только id — сами проекты берём из api.projects.list(). Клиентский
// стор по образцу lib/sidebarWidth.ts: модульное состояние + подписчики + localStorage +
// синхронизация между вкладками. Серверную модель Project не трогаем.

const PINNED_KEY = 'cc_pinned_projects';   // порядок = порядок закрепления
const RECENT_KEY = 'cc_recent_projects';   // MRU-список последних открытых
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

// === Недавние (MRU) ===

// Отметить проект открытым: поднимаем в начало списка, схлопываем дубли, режем хвост.
export function recordRecentProject(id: string) {
  const next = [id, ...recent.filter(x => x !== id)].slice(0, RECENT_MAX);
  if (next.length === recent.length && next.every((x, i) => x === recent[i])) return;
  recent = next;
  persist(RECENT_KEY, recent);
  emit();
}

// === Подписка ===

function subscribe(cb: () => void) { listeners.add(cb); return () => { listeners.delete(cb); }; }
function getPinnedSnapshot() { return pinned; }
function getRecentSnapshot() { return recent; }

export function usePinnedIds(): string[] {
  return useSyncExternalStore(subscribe, getPinnedSnapshot, getPinnedSnapshot);
}
export function useRecentIds(): string[] {
  return useSyncExternalStore(subscribe, getRecentSnapshot, getRecentSnapshot);
}

// Синхронизация между вкладками
if (typeof window !== 'undefined') {
  window.addEventListener('storage', e => {
    if (e.key === PINNED_KEY) { pinned = readList(PINNED_KEY); emit(); }
    else if (e.key === RECENT_KEY) { recent = readList(RECENT_KEY); emit(); }
  });
}
