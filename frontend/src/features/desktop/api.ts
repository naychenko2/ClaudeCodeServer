// Устройства владельца (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// список, сопряжение и отзыв (канал устройства ADR-016). Тонкая обёртка над живым REST;
// DTO-типы — types/index.ts (переедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type { DesktopDevice, DesktopPairingCode } from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.devices.* остаются прежними для всех потребителей).
export const devicesApi = {
  // Устройства владельца и сопряжение (канал устройства ADR-016).
  devices: {
    list: () => request<DesktopDevice[]>('/devices'),
    // Код сопряжения: 8 символов, живёт 5 минут, принадлежит ЭТОЙ веб-сессии
    startPairing: () => request<DesktopPairingCode>('/devices/pairing', { method: 'POST' }),
    pairingStatus: () => request<DesktopPairingCode>('/devices/pairing'),
    cancelPairing: () => request<void>('/devices/pairing', { method: 'DELETE' }),
    rename: (id: string, name: string) =>
      request<DesktopDevice>(`/devices/${encodeURIComponent(id)}`, {
        method: 'PATCH', body: JSON.stringify({ name }),
      }),
    // Отзыв: запись остаётся надгробием, токен устройства умирает немедленно
    revoke: (id: string) => request<void>(`/devices/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  },
};
