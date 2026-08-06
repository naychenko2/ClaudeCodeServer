// Раскладка «Фокуса»: окрестность ОДНОГО типа — единственный способ увидеть тип
// в деталях (полного графа на 1020 узлах, где шаг между соседями кольца 1.3 px, больше
// нет — навигация приводит сюда из «Обзора», см. lib/codeGraph.ts). Холст показывает
// только окружение узла: центр, слева — кто зависит от него, справа — от кого зависит
// он. Хвост сверх лимита уходит в заглушку «+N» и раскрывается списком в панели.
//
// Модуль чистый: считает координаты и ничего не знает про цвета и React —
// цвет ребра компонент берёт по relation из graphTokens.
// Детерминизм: никакого Math.random и force-симуляции.
import type { CodeGraph, CodeGraphNode, CodeGraphEdge, CodeGraphRelation } from '../../types';

export type FocusSide = 'in' | 'out';

export interface FocusNeighbour {
  node: CodeGraphNode;
  relations: CodeGraphRelation[];   // типы связей с центром (уникальные, отсортированные)
  weight: number;                   // сколько рёбер связывает соседа с центром
  degree: number;                   // общая связность соседа (для сортировки и размера)
}

export interface FocusPlacedNode {
  node: CodeGraphNode;
  x: number;
  y: number;
  r: number;
  side: FocusSide | 'center';
  second: boolean;      // узел второго кольца (глубина 2)
  isGod: boolean;
  label: string;        // подпись, уже обрезанная под ширину холста
}

export interface FocusEdgeShape {
  x1: number; y1: number;
  x2: number; y2: number;
  cx: number; cy: number;                 // контрольная точка квадратичной кривой
  relation: CodeGraphRelation | null;     // null — ребро второго кольца (пунктир, приглушённое)
  width: number;
}

export interface FocusStub {
  side: FocusSide;
  hidden: number;   // сколько соседей не поместилось
  x: number;
  y: number;
}

export interface FocusModel {
  center: CodeGraphNode;
  centerDegree: number;
  mobile: boolean;
  viewW: number;
  viewH: number;
  limit: number;                    // сколько соседей помещается на сторону
  incoming: FocusNeighbour[];       // ВСЕ входящие (не только показанные) — для списка хвоста
  outgoing: FocusNeighbour[];
  nodes: FocusPlacedNode[];
  edges: FocusEdgeShape[];
  stubs: FocusStub[];
  shownCount: number;               // сколько узлов реально на холсте (вместе с центром)
  secondShown: number;              // узлов второго кольца показано
  secondTotal: number;              // узлов второго кольца всего (сколько скрыто = total - shown)
}

export interface FocusOptions {
  filters?: Record<CodeGraphRelation, boolean>;
  hideTests?: boolean;
  degree?: Map<string, number>;
}

// Размеры виртуального холста фокуса. Десктоп шире полного графа (780×480):
// две колонки соседей с вынесенными наружу подписями требуют горизонтали.
export const FOCUS_VIEW_W = 980;
export const FOCUS_VIEW_H = 560;
// Мобила: раскладка разворачивается вертикально (входящие сверху, исходящие снизу),
// иначе десктопная пропорция ужимает узлы до нечитаемых 5px на 390px экрана.
export const FOCUS_VIEW_W_MOBILE = 390;
export const FOCUS_VIEW_H_MOBILE = 620;
// Карта в панели рельсы: раскладка та же вертикальная, что на мобиле, но полоса
// широкая и низкая — иначе холст вписался бы по высоте и занял треть ширины
export const FOCUS_VIEW_W_PANEL = 340;
export const FOCUS_VIEW_H_PANEL = 250;

// Лимит соседей на сторону: 16 + 16 = «~32 соседа на экран» из спеки Майи
export const FOCUS_LIMIT = 16;
export const FOCUS_LIMIT_MOBILE = 6;
export const FOCUS_LIMIT_PANEL = 4;

