// Стор Code Graph: загрузка карты типов/связей проекта по контракту
// GET /api/projects/{id}/code-graph и UI-режимы её отображения (фильтры по типу
// связи, поиск, выбранный узел, свёрнутость легенды). Паттерн модульного стора
// на useSyncExternalStore — как lib/knowledge.ts / lib/notes.ts.
//
// Документ «Граф» в центральной зоне (открыт/закрыт) сюда НЕ входит — это состояние
// контентной зоны наряду с openFile/selectedTask, им владеет WorkspacePage. Стор
// хранит только графовые данные и режимы панели/холста, которые шарят документ и панель.
//
// Навигация — единая цепочка (`navPath`), а не тумблер режимов: «Обзор» и «Фокус» не
// вкладки, а точки на одном пути. Группа-шаг ведёт в обзор с соответствующим раскрытием,
// узел-шаг — в фокус на этом типе. `viewMode`/`selectedId`/`focusHistory`/`overviewExpanded`/
// `overviewTypesGroup` — производные от navPath, кэшируются в состоянии при каждом set(),
// чтобы useSyncExternalStore получал стабильный снимок и старым потребителям не пришлось
// переписывать чтение (s.selectedId и т.п. работают как раньше).
import { useMemo, useSyncExternalStore } from 'react';
import type { CodeGraph, CodeGraphRelation } from '../types';
import { fqnIndex, defaultExpandedGroups, pathToType } from '../features/codegraph/graphOverview';
import { api } from './api';

export type CodeGraphStatus = 'idle' | 'loading' | 'building' | 'ready' | 'empty' | 'error';

export interface CodeGraphFilters {
  Calls: boolean;
  Implements: boolean;
  References: boolean;
}

const ALL_ON: CodeGraphFilters = { Calls: true, Implements: true, References: true };

export const GRAPH_RELATIONS: CodeGraphRelation[] = ['Calls', 'Implements', 'References'];

// Шаг навигационной цепочки: группа неймспейса (ведёт в «Обзор») или тип (ведёт в «Фокус»).
// `drilled` — группа раскрыта до уровня типов (в отличие от простого «раскрыть подгруппы»).
export type NavGroupStep = { kind: 'group'; group: string; drilled: boolean };
export type NavNodeStep = { kind: 'node'; id: string };
export type NavStep = NavGroupStep | NavNodeStep;

interface InternalState {
  projectId: string | null;
  data: CodeGraph | null;
  status: CodeGraphStatus;
  error: string | null;
  filters: CodeGraphFilters;
  query: string;
  legendOpen: boolean;
  hideTestNodes: boolean;
  hideOrphanNodes: boolean;
  // Языковые фильтры: оба включены по умолчанию, последний включённый не снимается
  // (toggleGraphLanguage молча отказывает, если выключатся оба — пустой граф никому не нужен).
  // Применяется и к списку/поиску, и к холстам через единый предикат nodeLanguage().
  langCSharp: boolean;
  langTypeScript: boolean;
  focusDepth2: boolean;
  // Раскрытый хвост соседей («+N ещё» на холсте → полный список в панели)
  focusTail: 'in' | 'out' | null;
  // Единая цепочка навигации: ноль-и-более группа-шагов (всегда общий префикс), затем
  // ноль-и-более узел-шагов (перефокус в «Фокусе» дописывает их в хвост).
  navPath: NavStep[];
}

// Производные поля, кэшируемые в состоянии при каждом set() — потребители читают их
// как раньше (s.selectedId, s.viewMode…), реальный источник истины — navPath.
interface DerivedFields {
  selectedId: string | null;
  viewMode: 'focus' | 'overview';
  focusHistory: string[];
  overviewExpanded: string[];
  overviewTypesGroup: string | null;
}

type State = InternalState & DerivedFields;

function groupSteps(navPath: NavStep[]): NavGroupStep[] {
  return navPath.filter((s): s is NavGroupStep => s.kind === 'group');
}

function derive(navPath: NavStep[]): DerivedFields {
  const last = navPath[navPath.length - 1];
  const gs = groupSteps(navPath);
  const lastGroup = gs[gs.length - 1];
  const expanded: string[] = [];
  gs.forEach((g, i) => { if (!(i === gs.length - 1 && g.drilled)) expanded.push(g.group); });

  const nodeIds: string[] = [];
  for (let i = navPath.length - 1; i >= 0; i--) {
    const step = navPath[i];
    if (step.kind !== 'node') break;
    nodeIds.unshift(step.id);
  }

  return {
    selectedId: last?.kind === 'node' ? last.id : null,
    viewMode: last?.kind === 'node' ? 'focus' : 'overview',
    focusHistory: nodeIds.slice(0, -1),
    overviewExpanded: expanded,
    overviewTypesGroup: lastGroup?.drilled ? lastGroup.group : null,
  };
}

// Глубина истории фокуса: блуждание по графу может быть долгим, но крошки
// показывают последние несколько шагов — хранить всё незачем
const NAV_PATH_MAX = 32;

let _state: State = {
  projectId: null,
  data: null,
  status: 'idle',
  error: null,
  filters: { ...ALL_ON },
  query: '',
  legendOpen: false,
  hideTestNodes: false,
  hideOrphanNodes: false,
  langCSharp: true,
  langTypeScript: true,
  focusDepth2: false,
  focusTail: null,
  navPath: [],
  ...derive([]),
};

