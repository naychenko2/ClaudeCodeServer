import { request } from '../../lib/offline';
import type { ModelUnavailableMark, SpendBadgeResponse, SpendOverviewResponse, SpendPivotResponse, SpendTaskPromptResponse, SpendTurnDetailResponse, SpendTurnsResponse, SpendWidgetResponse, UsageResponse } from './types';

// Методы REST расхода токенов и лимитов подписок; lib/api.ts подмешивает их в объект api
// spread'ом (адреса api.usage / api.spend остаются прежними).
export const spendApi = {
  usage: {
    get: () => request<UsageResponse>('/usage'),
    // Лёгкий ответ для шапки чата (перф-3 B6): только последний снимок на окно, без истории —
    // в десятки раз меньше; тип тот же UsageResponse (проекция той же службы, не второй источник)
    getLimits: () => request<UsageResponse>('/usage?scope=limits'),
    // Снять пометку «модель недоступна на этой подписке» досрочно (кнопка «Проверить
    // сейчас», только админ). Модель — ровно в том виде, в каком пришла в выдаче: в именах
    // встречаются точки и суффикс окна «[1m]», поэтому она в теле, а не в пути. В ответе —
    // обновлённый список, повторный запрос /usage не нужен
    clearModelAvailability: (key: string, model: string) =>
      request<{ unavailableModels: ModelUnavailableMark[] }>(
        `/usage/subscriptions/${encodeURIComponent(key)}/model-availability/clear`,
        { method: 'POST', body: JSON.stringify({ model }) }),
  },

  // Аналитика расхода токенов (Spend Analytics v2). query — готовая строка
  // из spendQuery() (период/скоуп/фильтры), чтобы не дублировать сборку параметров.
  spend: {
    overview: (query: string) => request<SpendOverviewResponse>(`/spend/overview${query}`),
    pivot: (query: string) => request<SpendPivotResponse>(`/spend/pivot${query}`),
    turns: (query: string) => request<SpendTurnsResponse>(`/spend/turns${query}`),
    turn: (id: string) => request<SpendTurnDetailResponse>(`/spend/turns/${encodeURIComponent(id)}`),
    taskPrompt: (taskId: string) =>
      request<SpendTaskPromptResponse>(`/spend/tasks/${encodeURIComponent(taskId)}/prompt`),
    widget: () => request<SpendWidgetResponse>('/spend/widget'),
    // Суммарные токены за жизнь чата: значение устойчивое (не счётчик «сейчас»), поэтому
    // в офлайне показываем последнее известное — по-чатовой класс кэша.
    badge: (sessionId: string) => request<SpendBadgeResponse>(`/spend/sessions/${encodeURIComponent(sessionId)}/badge`, { chatScope: sessionId }),
  },
};
