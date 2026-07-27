// Проверка детерминированной раскладки графа: позиции конечны, god-узлы
// в центре и крупнее, степени связности считаются, раскладка воспроизводима.
import { describe, it, expect } from 'vitest';
import { layoutGraph, VIEW_W, VIEW_H } from '../graphLayout';
import type { CodeGraph } from '../../../types';

function makeGraph(): CodeGraph {
  return {
    nodes: [
      { id: 'g1', label: 'Registry', fullyQualifiedName: 'A.Registry', sourceFile: 'A.cs', sourceLocation: '1:1', kind: 'Class' },
      { id: 'g2', label: 'Session', fullyQualifiedName: 'A.Session', sourceFile: 'S.cs', sourceLocation: '1:1', kind: 'Class' },
      { id: 'n1', label: 'IRunner', fullyQualifiedName: 'A.IRunner', sourceFile: 'I.cs', sourceLocation: '1:1', kind: 'Interface' },
      { id: 'n2', label: 'Tier', fullyQualifiedName: 'A.Tier', sourceFile: 'T.cs', sourceLocation: '1:1', kind: 'Enum' },
    ],
    edges: [
      { source: 'g1', target: 'n1', relation: 'Implements', confidence: 'Extracted' },
      { source: 'g1', target: 'n2', relation: 'References', confidence: 'Inferred' },
      { source: 'g2', target: 'g1', relation: 'Calls', confidence: 'Extracted' },
    ],
    godNodes: ['g1', 'g2'],
    metadata: { nodeCount: 4, edgeCount: 3, fileCount: 4, isStale: false },
  };
}

describe('layoutGraph', () => {
  it('даёт конечные координаты в пределах холста', () => {
    const l = layoutGraph(makeGraph());
    for (const ln of l.nodes) {
      expect(Number.isFinite(ln.x)).toBe(true);
      expect(Number.isFinite(ln.y)).toBe(true);
      expect(ln.x).toBeGreaterThanOrEqual(0);
      expect(ln.x).toBeLessThanOrEqual(VIEW_W);
      expect(ln.y).toBeGreaterThanOrEqual(0);
      expect(ln.y).toBeLessThanOrEqual(VIEW_H);
    }
  });

  it('god-узлы — в центре и крупнее обычных', () => {
    const l = layoutGraph(makeGraph());
    const god = l.nodes.filter(n => n.isGod);
    const rest = l.nodes.filter(n => !n.isGod);
    expect(god).toHaveLength(2);
    expect(rest).toHaveLength(2);
    const cx = VIEW_W / 2, cy = VIEW_H / 2;
    // god ближе к центру, чем любой обычный узел
    for (const g of god) {
      const dg = Math.hypot(g.x - cx, g.y - cy);
      for (const r of rest) {
        const dr = Math.hypot(r.x - cx, r.y - cy);
        expect(dg).toBeLessThan(dr);
      }
    }
    // god крупнее
    for (const g of god) for (const r of rest) expect(g.r).toBeGreaterThan(r.r);
  });

  it('степень узла = сумма инцидентных рёбер', () => {
    const l = layoutGraph(makeGraph());
    expect(l.degree.get('g1')).toBe(3); // 2 исходящих + 1 входящее
    expect(l.degree.get('g2')).toBe(1);
    expect(l.degree.get('n1')).toBe(1);
  });

  it('раскладка воспроизводима (детерминирована)', () => {
    const g = makeGraph();
    const a = layoutGraph(g).nodes.map(n => [n.x, n.y].join(',')).join('|');
    const b = layoutGraph(g).nodes.map(n => [n.x, n.y].join(',')).join('|');
    expect(a).toBe(b);
  });

  it('byId покрывает все узлы', () => {
    const l = layoutGraph(makeGraph());
    for (const n of makeGraph().nodes) expect(l.byId.has(n.id)).toBe(true);
  });
});
