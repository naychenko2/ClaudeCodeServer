// Раскладка режима «Обзор»: холст показывает не типы, а ГРУППЫ неймспейсов —
// отвечает на вопрос «как устроен проект», а не «что вокруг этого типа» (это
// вопрос «Фокуса», см. graphFocus.ts). Группы разложены по слоям зависимостей.
//
// Слои — ФИКСИРОВАННЫЙ маппинг по именам неймспейсов (Tests → точки входа →
// Services → Models/Protocol → прочее), а НЕ топологический ранг: у нас иерархия
// уже задана явно неймспейсами, а ранг по графу неустойчив — одно новое ребро
// переставляет группы местами и «дёргает» картинку между сборками (риск, который
// сама Майя записала в docs/mockups/code-graph-scale.md). Внутри слоя — сортировка
// по размеру группы, затем по имени: перестановки только локальные.
//
// Модуль чистый: без Math.random и force-симуляции — раскладка воспроизводима,
// как в graphFocus.ts.
import type { CodeGraph, CodeGraphNode, CodeGraphRelation } from '../../types';
import { graphDegree, isTestSourceFile, nodeLanguage, type NodeLanguage } from './graphFocus';
import { FOCUS_VIEW_W, FOCUS_VIEW_H, FOCUS_VIEW_W_MOBILE, FOCUS_VIEW_H_MOBILE } from './graphFocus';

// Размеры холста «Обзора» — те же, что у «Фокуса»: обоим нужна горизонталь под
// несколько колонок/слоёв, заводить отдельную пару констант незачем.
export const OVERVIEW_VIEW_W = FOCUS_VIEW_W;
export const OVERVIEW_VIEW_H = FOCUS_VIEW_H;
export const OVERVIEW_VIEW_W_MOBILE = FOCUS_VIEW_W_MOBILE;
export const OVERVIEW_VIEW_H_MOBILE = FOCUS_VIEW_H_MOBILE;
// Превью в панели рельсы — третий формат, а не «мобильный поменьше»: мобильный
// холст вытянут вверх (390×620), и в широкой невысокой полосе панели он вписался бы
// по высоте, заняв треть ширины. Здесь пропорции обратные — карта слоёв поперёк.
export const OVERVIEW_VIEW_W_PANEL = 340;
export const OVERVIEW_VIEW_H_PANEL = 250;

// Формат холста «Обзора». Влияет на размеры viewBox, плотность ряда и радиусы —
// втроём они и делают раскладку читаемой на своём носителе.
export type OverviewSize = 'desktop' | 'mobile' | 'panel';

interface SizeSpec {
  viewW: number;
  viewH: number;
  maxPerLine: number;
  // Потолок радиуса: узла-типа и группы соответственно, плюс множитель роста группы
  nodeRMax: number;
  groupRMax: number;
  groupRScale: number;
  // Запас снизу: подпись узла рисуется ПОД кружком, и без него нижний ряд обрезался
  bottomMargin: number;
}

const SIZE_SPECS: Record<OverviewSize, SizeSpec> = {
  desktop: { viewW: OVERVIEW_VIEW_W, viewH: OVERVIEW_VIEW_H, maxPerLine: 6, nodeRMax: 22, groupRMax: 36, groupRScale: 3.4, bottomMargin: 14 },
  mobile: { viewW: OVERVIEW_VIEW_W_MOBILE, viewH: OVERVIEW_VIEW_H_MOBILE, maxPerLine: 3, nodeRMax: 18, groupRMax: 26, groupRScale: 2.4, bottomMargin: 14 },
  panel: { viewW: OVERVIEW_VIEW_W_PANEL, viewH: OVERVIEW_VIEW_H_PANEL, maxPerLine: 3, nodeRMax: 12, groupRMax: 17, groupRScale: 1.7, bottomMargin: 18 },
};

