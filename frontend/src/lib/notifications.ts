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
  items = items.filter(i => i.id !== id);
  notify();
}

export async function deleteBatch(ids: string[]) {
  const result = await api<{ deleted: number }>(`${BASE}/batch`, {
    method: 'DELETE',
    body: JSON.stringify({ ids }),
  });
  const idSet = new Set(ids);
  items = items.filter(i => !idSet.has(i.id));
  notify();
  return result.deleted;
}

// ====== State queries ======

export function getNotifications() { return items; }
export function getUnreadCount() { return unreadCount; }
export function isLoaded() { return loaded; }

// ====== SignalR subscription ======

let subscribed = false;

export function ensureNotificationsSubscribed() {
  if (subscribed) return;
  subscribed = true;

  onMessage((msg) => {
    if (msg.type === 'notification') {
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
