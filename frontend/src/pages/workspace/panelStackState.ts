// Состояние панелей воркспейса: ДВЕ зоны (левая и правая рельсы), в каждой —
// раскладка колонками (как в Claude Code Desktop), режим, ширина колонки и
// спрятанный кнопкой «свернуть все» набор.
//
// Ключевой инвариант: панель лежит РОВНО В ОДНОЙ зоне. Открытие панели в зоне
// удаляет её из другой — это и есть перенос между рельсами. Поэтому состояние
// зон живёт в ОДНОМ сторе: два независимых стора не могли бы соблюсти инвариант.
//
// Раскладка ЯВНАЯ — PanelKey[][] (массив колонок): дефолт «по две на колонку»
// в порядке открытия, но drag-and-drop может дать любое распределение.
// Персист в localStorage — ключ cc_{ns}_zones; старые раздельные ключи
// (cc_{ns}_panels_* и cc_{ns}_left_panels_*) читаются как миграция и НЕ удаляются,
// чтобы откат кода вернул прежнее состояние.
// Стор параметризован неймспейсом (createPanelZones): воркспейс и раздел «Чаты»
// держат НЕЗАВИСИМЫЕ раскладки, не мешая друг другу.
import { useCallback, useSyncExternalStore } from 'react';
import { PANEL_HOME, isPanelKey, migrateLegacyKey, type PanelKey, type Zone } from './panelCatalog';

// Реестр панелей (ключи, мета, домашние зоны) — соседний panelCatalog.ts.
// Здесь только раскладка: что где лежит и какого размера.
export type { PanelKey, Zone } from './panelCatalog';

// Размеры самой рельсы (RAIL_W/RAIL_GAP) живут рядом с её компонентом —
// components/ui/PanelRail: к состоянию раскладки они отношения не имеют.
export const PANEL_MIN_H = 120;  // минимальная высота панельки, px (шапка 40 + контент)
export const COL_MIN = 280;      // клампы ширины ОДНОЙ колонки панелей
export const COL_MAX = 560;
export const COL_DEFAULT = 340;
export const COL_CAP = 2;        // дефолтная вместимость колонки при открытии новой панели

// Режим зоны: раскладка колонками (дефолт) или одна выбранная панель.
// Состояние ЕДИНОЕ (без отдельной памяти на режим): вход в solo схлопывает
// раскладку до одной панели, возврат в multi продолжает с текущего состояния.
export type PanelMode = 'multi' | 'solo';

export const ZONES: readonly Zone[] = ['left', 'right'];

// Тесты гоняются в среде node (без jsdom) — доступ к localStorage через guard,
// чтобы импорт модуля не падал вне браузера.
function lsGet(key: string): string | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage.getItem(key); } catch { return null; }
}
function lsSet(key: string, value: string) {
  try { if (typeof localStorage !== 'undefined') localStorage.setItem(key, value); } catch { /* квота/приватный режим — молча */ }
}

// ---------- раскладка ОДНОЙ зоны (чистые функции, покрыты panelStack.test.ts) ----------

