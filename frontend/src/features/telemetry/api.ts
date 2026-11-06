// Домен «Телеметрия» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// статус проброса SigNoz и инциденты. Тонкая обёртка над живым REST; DTO-типы —
// types/index.ts (Incident* доедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type { IncidentDossier, IncidentListResponse } from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.telemetry.* остаются прежними для всех потребителей).
export const telemetryApi = {
  // Раздел «Телеметрия»: статус проброса SigNoz — фронт решает, показать iframe или заглушку
  telemetry: {
    status: () =>
      request<{ configured: boolean; reachable: boolean; proxyPath: string; discussProjectId?: string | null }>('/telemetry/status'),
    // Вкладка «Инциденты»: список отдаёт статус телеметрии ОТДЕЛЬНО от элементов —
    // пустой список при выключенном SigNoz нельзя показывать как «всё тихо»
    incidents: () => request<IncidentListResponse>('/telemetry/incidents'),
    incident: (fingerprint: string) =>
      request<IncidentDossier>(`/telemetry/incidents/${encodeURIComponent(fingerprint)}`),
    // Досье markdown'ом — описание задачи и черновик сообщения в чат
    incidentText: (fingerprint: string) =>
      request<{ text: string }>(`/telemetry/incidents/${encodeURIComponent(fingerprint)}/text`),
    // Разбор моделью — единственное место фичи, где участвует LLM, и только по кнопке
    incidentExplain: (fingerprint: string) =>
      request<{ text: string }>(
        `/telemetry/incidents/${encodeURIComponent(fingerprint)}/explain`, { method: 'POST' }),
    // Заглушить/вернуть звук: инцидент остаётся в списке, но уходит из счётчика и из push
    muteIncident: (fingerprint: string, muted: boolean) =>
      request<void>(
        `/telemetry/incidents/${encodeURIComponent(fingerprint)}/mute?muted=${muted}`,
        { method: 'POST' }),
  },
};
