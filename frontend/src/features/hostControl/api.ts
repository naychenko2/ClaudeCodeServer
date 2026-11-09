import { request } from '../../lib/offline';
import type { BackupStatus, BackupSummary } from '../../types';
import type { PowerActionKind, PowerState, RemoteCommandResult, RemoteCommandsState } from './types';

// Потолок ожидания ответа на операцию пульта. Больше самого щедрого серверного
// TimeoutSeconds из примера конфига (Dify — 180 с) с запасом на дорогу: обрывать запрос
// раньше, чем сервер закончит команду, значит показывать «нет связи» поверх живого старта.
const REMOTE_COMMAND_TIMEOUT_MS = 200_000;

// Методы REST питания машины, пульта команд, бэкапов и файлов хоста; lib/api.ts подмешивает
// их в объект api spread'ом (адреса api.power / remoteCommands / backup / hostFiles прежние).
export const hostControlApi = {
  power: {
    // live: true — не подставлять ответ офлайн-кэша: важен сам факт, что сервер отвечает.
    // Машину гасят удалённо, и «всё в порядке» из IndexedDB поверх уже погашенного продукта
    // было бы ровно тем враньём, на котором подрывалась модалка выкатки.
    status: () => request<PowerState>('/admin/power/status', { cache: 'no-store', live: true }),
    schedule: (action: PowerActionKind) =>
      request<{ action: PowerActionKind; runAt: string; secondsLeft: number }>(
        '/admin/power', { method: 'POST', body: JSON.stringify({ action }) }),
    cancel: () => request<{ cancelled: boolean }>('/admin/power/cancel', { method: 'POST' }),
  },

  remoteCommands: {
    // live: true — по той же причине, что у питания и выкатки: важен сам факт ответа сервера.
    // Подставленный из офлайн-кэша список сказал бы «туннель запущен» поверх машины, до
    // которой уже не достучаться, — а пульт для того и нужен, чтобы этому верить.
    // GET не исполняет ни одной команды (инвариант §9.3): отдаётся кэш статуса.
    status: () => request<RemoteCommandsState>('/admin/remote-commands', { cache: 'no-store', live: true }),
    // Операции синхронные в пределах серверного TimeoutSeconds (у долгих действий вроде Dify
    // это 180 с), поэтому фронту нужен свой запас: с дефолтным лимитом запрос оборвался бы
    // раньше сервера, и человек увидел бы «нет связи» поверх идущего старта.
    start: (key: string) =>
      request<RemoteCommandResult>(`/admin/remote-commands/${encodeURIComponent(key)}/start`,
        { method: 'POST', timeoutMs: REMOTE_COMMAND_TIMEOUT_MS, live: true }),
    stop: (key: string) =>
      request<RemoteCommandResult>(`/admin/remote-commands/${encodeURIComponent(key)}/stop`,
        { method: 'POST', timeoutMs: REMOTE_COMMAND_TIMEOUT_MS, live: true }),
    refresh: (key: string) =>
      request<RemoteCommandResult>(`/admin/remote-commands/${encodeURIComponent(key)}/refresh`,
        { method: 'POST', timeoutMs: REMOTE_COMMAND_TIMEOUT_MS, live: true }),
    output: (key: string) =>
      request<{ text: string }>(`/admin/remote-commands/${encodeURIComponent(key)}/output`,
        { cache: 'no-store', live: true }),
  },

  // Бэкапы — только админ (инстансная штука). Настройки правятся руками в секции Backup
  // конфига, отсюда только чтение статуса и ручной снимок. Восстановления тут нет: оно
  // требует остановленного сервера и живёт в CLI (exe --restore) и меню трея.
  backup: {
    get: () => request<BackupStatus>('/admin/backup'),
    run: () => request<{ file: string; createdAt: string; summary: BackupSummary }>(
      '/admin/backup/run', { method: 'POST' }),
  },

  // Просмотр файла вне корня проекта (абсолютный путь) — например, файл открытый
  // из карточки инструмента чата в другом проекте/дереве. Формат ответа тот же, что
  // у files/content; 403 — путь вне досягаемости песочницы, 400 — путь не абсолютный.
  hostFiles: {
    getContent: (path: string) =>
      request<{ content: string | null; isBinary: boolean; isImage: boolean; isVideo?: boolean; isAudio?: boolean; isDocument?: boolean; docKind?: string; mimeType?: string; base64?: string; fileSize?: number }>(`/host-files/content?path=${encodeURIComponent(path)}`),
  },
};
