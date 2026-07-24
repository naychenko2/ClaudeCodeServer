// Состояние правых панелей нового интерфейса проекта (workspace-cc-panels):
// раскладка по колонкам (как в Claude Code Desktop), веса высот и ширина колонки.
// Раскладка ЯВНАЯ — PanelKey[][] (массив колонок): дефолт «по две на колонку»
// в порядке открытия, но drag-and-drop может дать любое распределение
// (например одна панель в первой колонке и две во второй).
// Персист в localStorage — свои ключи cc_{ns}_panels_*, старые cc_artifacts_* не трогаем.
// Стор параметризован неймспейсом (createPanelStack): воркспейс и раздел «Чаты»
// держат НЕЗАВИСИМЫЕ раскладки, не мешая друг другу.
import { useCallback, useSyncExternalStore } from 'react';

// Набор рабочих панелей рельсы (порядок = порядок иконок). Артефактные категории
// сессионная группа (plan/agents/context — План, Агенты, Персона) собирается из
// артефактов сессии; остальное — инструменты проекта, как в десктопном Claude Code.
// Ключи agents/context совпадают с meta.tsx ради panelBadge.
export const PANEL_KEYS = ['plan', 'agents', 'context', 'files', 'changes', 'tasks', 'team', 'terminal', 'preview'] as const;
export type PanelKey = typeof PANEL_KEYS[number];

export const PANEL_MIN_H = 120;  // минимальная высота панельки, px (шапка 40 + контент)
export const RAIL_W = 44;        // ширина рельсы иконок
export const COL_MIN = 280;      // клампы ширины ОДНОЙ колонки панелей
export const COL_MAX = 560;
export const COL_DEFAULT = 340;
export const COL_CAP = 2;        // дефолтная вместимость колонки при открытии новой панели

// Режим зоны панелей: раскладка колонками (дефолт) или одна выбранная панель.
// Состояние ЕДИНОЕ (без отдельной памяти на режим): вход в solo схлопывает
// раскладку до одной панели, возврат в multi продолжает с текущего состояния —
// старый набор «множественных вкладок» не воскресает.
export type PanelMode = 'multi' | 'solo';

// Тесты гоняются в среде node (без jsdom) — доступ к localStorage через guard,
// чтобы импорт модуля не падал вне браузера.
function lsGet(key: string): string | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage.getItem(key); } catch { return null; }
}
function lsSet(key: string, value: string) {
  try { if (typeof localStorage !== 'undefined') localStorage.setItem(key, value); } catch { /* квота/приватный режим — молча */ }
}

// ---------- чистые функции (покрыты тестом panelStack.test.ts) ----------

function isPanelKey(v: unknown): v is PanelKey {
  return typeof v === 'string' && (PANEL_KEYS as readonly string[]).includes(v);
}

// Санитайз раскладки: только известные ключи, без дублей, без пустых колонок
export function sanitizeLayout(cols: unknown): PanelKey[][] {
  if (!Array.isArray(cols)) return [];
  const seen = new Set<PanelKey>();
  const out: PanelKey[][] = [];
  for (const col of cols) {
    if (!Array.isArray(col)) continue;
    const clean: PanelKey[] = [];
    for (const v of col) if (isPanelKey(v) && !seen.has(v)) { seen.add(v); clean.push(v); }
    if (clean.length) out.push(clean);
  }
  return out;
}

// Загрузка раскладки: новый ключ layout, иначе миграция со старого плоского
// списка (порядок открытия → «по две на колонку»), иначе пусто.
export function parseLayout(rawLayout: string | null, rawLegacyOpen: string | null): PanelKey[][] {
  if (rawLayout) {
    try { return sanitizeLayout(JSON.parse(rawLayout)); } catch { /* мусор → миграция/дефолт */ }
  }
  if (rawLegacyOpen) {
    try {
      const arr = JSON.parse(rawLegacyOpen);
      if (Array.isArray(arr)) {
        const flat: PanelKey[] = [];
        for (const v of arr) if (isPanelKey(v) && !flat.includes(v)) flat.push(v);
        const cols: PanelKey[][] = [];
        for (let i = 0; i < flat.length; i += COL_CAP) cols.push(flat.slice(i, i + COL_CAP));
        return cols;
      }
    } catch { /* мусор → дефолт */ }
  }
  return [];
}