// Глубина 2: второе кольцо строим только для самых связанных соседей — полная
// окрестность глубины 2 у SessionManager это 471 узел, то есть снова каша
const D2_HOSTS = 6;
const D2_PER_HOST = 2;
const D2_PER_SIDE = 6;          // потолок узлов второго кольца на сторону
const D2_PER_SIDE_MOBILE = 2;

const CENTER_R = 30;
const SECOND_R = 10;
// Узел второго кольца стоит у самого края холста, подпись у него под кружком —
// длинную она бы вылезла за границу
const SECOND_MAX_LABEL = 11;

// Тестовые типы: единственная точка признака «скрыть тесты» — им пользуются и холст,
// и счётчик скрытого в панели. Альтернатива `\.Tests[\\/]` обязательна: в .NET тестовый
// проект — это СЕГМЕНТ пути вида `ClaudeHomeServer.Tests/`, где точка слева, а слэш
// справа, поэтому ни `[\\/]Tests[\\/]`, ни `\.Tests\.` его не ловят.
export function isTestSourceFile(file: string): boolean {
  return /[\\/]Tests[\\/]|\.Tests[\\/]|[\\/]test[\\/]|[\\/]__tests__[\\/]|\.Tests\./i.test(file);
}

export function graphDegree(graph: CodeGraph): Map<string, number> {
  const degree = new Map<string, number>();
  for (const e of graph.edges) {
    degree.set(e.source, (degree.get(e.source) ?? 0) + 1);
    degree.set(e.target, (degree.get(e.target) ?? 0) + 1);
  }
  return degree;
}

// Соседи узла с одной стороны: уникальные типы + их связи, отсортированные
// по связности (сначала самые нагруженные — их полезнее видеть первыми).
export function focusNeighbours(
  graph: CodeGraph,
  centerId: string,
  side: FocusSide,
  opts: FocusOptions = {},
): FocusNeighbour[] {
  const degree = opts.degree ?? graphDegree(graph);
  const byId = new Map(graph.nodes.map(n => [n.id, n]));
  const acc = new Map<string, { node: CodeGraphNode; rels: Set<CodeGraphRelation>; weight: number }>();

  const accept = (e: CodeGraphEdge) => !opts.filters || opts.filters[e.relation];

  for (const e of graph.edges) {
    if (!accept(e)) continue;
    const otherId = side === 'in'
      ? (e.target === centerId ? e.source : null)
      : (e.source === centerId ? e.target : null);
    if (!otherId || otherId === centerId) continue;
    const node = byId.get(otherId);
    if (!node) continue;
    if (opts.hideTests && isTestSourceFile(node.sourceFile)) continue;
    let o = acc.get(otherId);
    if (!o) { o = { node, rels: new Set(), weight: 0 }; acc.set(otherId, o); }
    o.rels.add(e.relation);
    o.weight++;
  }

  return [...acc.values()]
    .map(o => ({
      node: o.node,
      relations: [...o.rels].sort(),
      weight: o.weight,
      degree: degree.get(o.node.id) ?? 0,
    }))
    // Стабильно: по связности, при равенстве — по FQN (позиции не прыгают между рендерами)
    .sort((a, b) => b.degree - a.degree || a.node.fullyQualifiedName.localeCompare(b.node.fullyQualifiedName));
}

// Обрезка подписи: на холсте нет места под полные FQN, но многоточие честнее,
// чем наложение подписей друг на друга
function clip(label: string, max: number): string {
  return label.length > max ? `${label.slice(0, max - 1)}…` : label;
}

