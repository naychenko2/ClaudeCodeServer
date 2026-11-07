// Аккаунт: тонкая обёртка над живым REST входа (auth), push-подписок, пользователей,
// настроек и слотов моделей. lib/api.ts подмешивает это в объект api spread'ом
// (адреса api.auth.* / api.push.* / api.users.* / api.settings.* / api.meModelTiers.* /
// api.adminUserModelTiers.* остаются прежними для всех потребителей).
// Типы ответов Me/UserProfile/AppSettings и ModelTiers — в ./types.ts.

import { request } from '../../lib/offline';
import type { Me, UserProfile, AppSettings, ModelTiers } from './types';

export const accountApi = {
  auth: {
    login: (username: string, password: string) =>
      request<{ token: string; expiresAt: string; username: string; displayName?: string | null }>('/auth/login', {
        method: 'POST',
        body: JSON.stringify({ username, password }),
      }),
    // live: true — только для пути входа: ответ из IDB-кэша (ключ по URL, общий для владельцев)
    // подставил бы id прежнего владельца к токену нового.
    me: (opts?: { timeoutMs?: number; live?: boolean }) => request<Me>('/auth/me', opts),
    // Возвращает свежий токен: смена пароля отзывает все прежние, включая текущий
    changePassword: (currentPassword: string, newPassword: string) =>
      request<{ token: string; expiresAt: string }>('/auth/password', {
        method: 'PUT',
        body: JSON.stringify({ currentPassword, newPassword }),
      }),
    // Пороги индикатора контекста (per-user); пустой body → сброс к дефолтам
    setContextThresholds: (t: { warnPct?: number; dangerPct?: number }) =>
      request<{ contextThresholds: { warnPct: number; dangerPct: number } | null }>('/auth/context-thresholds', {
        method: 'PUT',
        body: JSON.stringify(t),
      }),
    // Таймзона устройства (IANA) — серверу для расчёта напоминаний по локальным срокам
    setTimeZone: (timeZone: string) =>
      request<void>('/auth/timezone', {
        method: 'PUT',
        body: JSON.stringify({ timeZone }),
      }),
  },

  push: {
    vapidPublicKey: () => request<{ publicKey: string }>('/push/vapid-public-key'),
    subscribe: (sub: { endpoint: string; p256dh: string; auth: string }) =>
      request<void>('/push/subscribe', { method: 'POST', body: JSON.stringify(sub) }),
    unsubscribe: (endpoint: string) =>
      request<void>('/push/unsubscribe', { method: 'POST', body: JSON.stringify({ endpoint }) }),
  },

  users: {
    list: () => request<UserProfile[]>('/users'),
    create: (data: { username: string; password: string; role: string; executionEnvironment?: string }) =>
      request<UserProfile>('/users', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: string, data: { username?: string; role?: string; executionEnvironment?: string }) =>
      request<UserProfile>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: string) => request<void>(`/users/${id}`, { method: 'DELETE' }),
    resetPassword: (id: string, newPassword: string) =>
      request<void>(`/users/${id}/password`, { method: 'PUT', body: JSON.stringify({ newPassword }) }),
  },

  settings: {
    get: () => request<AppSettings>('/settings'),
    // Патч: присылаем только изменённые поля, остальные сервер оставляет как есть
    save: (s: Partial<AppSettings>) => request<AppSettings>('/settings', { method: 'PUT', body: JSON.stringify(s) }),
  },

  // Личные слоты моделей текущего пользователя (GET /api/me/model-tiers).
  // PATCH: null = не трогать, "" = очистить к наследованию, string = override.
  meModelTiers: {
    get: () => request<ModelTiers>('/me/model-tiers'),
    save: (patch: Partial<ModelTiers>) => request<ModelTiers>('/me/model-tiers', { method: 'PUT', body: JSON.stringify(patch) }),
  },

  // Админские слоты моделей любого пользователя (GET /api/admin/users/{id}/model-tiers).
  adminUserModelTiers: {
    get: (userId: string) => request<ModelTiers>(`/admin/users/${encodeURIComponent(userId)}/model-tiers`),
    save: (userId: string, patch: Partial<ModelTiers>) => request<ModelTiers>(`/admin/users/${encodeURIComponent(userId)}/model-tiers`, { method: 'PUT', body: JSON.stringify(patch) }),
  },
};
