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

// Оси вида списка (группировка, порядок, иерархия) — живут в filters, персистятся
// вместе с ним. От фильтрующих полей отличаются тем, что не скрывают чаты, а лишь
// перестраивают список: isDefaultFilters их не учитывает.
export type ChatGroupBy = 'days' | 'tags' | 'none';
export type ChatSortOrder = 'newest' | 'oldest';

export interface ChatFilters {
  origins: Session['origin'][];
  statuses: ChatStatusChip[];
  personaId: string | null;
  only: ChatOnlyFilter[];
  search: string;
  // Выбранные общие теги: чат виден, если у него есть ХОТЯ БЫ ОДИН из них (OR-пересечение).
  // Пусто/отсутствует — без фильтра по тегам (дефолт)
  tags?: string[];
  // Группировка секций: по дням (с pinned-секцией) / по общим тегам / единый список
  groupBy: ChatGroupBy;
  // Порядок внутри секций и (для days/none) порядок самих секций
  sortOrder: ChatSortOrder;
  // Дерево по parentSessionId: группировка применяется к корням, дети — под корнём
  hierarchy: boolean;
  // Режим архива: список показывает ТОЛЬКО архивные чаты (обычные скрыты).
  // Это ОСЬ РЕЖИМА, а не значение в only[]: там OR-семантика «показать только»
  // среди обычных чатов, и архив подмешался бы к ним. Триггер фильтров ось не
  // красит (isDefaultFilters её не учитывает — как groupBy/sortOrder/hierarchy),
  // а «Сбросить всё» из режима архива не выкидывает.
  archivedOnly: boolean;
}

const KEY_PREFIX = 'cc_chat_filters:';
// Ключ старого формата (только тип чата) — читаем один раз для миграции
const LEGACY_ORIGINS_PREFIX = 'cc_chat_visible_origins:';
// Ключ удалённого режима вида ('flat'|'tree'|'tags') — мигрируем в оси groupBy/hierarchy
const LEGACY_VIEW_PREFIX = 'cc_chat_view:';

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
    groupBy: 'days',
    sortOrder: 'newest',
    hierarchy: false,
    archivedOnly: false,
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

// Миграция удалённого useChatView: 'tree' ⇒ hierarchy, 'tags' ⇒ groupBy='tags',
// 'flat' ⇒ дефолт. Ключ при чтении НЕ удаляем: до первой записи нового формата
// (persistChatFilters) это единственная копия выбора — удали мы её здесь, повторный
// load («открыли проект → ушли → вернулись») потерял бы мигрированные оси.
// Повторная миграция идемпотентна, а удаляет ключ persistChatFilters после успешной
// записи — тогда выбор уже сохранён в cc_chat_filters:.
function readLegacyView(scopeKey: string): Partial<ChatFilters> {
  try {
    const v = localStorage.getItem(LEGACY_VIEW_PREFIX + scopeKey);
    if (v === 'tree') return { hierarchy: true };
    if (v === 'tags') return { groupBy: 'tags' };
    return {};
  } catch {
    return {};
  }
}

