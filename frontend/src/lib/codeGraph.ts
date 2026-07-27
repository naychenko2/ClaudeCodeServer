// Стор Code Graph: загрузка карты типов/связей проекта по контракту
// GET /api/projects/{id}/code-graph и UI-режимы её отображения (фильтры по типу
// связи, поиск, выбранный узел, свёрнутость легенды). Паттерн модульного стора
// на useSyncExternalStore — как lib/knowledge.ts / lib/notes.ts.
//
// Документ «Граф» в центральной зоне (открыт/закрыт) сюда НЕ входит — это состояние
// контентной зоны наряду с openFile/selectedTask, им владеет WorkspacePage. Стор
// хранит только графовые данные и режимы панели/холста, которые шарят документ и панель.
import { useMemo, useSyncExternalStore } from 'react';
import type { CodeGraph, CodeGraphRelation } from '../types';
import { api } from './api';

export type CodeGraphStatus = 'idle' | 'loading' | 'ready' | 'empty' | 'error';

export interface CodeGraphFilters {
  Calls: boolean;
  Implements: boolean;
  References: boolean;
}

const ALL_ON: CodeGraphFilters = { Calls: true, Implements: true, References: true };

export const GRAPH_RELATIONS: CodeGraphRelation[] = ['Calls', 'Implements', 'References'];

interface State {
  projectId: string | null;
  data: CodeGraph | null;
  status: CodeGraphStatus;
  error: string | null;
  filters: CodeGraphFilters;
  query: string;
  selectedId: string | null;
  legendOpen: boolean;
}

let _state: State = {
  projectId: null,
  data: null,
  status: 'idle',
  error: null,
  filters: { ...ALL_ON },
  query: '',
  selectedId: null,
  legendOpen: false,
};

const listeners = new Set<() => void>();
function emit() { listeners.forEach(l => l()); }
function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }
function set(patch: Partial<State>) { _state = { ..._state, ...patch }; emit(); }

// Загрузка графа проекта. Идемпотентна: повторный вызов для того же projectId при
// готовых/загружаемых данных без force — no-op (не дёргаем сеть на каждый рендер).
// force=true — принудительное обновление (кнопка «Перестроить/Обновить»).
// 404 → статус 'empty' (граф не построен), 403/прочее → 'error'.
export async function loadCodeGraph(projectId: string, force = false): Promise<void> {
  if (!force && _state.projectId === projectId && (_state.status === 'ready' || _state.status === 'loading')) {
    return;
  }
  // Смена проекта — сбрасываем режимы к дефолтам, чтобы не тащить выбор/фильтры из старого
  const reset = _state.projectId !== projectId;
  _state = {
    projectId,
    data: reset ? null : _state.data,
    status: 'loading',
    error: null,
    filters: reset ? { ...ALL_ON } : _state.filters,
    query: reset ? '' : _state.query,
    selectedId: reset ? null : _state.selectedId,
    legendOpen: reset ? false : _state.legendOpen,
  };
  emit();
  try {
    const data = await api.projects.codeGraph(projectId);
    set({ data, status: 'ready', error: null });
  } catch (e) {
    const err = e as Error & { status?: number };
    if (err.status === 404) {
      set({ data: null, status: 'empty', error: null });
    } else {
      set({ data: null, status: 'error', error: err.message ?? 'Не удалось загрузить граф' });
    }
  }
}

export function setGraphFilter(rel: CodeGraphRelation, on: boolean) {
  set({ filters: { ..._state.filters, [rel]: on } });
}
export function toggleGraphFilter(rel: CodeGraphRelation) {
  setGraphFilter(rel, !_state.filters[rel]);
}
export function resetGraphFilters() {
  set({ filters: { ...ALL_ON } });
}

export function setGraphQuery(q: string) {
  // Ввод запроса снимает выбор узла (как в макете): поиск и фокус-подсветка
  // узла — взаимоисключающие режимы подсветки холста
  set({ query: q, selectedId: q ? null : _state.selectedId });
}

// Выбор узла на холсте/в god-списке: подсветка инцидентных рёбер + паспорт.
// При выборе легенда сворачивается сама — паспорт получает всю высоту панели.
export function selectGraphNode(id: string | null) {
  set({ selectedId: id, legendOpen: id ? false : _state.legendOpen });
}

export function setGraphLegendOpen(open: boolean) {
  set({ legendOpen: open });
}

// Подписка на снимок стора. Actions возвращаются стабильным useCallback —
// можно передавать в дочерние компоненты как пропсы без лишних ререндеров.
export function useCodeGraph(): State {
  const snapshot = useSyncExternalStore(subscribe, () => _state);
  return snapshot;
}

export function useCodeGraphActions() {
  // useMemo ([]): действия — стабильные функции, но без мемоизации возвращаемый
  // объект пересоздаётся каждый рендер, и эффекты потребителей с [actions, projectId]
  // перезапускаются на каждом рендере. Для loadCodeGraph это безобидно при ready/loading
  // (no-op), но при empty/error идемпотентность не срабатывает — панель уходила в
  // бесконечный цикл loading→404→empty→эффект→loading… (десятки запросов/сек).
  return useMemo(() => ({
    load: (projectId: string, force?: boolean) => { void loadCodeGraph(projectId, force); },
    toggleFilter: toggleGraphFilter,
    resetFilters: resetGraphFilters,
    setQuery: setGraphQuery,
    select: selectGraphNode,
    setLegendOpen: setGraphLegendOpen,
  }), []);
}