// Открытие панели: в последнюю колонку, пока в ней меньше COL_CAP, иначе новая
// колонка справа (1-я во всю высоту, 2-я вниз, 3-я вправо, 4-я вниз третьей…)
export function addPanel(layout: PanelKey[][], k: PanelKey): PanelKey[][] {
  if (layout.flat().includes(k)) return layout;
  const out = layout.map(c => [...c]);
  const last = out[out.length - 1];
  if (last && last.length < COL_CAP) last.push(k);
  else out.push([k]);
  return out;
}

// Закрытие панели: удалить, пустые колонки схлопнуть
export function removePanel(layout: PanelKey[][], k: PanelKey): PanelKey[][] {
  return layout.map(c => c.filter(x => x !== k)).filter(c => c.length > 0);
}

// Drag-and-drop: перенести from в колонку панели to, ВСТАВИВ ПЕРЕД ней.
// Так можно получить любое распределение (например 1-я колонка с одной панелью,
// 2-я с двумя): вместимость COL_CAP при ручном переносе не ограничивает.
export function movePanel(layout: PanelKey[][], from: PanelKey, to: PanelKey): PanelKey[][] {
  if (from === to) return layout;
  const flat = layout.flat();
  if (!flat.includes(from) || !flat.includes(to)) return layout;
  const without = layout.map(c => c.filter(x => x !== from));
  const out = without.map(c => [...c]);
  for (const col of out) {
    const ti = col.indexOf(to);
    if (ti >= 0) { col.splice(ti, 0, from); break; }
  }
  return out.filter(c => c.length > 0);
}

// Drag-and-drop в разделитель: вынести from в НОВУЮ колонку на позицию insertIdx
// (индекс разделителя в текущей раскладке: 0 — левее первой колонки,
// length — правее последней). Индекс считается ДО схлопывания опустевшей
// колонки-источника, поэтому пустые колонки фильтруются в самом конце.
export function movePanelToNewColumn(layout: PanelKey[][], from: PanelKey, insertIdx: number): PanelKey[][] {
  if (!layout.flat().includes(from)) return layout;
  const without = layout.map(c => c.filter(x => x !== from));
  const idx = Math.max(0, Math.min(without.length, insertIdx));
  without.splice(idx, 0, [from]);
  return without.filter(c => c.length > 0);
}

// Drag-and-drop в горизонтальный плейсхолдер: вставить from в колонку colIdx
// на позицию rowIdx (0 — над первой панелью, length — под последней).
// rowIdx приходит от рендера ДО удаления from — если from стоит в той же
// колонке выше цели, после удаления позиция сдвигается на 1.
export function movePanelAt(layout: PanelKey[][], from: PanelKey, colIdx: number, rowIdx: number): PanelKey[][] {
  if (!layout.flat().includes(from)) return layout;
  if (colIdx < 0 || colIdx >= layout.length) return layout;
  const srcRow = layout[colIdx].indexOf(from);
  const without = layout.map(c => c.filter(x => x !== from));
  const col = without[colIdx];
  const shift = srcRow >= 0 && srcRow < rowIdx ? 1 : 0;
  const idx = Math.max(0, Math.min(col.length, rowIdx - shift));
  col.splice(idx, 0, from);
  return without.filter(c => c.length > 0);
}

export function parseWeights(raw: string | null): Partial<Record<PanelKey, number>> {
  if (!raw) return {};
  try {
    const obj = JSON.parse(raw);
    if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return {};
    const out: Partial<Record<PanelKey, number>> = {};
    for (const [k, v] of Object.entries(obj)) {
      if (isPanelKey(k) && typeof v === 'number' && Number.isFinite(v) && v > 0.05) out[k] = v;
    }
    return out;
  } catch { return {}; }
}

