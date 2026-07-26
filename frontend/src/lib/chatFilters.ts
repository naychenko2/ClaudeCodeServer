// Персистентность фильтров списка чатов (localStorage).
// Раздельно по областям: глобальный список (scopeKey='global') и каждый проект
// отдельно (scopeKey=projectId) — переключение проекта не смешивает настройки.
import { useEffect, useRef, useState } from 'react';
import type { Session } from '../types';

// Чип статуса жизни чата (макет варианта А): маппинг фиксированный, покрывает все
// 7 значений Session.status + признак готовности задачи.
//   active  — Starting/Working/Active/Finished (обычный покоящийся чат, ждёт сообщения)
//   waiting — Waiting
//   done    — taskDone (архив: чаты выполненных задач-исполнителей)
//   error   — Error/Orphaned
export type ChatStatusChip = 'active' | 'waiting' | 'done' | 'error';

// Секция «Показать только»: мультивыбор тегов-особенностей чата.
export type ChatOnlyFilter = 'pinned' | 'temp' | 'group';

export interface ChatFilters {
  origins: Session['origin'][];
  statuses: ChatStatusChip[];
  personaId: string | null;
  only: ChatOnlyFilter[];
  search: string;
}

const KEY_PREFIX = 'cc_chat_filters:';
// Ключ старого формата (только тип чата) — читаем один раз для миграции
const LEGACY_ORIGINS_PREFIX = 'cc_chat_visible_origins:';

export const ALL_ORIGINS: Session['origin'][] = ['manual', 'task', 'automation'];
export const ALL_STATUS_CHIPS: ChatStatusChip[] = ['active', 'waiting', 'done', 'error'];
// Дефолт по макету А: активные срезы видны, архив (Готово) скрыт — список не зарастает
export const DEFAULT_STATUSES: ChatStatusChip[] = ['active', 'waiting', 'error'];

function defaults(): ChatFilters {
  return {
    origins: [...ALL_ORIGINS],
    statuses: [...DEFAULT_STATUSES],
    personaId: null,
    only: [],
    search: '',
  };
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

// Мирная миграция: читаем новый формат; старый объект с activeOnly (до переработки)
// просто игнорируется — этот фильтр заменён секцией статусов. origins/personaId
// наследуются, statuses/only/search берутся из хранилища или дефолтятся.
function normalize(p: Partial<ChatFilters>): ChatFilters {
  const origins = Array.isArray(p.origins) ? p.origins.filter(o => ALL_ORIGINS.includes(o)) : [...ALL_ORIGINS];
  const statuses = Array.isArray(p.statuses)
    ? p.statuses.filter(s => ALL_STATUS_CHIPS.includes(s))
    : [...DEFAULT_STATUSES];
  return {
    origins: origins.length ? origins : [...ALL_ORIGINS],
    // пустой набор статусов = «всё скрыто» — не имеет смысла, возвращаем дефолт
    statuses: statuses.length ? statuses : [...DEFAULT_STATUSES],
    personaId: typeof p.personaId === 'string' ? p.personaId : null,
    only: Array.isArray(p.only) ? p.only.filter(o => o === 'pinned' || o === 'temp' || o === 'group') : [],
    search: typeof p.search === 'string' ? p.search : '',
  };
}

export function loadChatFilters(scopeKey: string): ChatFilters {
  try {
    const raw = localStorage.getItem(KEY_PREFIX + scopeKey);
    if (raw) return normalize(JSON.parse(raw) as Partial<ChatFilters>);
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

// === Применение фильтров ===

// Чип статуса жизни чата по Session (с учётом taskDone). Единое место маппинга —
// используется и фильтром, и счётчиками на чипах, и сводкой.
// «Готово» = ТОЛЬКО taskDone (чат выполненной задачи-исполнителя). Обычный статус
// 'finished' сюда НЕ относится: это состояние покоя любого чата после хода/рестарта
// сервера, он «свободен», в него можно писать → идёт в «В работе». Иначе снятие чипа
// «Готово» прятало бы вообще все обычные чаты.
// Приоритет: ошибка важнее готовности — чат с Done-задачей, упавший в Error/Orphaned,
// остаётся в «С ошибкой» (иначе он уедет в «Готово», скрытое по умолчанию, и ошибку не видно).
export function chatStatusOf(s: Session): ChatStatusChip {
  if (s.status === 'error' || s.status === 'orphaned') return 'error';
  if (s.taskDone === true) return 'done';
  if (s.status === 'waiting') return 'waiting';
  return 'active'; // starting | working | active | finished
}

// Предикат видимости чата по фильтрам. Возвращается функцией, чтобы один и тот же
// предикат прошёл и по плоскому списку, и в buildChatTreeRows (isRootVisible).
export function matchChatFilter(filters: ChatFilters): (s: Session) => boolean {
  const q = filters.search.trim().toLowerCase();
  const onlySet = filters.only;
  return (s) => {
    if (!filters.origins.includes(s.origin)) return false;
    if (!filters.statuses.includes(chatStatusOf(s))) return false;
    if (filters.personaId && s.personaId !== filters.personaId) return false;
    if (onlySet.length) {
      // «Показать только» — OR по выбранным тегам: чат виден, если подпадает хотя бы
      // под один (закреплён ИЛИ временный ИЛИ групповой). Иначе два чекбокса гасили бы список.
      let ok = false;
      for (const o of onlySet) {
        if (o === 'pinned' && s.isPinned) { ok = true; break; }
        if (o === 'temp' && s.expiresAfterMinutes != null) { ok = true; break; }
        if (o === 'group' && (s.participants?.length ?? 0) > 1) { ok = true; break; }
      }
      if (!ok) return false;
    }
    if (q && !(s.name ?? '').toLowerCase().includes(q)) return false;
    return true;
  };
}

// Фильтр в дефолтном состоянии — сводка на триггере нейтральна.
export function isDefaultFilters(f: ChatFilters): boolean {
  return f.origins.length === ALL_ORIGINS.length && ALL_ORIGINS.every(o => f.origins.includes(o))
    && f.statuses.length === DEFAULT_STATUSES.length && DEFAULT_STATUSES.every(s => f.statuses.includes(s))
    && !f.personaId
    && f.only.length === 0
    && f.search.trim() === '';
}

export function defaultChatFilters(): ChatFilters {
  return defaults();
}

// === Empty state списка чатов (макет варианта А, сцена 3) ===

// Слово «чат» в правильной форме для числа
export function chatCountWord(n: number): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'чат';
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return 'чата';
  return 'чатов';
}

// Описание причины пустого списка: сколько чатов скрыто и поиском ли. Под empty-state.
export function buildHiddenReason(total: number, search: string): string {
  const q = search.trim();
  const who = `${total} ${chatCountWord(total)}`;
  const tail = 'Ослабьте условия или сбросьте их целиком.';
  return q ? `Все ${who} скрыты фильтрами и поиском «${q}». ${tail}` : `Все ${who} скрыты фильтрами. ${tail}`;
}
