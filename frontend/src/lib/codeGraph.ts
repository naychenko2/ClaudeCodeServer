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

export type CodeGraphStatus = 'idle' | 'loading' | 'building' | 'ready' | 'empty' | 'error';

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
  hideTestNodes: boolean;
  hideOrphanNodes: boolean;
  // Режим «Фокус» холста: выбранный узел (selectedId) — центр окрестности.
  // История переходов от узла к узлу — крошки в шапке документа и «Назад».
  focusHistory: string[];
  focusDepth2: boolean;
  // Раскрытый хвост соседей («+N ещё» на холсте → полный список в панели)
  focusTail: 'in' | 'out' | null;
}

// Глубина истории фокуса: блуждание по графу может быть долгим, но крошки
// показывают последние несколько шагов — хранить всё незачем
const FOCUS_HISTORY_MAX = 24;

let _state: State = {
  projectId: null,
  data: null,
  status: 'idle',
  error: null,
  filters: { ...ALL_ON },
  query: '',
  selectedId: null,
  legendOpen: false,
  hideTestNodes: false,
  hideOrphanNodes: false,
  focusHistory: [],
  focusDepth2: false,
  focusTail: null,
};

const listeners = new Set<() => void>();
function emit() { listeners.forEach(l => l()); }
function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }
function set(patch: Partial<State>) { _state = { ..._state, ...patch }; emit(); }

// Загрузка графа проекта. Идемпотентна: повторный вызов для того же projectId при
// готовых/загружаемых/строящихся данных без force — no-op (не дёргаем сеть на каждый
// рендер и не сбиваем идущий polling сборки).
// force=true — принудительное обновление (кнопка «Обновить»).
// 404 → статус 'empty' (граф не построен); 404 + заголовок X-CodeGraph-Building —
// бэкенд уже строит в фоне (build-on-first-GET): статус 'building' + авто-polling.
// 403/прочее → 'error'.
export async function loadCodeGraph(projectId: string, force = false): Promise<void> {
  if (!force && _state.projectId === projectId
    && (_state.status === 'ready' || _state.status === 'loading' || _state.status === 'building')) {
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
    hideTestNodes: reset ? false : _state.hideTestNodes,
    hideOrphanNodes: reset ? false : _state.hideOrphanNodes,
    focusHistory: reset ? [] : _state.focusHistory,
    focusDepth2: reset ? false : _state.focusDepth2,
    focusTail: reset ? null : _state.focusTail,
  };
  emit();
  try {
    const data = await api.projects.codeGraph(projectId);
    set({ data, status: 'ready', error: null });
  } catch (e) {
    const err = e as Error & { status?: number; responseHeaders?: Headers };
    if (err.status === 404) {
      if (err.responseHeaders?.get('X-CodeGraph-Building') === 'true') {
        // Бэкенд сам запустил сборку — показываем «строится…» и ждём готовности
        set({ data: null, status: 'building', error: null });
        startGraphPolling(projectId);
      } else {
        set({ data: null, status: 'empty', error: null });
      }
    } else {
      set({ data: null, status: 'error', error: err.message ?? 'Не удалось загрузить граф' });
    }
  }
}

// Явное построение графа (кнопка «Построить граф» в empty-state, «Перестроить» в
// stale-бейдже). POST /code-graph/build — синхронный rebuild на бэке (202 = построен),
// затем догружаем снапшот polling'ом (страховка на случай гонки «202 пришёл, а GET
// ещё 404»). Повторный вызов во время сборки — no-op (двойной клик по кнопке).
export async function buildCodeGraph(projectId: string): Promise<void> {
  if (_state.projectId === projectId && _state.status === 'building') return;
  set({ status: 'building', error: null });
  try {
    await api.projects.codeGraphBuild(projectId);
  } catch (e) {
    // Сборка могла завершиться, пока юзер уходил на другой проект, — не затираем его стор
    if (_state.projectId !== projectId) return;
    const err = e as Error & { status?: number };
    set({ status: 'error', error: err.message ?? 'Не удалось построить граф' });
    return;
  }
  if (_state.projectId !== projectId || _state.status !== 'building') return;
  startGraphPolling(projectId);
}

// Polling готовности графа после запуска сборки (явной или фоновой на бэке):
// GET раз в 2с до появления снапшота. Дедуплицируется — параллельных циклов нет.
// GET опрашивает напрямую api (не loadCodeGraph — тот no-op при 'building').
const POLL_INTERVAL_MS = 2_000;
// 45 попыток × 2с ≈ 90с ожидания — перекрывает типичную первичную сборку «около минуты»
const POLL_MAX_ATTEMPTS = 45;
let _pollingProjectId: string | null = null;