export function parseWidth(raw: string | null): number {
  // Number(null) и Number('') дают 0, а не NaN — отсутствие значения проверяем явно
  if (raw == null || raw.trim() === '') return COL_DEFAULT;
  const n = Number(raw);
  if (!Number.isFinite(n)) return COL_DEFAULT;
  return Math.min(COL_MAX, Math.max(COL_MIN, Math.round(n)));
}

// Нормализация весов открытых панелей: сумма = числу открытых (защита от дрейфа
// к 0/∞ после многих drag'ов). Панели без веса получают 1.
export function normalizeWeights(open: PanelKey[], weights: Partial<Record<PanelKey, number>>): Partial<Record<PanelKey, number>> {
  if (open.length === 0) return { ...weights };
  const cur = open.map(k => weights[k] ?? 1);
  const sum = cur.reduce((a, b) => a + b, 0);
  const factor = sum > 0 ? open.length / sum : 1;
  const out = { ...weights };
  open.forEach((k, i) => { out[k] = cur[i] * factor; });
  return out;
}

export interface PanelStack {
  layout: PanelKey[][];
  weights: Partial<Record<PanelKey, number>>;
  width: number;
  // Режим зоны: 'multi' — раскладка колонками (дефолт), 'solo' — одна выбранная панель
  mode: PanelMode;
  toggle: (k: PanelKey) => void;
  close: (k: PanelKey) => void;
  // Кнопка внизу рельсы: свернуть все открытые панели / вернуть свёрнутый набор как был
  collapsed: boolean;
  toggleCollapsed: () => void;
  setWeights: (next: Partial<Record<PanelKey, number>>) => void;
  setWidth: (n: number) => void;
  // Drag-and-drop: перенести панель from в колонку панели to (вставить перед ней)
  moveTo: (from: PanelKey, to: PanelKey) => void;
  // Drag-and-drop в разделитель: вынести панель в новую колонку на позицию insertIdx
  moveToNewColumn: (from: PanelKey, insertIdx: number) => void;
  // Drag-and-drop в горизонтальный плейсхолдер: вставить в колонку colIdx на позицию rowIdx
  moveAt: (from: PanelKey, colIdx: number, rowIdx: number) => void;
  setMode: (m: PanelMode) => void;
}

// ---------- модульный стор-инстанс (паттерн — как lib/sidebarWidth.ts) ----------