// Фиксированный порядок слоёв сверху вниз. Индекс — «глубина»: чем больше,
// тем ниже на холсте. Порядок проверки важен: `Tests` идёт первым, потому что
// тестовые неймспейсы часто зеркалят структуру (ClaudeHomeServer.Tests.Controllers.*)
// и без приоритета осели бы в слое точек входа.
//
// Состав слоя задаётся РОЛЬЮ неймспейса в потоке зависимостей (кто кого зовёт), а не
// тем, «сервис это или нет». Точка входа — то, у чего нет входящих связей из своего же
// кода: снаружи её дёргает не наш тип, а HTTP-запрос, SignalR-хаб или клик по трею.
// Отсюда `WebDav` (HTTP-handler: 0 входящих, 8 исходящих в Services/Models), `Tray`
// (UI трея) и `Filters` (ASP.NET-фильтры) — тот же слой, что Controllers/Hubs; иначе
// они падают в «Прочее» (самый низ) и нормальная зависимость сверху вниз выглядит как
// нарушение слоистости, а настоящее нарушение (Services.UserStore → WebDav.NtlmHelper)
// теряется. `Telemetry` и `ConPtyBridge` остаются в «Прочем» намеренно: это сквозная
// инфраструктура, её честнее держать внизу.
const LAYER_KEYWORDS: readonly string[][] = [
  ['Tests'],
  ['Controllers', 'Hubs', 'WebDav', 'Tray', 'Filters'],
  ['Services'],
  ['Models', 'Protocol'],
];
export const OTHER_LAYER = LAYER_KEYWORDS.length;
export const LAYER_COUNT = LAYER_KEYWORDS.length + 1;
export const LAYER_TITLES = ['Тесты', 'Точки входа', 'Services', 'Models / Protocol', 'Прочее'];

// Слой группы по фиксированному маппингу: первое совпадение сегмента пути с
// набором ключевых слов слоя. Ни одно совпадение — «прочее» (последний слой).
export function layerOf(group: string): number {
  const segs = group.split('.');
  for (let i = 0; i < LAYER_KEYWORDS.length; i++) {
    if (LAYER_KEYWORDS[i].some(kw => segs.includes(kw))) return i;
  }
  return OTHER_LAYER;
}

// Множество FQN всех узлов графа — по нему `namespaceOf` отличает неймспейс от
// внешнего типа (см. ниже). Строится один раз на сцену, а не на каждый узел.
export function fqnIndex(nodes: readonly CodeGraphNode[]): ReadonlySet<string> {
  return new Set(nodes.map(n => n.fullyQualifiedName));
}

// Неймспейс узла = FQN до последней точки, но у ВЛОЖЕННОГО типа так получается имя
// внешнего класса (`…Services.SessionManager.SessionEntry` → «неймспейс»
// `…Services.SessionManager`), и «Обзор» рисовал такой класс отдельной группой —
// на снимке прода это 72 фиктивные группы, которые вытесняли настоящие подгруппы
// за потолок плотности. Критерий отличия точный: если строка совпадает с FQN узла
// графа — это тип, а не неймспейс, поднимаемся на уровень выше (вложенность бывает
// и двойной, поэтому цикл).
export function namespaceOf(node: CodeGraphNode, fqns: ReadonlySet<string>): string {
  let ns = node.fullyQualifiedName;
  for (;;) {
    const i = ns.lastIndexOf('.');
    if (i < 0) return '';
    ns = ns.slice(0, i);
    if (!fqns.has(ns)) return ns;
  }
}

// Цепочка префиксов неймспейса узла ПОСЛЕ автоматически раскрытого корня — группы,
// которые нужно раскрыть, чтобы «Обзор» показал этот узел индивидуальным типом (сквозной
// вход в «Фокус» минуя ручное раскрытие: поиск, god-список). Последний элемент — лист:
// группа, которую нужно раскрыть до уровня типов (typesGroup), остальные — уровни expand.
export function pathToType(node: CodeGraphNode, fqns: ReadonlySet<string>, autoExpanded: ReadonlySet<string>): string[] {
  const ns = namespaceOf(node, fqns);
  if (!ns) return [];
  const parts = ns.split('.');
  const groups: string[] = [];
  let g = '';
  for (const seg of parts) {
    g = g ? `${g}.${seg}` : seg;
    if (!autoExpanded.has(g)) groups.push(g);
  }
  return groups;
}

