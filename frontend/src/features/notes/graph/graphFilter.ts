import type { NoteGraph } from '../../../types';
import type { GraphSettings } from './graphSettings';
import { matchNode, parseQuery } from './graphQuery';

// Применение фильтров панели к сырому графу. Чистая функция: порядок шагов —
// глубина (локальный режим) → поиск → призраки → источники/теги → рёбра →
// орфаны → мобильный лимит.
export function filterGraph(graph: NoteGraph, opts: {
  filters: GraphSettings['filters'];
  focusId?: string;    // локальный режим: BFS-окрестность заметки
  maxNodes?: number;   // мобильный лимит: топ-N по числу связей
}): NoteGraph {
  const f = opts.filters;
  let { nodes, edges } = graph;

  if (opts.focusId) {
    const adj = new Map<string, string[]>();
    for (const e of edges) {
      (adj.get(e.source) ?? adj.set(e.source, []).get(e.source)!).push(e.target);
      (adj.get(e.target) ?? adj.set(e.target, []).get(e.target)!).push(e.source);
    }
    const depth = Math.max(1, Math.min(3, f.depth || 1));
    const seen = new Set([opts.focusId]);
    let frontier = [opts.focusId];
    for (let d = 0; d < depth; d++) {
      const next: string[] = [];
      for (const id of frontier)
        for (const nb of adj.get(id) ?? [])
          if (!seen.has(nb)) { seen.add(nb); next.push(nb); }
      frontier = next;
    }
    nodes = nodes.filter(n => seen.has(n.id));
  }

  const terms = parseQuery(f.search);
  if (terms.length) nodes = nodes.filter(n => n.id === opts.focusId || matchNode(n, terms));

  if (f.existingOnly) nodes = nodes.filter(n => !n.ghost || n.id === opts.focusId);

  // Скрытые источники: призраки не привязаны к источнику — остаются видимыми
  if (f.hiddenSources.length) {
    const hidden = new Set(f.hiddenSources);
    nodes = nodes.filter(n => n.ghost || n.id === opts.focusId || !hidden.has(n.source));
  }
  // Теги: только заметки с любым из выбранных; призраки без тегов скрываются
  if (f.selectedTags.length) {
    const sel = new Set(f.selectedTags);
    nodes = nodes.filter(n => n.id === opts.focusId || (!n.ghost && n.tags?.some(t => sel.has(t))));
  }

  let ids = new Set(nodes.map(n => n.id));
  edges = edges.filter(e => ids.has(e.source) && ids.has(e.target));

  if (!f.showOrphans) {
    const linked = new Set<string>();
    edges.forEach(e => { linked.add(e.source); linked.add(e.target); });
    nodes = nodes.filter(n => linked.has(n.id) || n.id === opts.focusId);
  }

  if (opts.maxNodes && nodes.length > opts.maxNodes) {
    const keep = new Set(
      [...nodes].sort((a, b) => (b.degree ?? 0) - (a.degree ?? 0)).slice(0, opts.maxNodes).map(n => n.id),
    );
    if (opts.focusId) keep.add(opts.focusId);
    nodes = nodes.filter(n => keep.has(n.id));
    ids = new Set(nodes.map(n => n.id));
    edges = edges.filter(e => ids.has(e.source) && ids.has(e.target));
  }

  return { nodes, edges };
}