// Мирная миграция: читаем новый формат; старый объект с activeOnly (до переработки)
// просто игнорируется — этот фильтр заменён секцией статусов. origins/personaId
// наследуются, statuses/only/search берутся из хранилища или дефолтятся.
// Оси (groupBy/sortOrder/hierarchy) у записи старого формата отсутствуют —
// подставляем из legacy-ключа cc_chat_view: (или дефолт).
function normalize(p: Partial<ChatFilters>, scopeKey?: string): ChatFilters {
  const origins = Array.isArray(p.origins) ? p.origins.filter(o => ALL_ORIGINS.includes(o)) : [...ALL_ORIGINS];
  const statuses = Array.isArray(p.statuses)
    ? p.statuses.filter(s => ALL_STATUS_CHIPS.includes(s))
    : [...DEFAULT_STATUSES];
  // Оси отсутствуют в записи (формат до их появления) — пробуем legacy-ключ вида
  const legacyAxes = p.groupBy === undefined && p.sortOrder === undefined && p.hierarchy === undefined && scopeKey
    ? readLegacyView(scopeKey)
    : {};
  const groupBy: ChatGroupBy =
    p.groupBy === 'tags' || p.groupBy === 'none' || p.groupBy === 'days'
      ? p.groupBy
      : legacyAxes.groupBy ?? 'days';
  return {
    origins: origins.length ? origins : [...ALL_ORIGINS],
    // пустой набор статусов = «всё скрыто» — не имеет смысла, возвращаем дефолт
    statuses: statuses.length ? statuses : [...DEFAULT_STATUSES],
    personaId: typeof p.personaId === 'string' ? p.personaId : null,
    only: Array.isArray(p.only) ? p.only.filter(o => o === 'pinned' || o === 'temp' || o === 'group') : [],
    search: typeof p.search === 'string' ? p.search : '',
    tags: Array.isArray(p.tags) ? p.tags.filter((t): t is string => typeof t === 'string' && t.length > 0) : [],
    groupBy,
    sortOrder: p.sortOrder === 'oldest' || p.sortOrder === 'newest' ? p.sortOrder : 'newest',
    hierarchy: typeof p.hierarchy === 'boolean' ? p.hierarchy : legacyAxes.hierarchy ?? false,
    // Запись старого формата (до режима архива) — начинаем с обычного списка
    archivedOnly: typeof p.archivedOnly === 'boolean' ? p.archivedOnly : false,
  };
}

export function loadChatFilters(scopeKey: string): ChatFilters {
  try {
    const raw = localStorage.getItem(KEY_PREFIX + scopeKey);
    if (raw) return normalize(JSON.parse(raw) as Partial<ChatFilters>, scopeKey);
    // Записи фильтров ещё нет: могли остаться ключи старых форматов — мигрируем их
    const legacy = readLegacyOrigins(scopeKey);
    if (legacy) return normalize({ ...readLegacyView(scopeKey), origins: legacy }, scopeKey);
    return normalize(readLegacyView(scopeKey), scopeKey);
  } catch { /* повреждённое значение — дефолт */ }
  return defaults();
}