// Доминирующий префикс всех неймспейсов раскрыт всегда: единственная группа-обёртка
// на весь проект (например «ClaudeHomeServer») сама по себе бесполезна — раскрываем
// её автоматически, чтобы первый экран сразу показывал содержательные группы.
// Порог — а не строгое единогласие: горстка типов вне общей сборки (утилитный
// неймспейс, глобальный тип без пространства имён) не должна блокировать раскрытие
// префикса, которому принадлежит подавляющее большинство кода.
const ROOT_DOMINANCE = 0.9;

export function defaultExpandedGroups(nodes: CodeGraphNode[]): Set<string> {
  const expanded = new Set<string>();
  const fqns = fqnIndex(nodes);
  let prefix = '';
  for (;;) {
    const counts = new Map<string, number>();
    let considered = 0;
    for (const n of nodes) {
      const ns = namespaceOf(n, fqns);
      if (prefix && ns !== prefix && !ns.startsWith(`${prefix}.`)) continue; // вне текущего префикса
      const rest = prefix ? ns.slice(prefix.length + 1) : ns;
      const seg = rest.split('.')[0];
      if (!seg) continue;   // узел лежит ровно в prefix — лист, не голосует за более глубокий сегмент
      const next = prefix ? `${prefix}.${seg}` : seg;
      counts.set(next, (counts.get(next) ?? 0) + 1);
      considered++;
    }
    if (considered === 0) break;
    const [topPrefix, topCount] = [...counts.entries()].sort((a, b) => b[1] - a[1])[0];
    if (topCount / considered < ROOT_DOMINANCE) break;
    prefix = topPrefix;
    expanded.add(prefix);
  }
  return expanded;
}

// Группа узла = самый длинный раскрытый префикс неймспейса + ещё один сегмент
function groupOf(ns: string, expanded: ReadonlySet<string>): string {
  if (!ns) return '(без пространства имён)';
  const parts = ns.split('.');
  let g = parts[0];
  let k = 1;
  while (expanded.has(g) && k < parts.length) { g += '.' + parts[k]; k++; }
  return g;
}

// Насколько глубоко раскрыта ветка, которой принадлежит группа: длина (в сегментах)
// самого длинного раскрытого префикса группы. Общий корень раскрыт всегда, поэтому
// глубина 1 — обычный верхний уровень, а всё, что больше, пользователь раскрыл сам.
function expandDepth(group: string, expanded: ReadonlySet<string>): number {
  const parts = group.split('.');
  let depth = 0;
  let p = '';
  for (let i = 0; i < parts.length; i++) {
    p = i === 0 ? parts[0] : `${p}.${parts[i]}`;
    if (expanded.has(p)) depth = i + 1;
  }
  return depth;
}

export type OverviewItemKind = 'group' | 'node' | 'rest' | 'small';

export interface OverviewItem {
  key: string;
  kind: OverviewItemKind;
  label: string;
  group: string | null;    // группа-владелец (для node/rest — раскрытая до типов группа)
  layer: number;
  count: number;           // сколько типов внутри элемента
  godCount: number;
  nodeIds: string[];
  node?: CodeGraphNode;    // только для kind === 'node'
  degree?: number;         // только для kind === 'node' — связность конкретного типа
  hasChildren: boolean;    // группа раскрывается ещё на уровень глубже (для kind === 'group')
}

export interface OverviewBundle {
  fromKey: string;
  toKey: string;
  weight: number;
  byRelation: Record<CodeGraphRelation, number>;
  isBack: boolean;   // нарушение слоистости: источник лежит НИЖЕ приёмника по фиксированному порядку
}

