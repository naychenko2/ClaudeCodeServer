import { describe, expect, it } from 'vitest';
import type { NoteGraph, NoteGraphNode } from '../../../types';
import { filterGraph } from './graphFilter';
import { GRAPH_DEFAULTS, type GraphSettings } from './graphSettings';

const node = (id: string, over: Partial<NoteGraphNode> = {}): NoteGraphNode => ({
  id, title: id, source: 'personal', sourceLabel: 'Личный', degree: 0, ghost: false, ...over,
});

// Цепочка a-b-c-d + орфан e + призрак g, связанный с a
const chain: NoteGraph = {
  nodes: [node('a'), node('b'), node('c'), node('d'), node('e'), node('g', { ghost: true })],
  edges: [
    { source: 'a', target: 'b' },
    { source: 'b', target: 'c' },
    { source: 'c', target: 'd' },
    { source: 'a', target: 'g' },
  ],
};

const filters = (over: Partial<GraphSettings['filters']> = {}): GraphSettings['filters'] =>
  ({ ...GRAPH_DEFAULTS.filters, ...over });

const ids = (g: NoteGraph) => g.nodes.map(n => n.id).sort();

describe('filterGraph: локальный режим (BFS-глубина)', () => {
  it('глубина 1 — только прямые соседи', () => {
    const r = filterGraph(chain, { filters: filters({ depth: 1 }), focusId: 'b' });
    expect(ids(r)).toEqual(['a', 'b', 'c']);
  });

  it('глубина 2 — соседи соседей', () => {
    const r = filterGraph(chain, { filters: filters({ depth: 2 }), focusId: 'b' });
    expect(ids(r)).toEqual(['a', 'b', 'c', 'd', 'g']);
  });

  it('рёбра только между выжившими', () => {
    const r = filterGraph(chain, { filters: filters({ depth: 1 }), focusId: 'b' });
    expect(r.edges).toEqual([{ source: 'a', target: 'b' }, { source: 'b', target: 'c' }]);
  });
});

describe('filterGraph: тумблеры', () => {
  it('existingOnly убирает призраков', () => {
    const r = filterGraph(chain, { filters: filters({ existingOnly: true }) });
    expect(ids(r)).toEqual(['a', 'b', 'c', 'd', 'e']);
  });

  it('showOrphans=false убирает узлы без связей', () => {
    const r = filterGraph(chain, { filters: filters({ showOrphans: false }) });
    expect(ids(r)).toEqual(['a', 'b', 'c', 'd', 'g']);
  });

  it('орфаны, появившиеся после фильтра, тоже скрываются', () => {
    // Поиск оставляет a и d — связи между ними нет, оба орфаны
    const r = filterGraph(chain, { filters: filters({ search: '-b -c -e -g', showOrphans: false }) });
    expect(ids(r)).toEqual([]);
  });

  it('фокус-узел не считается орфаном', () => {
    const solo: NoteGraph = { nodes: [node('x')], edges: [] };
    const r = filterGraph(solo, { filters: filters({ showOrphans: false }), focusId: 'x' });
    expect(ids(r)).toEqual(['x']);
  });
});

describe('filterGraph: источники, теги, лимит', () => {
  it('скрытый источник прячет узлы, призраки остаются', () => {
    const g: NoteGraph = {
      nodes: [node('a'), node('p', { source: 'proj' }), node('g', { ghost: true })],
      edges: [],
    };
    const r = filterGraph(g, { filters: filters({ hiddenSources: ['proj'] }) });
    expect(ids(r)).toEqual(['a', 'g']);
  });

  it('фильтр тегов: любой из выбранных, призраки скрываются', () => {
    const g: NoteGraph = {
      nodes: [node('a', { tags: ['идея'] }), node('b', { tags: ['архив'] }), node('g', { ghost: true })],
      edges: [],
    };
    const r = filterGraph(g, { filters: filters({ selectedTags: ['идея'] }) });
    expect(ids(r)).toEqual(['a']);
  });

  it('maxNodes оставляет топ по degree, фокус защищён', () => {
    const g: NoteGraph = {
      nodes: [node('a', { degree: 5 }), node('b', { degree: 3 }), node('c', { degree: 1 })],
      edges: [{ source: 'a', target: 'c' }],
    };
    const r = filterGraph(g, { filters: filters({ depth: 3 }), maxNodes: 2, focusId: 'c' });
    expect(ids(r)).toContain('c');
  });
});
