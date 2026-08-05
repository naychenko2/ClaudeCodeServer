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
import { PANEL_HOME, RAIL_GROUPS, isPanelKey, migrateLegacyKey, type PanelKey, type Zone } from './panelCatalog';

// Реестр панелей (ключи, мета, домашние зоны) — соседний panelCatalog.ts.
// Здесь только раскладка: что где лежит и какого размера.
export type { PanelKey, Zone } from './panelCatalog';

// Размеры самой рельсы (RAIL_W/RAIL_GAP) живут рядом с её компонентом —
// components/ui/PanelRail: к состоянию раскладки они отношения не имеют.
export const PANEL_MIN_H = 120;  // минимальная высота панельки, px (шапка 40 + контент)
export const COL_MIN = 280;      // клампы ширины ОДНОЙ колонки панелей
export const COL_MAX = 560;
export const COL_DEFAULT = 340;
// Вместимость колонки при открытии новой панели — СКОЛЬКО ПАНЕЛЕЙ ТУДА ВЛЕЗАЕТ.
// Настоящее число считает зона по живой высоте колонки (сколько панелей получат
// хотя бы PANEL_MIN_H) и передаёт сюда; COL_CAP — только фолбэк, когда высоты
// ещё нет (первый рендер, вызов не из раскладки). Раньше это была ЗАШИТАЯ «пара
// на колонку», и третья панель уезжала колонкой вбок при свободном экране.
export const COL_CAP = 2;

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

// Открытие панели: в колонку У РЕЛЬСЫ, пока в неё влезает (cap — вместимость,
// см. COL_CAP), иначе новая колонка тоже РОЖДАЕТСЯ У РЕЛЬСЫ, а прежние отъезжают
// к центру. Рельса у правой зоны справа (её колонка — последняя в массиве, новая
// пушится в конец), у левой слева (её колонка — первая, новая встаёт в начало).
// Раньше сторону не учитывали и новая колонка всегда лезла в конец — у левой зоны
// это центр, а не край.
export function addPanel(layout: PanelKey[][], k: PanelKey, side: Zone = 'right', cap = COL_CAP, railSeq?: readonly PanelKey[]): PanelKey[][] {
  if (layout.flat().includes(k)) return layout;
  // Порядок кнопок известен — панель встаёт на СВОЁ место в нём (см. placeByRail).
  // Без него (внешний показ панели из гит-бара, дефолты, миграции) остаётся
  // прежнее правило «в конец колонки у рельсы».
  if (railSeq && railSeq.length > 0) return placeByRail(layout, k, side, cap, railSeq).layout;
  const out = layout.map(c => [...c]);
  const railIdx = side === 'left' ? 0 : out.length - 1;
  const railCol = out[railIdx];
  if (railCol && railCol.length < cap) railCol.push(k);
  else if (side === 'left') out.unshift([k]);
  else out.push([k]);
  return out;
}

// Ранг кнопки в вертикальном порядке рельсы (railSeq — плоский список кнопок
// столбца сверху вниз). Кнопки в списке нет — ранг бесконечный: такую панель
// считаем самой нижней. Так себя ведут ключи, которых на этом экране в рельсе
// не показывают; спорить с ними за место в середине колонки не за что.
function railRank(railSeq: readonly PanelKey[], k: PanelKey): number {
  const i = railSeq.indexOf(k);
  return i < 0 ? Number.MAX_SAFE_INTEGER : i;
}

export interface RailPlacement {
  // Раскладка ПОСЛЕ вставки — ею пользуется addPanel
  layout: PanelKey[][];
  // Где панель оказалась. ci — индекс колонки в ИСХОДНОЙ раскладке (призрак
  // рисуется по ней, до вставки); при newColumn он равен месту вставки новой
  // колонки, то есть тоже исходной координате.
  ci: number;
  ri: number;
  // Панель завела НОВУЮ колонку (место ci — куда та встанет)
  newColumn: boolean;
}