// Санитайз раскладки: только известные ключи, без дублей, без пустых колонок.
// exclude — ключи, уже занятые другой зоной (инвариант «панель в одной зоне»).
export function sanitizeLayout(cols: unknown, exclude?: Set<PanelKey>): PanelKey[][] {
  if (!Array.isArray(cols)) return [];
  const seen = new Set<PanelKey>(exclude);
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

// Drag-and-drop панели НА панель: они МЕНЯЮТСЯ МЕСТАМИ (a встаёт в слот b и
// наоборот), в том числе между колонками. Сама раскладка при этом не меняется —
// число колонок и порядок слотов те же, переезжает только содержимое.
export function swapPanels(layout: PanelKey[][], a: PanelKey, b: PanelKey): PanelKey[][] {
  if (a === b) return layout;
  const flat = layout.flat();
  if (!flat.includes(a) || !flat.includes(b)) return layout;
  return layout.map(col => col.map(k => (k === a ? b : k === b ? a : k)));
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
      const key = migrateLegacyKey(k);
      if (key && typeof v === 'number' && Number.isFinite(v) && v > 0.05) out[key] = v;
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

// ---------- состояние ОБЕИХ зон (чистые функции) ----------

export interface ZoneState {
  layout: PanelKey[][];
  mode: PanelMode;
  width: number;
  // Раскладка, спрятанная кнопкой «Свернуть все» — повторный клик вернёт её как была
  stash: PanelKey[][];
}

export interface PanelZones {
  left: ZoneState;
  right: ZoneState;
  // Вес = высота СЛОТА панели. Общий для зон: панель уносит свой вес с собой.
  weights: Partial<Record<PanelKey, number>>;
}

export function emptyZone(): ZoneState {
  return { layout: [], mode: 'multi', width: COL_DEFAULT, stash: [] };
}

export function emptyZones(): PanelZones {
  return { left: emptyZone(), right: emptyZone(), weights: {} };
}

// В какой зоне лежит панель (null — закрыта)
export function zoneOf(zones: PanelZones, k: PanelKey): Zone | null {
  if (zones.left.layout.flat().includes(k)) return 'left';
  if (zones.right.layout.flat().includes(k)) return 'right';
  return null;
}

export function openKeysOf(zones: PanelZones): PanelKey[] {
  return [...zones.left.layout.flat(), ...zones.right.layout.flat()];
}

// Санитайз пары зон с соблюдением инварианта «панель в одной зоне».
// Приоритет у ПРАВОЙ: там исторически живут панели с настоящим контентом
// (слева files/tasks успели побывать dev-заглушками), поэтому дубль из
// сохранённой раскладки остаётся справа, а слева отбрасывается.
export function sanitizeZones(raw: unknown): PanelZones {
  const src = (raw && typeof raw === 'object' ? raw : {}) as Partial<Record<Zone, unknown>> & { weights?: unknown };
  const readZone = (v: unknown, exclude?: Set<PanelKey>): ZoneState => {
    const z = (v && typeof v === 'object' ? v : {}) as Partial<ZoneState>;
    return {
      layout: sanitizeLayout(z.layout, exclude),
      mode: z.mode === 'solo' ? 'solo' : 'multi',
      width: typeof z.width === 'number' ? parseWidth(String(z.width)) : COL_DEFAULT,
      stash: sanitizeLayout(z.stash),
    };
  };
  const right = readZone(src.right);
  const left = readZone(src.left, new Set(right.layout.flat()));
  return { left, right, weights: parseWeights(JSON.stringify(src.weights ?? {})) };
}

function withZone(zones: PanelZones, zone: Zone, next: (z: ZoneState) => ZoneState): PanelZones {
  return { ...zones, [zone]: next(zones[zone]) };
}

// Убрать панель из ЛЮБОЙ зоны (не трогая веса — панель может открыться снова)
export function closePanel(zones: PanelZones, k: PanelKey): PanelZones {
  const zone = zoneOf(zones, k);
  if (!zone) return zones;
  return withZone(zones, zone, z => ({ ...z, layout: removePanel(z.layout, k) }));
}

// Открыть панель В ЗОНЕ. Если она открыта в другой — это перенос: из прежней
// зоны панель уходит. В solo-режиме целевой зоны раскладка схлопывается до неё.
export function openPanelIn(zones: PanelZones, zone: Zone, k: PanelKey): PanelZones {
  const base = closePanel(zones, k);
  return withZone(base, zone, z => ({
    ...z,
    layout: z.mode === 'solo' ? [[k]] : addPanel(z.layout, k),
  }));
}

// Клик по иконке рельсы: панель открыта В ЭТОЙ зоне — закрыть, иначе открыть
// здесь (в том числе забрав из соседней зоны).
export function togglePanelIn(zones: PanelZones, zone: Zone, k: PanelKey): PanelZones {
  return zoneOf(zones, k) === zone ? closePanel(zones, k) : openPanelIn(zones, zone, k);
}

// Дроп панели НА панель — они меняются местами, в том числе через границу зон:
// каждая встаёт в слот другой, раскладки обеих зон по форме не меняются.
export function swapAcross(zones: PanelZones, a: PanelKey, b: PanelKey): PanelZones {
  if (a === b) return zones;
  const za = zoneOf(zones, a);
  const zb = zoneOf(zones, b);
  if (!za || !zb) return zones;
  if (za === zb) return withZone(zones, za, z => ({ ...z, layout: swapPanels(z.layout, a, b) }));
  const swapIn = (layout: PanelKey[][], from: PanelKey, to: PanelKey) =>
    layout.map(col => col.map(k => (k === from ? to : k)));
  return {
    ...zones,
    [za]: { ...zones[za], layout: swapIn(zones[za].layout, a, b) },
    [zb]: { ...zones[zb], layout: swapIn(zones[zb].layout, b, a) },
  };
}

// Дроп в горизонтальный плейсхолдер зоны. Внутри своей зоны — обычная
// перестановка; из другой зоны панель сначала уходит из неё, а затем встаёт в
// указанную колонку. Пустая целевая зона принимает панель первой колонкой.
export function moveAcrossAt(zones: PanelZones, k: PanelKey, zone: Zone, colIdx: number, rowIdx: number): PanelZones {
  const src = zoneOf(zones, k);
  if (!src) return zones;
  if (src === zone) return withZone(zones, zone, z => ({ ...z, layout: movePanelAt(z.layout, k, colIdx, rowIdx) }));
  const base = closePanel(zones, k);
  return withZone(base, zone, z => {
    const cols = z.layout.map(c => [...c]);
    if (cols.length === 0) return { ...z, layout: [[k]] };
    if (colIdx < 0 || colIdx >= cols.length) return z;
    cols[colIdx].splice(Math.max(0, Math.min(cols[colIdx].length, rowIdx)), 0, k);
    return { ...z, layout: cols };
  });
}

// Дроп в разделитель колонок зоны: панель выносится в НОВУЮ колонку, в том
// числе перелетая из соседней зоны.
export function moveAcrossToNewColumn(zones: PanelZones, k: PanelKey, zone: Zone, insertIdx: number): PanelZones {
  const src = zoneOf(zones, k);
  if (!src) return zones;
  if (src === zone) return withZone(zones, zone, z => ({ ...z, layout: movePanelToNewColumn(z.layout, k, insertIdx) }));
  const base = closePanel(zones, k);
  return withZone(base, zone, z => {
    const cols = z.layout.map(c => [...c]);
    cols.splice(Math.max(0, Math.min(cols.length, insertIdx)), 0, [k]);
    return { ...z, layout: cols };
  });
}

// Зона «свёрнута»: своих открытых панелей нет, но спрятанный набор есть
export function isZoneCollapsed(z: ZoneState): boolean {
  return z.layout.flat().length === 0 && z.stash.flat().length > 0;
}

// Показать панель по внешнему запросу (git-бар над композером просит «Изменения»).
// Открытую НЕ трогаем и не перетаскиваем через полэкрана: вызывающий вместо этого
// просит её мигнуть. Закрытая открывается в своей домашней зоне.
export function revealPanel(zones: PanelZones, k: PanelKey): { zones: PanelZones; wasOpen: boolean } {
  if (zoneOf(zones, k)) return { zones, wasOpen: true };
  return { zones: openPanelIn(zones, PANEL_HOME[k], k), wasOpen: false };
}

// ---------- миграция со старых раздельных ключей ----------

// Раньше зоны были двумя независимыми сторами: cc_{ns}_panels_* (правая) и
// cc_{ns}_left_panels_* (левая). Читаем обе раскладки, переводим упразднённые
// ключи (personas → team, tools отбрасывается) и снимаем дубли в пользу правой.
export function migrateZones(read: (key: string) => string | null, ns: string, legacyOpenKey?: string): PanelZones | null {
  const rightRaw = read(`cc_${ns}_panels_layout`);
  const leftRaw = read(`cc_${ns}_left_panels_layout`);
  const legacyOpen = legacyOpenKey ? read(legacyOpenKey) : null;
  if (!rightRaw && !leftRaw && !legacyOpen) return null;

  const readLayout = (raw: string | null, legacy: string | null): PanelKey[][] => {
    const src = raw ? (() => { try { return JSON.parse(raw); } catch { return null; } })() : null;
    if (Array.isArray(src)) {
      // Перевод ключей делаем ДО санитайза: personas/tools иначе отсеются как чужие
      return sanitizeLayout(src.map(col => (Array.isArray(col) ? col.map(migrateLegacyKey).filter(Boolean) : col)));
    }
    return parseLayout(null, legacy);
  };

  const right = readLayout(rightRaw, legacyOpen);
  const left = sanitizeLayout(readLayout(leftRaw, null), new Set(right.flat()));

  const zone = (layout: PanelKey[][], prefix: string): ZoneState => ({
    layout,
    mode: read(`${prefix}_mode`) === 'solo' ? 'solo' : 'multi',
    width: parseWidth(read(`${prefix}_width`)),
    stash: sanitizeLayout((() => { try { return JSON.parse(read(`${prefix}_stash`) ?? '[]'); } catch { return []; } })()),
  });

  return {
    right: zone(right, `cc_${ns}_panels`),
    left: zone(left, `cc_${ns}_left_panels`),
    // Веса были раздельными; при совпадении ключа берём правый (см. sanitizeZones)
    weights: {
      ...parseWeights(read(`cc_${ns}_left_panels_weights`)),
      ...parseWeights(read(`cc_${ns}_panels_weights`)),
    },
  };
}

// ---------- API стора ----------

export interface PanelZonesApi {
  zones: PanelZones;
  // Клик по иконке рельсы зоны
  toggle: (zone: Zone, k: PanelKey) => void;
  // Крестик в шапке панели — закрывает, где бы она ни лежала
  close: (k: PanelKey) => void;
  // Дроп панели на панель (в том числе через границу зон)
  swapWith: (a: PanelKey, b: PanelKey) => void;
  // Дроп в плейсхолдер строки / в разделитель колонок целевой зоны
  moveAt: (k: PanelKey, zone: Zone, colIdx: number, rowIdx: number) => void;
  moveToNewColumn: (k: PanelKey, zone: Zone, insertIdx: number) => void;
  setMode: (zone: Zone, m: PanelMode) => void;
  setWidth: (zone: Zone, n: number) => void;
  toggleCollapsed: (zone: Zone) => void;
  setWeights: (next: Partial<Record<PanelKey, number>>) => void;
  // Показать панель по внешнему запросу (git-бар над композером). Возвращает
  // true, если она УЖЕ была открыта — тогда вызывающий просит её мигнуть.
  reveal: (k: PanelKey) => boolean;
}

export type PanelZonesStore = { use: () => PanelZonesApi };

// Фабрика независимого инстанса: своё состояние в замыкании + свой ключ
// localStorage cc_{ns}_zones. Инстансы создаются на уровне модуля (ниже),
// поэтому чтение localStorage происходит при импорте — как было до зон.
function createPanelZones(ns: string, opts?: { legacyOpenKey?: string; defaultZones?: Partial<Record<Zone, PanelKey[][]>> }): PanelZonesStore {
  const KEY = `cc_${ns}_zones`;

  // Приоритет: новое состояние → миграция со старых раздельных ключей → дефолт.
  // defaultZones нужен там, где базовая панель должна быть открыта при первом
  // запуске (напр. chats слева). После закрытия пользователем состояние
  // сохранится, и при следующем визите панель останется закрытой.
  // migrated — состояние переехало со старых ключей и ещё не записано под новым
  let migrated = false;
  let _zones: PanelZones = (() => {
    const raw = lsGet(KEY);
    if (raw) {
      try { return sanitizeZones(JSON.parse(raw)); } catch { /* мусор → миграция/дефолт */ }
    }
    const fromLegacy = migrateZones(lsGet, ns, opts?.legacyOpenKey);
    if (fromLegacy) { migrated = true; return fromLegacy; }
    return sanitizeZones({
      left: { layout: opts?.defaultZones?.left ?? [] },
      right: { layout: opts?.defaultZones?.right ?? [] },
    });
  })();

  const listeners = new Set<() => void>();
  function emit() { listeners.forEach(l => l()); }
  function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }

  function persist() { lsSet(KEY, JSON.stringify(_zones)); }

  // Переезд фиксируем сразу, не дожидаясь первого действия пользователя: иначе
  // состояние живёт только в памяти, и любая правка старых ключей другой вкладкой
  // (или откатом кода) молча разошлась бы с тем, что человек видит на экране.
  if (migrated) persist();

  // Единая точка записи: нормализует веса по всем открытым панелям обеих зон
  function commit(next: PanelZones) {
    _zones = { ...next, weights: normalizeWeights(openKeysOf(next), next.weights) };
    persist();
    emit();
  }

  function usePanelZones(): PanelZonesApi {
    const zones = useSyncExternalStore(subscribe, () => _zones);

    const toggle = useCallback((zone: Zone, k: PanelKey) => {
      // Вес новой панели заводим до раскладки, иначе normalizeWeights даст ей 1
      // уже после деления и соседи дрогнут
      if (_zones.weights[k] == null) {
        _zones = { ..._zones, weights: { ..._zones.weights, [k]: 1 } };
      }
      commit(togglePanelIn(_zones, zone, k));
    }, []);

    const close = useCallback((k: PanelKey) => { commit(closePanel(_zones, k)); }, []);

    const swapWith = useCallback((a: PanelKey, b: PanelKey) => {
      // Вес — высота СЛОТА, а не панели: вместе с местами меняем и веса,
      // иначе панель утащила бы свою высоту и раскладка «прыгнула» бы
      const wa = _zones.weights[a] ?? 1;
      const wb = _zones.weights[b] ?? 1;
      const swapped = swapAcross(_zones, a, b);
      commit({ ...swapped, weights: { ...swapped.weights, [a]: wb, [b]: wa } });
    }, []);

    const moveAt = useCallback((k: PanelKey, zone: Zone, colIdx: number, rowIdx: number) => {
      commit(moveAcrossAt(_zones, k, zone, colIdx, rowIdx));
    }, []);

    const moveToNewColumn = useCallback((k: PanelKey, zone: Zone, insertIdx: number) => {
      commit(moveAcrossToNewColumn(_zones, k, zone, insertIdx));
    }, []);

    const setMode = useCallback((zone: Zone, m: PanelMode) => {
      if (_zones[zone].mode === m) return;
      // Вход в solo СХЛОПЫВАЕТ раскладку зоны до одной панели (первой открытой) —
      // остальные реально закрываются; возврат в multi продолжает с текущего
      // состояния, старый набор не восстанавливается.
      const first = _zones[zone].layout.flat()[0];
      commit(withZone(_zones, zone, z => ({
        ...z,
        mode: m,
        layout: m === 'solo' ? (first ? [[first]] : []) : z.layout,
      })));
    }, []);

    const setWidth = useCallback((zone: Zone, n: number) => {
      commit(withZone(_zones, zone, z => ({ ...z, width: Math.min(COL_MAX, Math.max(COL_MIN, Math.round(n))) })));
    }, []);

    // Свернуть все панели зоны (набор прячется в stash) / вернуть его как был
    const toggleCollapsed = useCallback((zone: Zone) => {
      commit(withZone(_zones, zone, z => {
        if (z.layout.flat().length > 0) return { ...z, stash: z.layout, layout: [] };
        if (z.stash.flat().length > 0) return { ...z, layout: z.stash, stash: [] };
        return z;
      }));
    }, []);

    const setWeights = useCallback((next: Partial<Record<PanelKey, number>>) => {
      // Ресайз высот пишем БЕЗ нормализации: она бы тут же вернула перетянутую
      // границу к равным долям
      _zones = { ..._zones, weights: { ..._zones.weights, ...next } };
      persist();
      emit();
    }, []);

    const reveal = useCallback((k: PanelKey) => {
      const { zones: next, wasOpen } = revealPanel(_zones, k);
      if (!wasOpen) commit(next);
      return wasOpen;
    }, []);

    return { zones, toggle, close, swapWith, moveAt, moveToNewColumn, setMode, setWidth, toggleCollapsed, setWeights, reveal };
  }

  return { use: usePanelZones };
}

// Инстанс воркспейса — ключ cc_ws_zones, миграция со старых cc_ws_panels_* /
// cc_ws_left_panels_* и совсем старого плоского списка cc_ws_panels_open.
// Слева при первом запуске открыты «Чаты».
export const wsPanels = createPanelZones('ws', {
  legacyOpenKey: 'cc_ws_panels_open',
  defaultZones: { left: [['chats']] },
});

// Инстанс раздела «Чаты» — независимая раскладка (cc_chat_zones).
export const chatPanels = createPanelZones('chat', {
  defaultZones: { left: [['chats']] },
});
