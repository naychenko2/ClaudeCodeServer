// Аккаунты провайдеров: тонкая обёртка над живым REST панелей учёта (fal, glif,
// Yandex Cloud) и подключения Higgsfield. lib/api.ts подмешивает их в объект api
// spread'ом (адреса api.fal.* / api.glif.* / api.yandex.* / api.higgsfield.*
// остаются прежними для всех потребителей).
// Типы ответов fal/glif/yandex — DTO бэкенда, живут в ../../types; пользовательский
// и админский виды Higgsfield — в ./types.ts.

import { request } from '../../lib/offline';
import type { FalAccountResponse, GlifAccountResponse, YandexAccountResponse } from '../../types';
import type { HiggsfieldStatus, HiggsfieldAdminStatus } from './types';

export const providerAccountsApi = {
  fal: {
    account: (days = 7) => request<FalAccountResponse>(`/fal/account?days=${days}`),
  },

  glif: {
    // Агрегаты расхода приходят сразу за три окна (24ч/7д/30д) — параметра периода нет
    account: () => request<GlifAccountResponse>('/glif/account'),
  },

  // Деньги Yandex Cloud: остаток на счёте (только админу) и расход на озвучку за N дней —
  // его продукт считает сам, у Яндекса такого разреза в API нет
  yandex: {
    account: (days = 30) => request<YandexAccountResponse>(`/yandex/account?days=${days}`),
  },

  // Higgsfield: подключается администратором один раз на весь продукт.
  // Статус (GET /higgsfield/status) — { connected, expiresAt }:
  // обычная карточка показывает только connected, admin-вкладка — и срок.
  higgsfield: {
    status: () => request<HiggsfieldStatus>('/higgsfield/status'),
    // Тот же эндпоинт, тип расширен expiresAt для admin-вкладки.
    adminStatus: () => request<HiggsfieldAdminStatus>('/higgsfield/status'),
    connect: () => request<{ authorizeUrl: string }>('/higgsfield/connect', { method: 'POST' }),
    disconnect: () => request<{ ok: true }>('/higgsfield/disconnect', { method: 'POST' }),
  },
};