// Размещение панели ПО ПОРЯДКУ КНОПОК РЕЛЬСЫ — единое правило и для вставки
// (addPanel), и для прогноза под курсором (призрак места). Считать их порознь
// нельзя: обещание рельсы и итог клика обязаны совпадать.
//
// Правило: панель идёт в колонку у рельсы на место, где столько же панелей стоит
// ВЫШЕ неё в столбце кнопок. Позиция считается пересчётом, а не поиском первой
// «панели ниже», потому что колонка не обязана быть отсортирована по рельсе:
// раскладку правят перетаскиванием, переносом из соседней зоны и миграцией со
// старого формата — там порядок любой, и правило обязано давать однозначный
// результат на ЛЮБОЙ колонке.
//
// Переполнение: колонка переросла вместимость — «хвост по порядку» (панель с
// самым нижним рангом, а не нижняя по месту: колонка может быть неотсортирована)
// уезжает в соседнюю колонку К ЦЕНТРУ и встаёт там по тому же правилу. Дальше
// каскадом: соседняя тоже могла переполниться. Некуда — заводится новая колонка
// у центра. Так колонки читаются как продолжение одного упорядоченного потока:
// у рельсы начало порядка, к центру — продолжение.
//
// Вместимость на весь каскад ОДНА (её считает зона по колонке у рельсы) —
// осознанное упрощение: настоящая вместимость соседней колонки зависит от высот
// её панелей, а живого DOM у чистой функции нет.
export function placeByRail(
  layout: PanelKey[][],
  k: PanelKey,
  side: Zone,
  cap: number,
  railSeq: readonly PanelKey[],
): RailPlacement {
  const capN = Math.max(1, cap);
  const rank = (x: PanelKey) => railRank(railSeq, x);
  // origin — индекс колонки в ИСХОДНОЙ раскладке (null у заведённой здесь же):
  // перелив может добавить колонку и сдвинуть индексы, а призраку нужны
  // координаты той раскладки, которую человек видит под курсором.
  const cols: { origin: number | null; keys: PanelKey[] }[] =
    layout.map((keys, i) => ({ origin: i, keys: [...keys] }));

  if (cols.length === 0) return { layout: [[k]], ci: 0, ri: 0, newColumn: true };

  const railIdx = side === 'left' ? 0 : cols.length - 1;
  const at = cols[railIdx].keys.filter(x => rank(x) < rank(k)).length;
  cols[railIdx].keys.splice(at, 0, k);

  // Направление перелива — от рельсы К ЦЕНТРУ: у левой зоны рельса слева
  // (центр — в сторону роста индексов), у правой наоборот.
  const dir = side === 'left' ? 1 : -1;
  let newAt: number | null = null;
  let i = railIdx;
  while (cols[i].keys.length > capN) {
    const col = cols[i].keys;
    let tail = 0;
    for (let j = 1; j < col.length; j++) if (rank(col[j]) > rank(col[tail])) tail = j;
    const moved = col.splice(tail, 1)[0];
    const next = i + dir;
    if (next < 0 || next >= cols.length) {
      // Колонки у центра больше нет — заводим. Новая с одной панелью вместимость
      // не превысит, поэтому каскад на ней и заканчивается.
      newAt = dir > 0 ? layout.length : 0;
      cols.splice(dir > 0 ? cols.length : 0, 0, { origin: null, keys: [moved] });
      break;
    }
    const nc = cols[next].keys;
    nc.splice(nc.filter(x => rank(x) < rank(moved)).length, 0, moved);
    i = next;
  }

  const home = cols.find(c => c.keys.includes(k))!;
  return {
    layout: cols.map(c => c.keys),
    ci: home.origin ?? newAt ?? 0,
    ri: home.keys.indexOf(k),
    newColumn: home.origin == null,
  };
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

// Доли ширины колонок из сохранённого состояния: массив положительных конечных
// чисел, прочее отбрасываем (тогда колонки равны). Длину под layout приведёт
// normalizeColFlex при первом коммите.
export function parseColFlex(raw: unknown): number[] {
  if (!Array.isArray(raw)) return [];
  return raw.map(v => (typeof v === 'number' && Number.isFinite(v) && v > 0.05 ? v : 1));
}

// Привести доли к числу колонок layout: лишние отрезать, недостающим дать 1,
// сумму подравнять к числу колонок (защита от дрейфа после многих ресайзов, как у
// весов высот). Пустой/единичный вход у одной колонки не хранится — там делить
// нечего.
export function normalizeColFlex(colCount: number, flex: number[]): number[] {
  if (colCount <= 1) return [];
  const cur = Array.from({ length: colCount }, (_, i) => (flex[i] != null && flex[i] > 0.05 ? flex[i] : 1));
  const sum = cur.reduce((a, b) => a + b, 0);
  const factor = sum > 0 ? colCount / sum : 1;
  return cur.map(v => v * factor);
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
  // Ширина ОДНОЙ колонки — общий масштаб зоны (центральный сплиттер). Итоговая
  // ширина зоны = число колонок × width; между собой колонки делят её по colFlex.
  width: number;
  // Доли ширины колонок (grow-ratio), позиционно по РЕАЛЬНОМУ индексу колонки в
  // layout. Пусто/1 у всех — колонки равны (как было). Их меняет сплиттер МЕЖДУ
  // колонками, перераспределяя ширину внутри пары; общий масштаб (width) при этом
  // не трогается. Позиционно, а не по ключу панели: колонка — место, не сущность.
  colFlex: number[];
  // Раскладка, спрятанная кнопкой «Свернуть все» — повторный клик вернёт её как была
  stash: PanelKey[][];
}

export interface PanelZones {
  left: ZoneState;
  right: ZoneState;
  // Вес = высота СЛОТА панели. Общий для зон: панель уносит свой вес с собой.
  weights: Partial<Record<PanelKey, number>>;
  // Где панель лежала в последний раз. Закрытая панель показывает иконку именно
  // здесь, а не в зашитой домашней зоне: перетащив «Задачи» налево и закрыв их,
  // человек ждёт кнопку слева — там, где панель была, а не там, где родилась.
  // Пусто (панель ещё нигде не открывали) — берётся PANEL_HOME.
  home: Partial<Record<PanelKey, Zone>>;
  // Кнопки, убранные с рельсы в её ящик (меню «…»): редко используемые панели,
  // чтобы столбец иконок не рос бесконечно. Список ОБЩИЙ на обе зоны, а не свой у
  // каждой: в какой рельсе показывать строку, и так решает home — второй список
  // пришлось бы синхронизировать при каждом переезде кнопки между сторонами.
  tucked: PanelKey[];
  // Порядок кнопок в столбце рельсы, заданный перетаскиванием. Пусто — порядок
  // каталожный (PANEL_KEYS). Список ОБЩИЙ на обе зоны по тем же основаниям, что и
  // tucked: сторона кнопки живёт в home, и второй список пришлось бы синхронизировать
  // с ним при каждом переезде.
  //
  // Плоский на все группы рельсы, хотя переставлять можно только ВНУТРИ группы:
  // sortRail сравнивает лишь ключи переданного набора, поэтому чужие индексы в
  // сравнение не попадают и разводить список по группам незачем.
  railOrder: PanelKey[];
}

export function emptyZone(): ZoneState {
  return { layout: [], mode: 'multi', width: COL_DEFAULT, colFlex: [], stash: [] };
}

export function emptyZones(): PanelZones {
  return { left: emptyZone(), right: emptyZone(), weights: {}, home: {}, tucked: [], railOrder: [] };
}

// Зона, в которой панель показывает свою иконку, когда закрыта
export function homeOf(zones: PanelZones, k: PanelKey): Zone {
  return zones.home[k] ?? PANEL_HOME[k];
}

// Кнопка панели убрана в ящик рельсы (меню «…»)
export function isTucked(zones: PanelZones, k: PanelKey): boolean {
  return zones.tucked.includes(k);
}

// Запомнить, где сейчас лежит каждая панель — включая спрятанные «свернуть все»:
// разворачивание вернёт их в ту же зону, значит и иконка ждёт там же.
export function trackHome(zones: PanelZones): PanelZones {
  const home = { ...zones.home };
  for (const zone of ZONES) {
    for (const k of [...zones[zone].layout.flat(), ...zones[zone].stash.flat()]) home[k] = zone;
  }
  return { ...zones, home };
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

// Приведение пары зон к инварианту «панель ровно в одном месте».
//
// Мест на самом деле ЧЕТЫРЕ: раскладка каждой зоны и её спрятанный кнопкой
// «Свернуть все» набор. Stash — это отложенный layout, поэтому панель, уехавшая
// в соседнюю зону, обязана исчезнуть и из него: иначе разворачивание вернёт её
// второй копией, и одна и та же панель окажется в обеих рельсах.
//
// Приоритет: то, что НА ЭКРАНЕ, сильнее спрятанного; среди раскладок — правая
// (там исторически живут панели с настоящим контентом, слева files/tasks успели
// побывать dev-заглушками).
export function enforceZoneInvariant(zones: PanelZones): PanelZones {
  const seen = new Set<PanelKey>();
  const claim = (cols: PanelKey[][]): PanelKey[][] => {
    const out: PanelKey[][] = [];
    for (const col of cols) {
      const clean = col.filter(k => !seen.has(k));
      clean.forEach(k => seen.add(k));
      if (clean.length) out.push(clean);
    }
    return out;
  };
  const rightLayout = claim(zones.right.layout);
  const leftLayout = claim(zones.left.layout);
  const rightStash = claim(zones.right.stash);
  const leftStash = claim(zones.left.stash);
  return {
    ...zones,
    right: { ...zones.right, layout: rightLayout, stash: rightStash },
    left: { ...zones.left, layout: leftLayout, stash: leftStash },
  };
}

// Санитайз пары зон из сохранённого состояния: валидация полей плюс инвариант
export function sanitizeZones(raw: unknown): PanelZones {
  const src = (raw && typeof raw === 'object' ? raw : {}) as Partial<Record<Zone, unknown>> & { weights?: unknown };
  const readZone = (v: unknown): ZoneState => {
    const z = (v && typeof v === 'object' ? v : {}) as Partial<ZoneState>;
    const layout = sanitizeLayout(z.layout);
    return {
      layout,
      mode: z.mode === 'solo' ? 'solo' : 'multi',
      width: typeof z.width === 'number' ? parseWidth(String(z.width)) : COL_DEFAULT,
      colFlex: normalizeColFlex(layout.length, parseColFlex(z.colFlex)),
      stash: sanitizeLayout(z.stash),
    };
  };
  return enforceZoneInvariant({
    left: readZone(src.left),
    right: readZone(src.right),
    weights: parseWeights(JSON.stringify(src.weights ?? {})),
    home: parseHome((src as { home?: unknown }).home),
    tucked: parseKeyList((src as { tucked?: unknown }).tucked),
    railOrder: parseKeyList((src as { railOrder?: unknown }).railOrder),
  });
}

// Список ключей панелей из сохранённого состояния (спрятанные в ящик, порядок
// кнопок рельсы): чужие ключи отбрасываем, упразднённые имена переводим (как у
// весов и привязок к зонам), дубли снимаем — оба списка читаются позиционно, и
// повтор дал бы либо две одинаковые строки в меню, либо спор двух мест за одну
// кнопку.
export function parseKeyList(raw: unknown): PanelKey[] {
  if (!Array.isArray(raw)) return [];
  const out: PanelKey[] = [];
  for (const v of raw) {
    const key = typeof v === 'string' ? migrateLegacyKey(v) : null;
    if (key && !out.includes(key)) out.push(key);
  }
  return out;
}

// Сохранённая привязка «панель → зона»: чужие ключи и значения отбрасываем,
// упразднённые имена переводим (personas → team), как и у весов.
export function parseHome(raw: unknown): Partial<Record<PanelKey, Zone>> {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const out: Partial<Record<PanelKey, Zone>> = {};
  for (const [k, v] of Object.entries(raw)) {
    const key = migrateLegacyKey(k);
    if (key && (v === 'left' || v === 'right')) out[key] = v;
  }
  return out;
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

// Вынуть панель из свёрнутых наборов ОБЕИХ зон (кнопка «Свернуть все»).
//
// Нужно везде, где дом кнопки назначается ЯВНО: запись состояния заканчивается
// trackHome, а тот перечитывает layout и stash — и панель, забытая в свёрнутом
// наборе, утаскивает свой home обратно в прежнюю зону. Снаружи это выглядело так,
// будто брошенная на чужую рельсу кнопка не переезжает вовсе: дроп срабатывал, но
// его результат тут же откатывался. Заметно было только на панели, которую после
// сворачивания ни разу не открывали, — открытие вычищает её из stash само
// (enforceZoneInvariant: layout сильнее спрятанного набора).
//
// По смыслу это и правильно: кнопку унесли на другую сторону осознанно, и
// разворачивание зоны должно вернуть только то, что в ней осталось.
function dropFromStash(zones: PanelZones, k: PanelKey): PanelZones {
  const strip = (z: ZoneState): ZoneState => {
    if (!z.stash.some(col => col.includes(k))) return z;
    return { ...z, stash: z.stash.map(col => col.filter(x => x !== k)).filter(col => col.length > 0) };
  };
  return { ...zones, left: strip(zones.left), right: strip(zones.right) };
}

// Закрыть панель, бросив её на рельсу зоны: она не просто закрывается, а
// оставляет иконку ИМЕННО ТАМ, куда её бросили (даже если лежала в соседней
// зоне) — это и есть «убрать панель с глаз, но положить кнопку под руку».
export function closePanelTo(zones: PanelZones, zone: Zone, k: PanelKey): PanelZones {
  const closed = dropFromStash(closePanel(zones, k), k);
  return { ...closed, home: { ...closed.home, [k]: zone } };
}

// Убрать кнопку панели в ящик рельсы (её меню «…»). Панель при этом ЗАКРЫВАЕТСЯ:
// у открытой панели кнопка остаётся в рельсе (иначе её нечем было бы закрыть
// привычным кликом), и «спрятать», не убрав панель с глаз, ничего бы не изменило.
export function tuckPanel(zones: PanelZones, zone: Zone, k: PanelKey): PanelZones {
  const closed = closePanelTo(zones, zone, k);
  return isTucked(closed, k) ? closed : { ...closed, tucked: [...closed.tucked, k] };
}

// Вернуть кнопку из ящика на рельсу — именно той зоны, куда её бросили. Панель не
// открывается: возвращают кнопку, а не панель (открытие — отдельный жест, дроп в
// раскладку).
export function untuckPanel(zones: PanelZones, zone: Zone, k: PanelKey): PanelZones {
  const base = dropFromStash(zones, k);
  return {
    ...base,
    tucked: base.tucked.filter(x => x !== k),
    home: { ...base.home, [k]: zone },
  };
}

// Разовая укладка кнопок в ящик рельсы при появлении фичи (см. defaultTucked).
//
// Дефолт в чистом состоянии решает задачу только для НОВОГО пользователя, а у всех
// остальных раскладка уже сохранена — и новые «редкие» кнопки встали бы им в столбец.
// Догоняем их здесь, но ровно ОДИН раз на кнопку: applied — список ключей, которым
// ящик уже предлагали. Без него миграция срабатывала бы каждый запуск и утаскивала
// обратно кнопку, которую человек осознанно вернул в столбец.
//
// Список, а не флаг «миграция пройдена»: набор defaultTucked со временем пополняется,
// и новая кнопка обязана доехать до тех, кто прошлую волну уже видел.
//
// Открытую панель не трогаем и не закрываем (в отличие от tuckPanel): пока панель на
// экране, её кнопка и так стоит в столбце (см. railKeyVisible), а в ящик уедет, когда
// человек эту панель закроет.
export function mergeTuckDefaults(
  tucked: readonly PanelKey[],
  want: readonly PanelKey[],
  applied: readonly PanelKey[],
): { tucked: PanelKey[]; applied: PanelKey[]; changed: boolean } {
  const fresh = want.filter(k => !applied.includes(k));
  if (fresh.length === 0) return { tucked: [...tucked], applied: [...applied], changed: false };
  const nextTucked = [...tucked];
  for (const k of fresh) if (!nextTucked.includes(k)) nextTucked.push(k);
  return { tucked: nextTucked, applied: [...applied, ...fresh], changed: true };
}

// Порядок кнопок ОДНОЙ группы рельсы: сначала те, что человек расставил сам (по
// индексам railOrder), затем незнакомые — хвостом, в исходном каталожном порядке.
//
// Незнакомые появляются у группы, которую ещё ни разу не переставляли, и у панели,
// добавленной в продукт после того, как порядок был задан: в сохранённом списке её
// нет, и место ей остаётся только в конце. Каталожный порядок при этом сохраняется —
// новые панели встают в реестровой очерёдности между собой, а не как попало.
export function sortRail(order: readonly PanelKey[], keys: readonly PanelKey[]): PanelKey[] {
  if (order.length === 0) return [...keys];
  const known: PanelKey[] = [];
  const rest: PanelKey[] = [];
  for (const k of keys) (order.includes(k) ? known : rest).push(k);
  known.sort((a, b) => order.indexOf(a) - order.indexOf(b));
  return [...known, ...rest];
}

// Порядок кнопок рельсы СВЕРХУ ВНИЗ по одному состоянию, без оглядки на экран:
// группы реестра подряд, внутри группы — пользовательский порядок railOrder.
//
// Нужен ФОЛБЭКУ внешнего показа панели (revealPanel), когда спросить настоящий
// порядок не у кого — зона на этом экране не смонтирована. Своих фильтров видимости
// здесь нет намеренно: зона выбрасывает из столбца часть кнопок (чужая сторона,
// ящик, сессионные без содержимого), но фильтрация ПОРЯДОК СОХРАНЯЕТ, а место в
// колонке считается по относительному рангу панелей (placeByRail) — на нём выпавшие
// ключи не сказываются. Полный список при этом строже: у ключа, которого в столбце
// нет, ранга не было бы вовсе, и панель считалась бы самой нижней.
export function railSequence(railOrder: readonly PanelKey[]): PanelKey[] {
  return RAIL_GROUPS.flatMap(g => sortRail(railOrder, g));
}

// Перестановка кнопки внутри своей группы: moved встаёт ПЕРЕД before (null — в
// конец группы). Место задаётся соседом, а не индексом, потому что рельса считает
// его по ВИДИМЫМ кнопкам, а порядок хранится для всей группы целиком — вместе с
// теми, что сейчас закрыты в соседней зоне или лежат в ящике. По индексу такие
// кнопки молча съезжали бы на чужие места.
//
// Группа материализуется в railOrder ЦЕЛИКОМ при первой же перестановке: храни мы
// только сдвинутый ключ, он всплывал бы поверх нетронутых соседей (у тех индекса
// нет вовсе, и они уходят в хвост).
export function reorderRail(
  zones: PanelZones,
  keys: readonly PanelKey[],
  moved: PanelKey,
  before: PanelKey | null,
): PanelZones {
  if (!keys.includes(moved) || moved === before) return zones;
  const cur = sortRail(zones.railOrder, keys);
  const next = cur.filter(k => k !== moved);
  const at = before ? next.indexOf(before) : -1;
  next.splice(at >= 0 ? at : next.length, 0, moved);
  // Порядок не изменился — не будим подписчиков и не пишем localStorage
  if (next.every((k, i) => k === cur[i])) return zones;
  return { ...zones, railOrder: [...zones.railOrder.filter(k => !keys.includes(k)), ...next] };
}

// Открыть панель В ЗОНЕ. Если она открыта в другой — это перенос: из прежней
// зоны панель уходит. В solo-режиме целевой зоны раскладка схлопывается до неё.
// cap — вместимость колонки у рельсы (сколько панелей влезает по высоте), её
// считает зона: см. COL_CAP.
export function openPanelIn(zones: PanelZones, zone: Zone, k: PanelKey, cap = COL_CAP, railSeq?: readonly PanelKey[]): PanelZones {
  const base = closePanel(zones, k);
  return withZone(base, zone, z => ({
    ...z,
    layout: z.mode === 'solo' ? [[k]] : addPanel(z.layout, k, zone, cap, railSeq),
  }));
}

// Клик по иконке рельсы: панель открыта В ЭТОЙ зоне — закрыть, иначе открыть
// здесь (в том числе забрав из соседней зоны).
export function togglePanelIn(zones: PanelZones, zone: Zone, k: PanelKey, cap = COL_CAP, railSeq?: readonly PanelKey[]): PanelZones {
  return zoneOf(zones, k) === zone ? closePanel(zones, k) : openPanelIn(zones, zone, k, cap, railSeq);
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

// Дроп панели ИЗ РЕЛЬСЫ на открытую панель: гость занимает её слот (вместе с
// местом в колонке и весом — вес переносит вызывающий), а хозяин уходит с экрана,
// оставляя иконку на рельсе СВОЕЙ зоны — там, где он только что был.
//
// Обменом (swapAcross) это быть не может: у закрытого гостя нет слота, который он
// отдал бы взамен, поэтому swapAcross на закрытой панели молча возвращает всё как
// было. Отсюда отдельная операция, а не ветка в обмене.
export function replacePanelWith(zones: PanelZones, guest: PanelKey, host: PanelKey): PanelZones {
  if (guest === host) return zones;
  const zh = zoneOf(zones, host);
  if (!zh) return zones;
  // Гость мог быть открыт в соседней зоне — оттуда он уходит (инвариант «панель
  // ровно в одной зоне»). Дроп открытой панели на панель обычно идёт обменом, но
  // операция обязана быть корректной и здесь, а не только для гостя из рельсы.
  const base = closePanel(zones, guest);
  const filled = withZone(base, zh, z => ({
    ...z,
    layout: z.layout.map(col => col.map(k => (k === host ? guest : k))),
  }));
  return { ...filled, home: { ...filled.home, [host]: zh } };
}

// Дроп в горизонтальный плейсхолдер зоны. Внутри своей зоны — обычная
// перестановка; из другой зоны панель сначала уходит из неё, а затем встаёт в
// указанную колонку. Пустая целевая зона принимает панель первой колонкой.
export function moveAcrossAt(zones: PanelZones, k: PanelKey, zone: Zone, colIdx: number, rowIdx: number): PanelZones {
  // src === null — панель закрыта: её тащат из рельсы, и дроп её ОТКРЫВАЕТ ровно
  // в указанном месте (ветка ниже так и работает: убрать из прежней зоны нечего,
  // остаётся вставка)
  const src = zoneOf(zones, k);
  if (src === zone) return withZone(zones, zone, z => ({ ...z, layout: movePanelAt(z.layout, k, colIdx, rowIdx) }));
  const base = closePanel(zones, k);
  return withZone(base, zone, z => {
    // Зона в режиме одной панели принимает гостя вместо своей — как при клике по
    // иконке (openPanelIn), иначе перенос втихую сломал бы её режим
    if (z.mode === 'solo') return { ...z, layout: [[k]] };
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
  // Закрытую панель (src === null) дроп открывает новой колонкой на этом месте
  const src = zoneOf(zones, k);
  if (src === zone) return withZone(zones, zone, z => ({ ...z, layout: movePanelToNewColumn(z.layout, k, insertIdx) }));
  const base = closePanel(zones, k);
  return withZone(base, zone, z => {
    if (z.mode === 'solo') return { ...z, layout: [[k]] };
    const cols = z.layout.map(c => [...c]);
    cols.splice(Math.max(0, Math.min(cols.length, insertIdx)), 0, [k]);
    return { ...z, layout: cols };
  });
}

// Выселить из зоны панели, которых на этом экране в ней быть не может, и вернуть
// их иконки в домашнюю зону. Нужно как ремонт: экраны отличаются набором панелей
// (в разделе «Чаты» правая зона держит только артефакты сессии), а сохранённая
// раскладка живёт дольше — панель, уехавшая туда, где её некому нарисовать,
// пропадала с концами: в своей зоне её нет, в чужой она невидима.
// null — выселять нечего (сигнал вызывающему не дёргать запись).
export function evictForeign(zones: PanelZones, zone: Zone, allowed: readonly PanelKey[]): PanelZones | null {
  const z = zones[zone];
  const strays = [...z.layout.flat(), ...z.stash.flat()].filter(k => !allowed.includes(k));
  if (strays.length === 0) return null;
  const drop = (cols: PanelKey[][]) => cols.map(c => c.filter(k => allowed.includes(k))).filter(c => c.length > 0);
  const home = { ...zones.home };
  for (const k of strays) home[k] = PANEL_HOME[k];
  return {
    ...zones,
    home,
    [zone]: { ...z, layout: drop(z.layout), stash: drop(z.stash) },
  };
}

// Зона «свёрнута»: своих открытых панелей нет, но спрятанный набор есть
export function isZoneCollapsed(z: ZoneState): boolean {
  return z.layout.flat().length === 0 && z.stash.flat().length > 0;
}

// Показать панель по внешнему запросу (git-бар над композером просит «Изменения»).
// Открытую НЕ трогаем и не перетаскиваем через полэкрана: вызывающий вместо этого
// просит её мигнуть. Закрытая открывается в своей домашней зоне.
//
// ФОЛБЭК-путь: обычно внешний показ исполняет сама зона (registerOpener), потому что
// правило размещения опирается на живую высоту колонки и настоящий порядок кнопок.
// Здесь — то же правило (placeByRail через openPanelIn), но по каноническому порядку
// кнопок и с фолбэк-вместимостью COL_CAP: зоны на экране нет, спросить не у кого.
// Раньше этот путь шёл мимо правила рельсы вовсе: панель дописывалась в конец колонки,
// не глядя на порядок кнопок, и высоты соседей при этом делились заново.
export function revealPanel(zones: PanelZones, k: PanelKey): { zones: PanelZones; wasOpen: boolean } {
  if (zoneOf(zones, k)) return { zones, wasOpen: true };
  const home = homeOf(zones, k);
  return { zones: openPanelIn(zones, home, k, COL_CAP, railSequence(zones.railOrder)), wasOpen: false };
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

  const zone = (layout: PanelKey[][], prefix: string): ZoneState => ({
    layout,
    mode: read(`${prefix}_mode`) === 'solo' ? 'solo' : 'multi',
    width: parseWidth(read(`${prefix}_width`)),
    // Долей ширины старое состояние не знало — колонки равны
    colFlex: [],
    stash: sanitizeLayout((() => { try { return JSON.parse(read(`${prefix}_stash`) ?? '[]'); } catch { return []; } })()),
  });

  // Дубли между старыми раскладками (files/tasks были в обоих наборах) снимает
  // общий инвариант — он же чистит и спрятанные наборы
  return enforceZoneInvariant({
    right: zone(readLayout(rightRaw, legacyOpen), `cc_${ns}_panels`),
    left: zone(readLayout(leftRaw, null), `cc_${ns}_left_panels`),
    // Веса были раздельными; при совпадении ключа берём правый
    weights: {
      ...parseWeights(read(`cc_${ns}_left_panels_weights`)),
      ...parseWeights(read(`cc_${ns}_panels_weights`)),
    },
    // Привязки к зонам старое состояние не знало — их проставит trackHome по
    // фактическому расположению панелей при первой же записи
    home: {},
    // Ящик рельсы и свой порядок кнопок появились позже раздельных ключей —
    // переносить нечего
    tucked: [],
    railOrder: [],
  });
}

// Разделы хаба (Заметки, Знания, Персоны, Проекты) до перехода на рельсу держали
// обычный сайдбар: режим pinned/collapsed в своём ключе и ОБЩУЮ на все разделы
// ширину. Переводим это в состояние зоны, чтобы свёрнутый сайдбар остался
// свёрнутым, а привычная ширина не сбросилась на дефолтную.
export const LEGACY_SIDEBAR_WIDTH_KEY = 'cc_sidebar_width';

export function migrateSidebarSection(
  read: (key: string) => string | null,
  modeKey: string,
  panelKey: PanelKey,
): PanelZones | null {
  const mode = read(modeKey);
  if (mode == null) return null;
  // collapsed — панель прячется в stash: кнопка «свернуть» вернёт её как была
  const collapsed = mode === 'collapsed';
  return {
    ...emptyZones(),
    left: {
      layout: collapsed ? [] : [[panelKey]],
      stash: collapsed ? [[panelKey]] : [],
      mode: 'multi',
      width: parseWidth(read(LEGACY_SIDEBAR_WIDTH_KEY)),
      colFlex: [],
    },
  };
}

// ---------- API стора ----------

export interface PanelZonesApi {
  zones: PanelZones;
  // Клик по иконке рельсы зоны. cap — вместимость колонки у рельсы по живой
  // высоте (см. COL_CAP): её знает только зона, состояние пикселей не хранит.
  // railSeq — порядок кнопок столбца сверху вниз: по нему панель встаёт на своё
  // место среди уже открытых (см. placeByRail). Знает его тоже только зона.
  toggle: (zone: Zone, k: PanelKey, cap?: number, railSeq?: readonly PanelKey[]) => void;
  // Закрыть панель, где бы она ни лежала (её иконка остаётся в прежней зоне)
  close: (k: PanelKey) => void;
  // Дроп панели на рельсу: закрыть и положить иконку ИМЕННО в эту зону — панель
  // убирают туда, где потом хотят её найти. Для ЗАКРЫТОЙ панели закрывать нечего,
  // и остаётся только переезд иконки: так кнопка переносится между сторонами, не
  // открывая панель.
  closeTo: (zone: Zone, k: PanelKey) => void;
  // Ремонт сохранённой раскладки: выгнать из зоны панели, которых на этом экране
  // в ней быть не может (см. evictForeign)
  evict: (zone: Zone, allowed: readonly PanelKey[]) => void;
  // Дроп кнопки в ящик рельсы (её меню «…») / возврат кнопки из ящика на рельсу.
  // Зона в обоих случаях та, к чьей рельсе относится жест: кнопка ложится и
  // возвращается туда, куда её бросили.
  tuck: (zone: Zone, k: PanelKey) => void;
  untuck: (zone: Zone, k: PanelKey) => void;
  // Перестановка кнопки в столбце рельсы: moved встаёт перед before (null — в конец).
  // keys — ПОЛНЫЙ набор её группы (перетаскивать можно только внутри своей), см.
  // reorderRail.
  reorder: (keys: readonly PanelKey[], moved: PanelKey, before: PanelKey | null) => void;
  // Дроп панели на панель (в том числе через границу зон)
  swapWith: (a: PanelKey, b: PanelKey) => void;
  // Дроп панели из рельсы на открытую панель: гость встаёт в её слот, хозяин
  // закрывается (см. replacePanelWith)
  replaceWith: (guest: PanelKey, host: PanelKey) => void;
  // Дроп в плейсхолдер строки / в разделитель колонок целевой зоны
  moveAt: (k: PanelKey, zone: Zone, colIdx: number, rowIdx: number) => void;
  moveToNewColumn: (k: PanelKey, zone: Zone, insertIdx: number) => void;
  setMode: (zone: Zone, m: PanelMode) => void;
  setWidth: (zone: Zone, n: number) => void;
  toggleCollapsed: (zone: Zone) => void;
  setWeights: (next: Partial<Record<PanelKey, number>>) => void;
  // Доли ширины колонок зоны — ресайз границы МЕЖДУ колонками, см. colFlex
  setColFlex: (zone: Zone, next: number[]) => void;
  // Показать панель по внешнему запросу (git-бар над композером). Возвращает
  // true, если она УЖЕ была открыта — тогда вызывающий просит её мигнуть.
  reveal: (k: PanelKey) => boolean;
  // Зона объявляет стору, как она открывает у себя панель (её правило размещения:
  // живая вместимость колонки, порядок кнопок, сохранение высот соседей). Стор
  // сам этого не умеет и не должен: правило зависит от пикселей на экране.
  // Возвращает функцию отписки — вызывать в useEffect зоны.
  registerOpener: (zone: Zone, open: (k: PanelKey) => void) => () => void;
}

export type PanelZonesStore = { use: () => PanelZonesApi };

// Фабрика независимого инстанса: своё состояние в замыкании + свой ключ
// localStorage cc_{ns}_zones. Инстансы создаются на уровне модуля (ниже),
// поэтому чтение localStorage происходит при импорте — как было до зон.
function createPanelZones(ns: string, opts?: {
  legacyOpenKey?: string;
  defaultZones?: Partial<Record<Zone, PanelKey[][]>>;
  // Кнопки, которые уезжают в ящик рельсы («…»), а не встают в столбец: редко
  // используемые панели, чтобы столбец иконок не был длинным. Применяется и к
  // чистому состоянию (новый пользователь), и РАЗОВО к уже сохранённому — иначе
  // новая «редкая» кнопка встала бы в столбец всем, кроме новичков. Подробности
  // и защита от повтора — mergeTuckDefaults.
  defaultTucked?: PanelKey[];
  // Раздел переезжает со старого сайдбара: его ключ режима и панель, в которую
  // превращается прежний сайдбар
  legacySidebar?: { modeKey: string; panelKey: PanelKey };
}): PanelZonesStore {
  const KEY = `cc_${ns}_zones`;
  // Ключи, которым ящик рельсы уже предлагали (см. mergeTuckDefaults). Отдельным
  // ключом, а не полем состояния: список переживает и сброс раскладки, и её порчу —
  // иначе «почини мусор в cc_ws_zones» означало бы заново утащить в ящик кнопки,
  // которые человек оттуда достал.
  const TUCKED_KEY = `cc_${ns}_tucked_applied`;

  // Приоритет: новое состояние → миграция со старых раздельных ключей → дефолт.
  // defaultZones нужен там, где базовая панель должна быть открыта при первом
  // запуске (напр. chats слева). После закрытия пользователем состояние
  // сохранится, и при следующем визите панель останется закрытой.
  // migrated — состояние переехало со старых ключей и ещё не записано под новым
  let migrated = false;
  // trackHome на входе: у сохранённого состояния привязок может не быть (старый
  // формат, миграция, дефолт) — тогда они выводятся из того, где панели лежат
  let _zones: PanelZones = trackHome((() => {
    const base = (() => {
      const raw = lsGet(KEY);
      if (raw) {
        try { return sanitizeZones(JSON.parse(raw)); } catch { /* мусор → миграция/дефолт */ }
      }
      const fromLegacy = migrateZones(lsGet, ns, opts?.legacyOpenKey);
      if (fromLegacy) { migrated = true; return fromLegacy; }
      const fromSidebar = opts?.legacySidebar
        && migrateSidebarSection(lsGet, opts.legacySidebar.modeKey, opts.legacySidebar.panelKey);
      if (fromSidebar) { migrated = true; return fromSidebar; }
      return sanitizeZones({
        left: { layout: opts?.defaultZones?.left ?? [] },
        right: { layout: opts?.defaultZones?.right ?? [] },
      });
    })();

    // Укладка новых «редких» кнопок в ящик — одинаково для всех веток выше: и
    // новичок, и старожил проходят через одну проверку applied.
    const want = opts?.defaultTucked ?? [];
    if (want.length === 0) return base;
    const applied = parseKeyList((() => {
      try { return JSON.parse(lsGet(TUCKED_KEY) ?? '[]'); } catch { return []; }
    })());
    const merged = mergeTuckDefaults(base.tucked, want, applied);
    if (!merged.changed) return base;
    lsSet(TUCKED_KEY, JSON.stringify(merged.applied));
    // Состояние поехало — его надо записать, иначе отметка applied уже стоит, а
    // сам ящик пуст: следующий запуск сочтёт волну пройденной и кнопки останутся
    // в столбце.
    migrated = true;
    return { ...base, tucked: merged.tucked };
  })());

  const listeners = new Set<() => void>();
  function emit() { listeners.forEach(l => l()); }
  function subscribe(l: () => void) { listeners.add(l); return () => { listeners.delete(l); }; }

  // Открыватели смонтированных зон (см. registerOpener): по одному на сторону.
  // Императивный реестр, а не поле состояния, СОЗНАТЕЛЬНО — это не данные, а канал
  // к тому, кто знает пиксели: внешний показ панели обязан идти по тому же правилу,
  // что и клик по кнопке рельсы, а оно считается по живой высоте колонки. Через
  // состояние это была бы отложенная заявка с гонками (кто её исполнил, что делать
  // с зависшей), здесь же вызов синхронный, и reveal по-прежнему сразу отвечает
  // вызывающему, была ли панель открыта.
  const openers = new Map<Zone, (k: PanelKey) => void>();

  function persist() { lsSet(KEY, JSON.stringify(_zones)); }

  // Переезд фиксируем сразу, не дожидаясь первого действия пользователя: иначе
  // состояние живёт только в памяти, и любая правка старых ключей другой вкладкой
  // (или откатом кода) молча разошлась бы с тем, что человек видит на экране.
  if (migrated) persist();

  // Единая точка записи: держит инвариант «панель ровно в одном месте» и
  // нормализует веса по всем открытым панелям обеих зон.
  //
  // Инвариант проверяется ИМЕННО ЗДЕСЬ, а не в операциях: спрятанный набор
  // (stash) живёт мимо них — панель успевает уехать в соседнюю зону уже после
  // того, как её запомнила кнопка «Свернуть все», и разворачивание вернуло бы
  // вторую копию.
  function commit(next: PanelZones) {
    const clean = trackHome(enforceZoneInvariant(next));
    // Доли ширины колонок держим синхронными с числом колонок каждой зоны: открыли/
    // закрыли колонку — массив подрос/укоротился, лишнего мусора не копится.
    const withFlex = {
      ...clean,
      left: { ...clean.left, colFlex: normalizeColFlex(clean.left.layout.length, clean.left.colFlex) },
      right: { ...clean.right, colFlex: normalizeColFlex(clean.right.layout.length, clean.right.colFlex) },
    };
    _zones = { ...withFlex, weights: normalizeWeights(openKeysOf(withFlex), withFlex.weights) };
    persist();
    emit();
  }

  // Вес новой панели заводим ДО раскладки, иначе normalizeWeights даст ей 1 уже
  // после деления и соседи дрогнут. Нужно и клику по иконке, и дропу из рельсы:
  // оба открывают панель, которой в раскладке ещё не было.
  function ensureWeight(k: PanelKey) {
    if (_zones.weights[k] == null) {
      _zones = { ..._zones, weights: { ..._zones.weights, [k]: 1 } };
    }
  }

  function usePanelZones(): PanelZonesApi {
    const zones = useSyncExternalStore(subscribe, () => _zones);

    const toggle = useCallback((zone: Zone, k: PanelKey, cap?: number, railSeq?: readonly PanelKey[]) => {
      ensureWeight(k);
      commit(togglePanelIn(_zones, zone, k, cap, railSeq));
    }, []);

    const close = useCallback((k: PanelKey) => { commit(closePanel(_zones, k)); }, []);

    const closeTo = useCallback((zone: Zone, k: PanelKey) => { commit(closePanelTo(_zones, zone, k)); }, []);

    const tuck = useCallback((zone: Zone, k: PanelKey) => { commit(tuckPanel(_zones, zone, k)); }, []);

    const untuck = useCallback((zone: Zone, k: PanelKey) => { commit(untuckPanel(_zones, zone, k)); }, []);

    const reorder = useCallback((keys: readonly PanelKey[], moved: PanelKey, before: PanelKey | null) => {
      const next = reorderRail(_zones, keys, moved, before);
      // Порядок не изменился — операция вернула то же состояние, писать нечего
      if (next !== _zones) commit(next);
    }, []);

    const evict = useCallback((zone: Zone, allowed: readonly PanelKey[]) => {
      // null — выселять нечего: молчим, чтобы не будить подписчиков на каждый рендер
      const next = evictForeign(_zones, zone, allowed);
      if (next) commit(next);
    }, []);

    const swapWith = useCallback((a: PanelKey, b: PanelKey) => {
      // Вес — высота СЛОТА, а не панели: вместе с местами меняем и веса,
      // иначе панель утащила бы свою высоту и раскладка «прыгнула» бы
      const wa = _zones.weights[a] ?? 1;
      const wb = _zones.weights[b] ?? 1;
      const swapped = swapAcross(_zones, a, b);
      commit({ ...swapped, weights: { ...swapped.weights, [a]: wb, [b]: wa } });
    }, []);

    // Дроп из рельсы на открытую панель: гость занимает её слот вместе с высотой —
    // вес привязан к СЛОТУ, а не к панели, поэтому переезжает к гостю (иначе
    // раскладка «прыгнула» бы на чужой высоте). Хозяин уходит закрытым, свой
    // прежний вес не теряет: откроется — вернётся с ним.
    const replaceWith = useCallback((guest: PanelKey, host: PanelKey) => {
      const wh = _zones.weights[host] ?? 1;
      const next = replacePanelWith(_zones, guest, host);
      commit({ ...next, weights: { ...next.weights, [guest]: wh } });
    }, []);

    // Дроп в направляющую: перенос открытой панели либо открытие закрытой —
    // её тащат из рельсы, и место дропа и есть выбранное для неё место
    const moveAt = useCallback((k: PanelKey, zone: Zone, colIdx: number, rowIdx: number) => {
      ensureWeight(k);
      commit(moveAcrossAt(_zones, k, zone, colIdx, rowIdx));
    }, []);

    const moveToNewColumn = useCallback((k: PanelKey, zone: Zone, insertIdx: number) => {
      ensureWeight(k);
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

    // Ресайз ширины между колонками: пишем массив долей напрямую, мимо commit —
    // как у весов, нормализацию длины делает commit при смене раскладки, а на
    // каждом кадре ресайза она не нужна (число колонок не меняется)
    const setColFlex = useCallback((zone: Zone, next: number[]) => {
      _zones = withZone(_zones, zone, z => ({ ...z, colFlex: next }));
      persist();
      emit();
    }, []);

    const registerOpener = useCallback((zone: Zone, open: (k: PanelKey) => void) => {
      openers.set(zone, open);
      // Снимаем только СВОЙ открыватель: зона могла перемонтироваться, и её место
      // уже занял новый — стереть его отпиской старого значило бы обезоружить стор.
      return () => { if (openers.get(zone) === open) openers.delete(zone); };
    }, []);

    const reveal = useCallback((k: PanelKey) => {
      if (zoneOf(_zones, k)) return true;
      // Открывает ЗОНА — правило размещения одно на все входы (клик по кнопке,
      // перенос с чужой рельсы, этот показ). Вес панели она заведёт сама (toggle).
      const open = openers.get(homeOf(_zones, k));
      if (open) { open(k); return false; }
      // Домашняя зона не смонтирована (другой экран, первый кадр) — раскладку правим
      // сами, по тому же правилу, но с фолбэк-вместимостью: см. revealPanel
      commit(revealPanel(_zones, k).zones);
      return false;
    }, []);

    return { zones, toggle, close, closeTo, tuck, untuck, reorder, evict, swapWith, replaceWith, moveAt, moveToNewColumn, setMode, setWidth, toggleCollapsed, setWeights, setColFlex, reveal, registerOpener };
  }

  return { use: usePanelZones };
}

// Инстанс воркспейса — ключ cc_ws_zones, миграция со старых cc_ws_panels_* /
// cc_ws_left_panels_* и совсем старого плоского списка cc_ws_panels_open.
// Слева при первом запуске открыты «Чаты».
export const wsPanels = createPanelZones('ws', {
  legacyOpenKey: 'cc_ws_panels_open',
  defaultZones: { left: [['chats']] },
  // Редкие кнопки прячем в ящик рельсы из коробки: столбец остаётся коротким
  // (Файлы/Изменения/Задачи/Документация/Команда), а Граф, Знания, Навыки,
  // Терминал и Сервисы достаются из «…» по мере надобности.
  defaultTucked: ['graph', 'knowledge', 'skills', 'terminal', 'preview'],
});

// Инстанс раздела «Чаты» — независимая раскладка (cc_chat_zones).
export const chatPanels = createPanelZones('chat', {
  defaultZones: { left: [['chats']] },
});

// Разделы хаба: у каждого своя раскладка и своя ширина колонки (раньше ширина
// была общей на все разделы — один ключ cc_sidebar_width).
export const notesPanels = createPanelZones('notes', {
  defaultZones: { left: [['notesList']] },
  legacySidebar: { modeKey: 'cc_notes_sidebar_mode', panelKey: 'notesList' },
});
export const knowledgePanels = createPanelZones('knowledge', {
  defaultZones: { left: [['knowledgeList']] },
  legacySidebar: { modeKey: 'cc_knowledge_sidebar_mode', panelKey: 'knowledgeList' },
});
export const personasPanels = createPanelZones('personas', {
  defaultZones: { left: [['personasList']] },
  legacySidebar: { modeKey: 'cc_personas_sidebar_mode', panelKey: 'personasList' },
});
export const projectsPanels = createPanelZones('projects', {
  defaultZones: { left: [['projectGroups']] },
  legacySidebar: { modeKey: 'cc_projects_sidebar_mode', panelKey: 'projectGroups' },
});
