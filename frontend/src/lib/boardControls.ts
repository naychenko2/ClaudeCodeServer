// Контролы Kanban-доски (группировка/поиск/фильтры/WIP) — общий синглтон-стор,
// чтобы тулбар (в сайдбаре на десктопе) и сетка колонок (в основной области) читали
// одно состояние. Паттерн — как featureFlags.ts. groupBy и WIP персистятся в localStorage,
// поиск/приоритеты/исполнитель — эфемерны (сбрасываются при перезагрузке).

import { useSyncExternalStore } from 'react';
import type { TaskPriority } from '../types';
import type { BoardGroupBy } from './tasks';

const GROUP_KEY = 'cc_board_group';
const WIP_KEY = 'cc_board_wip';

// 'member' — фильтр по человеку-исполнителю общего проекта (S2b-F5); появляется
// только в тулбаре общего проекта. assigneeUserId — выбранный участник; null — любой человек.
export type BoardAssigneeFilter = 'all' | 'me' | 'claude' | 'member';

export interface BoardControls {
  groupBy: BoardGroupBy;
  search: string;
  priorities: TaskPriority[];
  assignee: BoardAssigneeFilter;
  assigneeUserId: string | null;
  wip: Record<string, number>;   // лимит по id колонки
}

function loadGroup(): BoardGroupBy {
  const v = localStorage.getItem(GROUP_KEY);
  return v === 'priority' || v === 'assignee' || v === 'project' || v === 'due' ? v : 'none';
}
function loadWip(): Record<string, number> {
  try { return JSON.parse(localStorage.getItem(WIP_KEY) || '{}'); } catch { return {}; }
}

let _state: BoardControls = {
  groupBy: loadGroup(), search: '', priorities: [], assignee: 'all', assigneeUserId: null, wip: loadWip(),
};
const _listeners = new Set<() => void>();
function emit() { _listeners.forEach(fn => fn()); }
function patch(p: Partial<BoardControls>) { _state = { ..._state, ...p }; emit(); }

export function setGroupBy(g: BoardGroupBy) { patch({ groupBy: g }); localStorage.setItem(GROUP_KEY, g); }
export function setSearch(s: string) { patch({ search: s }); }
export function setPriorities(p: TaskPriority[]) { patch({ priorities: p }); }
export function togglePriorityFilter(p: TaskPriority) {
  const cur = _state.priorities;
  patch({ priorities: cur.includes(p) ? cur.filter(x => x !== p) : [...cur, p] });
}
// Любой переход по сегменту сбрасывает выбранного участника; «Участники» без id —
// любой человек (дальше выбор — чипом участника)
export function setAssigneeFilter(a: BoardAssigneeFilter, userId?: string | null) {
  patch({ assignee: a, assigneeUserId: a === 'member' ? (userId ?? null) : null });
}
// Доработка S2b-F5 (вердикт ревью 136d1069, п.1): стор общий для хаб-доски и всех
// проектных досок, а 'member' живёт только на доске общего проекта и относится к его
// участникам. TaskBoard сообщает сюда контекст своей доски (projectKey — id проекта,
// null — хаб; shared — общий ли он): вне общего проекта (хаб, личный проект) и при смене
// проекта — даже между двумя общими (id участника принадлежит прежнему) — 'member'
// сбрасывается на 'all'. Иначе фильтр остаётся активным и невидимым: ни одна кнопка
// тулбара не подсвечена, а matchTaskAssignee отсекает все задачи — доска пустая.
let _boardScope: { key: string; shared: boolean } | null = null;
export function reportBoardScope(projectKey: string | null, shared: boolean) {
  const key = projectKey ?? '';
  const prev = _boardScope;
  _boardScope = { key, shared };
  const changed = prev !== null && (prev.key !== key || prev.shared !== shared);
  if (_state.assignee === 'member' && (!shared || changed)) setAssigneeFilter('all');
}

export function setWip(columnId: string, v?: number) {
  const wip = { ..._state.wip };
  if (v) wip[columnId] = v; else delete wip[columnId];
  localStorage.setItem(WIP_KEY, JSON.stringify(wip));
  patch({ wip });
}

export function useBoardControls(): BoardControls {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _state,
    () => _state,
  );
}