const listeners = new Set<() => void>();
function emit() { listeners.forEach(l => l()); }
function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }
function set(patch: Partial<InternalState>) {
  const navPath = patch.navPath ?? _state.navPath;
  _state = { ..._state, ...patch, navPath, ...derive(navPath) };
  emit();
}

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
  const navPath = reset ? [] : _state.navPath;
  _state = {
    ..._state,
    projectId,
    data: reset ? null : _state.data,
    status: 'loading',
    error: null,
    filters: reset ? { ...ALL_ON } : _state.filters,
    query: reset ? '' : _state.query,
    legendOpen: reset ? false : _state.legendOpen,
    hideTestNodes: reset ? false : _state.hideTestNodes,
    hideOrphanNodes: reset ? false : _state.hideOrphanNodes,
    langCSharp: reset ? true : _state.langCSharp,
    langTypeScript: reset ? true : _state.langTypeScript,
    focusDepth2: reset ? false : _state.focusDepth2,
    focusTail: reset ? null : _state.focusTail,
    navPath,
    ...derive(navPath),
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

// Языковой фильтр. Хотя бы один язык должен оставаться включённым — иначе
// граф пустеет целиком, а это против цели фильтра («убрать лишнее», не «убрать всё»).
// Поэтому попытка снять последний включённый — no-op без побочных эффектов.
export function toggleGraphLanguage(lang: 'csharp' | 'typescript') {
  const nextCSharp = lang === 'csharp' ? !_state.langCSharp : _state.langCSharp;
  const nextTypeScript = lang === 'typescript' ? !_state.langTypeScript : _state.langTypeScript;
  if (!nextCSharp && !nextTypeScript) return;
  set({ langCSharp: nextCSharp, langTypeScript: nextTypeScript });
}

export function resetGraphFilters() {
  set({ filters: { ...ALL_ON }, langCSharp: true, langTypeScript: true });
}

export function setGraphQuery(q: string) {
  // Ввод запроса снимает выбор узла (как в макете): поиск и фокус-подсветка
  // узла — взаимоисключающие режимы подсветки холста
  set({ query: q, navPath: q ? [] : _state.navPath });
}

// Сквозной вход в «Фокус» (поиск / god-список / клик по типу в «Обзоре»): ВСЕГДА
// свежий переход — цепочка группа-шагов пересчитывается заново от корня к типу через
// pathToType, независимо от того, в каком состоянии был документ до этого. Легенда
// сворачивается сама — паспорт получает всю высоту панели. id === null — полный сброс
// к корню «Обзора» (клик по пустому холсту фокуса).
export function selectGraphNode(id: string | null) {
  if (!id) {
    if (!_state.navPath.length) return;
    set({ navPath: [], legendOpen: _state.legendOpen, focusTail: null });
    return;
  }
  if (_state.selectedId === id) return;
  const node = _state.data?.nodes.find(n => n.id === id);
  let steps: NavStep[];
  if (!node || !_state.data) {
    steps = [{ kind: 'node', id }];
  } else {
    const fqns = fqnIndex(_state.data.nodes);
    const auto = defaultExpandedGroups(_state.data.nodes);
    const groups = pathToType(node, fqns, auto);
    steps = [
      ...groups.map((group, i): NavGroupStep => ({ kind: 'group', group, drilled: i === groups.length - 1 })),
      { kind: 'node', id },
    ];
  }
  set({ navPath: steps, legendOpen: false, focusTail: null });
}

// Перефокус на соседа ТЕКУЩЕГО фокуса (клик по узлу на холсте фокуса, по ссылке связи
// в паспорте, по элементу раскрытого хвоста «+N») — дописывает шаг в конец цепочки,
// группа-префикс (откуда мы пришли в фокус) не трогается.
export function refocusGraphNode(id: string) {
  if (_state.selectedId === id) return;
  const navPath = [..._state.navPath, { kind: 'node' as const, id }].slice(-NAV_PATH_MAX);
  set({ navPath, focusTail: null });
}

// Клик по группе «Обзора» с подгруппами: раскрыть на уровень глубже (subgroups становятся
// видимыми элементами вместо самой группы)
export function expandOverviewGroup(group: string) {
  set({ navPath: [..._state.navPath, { kind: 'group', group, drilled: false }] });
}

// Клик по листовой группе (без подгрупп) или по уже раскрытой — раскрыть до уровня типов
export function drillOverviewTypes(group: string) {
  const cur = _state.navPath;
  const last = cur[cur.length - 1];
  if (last?.kind === 'group' && last.group === group) {
    set({ navPath: [...cur.slice(0, -1), { ...last, drilled: true }] });
  } else {
    set({ navPath: [...cur, { kind: 'group', group, drilled: true }] });
  }
}

// «Назад»: один шаг цепочки назад — из фокуса в предыдущий тип или в «Обзор» с тем
// раскрытием, до которого дошли; из «Обзора» — на уровень выше раскрытия.
export function navGraphBack() {
  if (!_state.navPath.length) return;
  set({ navPath: _state.navPath.slice(0, -1), focusTail: null });
}

// Клик по ступени цепочки крошек: -1 — корень «Обзора», иначе индекс шага в navPath —
// возврат ровно на неё, всё правее отбрасывается.
export function navGraphToStep(index: number) {
  if (index < -1) return;
  set({ navPath: index < 0 ? [] : _state.navPath.slice(0, index + 1), focusTail: null });
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
    toggleLanguage: toggleGraphLanguage,
    resetFilters: resetGraphFilters,
    setQuery: setGraphQuery,
    select: selectGraphNode,
    refocus: refocusGraphNode,
    setLegendOpen: setGraphLegendOpen,
    toggleHideTestNodes,
    toggleHideOrphanNodes,
    back: navGraphBack,
    toStep: navGraphToStep,
    toggleFocusDepth2: toggleGraphFocusDepth2,
    setFocusTail: setGraphFocusTail,
    expandGroup: expandOverviewGroup,
    drillOverviewTypes,
  }), []);
}