export interface OverviewScene {
  items: OverviewItem[];
  byKey: Map<string, OverviewItem>;
  bundles: OverviewBundle[];
  hiddenTestCount: number;
  shownEdgeCount: number;
  totalTypeCount: number;
}

export interface OverviewOptions {
  expanded: ReadonlySet<string>;
  typesGroup: string | null;
  hideTests?: boolean;
  filters?: Record<CodeGraphRelation, boolean>;   // те же чипы связей, что у «Фокуса» — панель общая
  languages?: Record<NodeLanguage, boolean>;       // языковой фильтр, по умолчанию оба включены
  maxItems?: number;     // потолок элементов на холсте (плотность не зависит от размера репо)
  typesLimit?: number;   // топ-N типов при раскрытии листа до типов (мобила — меньше)
}

const MAX_ITEMS_DEFAULT = 26;
const TYPES_LIMIT_DEFAULT = 30;

export function buildOverviewScene(graph: CodeGraph, opts: OverviewOptions): OverviewScene {
  const { expanded, typesGroup, hideTests, maxItems = MAX_ITEMS_DEFAULT, typesLimit = TYPES_LIMIT_DEFAULT } = opts;
  const langs = opts.languages ?? { csharp: true, typescript: true };
  const godSet = new Set(graph.godNodes);
  const degree = graphDegree(graph);
  // Индекс по ВСЕМ узлам, а не по видимым: скрытие тестов не должно превращать
  // внешний тип в «неймспейс» для своих же вложенных
  const fqns = fqnIndex(graph.nodes);

  let hiddenTestCount = 0;
  const visible: CodeGraphNode[] = [];
  for (const n of graph.nodes) {
    if (hideTests && isTestSourceFile(n.sourceFile)) { hiddenTestCount++; continue; }
    if (!langs[nodeLanguage(n.sourceFile)]) continue;
    visible.push(n);
  }

  const items = new Map<string, OverviewItem>();
  const nodeItem = new Map<string, string>();        // nodeId -> item key
  const groupHasDeeper = new Map<string, boolean>();  // группа -> есть ли типы глубже текущего уровня

  for (const node of visible) {
    const ns = namespaceOf(node, fqns);
    const g = groupOf(ns, expanded);
    if (ns.length > g.length) groupHasDeeper.set(g, true);

    let key: string; let kind: OverviewItemKind; let label: string;
    if (typesGroup && g === typesGroup) {
      key = `n:${node.id}`; kind = 'node'; label = node.label;
    } else {
      key = `g:${g}`; kind = 'group'; label = g.split('.').pop() ?? g;
    }
    let item = items.get(key);
    if (!item) {
      item = {
        key, kind, label, group: g, layer: layerOf(g),
        count: 0, godCount: 0, nodeIds: [],
        node: kind === 'node' ? node : undefined,
        degree: kind === 'node' ? (degree.get(node.id) ?? 0) : undefined,
        hasChildren: false,
      };
      items.set(key, item);
    }
    item.count++;
    item.nodeIds.push(node.id);
    if (godSet.has(node.id)) item.godCount++;
    nodeItem.set(node.id, key);
  }

  for (const item of items.values()) {
    if (item.kind === 'group') item.hasChildren = !!groupHasDeeper.get(item.group!);
  }

  // Раскрытая до типов группа: топ-N по связности остаются узлами, остальное —
  // одна заглушка «+N прочих» (лист графа не менее читаем, чем группы верхнего уровня)
  if (typesGroup) {
    const own = [...items.values()].filter(it => it.kind === 'node')
      .sort((a, b) => (b.degree ?? 0) - (a.degree ?? 0) || a.label.localeCompare(b.label));
    const cut = own.slice(typesLimit);
    if (cut.length) {
      for (const it of cut) items.delete(it.key);
      const restKey = `r:${typesGroup}`;
      const restIds = cut.flatMap(it => it.nodeIds);
      items.set(restKey, {
        key: restKey, kind: 'rest', label: `+${cut.length} прочих`, group: typesGroup,
        layer: layerOf(typesGroup), count: cut.length,
        godCount: cut.reduce((s, it) => s + it.godCount, 0), nodeIds: restIds, hasChildren: false,
      });
      for (const id of restIds) nodeItem.set(id, restKey);
    }
  }

  // Потолок плотности холста: сколько бы уровней ни было раскрыто, элементов не
  // больше maxItems. Мелкие группы сворачиваются в одну заглушку — читаемость
  // холста не зависит от размера репозитория.
  const groupsList = [...items.values()].filter(it => it.kind === 'group');
  if (items.size > maxItems && groupsList.length > 4) {
    const nonGroupCount = items.size - groupsList.length;
    // -1: место самой заглушки тоже входит в потолок
    const keepCount = Math.max(4, maxItems - nonGroupCount - 1);
    const bySize = (a: OverviewItem, b: OverviewItem) => b.count - a.count || a.label.localeCompare(b.label);

    // Отбор только по размеру схлопывал ровно то, ради чего пользователь кликал:
    // подгруппы раскрытой ветки мелкие рядом с группами верхнего уровня и уезжали
    // в заглушку — раскрытие выглядело неработающим. Поэтому места сначала уходят
    // раскрытым веткам (чем глубже раскрыта, тем раньше), а под верхний уровень
    // остаётся резерв — без него карта слоёв исчезала бы целиком.
    const depth = new Map(groupsList.map(it => [it.key, expandDepth(it.group!, expanded)]));
    const byDepthThenSize = (a: OverviewItem, b: OverviewItem) =>
      depth.get(b.key)! - depth.get(a.key)! || bySize(a, b);
    const reserve = Math.min(groupsList.length - 1, Math.round(keepCount / 3));
    const keep = new Set([...groupsList].sort(byDepthThenSize)
      .slice(0, Math.max(1, keepCount - reserve)).map(it => it.key));
    for (const it of [...groupsList].sort(bySize)) {
      if (keep.size >= keepCount) break;
      keep.add(it.key);
    }
    const drop = groupsList.filter(it => !keep.has(it.key));
    if (drop.length > 1) {
      for (const it of drop) items.delete(it.key);
      const nodeIds = drop.flatMap(it => it.nodeIds);
      // Слой заглушки — тот, что вобрал больше всего свёрнутых типов
      const byLayer = new Map<number, number>();
      for (const it of drop) byLayer.set(it.layer, (byLayer.get(it.layer) ?? 0) + it.count);
      const dominant = [...byLayer.entries()].sort((a, b) => b[1] - a[1])[0][0];
      const smallKey = 's:small';
      items.set(smallKey, {
        key: smallKey, kind: 'small', label: `+${drop.length} мелких групп`, group: null,
        layer: dominant, count: nodeIds.length,
        godCount: drop.reduce((s, it) => s + it.godCount, 0), nodeIds, hasChildren: false,
      });
      for (const id of nodeIds) nodeItem.set(id, smallKey);
    }
  }

  // Агрегация рёбер между элементами сцены — пучки, а не отдельные линии.
  // Ребро «снизу вверх» (нарушение фиксированной слоистости) помечается isBack.
  const bundleMap = new Map<string, OverviewBundle>();
  let shownEdgeCount = 0;
  for (const e of graph.edges) {
    if (opts.filters && !opts.filters[e.relation]) continue;
    const a = nodeItem.get(e.source);
    const b = nodeItem.get(e.target);
    if (!a || !b) continue;
    shownEdgeCount++;
    if (a === b) continue;
    const k = `${a}>${b}`;
    let bu = bundleMap.get(k);
    if (!bu) {
      const fromLayer = items.get(a)!.layer;
      const toLayer = items.get(b)!.layer;
      bu = { fromKey: a, toKey: b, weight: 0, byRelation: { Calls: 0, Implements: 0, References: 0 }, isBack: fromLayer > toLayer };
      bundleMap.set(k, bu);
    }
    bu.weight++;
    bu.byRelation[e.relation]++;
  }

  return {
    items: [...items.values()],
    byKey: items,
    bundles: [...bundleMap.values()],
    hiddenTestCount,
    shownEdgeCount,
    // Знаменатель «N типов свёрнуты» — про ВИДИМЫЕ типы (после языка и тестов),
    // иначе при выключенном C# число завышено на размер скрытого языка (M3)
    totalTypeCount: visible.length,
  };
}

