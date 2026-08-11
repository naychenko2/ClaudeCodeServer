// Состояние «прочитанности» чатов — гибрид: localStorage как optimistic-кеш
// (мгновенное гашение метки, работа офлайн) + серверная отметка Session.lastReadAt
// (PUT /api/chats/{id}/read) как источник синка между устройствами. Серверное
// значение приезжает в обычном поллинге списков чатов (5с) — отдельного
// транспорта синка нет.
//
// Ключи localStorage: cc_chat_read_{chatId} = timestamp последнего открытия чата.
//
// Правило: чат «непрочитанный», если updatedAt новее МАКСИМУМА из локальной
// отметки, серверной lastReadAt и baseline (см. readBaseline). Baseline участвует
// всегда: новое устройство не вспыхивает пачкой старых «непрочитанных» чатов.
//
// Стор реактивный: значения кешируются в памяти (иначе бейдж дёргал бы
// localStorage по разу на чат при каждом рендере, а список чатов перечитывается
// поллингом каждые 5с), подписчики уведомляются через useSyncExternalStore.
import { useSyncExternalStore } from 'react';
import { api } from './api';
import { isOnline } from './offline';

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

// Подписка для сторонних сторов (агрегат активности проектов): им нужно
// пересчитаться, когда чат отметили прочитанным
export function subscribeReadState(fn: () => void): () => void {
  return subscribe(fn);
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
// Локальная отметка ставится мгновенно, серверная досылается фоном с троттлом.
export function markChatRead(chatId: string): void {
  const now = Date.now();
  readCache().set(chatId, now);
  try {
    localStorage.setItem(KEY_PREFIX + chatId, String(now));
  } catch { /* квота/приватный режим — молча, кеш в памяти уже обновлён */ }
  emit();
  syncReadToServer(chatId);
}

// --- фоновый синк отметки на бэк ---
// Leading + trailing троттл per chat: первый вызов уходит сразу (открытие чата),
// повторы в окне схлопываются в один trailing-дослов. Trailing обязателен: во время
// активного хода markChatRead зовётся на каждый status_changed, и без дослова
// второе устройство видело бы чат непрочитанным весь ход (LastReadAt на бэке
// отставал бы от UpdatedAt). Окно 5с — под период поллинга списков, чаще нет смысла.
const SYNC_WINDOW_MS = 5_000;
const syncState = new Map<string, { sentAt: number; timer: ReturnType<typeof setTimeout> | null }>();

function syncReadToServer(chatId: string): void {
  const st = syncState.get(chatId);
  const now = Date.now();
  if (!st || now - st.sentAt >= SYNC_WINDOW_MS) {
    sendMarkRead(chatId);
    return;
  }
  if (st.timer !== null) return; // дослов уже взведён — этот вызов он и покроет
  st.timer = setTimeout(() => {
    const cur = syncState.get(chatId);
    if (cur) cur.timer = null;
    sendMarkRead(chatId);
  }, SYNC_WINDOW_MS - (now - st.sentAt));
}

function sendMarkRead(chatId: string): void {
  const prev = syncState.get(chatId);
  syncState.set(chatId, { sentAt: Date.now(), timer: prev?.timer ?? null });
  // Гард заведомого офлайна — против шумной OfflineError; прочие ошибки глотаем:
  // локальная отметка уже стоит, синк догонит со следующего вызова
  if (!isOnline()) return;
  api.chats.markRead(chatId).catch(() => { /* молча */ });
}

// Время последнего прочтения чата (timestamp ms). 0 — чат ни разу не открывали.
export function getChatReadTime(chatId: string): number {
  return readCache().get(chatId) ?? 0;
}

// Есть ли в чате непрочитанные сообщения.
// readTime = max(локальная отметка, серверная lastReadAt, baseline): серверная
// гасит метку, если чат читали на другом устройстве; baseline прикрывает чаты,
// существовавшие до первого запуска этого устройства (см. шапку модуля).
export function hasUnread(updatedAt: string, chatId: string, lastReadAt?: string | null): boolean {
  const serverRead = lastReadAt ? Date.parse(lastReadAt) || 0 : 0; // NaN/битое → 0
  const readTime = Math.max(getChatReadTime(chatId), serverRead, readBaseline());
  const updatedTime = new Date(updatedAt).getTime();
  if (!Number.isFinite(updatedTime)) return false;
  return updatedTime > readTime;
}

// Сколько чатов имеют непрочитанные сообщения — для бейджа на иконке рельсы.
export function countUnreadChats(chats: { id: string; updatedAt: string; lastReadAt?: string | null }[]): number {
  return chats.reduce((n, c) => n + (hasUnread(c.updatedAt, c.id, c.lastReadAt) ? 1 : 0), 0);
}

// Реактивный бейдж: подписка гарантирует ререндер при отметке прочтения — без
// неё markChatRead писал бы мимо React, и число обновлялось бы только случайно,
// на постороннем ререндере. Сам подсчёт не мемоизируем: он читает Map в памяти,
// а не localStorage, и стоит дешевле, чем сравнение зависимостей.
export function useUnreadChatCount(chats: { id: string; updatedAt: string; lastReadAt?: string | null }[]): number {
  useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return countUnreadChats(chats);
}

// То же для одного чата — подсветка непрочитанности на его карточке. Через ту же
// подписку: иначе метка гасла бы не при открытии чата, а на случайном ререндере
// списка (в лучшем случае — на ближайшем поллинге, до 5с спустя).
export function useHasUnread(updatedAt: string, chatId: string, lastReadAt?: string | null): boolean {
  useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return hasUnread(updatedAt, chatId, lastReadAt);
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
