// Специальности персон (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// каталог, общий слой настроек, сброс к наследованию, лимит подмен, каталог секций
// промптов. Тонкая обёртка над живым REST; DTO-типы — types/index.ts (переедут в S5-P3).
import { request } from '../../lib/offline';
import type {
  ResetResult,
  SpecialtyCatalogEntry,
  SpecialtyPromptSectionsCatalog,
  SpecialtySettingsLayer,
  SpecialtySettingsResponse,
  SpecialtyWithInheritors,
} from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.specialties.* остаются прежними для всех потребителей).
export const specialtiesApi = {
  // Специальности персон и настройки к ним. Каталог отдаёт подписи и эффективные
  // шаблоны прав; настройки — единственный (общий) слой, пишет только админ.
  // ADR-012 снял owner/user-слои, поэтому здесь НЕ должно быть путей
  // settings/owner, settings/user/{id}, reset/user и fallback/owner — на бэкенде
  // их больше нет, и любой такой вызов вернул бы 404.
  specialties: {
    list: () => request<SpecialtyCatalogEntry[]>('/specialties'),
    // Одна специальность и её наследники (B09): кто из персон вызывающего наследует
    // её секции и сколько секций у каждой «своё». Пустой key — 404 на бэке.
    get: (key: string) =>
      request<SpecialtyWithInheritors>(`/specialties/${encodeURIComponent(key)}`),
    getSettings: () => request<SpecialtySettingsResponse>('/specialties/settings'),
    saveGlobalLayer: (layer: SpecialtySettingsLayer) =>
      request<{ global: SpecialtySettingsLayer }>('/specialties/settings/global', {
        method: 'PUT', body: JSON.stringify(layer),
      }),
    // МЁРТВЫЙ МАРШРУТ. Эндпоинта settings/user/{id} на бэкенде нет с ADR-012; ни один
    // экран его больше не зовёт (см. lib/presets — канал user-слоёв остался только под
    // своими юнит-тестами и уезжает хвостовой чисткой вместе с ними). Новых вызовов
    // не добавлять: ответ будет 404.
    getUserLayer: (userId: string) =>
      request<{ user: SpecialtySettingsLayer; userId: string }>(`/specialties/settings/user/${encodeURIComponent(userId)}`),
    // Сброс исключений к наследованию (возврат = удаление записи слоя, а не обнуление
    // ячеек): preview — числа/имена ДО подтверждения, reset — фактическая запись.
    // key — точечный жест (одна специальность), без него — весь слой.
    // Скоупы — только те, что остались на бэкенде: global (общий слой) и owner
    // (сброс персон вызывающего).
    resetPreview: (scope: 'owner' | 'global', key?: string) => {
      const qs = new URLSearchParams();
      if (key) qs.set('key', key);
      const s = qs.toString();
      return request<ResetResult>(`/specialties/settings/reset/${scope}/preview${s ? `?${s}` : ''}`);
    },
    reset: (scope: 'owner' | 'global', key?: string) => {
      const body: { key?: string } = {};
      if (key) body.key = key;
      return request<ResetResult>(`/specialties/settings/reset/${scope}`, {
        method: 'POST', body: JSON.stringify(body),
      });
    },
    // Лимит подмен за ход (фолбэк): `null` = снять настройку слоя (наследование).
    // Управляется через ту же дорогу, что сброс — отдельных типов в index.ts нет,
    // описаны здесь, чтобы не разъезжаться с бэком. Слой один: только 'global'.
    setMaxSubstitutions: (scope: 'global', value: number | null) => {
      const body: { maxSubstitutions: number | null } = { maxSubstitutions: value };
      return request<{ maxSubstitutions: number }>(`/specialties/settings/fallback/${scope}`, {
        method: 'PUT', body: JSON.stringify(body),
      });
    },
    // Каталог секций промптов и типовых профилей умений роли (фича specialty-prompt-sections):
    // дефолты кода с лимитом 1024, метаданные секций, включённость и типовые тексты по
    // каждой специальности, плюс дефолтные умения ролей для шага выбора цели.
    promptSectionsCatalog: () =>
      request<SpecialtyPromptSectionsCatalog>('/specialties/prompt-sections'),
  },
};