// Толщина пучка — по логарифму веса (иначе god-связка в сотни рёбер утопит остальные)
export function bundleWidth(weight: number): number {
  return Math.max(1, Math.min(9, 1 + Math.log2(Math.max(1, weight)) * 1.5));
}

export interface OverviewPlacedItem {
  key: string;
  x: number;
  y: number;
  r: number;
  row: number;   // индекс визуального ряда (среди занятых слоёв, не индекс слоя)
}

export interface OverviewLayoutRow {
  layer: number;
  title: string;
  y0: number;
  y1: number;
}

export interface OverviewLayout {
  viewW: number;
  viewH: number;
  mobile: boolean;
  // Формат холста — по нему отрисовка решает, что показывать: в панельной мини-карте
  // подписи слоёв съедали бы левый край и налезали на кружки
  size: OverviewSize;
  positions: Map<string, OverviewPlacedItem>;
  rows: OverviewLayoutRow[];
}

// Раскладка по слоям: детерминированная, без Math.random и force-симуляции.
// Занятые слои сжимаются в ряды подряд (пустых слоёв не бывает — не тратим место
// на слой, в котором сейчас ничего не раскрыто).
export function layoutOverview(scene: OverviewScene, opts: { size?: OverviewSize } = {}): OverviewLayout {
  const size = opts.size ?? 'desktop';
  const spec = SIZE_SPECS[size];
  const { viewW, viewH } = spec;
  // Компактный — всё, что уже десктопа: холст рисует по нему длину подписей
  const mobile = size !== 'desktop';

  const byLayer = new Map<number, OverviewItem[]>();
  for (const it of scene.items) {
    const list = byLayer.get(it.layer);
    if (list) list.push(it); else byLayer.set(it.layer, [it]);
  }
  const occupied = [...byLayer.keys()].sort((a, b) => a - b);
  // Подписи слоёв рисуются у верхней кромки ряда; в панельном формате их нет —
  // и верхний запас там не нужен
  const topMargin = size === 'panel' ? 12 : 28;
  const rowH = (viewH - topMargin - spec.bottomMargin) / Math.max(occupied.length, 1);
  const maxPerLine = spec.maxPerLine;

  const positions = new Map<string, OverviewPlacedItem>();
  const rows: OverviewLayoutRow[] = [];

  occupied.forEach((layerIdx, rowIdx) => {
    // Внутри слоя: по размеру группы, затем по имени — перестановки только локальные
    const row = [...byLayer.get(layerIdx)!].sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
    const lines = Math.max(1, Math.ceil(row.length / maxPerLine));
    const perLine = Math.ceil(row.length / lines);
    const lineH = rowH / lines;
    const y0 = topMargin + rowIdx * rowH;
    row.forEach((it, i) => {
      const line = Math.floor(i / perLine);
      const inLineCount = Math.min(perLine, row.length - line * perLine);
      const j = i - line * perLine;
      const step = viewW / (inLineCount + 1);
      const r = it.kind === 'node'
        ? Math.max(11, Math.min(spec.nodeRMax, 11 + (it.degree ?? 0) / 6))
        : Math.max(13, Math.min(spec.groupRMax, 11 + Math.sqrt(it.count) * spec.groupRScale));
      positions.set(it.key, {
        key: it.key,
        x: step * (j + 1),
        y: y0 + line * lineH + lineH / 2,
        r,
        row: rowIdx,
      });
    });
    rows.push({ layer: layerIdx, title: LAYER_TITLES[layerIdx] ?? 'Прочее', y0, y1: y0 + rowH });
  });

  return { viewW, viewH, mobile, size, positions, rows };
}
