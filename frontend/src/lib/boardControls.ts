// Контролы Kanban-доски (группировка/поиск/фильтры/WIP) — общий синглтон-стор,
// чтобы тулбар (в сайдбаре на десктопе) и сетка колонок (в основной области) читали
// одно состояние. Паттерн — как featureFlags.ts. groupBy и WIP персистятся в localStorage,
// поиск/приоритеты/исполнитель — эфемерны (сбрасываются при перезагрузке).

import { useSyncExternalStore } from 'react';
import type { TaskPriority } from '../types';
import type { BoardGroupBy } from './tasks';

const GROUP_KEY = 'cc_board_group';
const WIP_KEY = 'cc_board_wip';

export interface BoardControls {
  groupBy: BoardGroupBy;
  search: string;
  priorities: TaskPriority[];
  assignee: 'all' | 'me' | 'claude';
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
  groupBy: loadGroup(), search: '', priorities: [], assignee: 'all', wip: loadWip(),
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
export function setAssigneeFilter(a: 'all' | 'me' | 'claude') { patch({ assignee: a }); }
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
