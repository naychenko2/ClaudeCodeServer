// Детерминированная радиальная раскладка графа без внешних библиотек.
// god-узлы (точки перегруза) — в центре/на малом кругу (фокус взгляда), остальные
// типы — равномерно по внешнему кольцу, отсортированные по FQN (стабильно).
// Никакого force-simulation и Math.random: позиции не «прыгают» между рендерами,
// раскладка воспроизводима и дёшева (O(n)). Размер узла растёт со степенью связности.
import type { CodeGraph, CodeGraphNode } from '../../types';

export interface LayoutNode {
  node: CodeGraphNode;
  x: number;
  y: number;
  r: number;       // радиус кружка
  isGod: boolean;
}

export interface GraphLayout {
  nodes: LayoutNode[];
  byId: Map<string, LayoutNode>;
  degree: Map<string, number>;   // суммарное число инцидентных рёбер (для god-списка)
}

// Размеры виртуального холста (viewBox SVG). preserveAspectRatio вписывает его в контейнер.
export const VIEW_W = 780;
export const VIEW_H = 480;
const CX = VIEW_W / 2;
const CY = VIEW_H / 2;

const GOD_R = 24;       // радиус god-узла (крупнее обычного)
const ORD_BASE = 14;    // базовый радиус обычного узла
const ORD_MAX_BONUS = 8; // потолок прибавки за степень
const RING_GOD = 62;    // радиус малого круга god-узлов (когда их > 1)
const RING_ORD = 182;   // радиус внешнего кольца остальных типов

export function layoutGraph(graph: CodeGraph): GraphLayout {
  const godSet = new Set(graph.godNodes);

  // Степень узла = число рёбер, где он источник или приёмник
  const degree = new Map<string, number>();
  for (const e of graph.edges) {
    degree.set(e.source, (degree.get(e.source) ?? 0) + 1);
    degree.set(e.target, (degree.get(e.target) ?? 0) + 1);
  }

  const gods = graph.nodes
    .filter(n => godSet.has(n.id))
    .sort((a, b) => a.fullyQualifiedName.localeCompare(b.fullyQualifiedName));
  const rest = graph.nodes
    .filter(n => !godSet.has(n.id))
    .sort((a, b) => a.fullyQualifiedName.localeCompare(b.fullyQualifiedName));

  const out: LayoutNode[] = [];
  const place = (list: CodeGraphNode[], isGod: boolean, radius: number, startAngle: number) => {
    const n = list.length;
    if (n === 0) return;
    list.forEach((node, i) => {
      // Один узел в группе — в центре; иначе равномерно по кругу от стартового угла
      const angle = n === 1 ? startAngle : startAngle + (2 * Math.PI * i) / n;
      const r = isGod ? GOD_R : Math.min(ORD_BASE + ORD_MAX_BONUS, ORD_BASE + Math.floor((degree.get(node.id) ?? 0) / 2));
      out.push({
        node,
        x: CX + radius * Math.cos(angle),
        y: CY + radius * Math.sin(angle),
        r,
        isGod,
      });
    });
  };

  place(gods, true, gods.length > 1 ? RING_GOD : 0, -Math.PI / 2);
  place(rest, false, RING_ORD, -Math.PI / 2);

  const byId = new Map(out.map(ln => [ln.node.id, ln]));
  return { nodes: out, byId, degree };
}
