// Персистентность фильтров списка чатов (localStorage).
// Раздельно по областям: глобальный список (scopeKey='global') и каждый проект
// отдельно (scopeKey=projectId) — переключение проекта не смешивает настройки.
// Все три фильтра (тип/время/персона) хранятся одним объектом: раньше персистился
// только тип, из-за чего часть настроек молча слетала на любом ремаунте панели.
import { useEffect, useRef, useState } from 'react';
import type { Session } from '../types';

export interface ChatFilters {
  origins: Session['origin'][];
  activeOnly: boolean;
  personaId: string | null;
}

const KEY_PREFIX = 'cc_chat_filters:';
// Ключ старого формата (только тип чата) — читаем один раз для миграции
const LEGACY_ORIGINS_PREFIX = 'cc_chat_visible_origins:';

export const ALL_ORIGINS: Session['origin'][] = ['manual', 'task', 'automation'];

function defaults(): ChatFilters {
  return { origins: [...ALL_ORIGINS], activeOnly: false, personaId: null };
}

function readLegacyOrigins(scopeKey: string): Session['origin'][] | null {
  try {
    const raw = localStorage.getItem(LEGACY_ORIGINS_PREFIX + scopeKey);
    if (!raw) return null;
    const arr = JSON.parse(raw) as Session['origin'][];
    return Array.isArray(arr) ? arr : null;
  } catch {
    return null;
  }
}

export function loadChatFilters(scopeKey: string): ChatFilters {
  try {
    const raw = localStorage.getItem(KEY_PREFIX + scopeKey);
    if (raw) {
      const p = JSON.parse(raw) as Partial<ChatFilters>;
      return {
        origins: Array.isArray(p.origins) ? p.origins : [...ALL_ORIGINS],
        activeOnly: p.activeOnly === true,
        personaId: typeof p.personaId === 'string' ? p.personaId : null,
      };
    }
    const legacy = readLegacyOrigins(scopeKey);
    if (legacy) return { ...defaults(), origins: legacy };
  } catch { /* повреждённое значение — дефолт */ }
  return defaults();
}

export function persistChatFilters(scopeKey: string, v: ChatFilters): void {
  try { localStorage.setItem(KEY_PREFIX + scopeKey, JSON.stringify(v)); } catch { /* квота/приватный режим */ }
}

// Состояние фильтров для одной области. Перечитывает хранилище при смене scopeKey,
// поэтому сохранность не зависит от того, размонтируется ли панель (key проекта,
// переключение вкладок сайдбара, смена мобильной/десктопной раскладки).
export function useChatFilters(scopeKey: string) {
  const [filters, setFilters] = useState<ChatFilters>(() => loadChatFilters(scopeKey));
  const scopeRef = useRef(scopeKey);

  useEffect(() => {
    if (scopeRef.current === scopeKey) return;
    scopeRef.current = scopeKey;
    setFilters(loadChatFilters(scopeKey));
  }, [scopeKey]);

  const patch = (p: Partial<ChatFilters>) => {
    const next = { ...filters, ...p };
    persistChatFilters(scopeKey, next);
    setFilters(next);
  };

  return { filters, patch };
}

// Сброс фильтра по персоне, если её чатов в списке больше нет (персона удалена,
// её чаты удалены). Иначе список молча пустеет без видимой пользователю причины.
// Ждём непустого списка: пока чаты грузятся, сбрасывать нечего.
export function useSanitizePersonaFilter(
  filters: ChatFilters,
  patch: (p: Partial<ChatFilters>) => void,
  personaIdsInList: string[],
  listLoaded: boolean,
) {
  const key = personaIdsInList.join(',');
  useEffect(() => {
    if (!listLoaded || !filters.personaId) return;
    if (!personaIdsInList.includes(filters.personaId)) patch({ personaId: null });
    // patch пересоздаётся каждый рендер — намеренно вне зависимостей
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [listLoaded, filters.personaId, key]);
}