export function persistChatFilters(scopeKey: string, v: ChatFilters): void {
  try {
    localStorage.setItem(KEY_PREFIX + scopeKey, JSON.stringify(v));
    // Новый формат сохранён — legacy-ключи миграции больше не нужны (до этого
    // момента readLegacyView их намеренно не удалял, чтобы не потерять выбор).
    localStorage.removeItem(LEGACY_VIEW_PREFIX + scopeKey);
    localStorage.removeItem(LEGACY_ORIGINS_PREFIX + scopeKey);
  } catch { /* квота/приватный режим */ }
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

// Признак архива — производное бэка (ArchivedAt != null && UpdatedAt <= ArchivedAt),
// отдаётся на фронт готовым bool в Session.archived / HomeSessionInfo.archived.
// На фронте НЕ пересчитываем из updatedAt/archivedAt — это вторая копия правила,
// плюс мигание на равных таймстемпах (см. docs/architecture/features.md, раздел
// «Архив чатов»). Узкий type guard: тип Session/HomeSessionInfo сейчас не содержит
// поля явно (тип обновляется параллельно), читаем без строгой типизации.
// Сервер отдаёт `isArchived` (см. Session.DTO); на случай старого поля `archived`
// — пробуем оба. Тип Session расширяется в рантайме: фронт не должен падать, если
// бэкенд прислал только одно из двух.
export function isArchivedChat(s: Session): boolean {
  const x = s as unknown as { archived?: unknown; isArchived?: unknown };
  return Boolean(x.archived ?? x.isArchived);
}

// Предикат видимости чата по фильтрам. Возвращается функцией, чтобы один и тот же
// предикат прошёл и по плоскому списку, и в buildChatTreeRows (isVisible — ко всем узлам).
export function matchChatFilter(filters: ChatFilters): (s: Session) => boolean {
  const q = filters.search.trim().toLowerCase();
  const onlySet = filters.only;
  return (s) => {
    // Ось режима: обычный список прячет архивные чаты, режим архива — обычные.
    // Смешения нет ни в одну сторону. Дочерние чаты архивного родителя «всплывают»
    // через прокол в buildChatTreeRows (filterForest) — то же множество, что в
    // плоском списке, и hiddenCount не зависит от вида.
    if (isArchivedChat(s) !== filters.archivedOnly) return false;
    // ЛОВУШКА: остальные фильтры в режиме архива продолжают работать, а дефолтный
    // набор статусов НЕ содержит 'done' — архивный чат выполненной задачи (taskDone)
    // остаётся скрыт, пока чип «Завершён» не включён. Это не баг фильтра: список
    // архива подчиняется тем же чипам, что обычный. Пустой экран в этом случае —
    // повод для отдельной ветки empty-state («скрыты фильтрами»), а не для проверки
    // архива вперёд статусов.
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
    // Теги: выбран непустой набор — чат обязан иметь хотя бы один из него (пересечение)
    if (filters.tags?.length && !(s.tags ?? []).some(t => filters.tags!.includes(t))) return false;
    return true;
  };
}

// Фильтр в дефолтном состоянии — сводка на триггере нейтральна.
// Оси вида (groupBy/sortOrder/hierarchy) намерено НЕ учитываются: они не скрывают
// чаты, а перестраивают список — активная ось не должна красить триггер фильтров.
// archivedOnly — тоже ось (у неё свой видимый переключатель «Архивные»), поэтому
// режим архива не красит триггер и не считается «включённым фильтром».
export function isDefaultFilters(f: ChatFilters): boolean {
  return f.origins.length === ALL_ORIGINS.length && ALL_ORIGINS.every(o => f.origins.includes(o))
    && f.statuses.length === DEFAULT_STATUSES.length && DEFAULT_STATUSES.every(s => f.statuses.includes(s))
    && !f.personaId
    && f.only.length === 0
    && f.search.trim() === ''
    && (f.tags?.length ?? 0) === 0;
}

export function defaultChatFilters(): ChatFilters {
  return defaults();
}

// Сброс только фильтрующих полей, оси вида сохраняются — «Сбросить всё» в поповере
// фильтров не должен неожиданно перестраивать список (группировка/порядок/иерархия)
// и уж тем более выкидывать из режима архива: выйти из него — дело переключателя.
export function defaultChatFiltersKeepingView(f: ChatFilters): ChatFilters {
  return {
    ...defaults(),
    groupBy: f.groupBy,
    sortOrder: f.sortOrder,
    hierarchy: f.hierarchy,
    archivedOnly: f.archivedOnly,
  };
}

// === Тексты режима архива (единый источник для обоих списков) ===

export const ARCHIVE_EMPTY_TITLE = 'Архивных чатов нет';
export const ARCHIVE_EMPTY_SUBTITLE =
  'Уберите чат в архив из его меню — он спрячется из списка, но останется целым: история и контекст на месте';

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
// Число согласовано с глаголом: 1 — «Единственный чат скрыт», 3 — «Все 3 чата скрыты»,
// 12 — «Все 12 чатов скрыты», 21 — «Все 21 чат скрыт».
export function buildHiddenReason(total: number, search: string): string {
  const q = search.trim();
  const word = chatCountWord(total);
  const subject = total === 1 ? 'Единственный чат' : `Все ${total} ${word}`;
  const verb = word === 'чат' ? 'скрыт' : 'скрыты';
  const by = q ? `фильтрами и поиском «${q}»` : 'фильтрами';
  return `${subject} ${verb} ${by}. Ослабьте условия или сбросьте их целиком.`;
}