export function buildFocusModel(
  graph: CodeGraph,
  centerId: string,
  opts: FocusOptions & { depth2?: boolean; mobile?: boolean; panel?: boolean } = {},
): FocusModel | null {
  const center = graph.nodes.find(n => n.id === centerId);
  if (!center) return null;

  const degree = opts.degree ?? graphDegree(graph);
  const godSet = new Set(graph.godNodes);
  // Панель наследует вертикальную раскладку мобилы (соседи сверху и снизу), но со
  // своими размерами холста и более жёстким лимитом соседей
  const panel = !!opts.panel;
  const mobile = panel || !!opts.mobile;
  const viewW = panel ? FOCUS_VIEW_W_PANEL : mobile ? FOCUS_VIEW_W_MOBILE : FOCUS_VIEW_W;
  const viewH = panel ? FOCUS_VIEW_H_PANEL : mobile ? FOCUS_VIEW_H_MOBILE : FOCUS_VIEW_H;
  const limit = panel ? FOCUS_LIMIT_PANEL : mobile ? FOCUS_LIMIT_MOBILE : FOCUS_LIMIT;
  const maxLabel = mobile ? 13 : 22;
  const nOpts: FocusOptions = { filters: opts.filters, hideTests: opts.hideTests, degree };

  const incoming = focusNeighbours(graph, centerId, 'in', nOpts);
  const outgoing = focusNeighbours(graph, centerId, 'out', nOpts);

  const CX = viewW / 2;
  const CY = viewH / 2;
  const placed = new Map<string, FocusPlacedNode>();
  const edges: FocusEdgeShape[] = [];

  const neighbourR = (deg: number) => Math.max(11, Math.min(15, 10 + deg / 24));

  // При глубине 2 колонки соседей подтягиваются к центру: внешняя полоса холста
  // отдаётся второму кольцу, иначе оно встаёт прямо на подписи первого
  const colRadius = opts.depth2 ? 172 : 238;
  const colBend = opts.depth2 ? 44 : 66;

  // Раскладка: на десктопе — две колонки с лёгким изгибом (середина дальше от
  // центра, края ближе: линии не пересекаются, подписи уходят наружу свободно),
  // на мобиле — сетка сверху (кто зависит) и снизу (от кого зависит он).
  const place = (list: FocusNeighbour[], side: FocusSide, hasTail: boolean) => {
    const n = Math.max(list.length, 1);
    list.forEach((o, i) => {
      let x: number, y: number;
      if (!mobile) {
        const mid = (n - 1) / 2;
        const t = mid === 0 ? 0 : (i - mid) / mid;                 // -1…1
        // С хвостом колонка ужимается: под ней ещё встанет заглушка «+N» с подписью
        const step = Math.min(34, (viewH - (hasTail ? 190 : 90)) / Math.max(n, 1));
        x = CX + (side === 'in' ? -1 : 1) * (colRadius + colBend * Math.cos((t * Math.PI) / 2));
        y = CY + (i - mid) * step;
      } else {
        const cols = Math.min(2, n);
        const col = i % cols;
        const row = Math.floor(i / cols);
        const rows = Math.ceil(n / cols);
        x = (viewW / (cols + 1)) * (col + 1);
        y = side === 'in' ? 64 + row * 72 : viewH - 64 - (rows - 1 - row) * 72;
      }
      placed.set(o.node.id, {
        node: o.node, x, y, r: neighbourR(o.degree), side, second: false,
        isGod: godSet.has(o.node.id), label: clip(o.node.label, maxLabel),
      });
    });
  };

  const inShow = incoming.slice(0, limit);
  const outShow = outgoing.slice(0, limit);
  place(inShow, 'in', incoming.length > limit);
  place(outShow, 'out', outgoing.length > limit);

  // Рёбра «сосед ↔ центр»: направление задано стороной, поэтому стрелки не нужны
  const edgeShape = (
    a: { x: number; y: number }, b: { x: number; y: number },
    relation: CodeGraphRelation | null, width: number,
  ): FocusEdgeShape => ({
    x1: a.x, y1: a.y, x2: b.x, y2: b.y,
    cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2 + (mobile ? 0 : 18),
    relation, width,
  });

  const weightOf = new Map<string, FocusNeighbour>();
  for (const o of [...inShow, ...outShow]) weightOf.set(o.node.id, o);
  placed.forEach(p => {
    const o = weightOf.get(p.node.id);
    if (!o) return;
    const a = p.side === 'in' ? p : { x: CX, y: CY };
    const b = p.side === 'in' ? { x: CX, y: CY } : p;
    edges.push(edgeShape(a, b, o.relations[0] ?? null, Math.min(4, 1.2 + o.weight * 0.5)));
  });

  // Второе кольцо: узлы уходят во внешнюю полосу холста (по краям), а не «вокруг
  // хоста» — иначе кружки садятся прямо на подписи первого кольца. На сторону —
  // жёсткий потолок, счётчик под холстом честно говорит, сколько осталось скрыто.
  let secondShown = 0;
  let secondTotal = 0;
  if (opts.depth2) {
    const hosts = [...placed.values()]
      .sort((a, b) => (degree.get(b.node.id) ?? 0) - (degree.get(a.node.id) ?? 0))
      .slice(0, D2_HOSTS);
    const shown = new Set<string>([centerId, ...placed.keys()]);
    const all = new Set<string>();
    const picks: { host: FocusPlacedNode; kid: FocusNeighbour }[] = [];
    for (const host of hosts) {
      const kids = [
        ...focusNeighbours(graph, host.node.id, 'out', nOpts),
        ...focusNeighbours(graph, host.node.id, 'in', nOpts),
      ].filter(o => !shown.has(o.node.id));
      for (const k of kids) all.add(k.node.id);
      for (const k of kids.slice(0, D2_PER_HOST)) picks.push({ host, kid: k });
    }
    secondTotal = all.size;

    const perSide = mobile ? D2_PER_SIDE_MOBILE : D2_PER_SIDE;
    for (const side of ['in', 'out'] as FocusSide[]) {
      const list = picks.filter(p => p.host.side === side).slice(0, perSide);
      const n = list.length;
      const mid = (n - 1) / 2;
      const step = mobile ? 56 : Math.min(70, (viewH - 140) / Math.max(n, 1));
      list.forEach((p, i) => {
        if (placed.has(p.kid.node.id)) return;
        const x = side === 'in' ? (mobile ? 54 : 62) : viewW - (mobile ? 54 : 62);
        const y = CY + (i - mid) * step;
        placed.set(p.kid.node.id, {
          node: p.kid.node, x, y, r: SECOND_R, side, second: true,
          isGod: godSet.has(p.kid.node.id), label: clip(p.kid.node.label, SECOND_MAX_LABEL),
        });
        edges.push(edgeShape(p.host, { x, y }, null, 1.2));
        secondShown++;
      });
    }
  }

  // Заглушки хвоста — под своей колонкой, чтобы не спорить с подписями соседей
  const stubs: FocusStub[] = [];
  const addStub = (list: FocusNeighbour[], side: FocusSide) => {
    const hidden = list.length - limit;
    if (hidden <= 0) return;
    const col = [...placed.values()].filter(p => p.side === side && !p.second);
    const x = mobile
      ? viewW - 54
      : (col.length ? col[col.length - 1].x : (side === 'in' ? 150 : viewW - 150));
    // Ниже viewH-56 нельзя: под кружком заглушки ещё идёт подпись «ещё в списке»
    const y = mobile
      ? (side === 'in' ? 40 : viewH - 56)
      : Math.min(viewH - 56, col.length ? Math.max(...col.map(p => p.y)) + 46 : CY);
    stubs.push({ side, hidden, x, y });
  };
  addStub(incoming, 'in');
  addStub(outgoing, 'out');

  const nodes: FocusPlacedNode[] = [
    ...placed.values(),
    {
      node: center, x: CX, y: CY, r: CENTER_R, side: 'center', second: false,
      isGod: godSet.has(centerId), label: center.label,
    },
  ];

  return {
    center,
    centerDegree: degree.get(centerId) ?? 0,
    mobile, viewW, viewH, limit,
    incoming, outgoing,
    nodes, edges, stubs,
    shownCount: nodes.length,
    secondShown, secondTotal,
  };
}
