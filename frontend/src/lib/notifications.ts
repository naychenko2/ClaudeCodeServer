import { onMessage } from './signalr';
import { request } from './offline';
import type { NotificationItem, NotificationKind, NotificationListResponse, CreateNotificationRequest } from '../types';

// Стор уведомлений: получает реальтайм-уведомления через SignalR,
// предоставляет список, счётчики и методы для работы с REST API.

// ====== State ======
let items: NotificationItem[] = [];
let unreadCount = 0;
let listeners: (() => void)[] = [];
let loaded = false;

function notify() {
  listeners.forEach(fn => fn());
}

// ====== API helpers ======
const BASE = '/notifications';

function api<T>(url: string, init?: RequestInit): Promise<T> {
  return request<T>(url, init ?? {});
}

// ====== Public API ======

export async function loadNotifications(limit = 50, offset = 0, kind?: string, unreadOnly?: boolean) {
  const params = new URLSearchParams({ limit: String(limit), offset: String(offset) });
  if (kind) params.set('kind', kind);
  if (unreadOnly) params.set('unreadOnly', 'true');
  const result = await api<NotificationListResponse>(`${BASE}?${params}`);
  items = result.items;
  unreadCount = result.unreadCount;
  loaded = true;
  notify();
  return result;
}

export async function loadUnreadCount() {
  const result = await api<{ count: number }>(`${BASE}/unread-count`);
  unreadCount = result.count;
  notify();
  return result.count;
}

export async function getNotification(id: string) {
  return api<NotificationItem>(`${BASE}/${id}`);
}

export async function createNotification(req: CreateNotificationRequest) {
  return api<NotificationItem>(BASE, {
    method: 'POST',
    body: JSON.stringify(req),
  });
}

export async function markRead(id: string) {
  await api(`${BASE}/${id}/read`, { method: 'PUT' });
  const n = items.find(i => i.id === id);
  if (n) {
    n.isRead = true;
    unreadCount = Math.max(0, unreadCount - 1);
    notify();
  }
}

export async function markAllRead() {
  const result = await api<{ marked: number }>(`${BASE}/read-all`, { method: 'PUT' });
  items.forEach(i => { i.isRead = true; });
  unreadCount = 0;
  notify();
  return result.marked;
}

export async function markReadBatch(ids: string[]) {
  const result = await api<{ marked: number }>(`${BASE}/read-batch`, {
    method: 'PUT',
    body: JSON.stringify({ ids }),
  });
  const idSet = new Set(ids);
  items.forEach(i => { if (idSet.has(i.id)) { i.isRead = true; } });
  unreadCount = Math.max(0, unreadCount - result.marked);
  notify();
  return result.marked;
}

export async function deleteNotification(id: string) {
  await api(`${BASE}/${id}`, { method: 'DELETE' });
  // Удаляем непрочитанное — счётчик тоже уменьшаем, иначе бейдж «зависнет» ненулевым
  if (items.some(i => i.id === id && !i.isRead)) {
    unreadCount = Math.max(0, unreadCount - 1);
  }
  items = items.filter(i => i.id !== id);
  notify();
}

export async function deleteBatch(ids: string[]) {
  const result = await api<{ deleted: number }>(`${BASE}/batch`, {
    method: 'DELETE',
    body: JSON.stringify({ ids }),
  });
  const idSet = new Set(ids);
  // Считаем непрочитанные среди удаляемых ДО фильтрации — иначе счётчик разъедется
  const removedUnread = items.filter(i => idSet.has(i.id) && !i.isRead).length;
  if (removedUnread > 0) {
    unreadCount = Math.max(0, unreadCount - removedUnread);
  }
  items = items.filter(i => !idSet.has(i.id));
  notify();
  return result.deleted;
}

export async function deleteReadAll() {
  const result = await api<{ deleted: number }>(`${BASE}/read-all`, {
    method: 'DELETE',
  });
  items = items.filter(i => !i.isRead);
  notify();
  return result.deleted;
}

// ====== State queries ======

export function getNotifications() { return items; }
export function getUnreadCount() { return unreadCount; }
export function isLoaded() { return loaded; }

// ====== SignalR subscription ======

let subscribed = false;

// Гейт для счётчика непрочитанных: шапка (HubHeader) монтируется заново в каждом
// разделе, поэтому наивный вызов бил бы по API на каждое переключение таба.
let unreadInFlight = false;
let unreadLoadedAt = 0;
const UNREAD_TTL_MS = 60_000;

/** Подтянуть счётчик непрочитанных для бейджа, если он ещё не известен или протух. */
export async function ensureUnreadCountLoaded() {
  // Список уже загружен целиком — счётчик пришёл вместе с ним, запрос не нужен
  if (loaded) return;
  if (unreadInFlight) return;
  if (unreadLoadedAt && Date.now() - unreadLoadedAt < UNREAD_TTL_MS) return;

  unreadInFlight = true;
  try {
    await loadUnreadCount();
    unreadLoadedAt = Date.now();
  } catch {
    // Бейдж не критичен — молча живём без него, как HubHeader с api.history.newCount
  } finally {
    unreadInFlight = false;
  }
}

// Гейт загрузки списка: виджет дашборда и раздел монтируются независимо,
// без него один и тот же список тянулся бы дважды (плюс дубль в StrictMode).
let listInFlight: Promise<unknown> | null = null;

/** Загрузить список уведомлений, если он еще не загружен. */
export async function ensureNotificationsLoaded() {
  if (loaded) return;
  if (!listInFlight) {
    listInFlight = loadNotifications()
      .catch(() => { /* виджет не критичен — переживем без списка */ })
      .finally(() => { listInFlight = null; });
  }
  await listInFlight;
}

export function ensureNotificationsSubscribed() {
  if (subscribed) return;
  subscribed = true;

  onMessage((msg) => {
    if (msg.type === 'notification') {
      // Дедуп по id: одно уведомление может прийти повторно (доставка в несколько
      // SignalR-групп, реконнект) — не задваиваем список и счётчик непрочитанных
      if (msg.notificationId && items.some(i => i.id === msg.notificationId)) return;
      // Новое уведомление пришло через SignalR — добавляем в начало списка
      const item: NotificationItem = {
        id: msg.notificationId ?? `_local_${Date.now()}`,
        kind: msg.kind as NotificationKind,
        type: msg.notifType ?? '',
        title: msg.title,
        body: msg.body,
        url: msg.url,
        projectId: msg.projectId,
        sessionId: msg.sessionId,
        taskId: msg.taskId,
        source: msg.source,
        tag: msg.tag,
        personaId: msg.personaId,
        personaName: msg.personaName,
        personaRole: msg.personaRole,
        personaColor: msg.personaColor,
        personaHasAvatar: msg.personaHasAvatar,
        projectName: msg.projectName,
        isRead: false,
        createdAt: new Date().toISOString(),
      };
      items = [item, ...items];
      unreadCount++;
      notify();
    }
  });
}

// ====== Subscribe for React ======

export function subscribeToNotifications(fn: () => void): () => void {
  listeners.push(fn);
  return () => {
    listeners = listeners.filter(f => f !== fn);
  };
}
