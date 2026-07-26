// Персистентность настроек вида списка задач (localStorage): фильтры и группировка.
// Раздельно по проектам — переключение проекта не смешивает настройки, как у чатов
// (useChatFilters). Зеркало lib/chatFilters.ts по структуре и правилам нормализации.
import { useCallback, useEffect, useRef, useState } from 'react';
import type { TaskPriority, TaskStatus } from '../types';
import {
  EMPTY_TASK_FILTERS, type DueKey, type TaskAssigneeFilter, type TaskListFilters,
} from '../features/tasks/TasksListFilter';

// === Фильтры списка задач ===

const FILTERS_KEY_PREFIX = 'cc_task_filters:';

// Допустимые значения каждого поля — чтобы при чтении из localStorage мирно отбросить
// неизвестные/устаревшие (как normalize в chatFilters).
const VALID_STATUSES = new Set<TaskStatus>(['todo', 'inProgress', 'done']);
const VALID_PRIORITIES = new Set<TaskPriority>(['urgent', 'high', 'medium', 'low']);
const VALID_ASSIGNEE = new Set<TaskAssigneeFilter>(['all', 'me', 'claude']);
const VALID_DUE = new Set<DueKey>(['overdue', 'today', 'week', 'later', 'none']);

function normalizeFilters(p: Partial<TaskListFilters> | null | undefined): TaskListFilters {
  if (!p) return { ...EMPTY_TASK_FILTERS };
  const status = Array.isArray(p.status) ? p.status.filter(s => VALID_STATUSES.has(s)) : [];
  const priorities = Array.isArray(p.priorities) ? p.priorities.filter(v => VALID_PRIORITIES.has(v)) : [];
  const due = Array.isArray(p.due) ? p.due.filter(v => VALID_DUE.has(v)) : [];
  const assignee = VALID_ASSIGNEE.has(p.assignee as TaskAssigneeFilter) ? p.assignee as TaskAssigneeFilter : 'all';
  return { status, assignee, priorities, due };
}

export function loadTaskFilters(scopeKey: string): TaskListFilters {
  try {
    const raw = localStorage.getItem(FILTERS_KEY_PREFIX + scopeKey);
    if (raw) return normalizeFilters(JSON.parse(raw) as Partial<TaskListFilters>);
  } catch { /* повреждённое значение — дефолт */ }
  return { ...EMPTY_TASK_FILTERS };
}

export function persistTaskFilters(scopeKey: string, v: TaskListFilters): void {
  try { localStorage.setItem(FILTERS_KEY_PREFIX + scopeKey, JSON.stringify(v)); } catch { /* квота/приватный режим */ }
}

// Состояние фильтров для одного проекта. Перечитывает хранилище при смене scopeKey.
// setFilters — стабильная (useCallback) обёртка полной замены с персистом: её можно
// передавать как onFilters в мемоизированные компоненты (TasksPanel, FilterButton),
// не ломая мемоизацию (обычный useState-сеттер мы обернули, поэтому стабильность
// обязана сохраняться явно — иначе вернём регрессию).
export function useTaskFilters(scopeKey: string) {
  const [filters, setFiltersState] = useState<TaskListFilters>(() => loadTaskFilters(scopeKey));
  const scopeRef = useRef(scopeKey);

  useEffect(() => {
    if (scopeRef.current === scopeKey) return;
    scopeRef.current = scopeKey;
    setFiltersState(loadTaskFilters(scopeKey));
  }, [scopeKey]);

  const setFilters = useCallback((f: TaskListFilters) => {
    persistTaskFilters(scopeKey, f);
    setFiltersState(f);
  }, [scopeKey]);

  return { filters, setFilters };
}

// === Группировка списка задач: «Список»(по статусу) | «По дате» ===

const GROUP_KEY_PREFIX = 'cc_task_group:';
type TaskGroupTab = 'status' | 'date';

const VALID_TABS = new Set<TaskGroupTab>(['status', 'date']);

export function loadTaskGroupTab(scopeKey: string): TaskGroupTab {
  try {
    const raw = localStorage.getItem(GROUP_KEY_PREFIX + scopeKey);
    if (raw && VALID_TABS.has(raw as TaskGroupTab)) return raw as TaskGroupTab;
  } catch { /* повреждённое значение — дефолт */ }
  return 'status';
}

export function persistTaskGroupTab(scopeKey: string, v: TaskGroupTab): void {
  try { localStorage.setItem(GROUP_KEY_PREFIX + scopeKey, v); } catch { /* квота/приватный режим */ }
}

export function useTaskGroupTab(scopeKey: string) {
  const [tab, setTabState] = useState<TaskGroupTab>(() => loadTaskGroupTab(scopeKey));
  const scopeRef = useRef(scopeKey);

  useEffect(() => {
    if (scopeRef.current === scopeKey) return;
    scopeRef.current = scopeKey;
    setTabState(loadTaskGroupTab(scopeKey));
  }, [scopeKey]);

  const setTab = useCallback((v: TaskGroupTab) => {
    persistTaskGroupTab(scopeKey, v);
    setTabState(v);
  }, [scopeKey]);

  return { tab, setTab };
}
