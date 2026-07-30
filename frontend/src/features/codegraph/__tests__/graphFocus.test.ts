// Проверка раскладки режима «Фокус»: центр в середине холста, входящие слева,
// исходящие справа, лимит соседей на сторону с заглушкой хвоста, фильтр связей.
import { describe, it, expect } from 'vitest';
import { buildFocusModel, focusNeighbours, isTestSourceFile, FOCUS_LIMIT, FOCUS_VIEW_W, FOCUS_VIEW_H } from '../graphFocus';
import type { CodeGraph, CodeGraphNode, CodeGraphEdge } from '../../../types';

function node(id: string, file = `${id}.cs`): CodeGraphNode {
  return { id, label: id, fullyQualifiedName: `A.${id}`, sourceFile: file, sourceLocation: '1:1', kind: 'Class' };
}

// Центр c: 20 входящих (in0…in19) и 3 исходящих (out0…out2)
function makeGraph(): CodeGraph {
  const nodes: CodeGraphNode[] = [node('c')];
  const edges: CodeGraphEdge[] = [];
  for (let i = 0; i < 20; i++) {
    nodes.push(node(`in${i}`, i === 0 ? 'backend/Tests/InTest.cs' : `in${i}.cs`));
    edges.push({ source: `in${i}`, target: 'c', relation: 'References', confidence: 'Extracted' });
  }
  for (let i = 0; i < 3; i++) {
    nodes.push(node(`out${i}`));
    edges.push({ source: 'c', target: `out${i}`, relation: 'Calls', confidence: 'Extracted' });
  }
  return { nodes, edges, godNodes: ['c'], metadata: { nodeCount: nodes.length, edgeCount: edges.length, fileCount: nodes.length, isStale: false } };
}

const ALL = { Calls: true, Implements: true, References: true };

describe('isTestSourceFile', () => {
  it('ловит .NET-проект вида ClaudeHomeServer.Tests/ (точка слева, слэш справа)', () => {
    expect(isTestSourceFile('backend/ClaudeHomeServer.Tests/Services/Foo.cs')).toBe(true);
    expect(isTestSourceFile('backend\\ClaudeHomeServer.Tests\\Services\\Foo.cs')).toBe(true);
  });

  it('не считает тестом обычный код продукта', () => {
    expect(isTestSourceFile('backend/ClaudeHomeServer/Services/Foo.cs')).toBe(false);
    expect(isTestSourceFile('frontend/src/features/codegraph/graphFocus.ts')).toBe(false);
  });

  it('держит остальные раскладки тестов', () => {
    expect(isTestSourceFile('backend/Tests/InTest.cs')).toBe(true);
    expect(isTestSourceFile('src/__tests__/foo.test.ts')).toBe(true);
    expect(isTestSourceFile('src/test/foo.ts')).toBe(true);
    expect(isTestSourceFile('src/Some.Tests.Helpers/foo.cs')).toBe(true);
  });
});

describe('buildFocusModel', () => {
  it('центр в середине холста и крупнее соседей', () => {
    const m = buildFocusModel(makeGraph(), 'c', { filters: ALL })!;
    const center = m.nodes.find(n => n.side === 'center')!;
    expect(center.x).toBe(FOCUS_VIEW_W / 2);
    expect(center.y).toBe(FOCUS_VIEW_H / 2);
    for (const n of m.nodes.filter(n => n.side !== 'center')) {
      expect(n.r).toBeLessThan(center.r);
    }
  });

  it('входящие слева, исходящие справа, координаты в пределах холста', () => {
    const m = buildFocusModel(makeGraph(), 'c', { filters: ALL })!;
    for (const n of m.nodes) {
      expect(Number.isFinite(n.x) && Number.isFinite(n.y)).toBe(true);
      expect(n.x).toBeGreaterThanOrEqual(0);
      expect(n.x).toBeLessThanOrEqual(m.viewW);
      expect(n.y).toBeGreaterThanOrEqual(0);
      expect(n.y).toBeLessThanOrEqual(m.viewH);
      if (n.side === 'in') expect(n.x).toBeLessThan(m.viewW / 2);
      if (n.side === 'out') expect(n.x).toBeGreaterThan(m.viewW / 2);
    }
  });

  it('лимит соседей на сторону, хвост уходит в заглушку', () => {
    const m = buildFocusModel(makeGraph(), 'c', { filters: ALL })!;
    expect(m.incoming).toHaveLength(20);           // список полный — он нужен панели
    expect(m.nodes.filter(n => n.side === 'in')).toHaveLength(FOCUS_LIMIT);
    const stub = m.stubs.find(s => s.side === 'in')!;
    expect(stub.hidden).toBe(20 - FOCUS_LIMIT);
    expect(m.stubs.find(s => s.side === 'out')).toBeUndefined();   // исходящих всего 3
  });

  it('фильтр связей и «скрыть тесты» убирают соседей', () => {
    const g = makeGraph();
    const noRefs = buildFocusModel(g, 'c', { filters: { ...ALL, References: false } })!;
    expect(noRefs.incoming).toHaveLength(0);
    expect(noRefs.outgoing).toHaveLength(3);
    const noTests = focusNeighbours(g, 'c', 'in', { filters: ALL, hideTests: true });
    expect(noTests).toHaveLength(19);              // in0 лежит в .Tests-пути
  });

  it('мобильная раскладка вертикальная и со своим лимитом', () => {
    const m = buildFocusModel(makeGraph(), 'c', { filters: ALL, mobile: true })!;
    const ins = m.nodes.filter(n => n.side === 'in');
    const outs = m.nodes.filter(n => n.side === 'out');
    expect(ins.length).toBeLessThan(FOCUS_LIMIT);
    for (const n of ins) expect(n.y).toBeLessThan(m.viewH / 2);     // входящие сверху
    for (const n of outs) expect(n.y).toBeGreaterThan(m.viewH / 2); // исходящие снизу
  });

  it('раскладка воспроизводима и не падает на неизвестном узле', () => {
    const g = makeGraph();
    const key = (id: string) => buildFocusModel(g, id, { filters: ALL })!.nodes.map(n => `${n.node.id}:${n.x},${n.y}`).join('|');
    expect(key('c')).toBe(key('c'));
    expect(buildFocusModel(g, 'нет-такого', { filters: ALL })).toBeNull();
  });
});