function startGraphPolling(projectId: string): void {
  if (_pollingProjectId === projectId) return;
  _pollingProjectId = projectId;
  void (async () => {
    // Допуск на сетевые глитчи: три подряд не-404 ошибки — сдаёмся в error
    let failures = 0;
    try {
      for (let attempt = 0; attempt < POLL_MAX_ATTEMPTS; attempt++) {
        await new Promise(r => setTimeout(r, POLL_INTERVAL_MS));
        // Юзер сменил проект или статус уже сменился (повтор/ошибка) — цикл не нужен
        if (_state.projectId !== projectId || _state.status !== 'building') return;
        try {
          const data = await api.projects.codeGraph(projectId);
          if (_state.projectId !== projectId || _state.status !== 'building') return;
          set({ data, status: 'ready', error: null });
          return;
        } catch (e) {
          const err = e as Error & { status?: number };
          if (err.status === 404) {
            failures = 0; // сборка ещё идёт — штатное ожидание
            continue;
          }
          if (++failures >= 3) {
            if (_state.projectId !== projectId || _state.status !== 'building') return;
            set({ status: 'error', error: err.message ?? 'Не удалось загрузить граф' });
            return;
          }
        }
      }
      // Таймаут ожидания: сборка затянулась — отдаём ошибку с возможностью повторить
      if (_state.projectId === projectId && _state.status === 'building') {
        set({ status: 'error', error: 'Сборка графа заняла слишком много времени — попробуйте ещё раз.' });
      }
    } finally {
      if (_pollingProjectId === projectId) _pollingProjectId = null;
    }
  })();
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

// Выбор узла на холсте/в god-списке/в поиске/в паспорте: холст переходит в режим
// «Фокус» — окрестность этого типа. История переходов копится здесь, а не в точках
// вызова, поэтому крошки работают одинаково для всех входов в фокус.
// При выборе легенда сворачивается сама — паспорт получает всю высоту панели.
export function selectGraphNode(id: string | null) {
  if (id === _state.selectedId) return;
  const prev = _state.selectedId;
  const history = id && prev
    ? [..._state.focusHistory, prev].slice(-FOCUS_HISTORY_MAX)
    : [];
  set({
    selectedId: id,
    legendOpen: id ? false : _state.legendOpen,
    focusHistory: history,
    focusTail: null,   // новый центр — старый раскрытый хвост уже не про него
  });
}

// «← Назад»: возврат к предыдущему центру фокуса
export function focusGraphBack() {
  const history = _state.focusHistory;
  if (!history.length) return;
  set({
    selectedId: history[history.length - 1],
    focusHistory: history.slice(0, -1),
    focusTail: null,
  });
}

// Клик по крошке: возврат к узлу пути, всё, что было после него, отбрасывается
export function focusGraphCrumb(id: string) {
  const i = _state.focusHistory.indexOf(id);
  if (i < 0) return;
  set({ selectedId: id, focusHistory: _state.focusHistory.slice(0, i), focusTail: null });
}

export function toggleGraphFocusDepth2() {
  set({ focusDepth2: !_state.focusDepth2 });
}

// Раскрытие хвоста соседей: заглушка «+N» на холсте → полный список в панели
export function setGraphFocusTail(side: 'in' | 'out' | null) {
  set({ focusTail: _state.focusTail === side ? null : side });
}

export function setGraphLegendOpen(open: boolean) {
  set({ legendOpen: open });
}

// Фильтры «скрыть тесты» и «скрыть сироты» — на стороне фронта
export function toggleHideTestNodes() {
  set({ hideTestNodes: !_state.hideTestNodes });
}
export function toggleHideOrphanNodes() {
  set({ hideOrphanNodes: !_state.hideOrphanNodes });
}

// Снимок состояния вне React — для тестов и не-компонентных потребителей
export function getCodeGraphState(): Readonly<State> {
  return _state;
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
    build: (projectId: string) => { void buildCodeGraph(projectId); },
    toggleFilter: toggleGraphFilter,
    resetFilters: resetGraphFilters,
    setQuery: setGraphQuery,
    select: selectGraphNode,
    setLegendOpen: setGraphLegendOpen,
    toggleHideTestNodes,
    toggleHideOrphanNodes,
    focusBack: focusGraphBack,
    focusCrumb: focusGraphCrumb,
    toggleFocusDepth2: toggleGraphFocusDepth2,
    setFocusTail: setGraphFocusTail,
  }), []);
}