// Фабрика независимого инстанса: своё состояние в замыкании + свои ключи
// localStorage `cc_{ns}_panels_*`. Инстансы создаются на уровне модуля (ниже),
// поэтому семантика чтения localStorage при импорте — та же, что была у синглтона.
function createPanelStack(ns: string, opts?: { legacyOpenKey?: string }) {
  const KEY_LAYOUT = `cc_${ns}_panels_layout`;
  const KEY_WEIGHTS = `cc_${ns}_panels_weights`;
  const KEY_WIDTH = `cc_${ns}_panels_width`;
  const KEY_MODE = `cc_${ns}_panels_mode`;     // 'multi' (раскладка, дефолт) | 'solo' (одна панель)
  const KEY_STASH = `cc_${ns}_panels_stash`;   // раскладка, спрятанная кнопкой «Свернуть все»
  // Старый плоский список — мигрируется в layout (только у воркспейсного инстанса)
  const legacyOpen = opts?.legacyOpenKey ? lsGet(opts.legacyOpenKey) : null;

  let _layout: PanelKey[][] = parseLayout(lsGet(KEY_LAYOUT), legacyOpen);
  let _weights: Partial<Record<PanelKey, number>> = parseWeights(lsGet(KEY_WEIGHTS));
  let _width = parseWidth(lsGet(KEY_WIDTH));
  let _mode: PanelMode = lsGet(KEY_MODE) === 'solo' ? 'solo' : 'multi';
  // Спрятанная кнопкой «Свернуть все» раскладка — повторный клик вернёт её как была
  let _stash: PanelKey[][] = (() => {
    try { return sanitizeLayout(JSON.parse(lsGet(KEY_STASH) ?? '[]')); } catch { return []; }
  })();
  const listeners = new Set<() => void>();
  function emit() { listeners.forEach(l => l()); }
  function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }

  function persist() {
    lsSet(KEY_LAYOUT, JSON.stringify(_layout));
    lsSet(KEY_WEIGHTS, JSON.stringify(_weights));
    lsSet(KEY_WIDTH, String(_width));
    lsSet(KEY_MODE, _mode);
    lsSet(KEY_STASH, JSON.stringify(_stash));
  }

  function setLayout(next: PanelKey[][]) {
    _layout = sanitizeLayout(next);
    _weights = normalizeWeights(_layout.flat(), _weights);
    persist();
    emit();
  }

  function usePanelStack(): PanelStack {
    const layout = useSyncExternalStore(subscribe, () => _layout);
    const weights = useSyncExternalStore(subscribe, () => _weights);
    const width = useSyncExternalStore(subscribe, () => _width);
    const mode = useSyncExternalStore(subscribe, () => _mode);

    const toggle = useCallback((k: PanelKey) => {
      const isOpen = _layout.flat().includes(k);
      if (_mode === 'solo') {
        // Solo: иконки работают как радио — открытая панель ЗАМЕНЯЕТСЯ выбранной,
        // повторный клик по активной скрывает её (состояние общее с multi)
        setLayout(isOpen ? [] : [[k]]);
        return;
      }
      if (isOpen) {
        setLayout(removePanel(_layout, k));
      } else {
        const cur = _weights[k];
        if (cur == null) _weights = { ..._weights, [k]: 1 };
        setLayout(addPanel(_layout, k));
      }
    }, []);

    const close = useCallback((k: PanelKey) => {
      setLayout(removePanel(_layout, k));
    }, []);

    const collapsed = useSyncExternalStore(subscribe, () => _layout.flat().length === 0 && _stash.flat().length > 0);

    // Свернуть все панели (набор прячется в stash) / вернуть спрятанный набор как был
    const toggleCollapsed = useCallback(() => {
      if (_layout.flat().length > 0) {
        _stash = _layout;
        setLayout([]);
      } else if (_stash.flat().length > 0) {
        const restore = _stash;
        _stash = [];
        setLayout(restore);
      }
    }, []);

    const setWeights = useCallback((next: Partial<Record<PanelKey, number>>) => {
      _weights = { ..._weights, ...next };
      persist();
      emit();
    }, []);

    const setWidth = useCallback((n: number) => {
      _width = Math.min(COL_MAX, Math.max(COL_MIN, Math.round(n)));
      persist();
      emit();
    }, []);

    const moveTo = useCallback((from: PanelKey, to: PanelKey) => {
      setLayout(movePanel(_layout, from, to));
    }, []);

    const moveToNewColumn = useCallback((from: PanelKey, insertIdx: number) => {
      setLayout(movePanelToNewColumn(_layout, from, insertIdx));
    }, []);

    const moveAt = useCallback((from: PanelKey, colIdx: number, rowIdx: number) => {
      setLayout(movePanelAt(_layout, from, colIdx, rowIdx));
    }, []);

    const setMode = useCallback((m: PanelMode) => {
      if (_mode === m) return;
      _mode = m;
      // Вход в solo СХЛОПЫВАЕТ раскладку до одной панели (первой открытой) —
      // остальные реально закрываются; возврат в multi продолжает с текущего
      // состояния, старый набор не восстанавливается.
      if (m === 'solo') {
        const first = _layout.flat()[0];
        setLayout(first ? [[first]] : []);
        return;
      }
      persist();
      emit();
    }, []);

    return { layout, weights, width, mode, toggle, close, collapsed, toggleCollapsed, setWeights, setWidth, moveTo, moveToNewColumn, moveAt, setMode };
  }

  return { use: usePanelStack };
}

// Инстанс воркспейса — ключи cc_ws_panels_* и legacy-миграция, как было до фабрики.
export const wsPanelStack = createPanelStack('ws', { legacyOpenKey: 'cc_ws_panels_open' });
// Инстанс раздела «Чаты» — независимая раскладка сессионной рельсы (cc_chat_panels_*).
export const chatPanelStack = createPanelStack('chat');

// Совместимость: прежний хук = воркспейсный инстанс (RightPanelStack, ProjectGitBar).
export const usePanelStack = wsPanelStack.use;
