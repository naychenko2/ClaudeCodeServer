// Реальный снимок графа проекта (1020 типов / 2816 связей, снят 28.07.2026 для
// макета docs/mockups/code-graph-scale-layers.html) — превращаем компактный формат
// макета обратно в CodeGraph, чтобы прогонять раскладку «Обзора» на настоящих данных,
// а не на синтетике из 4 узлов. Число элементов на холсте и найденные нарушения
// слоистости должны совпасть с тем, что видно в самом макете.
import type { CodeGraph, CodeGraphNode, CodeGraphNodeKind, CodeGraphRelation } from '../../../../types';
import raw from './real-snapshot.json';

interface RawGraph {
  kinds: string[];
  rels: string[];
  ns: string[];
  files: string[];
  nodes: [string, number, number, number, number][];   // [name, nsIndex, kindIndex, fileIndex, line]
  edges: [number, number, number][];                     // [sourceIndex, targetIndex, relIndex]
  builtAt: string;
  fileCount: number;
}

const G = raw as RawGraph;

export function loadRealSnapshot(): CodeGraph {
  const nodes: CodeGraphNode[] = G.nodes.map(([name, nsIdx, kindIdx, fileIdx, line], i) => ({
    id: `n${i}`,
    label: name,
    fullyQualifiedName: `${G.ns[nsIdx]}.${name}`,
    sourceFile: G.files[fileIdx],
    sourceLocation: `${line}:1`,
    kind: G.kinds[kindIdx] as CodeGraphNodeKind,
  }));

  const degree = new Map<string, number>();
  const edges = G.edges.map(([s, t, r]) => {
    const source = `n${s}`, target = `n${t}`;
    degree.set(source, (degree.get(source) ?? 0) + 1);
    degree.set(target, (degree.get(target) ?? 0) + 1);
    return {
      source, target,
      relation: G.rels[r] as CodeGraphRelation,
      confidence: 'Extracted' as const,
    };
  });

  // Тот же порог, что использует бэкенд (minDegree=10) и сам макет
  const godNodes = nodes.filter(n => (degree.get(n.id) ?? 0) >= 10).map(n => n.id);

  return {
    nodes,
    edges,
    godNodes,
    metadata: {
      builtAt: G.builtAt,
      nodeCount: nodes.length,
      edgeCount: edges.length,
      fileCount: G.fileCount,
      isStale: false,
    },
  };
}
