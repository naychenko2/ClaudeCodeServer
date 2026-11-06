import { request } from '../../lib/offline';
import type { ProviderCapabilities } from '../../lib/models';
import type { ModelPreviewResponse, PlacePresetRef, PresetUsageResponse, ProviderBalanceInfo } from '../../types';

// Методы REST провайдеров, моделей и исполнителя фоновых действий; lib/api.ts подмешивает их
// в объект api spread'ом (адреса api.providers / api.models / api.localActions прежние).
export const modelsApi = {
  providers: {
    balance: (key: string) => request<ProviderBalanceInfo>(`/providers/${key}/balance`),
    usage: (key: string) =>
      request<{
        balance: ProviderBalanceInfo | null;
        snapshots: { timestamp: string; balance: number; currency: string }[];
      }>(`/providers/${key}/usage`),
  },

  models: {
    list: () =>
      request<{
        models: { value: string; displayName: string; description?: string | null; provider?: string | null; contextWindow?: number | null; isCurated?: boolean; resolvedVersion?: string | null }[];
        providers?: Record<string, ProviderCapabilities>;
        // Резолвнутые модели агентных мест (ключ каталога → модель или null): по ним
        // пикеры подписывают пункт «По умолчанию (<модель>)»
        assignments?: Record<string, string | null>;
      }>('/models'),
    // Версия claude CLI хоста и последняя вышедшая (только админ). refresh — перечитать
    // локальную версию (после claude update), npm сервер при этом не опрашивает.
    // updateAvailable = null — сравнить нельзя.
    claudeCli: (refresh = false) =>
      request<{
        current: string | null; latest: string | null; updateAvailable: boolean | null; checkedAt: string | null;
        // Из CHANGELOG пропущенных версий (current, latest]; пусто — списка нет.
        // isFamilyDefault — самая новая в семействе: алиас пойдёт на неё после обновления
        newModels?: { name: string; id: string; cliVersion: string; isFamilyDefault: boolean }[];
        changes?: { version: string; items: string[]; hidden: number }[];
        hiddenCount?: number;
        truncated?: boolean;
      }>(
        `/models/claude-cli${refresh ? '?refresh=true' : ''}`),
    // Эффективный резолв для строки «Сейчас пойдёт» (считается той же кодовой дорогой,
    // что запуск хода — второй точки истины нет). sessionId вместе с personaId добавляет
    // в ответ subagentChip — чип модели на карточке персоны-сабагента.
    preview: (q: { place?: string; personaId?: string; specialty?: string; tier?: string; sessionId?: string }) => {
      const qs = new URLSearchParams();
      if (q.place) qs.set('place', q.place);
      if (q.personaId) qs.set('personaId', q.personaId);
      if (q.specialty) qs.set('specialty', q.specialty);
      if (q.tier) qs.set('tier', q.tier);
      if (q.sessionId) qs.set('sessionId', q.sessionId);
      const s = qs.toString();
      return request<ModelPreviewResponse>(`/models/preview${s ? `?${s}` : ''}`);
    },
    // Места, где выбран пресет (диалог удаления)
    presetUsage: (id: string) =>
      request<PresetUsageResponse>(`/models/presets/${encodeURIComponent(id)}/usage`),
  },

  // Исполнитель фоновых действий — правит только админ (настройка серверная, общая для всех).
  // route: 'local' | 'claude' | id модели провайдера. Текущее состояние приходит в блоке
  // ollama ответа /usage.
  localActions: {
    setRoute: (key: string, route: string) =>
      request<{ key: string; route: string; source: string; preset?: PlacePresetRef | null; engine?: string | null }>(`/admin/local-actions/${key}`,
        { method: 'PUT', body: JSON.stringify({ route }) }),
    reset: (key: string) =>
      request<{ key: string; route: string; source: string; preset?: PlacePresetRef | null; engine?: string | null }>(`/admin/local-actions/${key}`,
        { method: 'DELETE' }),
    // Массовый автоподбор исполнителя всем действиям по пресету; актуальные маршруты фронт
    // затем перечитывает из /usage. preset: 'tiers' | 'tiers-local'.
    applyPreset: (preset: 'tiers' | 'tiers-local') =>
      request<{ preset: string; count: number }>(`/admin/local-actions/preset`,
        { method: 'POST', body: JSON.stringify({ preset }) }),
  },
};
