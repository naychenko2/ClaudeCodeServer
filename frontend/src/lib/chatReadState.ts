// Состояние «прочитанности» чатов — per-device, хранится в localStorage.
// Ключи: cc_chat_read_{chatId} = timestamp последнего открытия чата.
//
// Чат «непрочитанный», если его updatedAt новее времени последнего прочтения,
// то есть после нашего последнего визита в него пришли сообщения.
//
// Стор реактивный: значения кешируются в памяти (иначе бейдж дёргал бы
// localStorage по разу на чат при каждом рендере, а список чатов перечитывается
// поллингом каждые 5с), подписчики уведомляются через useSyncExternalStore.
//
// MVP: localStorage. Потом можно мигрировать на бэк (поле lastReadAt в Session,
// API POST /api/chats/{id}/read) для синхронизации между устройствами.
import { useSyncExternalStore } from 'react';

const KEY_PREFIX = 'cc_chat_read_';
// Базовая отметка «когда это устройство впервые увидело список чатов».
// Namespace НАРОЧНО другой (cc_chats_, не cc_chat_): иначе ключ попал бы под
// KEY_PREFIX и распознавался как чат с id «since».
const KEY_SINCE = 'cc_chats_read_since';

// --- подписка (useSyncExternalStore) ---
const listeners = new Set<() => void>();
let version = 0;

function subscribe(fn: () => void): () => void {
  listeners.add(fn);
  return () => { listeners.delete(fn); };
}
function getSnapshot(): number {
  return version;
}
function emit(): void {
  version++;
  listeners.forEach(fn => fn());
}

// --- кеш значений в памяти ---
let cache: Map<string, number> | null = null;

function readCache(): Map<string, number> {
  if (cache) return cache;
  const m = new Map<string, number>();
  try {
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i);
      if (!k?.startsWith(KEY_PREFIX)) continue;
      const v = Number(localStorage.getItem(k));
      if (Number.isFinite(v) && v > 0) m.set(k.slice(KEY_PREFIX.length), v);
    }
  } catch { /* приватный режим — работаем без персиста */ }
  cache = m;
  return m;
}

// Момент, начиная с которого чаты вообще могут считаться непрочитанными.
// Без него у нового устройства ВСЕ существующие чаты разом стали бы
// непрочитанными (их никто на этом устройстве не открывал) — бейдж показал бы
// весь список. Отметка ставится один раз, при первом обращении.
let baseline: number | null = null;

function readBaseline(): number {
  if (baseline !== null) return baseline;
  try {
    const stored = localStorage.getItem(KEY_SINCE);
    if (stored) {
      const v = Number(stored);
      if (Number.isFinite(v) && v > 0) return (baseline = v);
    }
    const now = Date.now();
    localStorage.setItem(KEY_SINCE, String(now));
    return (baseline = now);
  } catch {
    return (baseline = Date.now());
  }
}

// Отметить чат прочитанным — сейчас.
// Вызывать при открытии чата (selectChat), после отправки сообщения, при создании.
export function markChatRead(chatId: string): void {
  const now = Date.now();
  readCache().set(chatId, now);
  try {
    localStorage.setItem(KEY_PREFIX + chatId, String(now));
  } catch { /* квота/приватный режим — молча, кеш в памяти уже обновлён */ }
  emit();
}

// Время последнего прочтения чата (timestamp ms). 0 — чат ни разу не открывали.
export function getChatReadTime(chatId: string): number {
  return readCache().get(chatId) ?? 0;
}

// Есть ли в чате непрочитанные сообщения.
// Чат, который на этом устройстве не открывали, сверяется с baseline: созданный
// после первого запуска (например персоной или задачей) считается непрочитанным,
// а существовавший до — нет.
export function hasUnread(updatedAt: string, chatId: string): boolean {
  const readTime = getChatReadTime(chatId) || readBaseline();
  const updatedTime = new Date(updatedAt).getTime();
  if (!Number.isFinite(updatedTime)) return false;
  return updatedTime > readTime;
}

// Сколько чатов имеют непрочитанные сообщения — для бейджа на иконке рельсы.
export function countUnreadChats(chats: { id: string; updatedAt: string }[]): number {
  return chats.reduce((n, c) => n + (hasUnread(c.updatedAt, c.id) ? 1 : 0), 0);
}

// Реактивный бейдж: подписка гарантирует ререндер при отметке прочтения — без
// неё markChatRead писал бы мимо React, и число обновлялось бы только случайно,
// на постороннем ререндере. Сам подсчёт не мемоизируем: он читает Map в памяти,
// а не localStorage, и стоит дешевле, чем сравнение зависимостей.
export function useUnreadChatCount(chats: { id: string; updatedAt: string }[]): number {
  useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return countUnreadChats(chats);
}

// Очистка состояния прочтённости (напр. при logout).
export function clearAllChatReadState(): void {
  cache = null;
  baseline = null;
  try {
    Object.keys(localStorage)
      .filter(k => k.startsWith(KEY_PREFIX) || k === KEY_SINCE)
      .forEach(k => localStorage.removeItem(k));
  } catch { /* молча */ }
  emit();
}
