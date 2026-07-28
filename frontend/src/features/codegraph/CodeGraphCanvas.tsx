// SVG-холст Code Graph: узлы-типы и рёбра-связи между ними. Без внешних графовых
// библиотек — позиционирование детерминированное даёт layoutGraph (см. graphLayout.ts).
// Подсветка: клик по узлу выделяет его и инцидентные рёбра (остальное меркнет);
// поиск подсвечивает совпадения accent-ом. god-узлы — accent-нимб + увеличенный размер.
import { useMemo, type MouseEvent } from 'react';
import { C, FONT, FS } from '../../lib/design';
import type { CodeGraph, CodeGraphEdge } from '../../types';
import type { GraphLayout } from './graphLayout';
import { VIEW_W, VIEW_H } from './graphLayout';
import { EDGE_COLOR, KIND_RING, KIND_COLOR, KIND_GLYPH, isDashed } from './graphTokens';

interface Props {
  graph: CodeGraph;
  layout: GraphLayout;
  filters: { Calls: boolean; Implements: boolean; References: boolean };
  selectedId: string | null;
  query: string;
  onSelect: (id: string | null) => void;
  hideTestNodes?: boolean;
  hideOrphanNodes?: boolean;
}

const GLYPH_FS = FS.base;   // моноширинная буква типа в кружке
const LABEL_FS = FS.xs;     // моноширинная подпись узла под кружком

export function CodeGraphCanvas({ graph, layout, filters, selectedId, query, onSelect, hideTestNodes, hideOrphanNodes }: Props) {
  // Фильтр «скрыть тесты»: sourceFile содержит Tests/test/__tests__/.Tests.
  const hiddenByTest = useMemo(() => {
    if (!hideTestNodes) return new Set<string>();
    const s = new Set<string>();
    for (const n of graph.nodes) {
      if (/[\\/]Tests[\\/]|[\\/]test[\\/]|[\\/]__tests__[\\/]|\.Tests\./i.test(n.sourceFile)) {
        s.add(n.id);
      }
    }
    return s;
  }, [hideTestNodes, graph.nodes]);

  // Фильтр «скрыть сироты»: узлы без входящих/исходящих рёбер (degree = 0)
  const hiddenOrphan = useMemo(() => {
    if (!hideOrphanNodes) return new Set<string>();
    const deg = new Map<string, number>();
    for (const e of graph.edges) {
      deg.set(e.source, (deg.get(e.source) ?? 0) + 1);
      deg.set(e.target, (deg.get(e.target) ?? 0) + 1);
    }
    const s = new Set<string>();
    for (const n of graph.nodes) {
      if ((deg.get(n.id) ?? 0) === 0) s.add(n.id);
    }
    return s;
  }, [hideOrphanNodes, graph.nodes]);

  // Поиск: при активном запросе показываем только совпавшие узлы + их соседей первого уровня
  const searchVisible = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return null; // null = неактивен, не фильтруем
    const matching = new Set<string>();
    for (const n of graph.nodes) {
      if (n.label.toLowerCase().includes(q) || n.fullyQualifiedName.toLowerCase().includes(q)) {
        matching.add(n.id);
      }
    }
    // Добавляем соседей первого уровня
    const result = new Set(matching);
    for (const e of graph.edges) {
      if (matching.has(e.source)) result.add(e.target);
      if (matching.has(e.target)) result.add(e.source);
    }
    return result;
  }, [query, graph.nodes, graph.edges]);

  const hidden = useMemo(() => {
    const s = new Set([...hiddenByTest, ...hiddenOrphan]);
    // При активном поиске скрываем всё, что не входит в searchVisible
    if (searchVisible) {
      for (const n of graph.nodes) {
        if (!searchVisible.has(n.id)) s.add(n.id);
      }
    }
    return s;
  }, [hiddenByTest, hiddenOrphan, searchVisible, graph.nodes]);

  // Рёбра, чей тип связи включён фильтром + оба конца видимы
  const visibleEdges = useMemo(
    () => graph.edges.filter(e => filters[e.relation] && !hidden.has(e.source) && !hidden.has(e.target)),
    [graph.edges, filters, hidden],
  );

  // Множество узлов, инцидентных выделенному (для подсветки/приглушения)
  const incident = useMemo(() => {
    const s = new Set<string>();
    if (selectedId) {
      s.add(selectedId);
      for (const e of visibleEdges) {
        if (e.source === selectedId) s.add(e.target);
        if (e.target === selectedId) s.add(e.source);
      }
    }
    return s;
  }, [selectedId, visibleEdges]);

  const q = query.trim().toLowerCase();
  const hasFocus = !!selectedId;
  const hasQuery = q.length > 0;

  // Подпись узла «попадает в поиск»: по label или FQN
  const matches = (id: string): boolean => {
    if (!hasQuery) return false;
    const ln = layout.byId.get(id);
    if (!ln) return false;
    return ln.node.label.toLowerCase().includes(q) || ln.node.fullyQualifiedName.toLowerCase().includes(q);
  };

  const edgeState = (e: CodeGraphEdge): 'hi' | 'dim' | 'normal' => {
    if (hasFocus) return (e.source === selectedId || e.target === selectedId) ? 'hi' : 'dim';
    if (hasQuery) return 'normal'; // поиск не гасит рёбра — узлы уже отфильтрованы
    return 'normal';
  };

  const nodeState = (id: string): 'sel' | 'match' | 'dim' | 'normal' => {
    if (hasFocus) return id === selectedId ? 'sel' : (incident.has(id) ? 'normal' : 'dim');
    // При поиске несовпадающие узлы уже скрыты через hidden; оставшиеся — normal либо match (для accent-подсветки)
    if (hasQuery) return matches(id) ? 'match' : 'normal';
    return 'normal';
  };

  const handleSvgClick = (e: MouseEvent<SVGSVGElement>) => {
    // Клик по пустому холсту (не по узлу) снимает выделение — если не активен поиск
    if (e.target === e.currentTarget && !hasQuery) onSelect(null);
  };

  return (
    <svg
      viewBox={`0 0 ${VIEW_W} ${VIEW_H}`}
      preserveAspectRatio="xMidYMid meet"
      onClick={handleSvgClick}
      style={{ width: '100%', height: '100%', display: 'block' }}
    >
      <defs>
        {/* Стрелки направления связи — цветом ребра. References идёт без стрелки (см. ниже) */}
        {(['Calls', 'Implements'] as const).map(rel => (
          <marker key={rel} id={`cg-arrow-${rel}`} viewBox="0 0 10 10" refX="9" refY="5"
            markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M0,0 L10,5 L0,10 z" fill={EDGE_COLOR[rel]} />
          </marker>
        ))}
      </defs>

      {/* Рёбра рисуем под узлами */}
      <g>
        {visibleEdges.map((e, i) => {
          const a = layout.byId.get(e.source);
          const b = layout.byId.get(e.target);
          if (!a || !b) return null;
          const st = edgeState(e);
          const color = EDGE_COLOR[e.relation];
          const dashed = isDashed(e.confidence);
          return (
            <line key={i}
              x1={a.x} y1={a.y} x2={b.x} y2={b.y}
              stroke={color}
              strokeWidth={st === 'hi' ? 2.6 : 1.6}
              strokeDasharray={dashed ? '5 4' : undefined}
              markerEnd={e.relation === 'References' ? undefined : `url(#cg-arrow-${e.relation})`}
              opacity={st === 'dim' ? 0.1 : st === 'hi' ? 1 : 0.85}
              style={{ transition: 'opacity 0.15s, stroke-width 0.15s' }}
            />
          );
        })}
      </g>

      {/* Узлы — скрытые удаляются из DOM (display: none), а не меркнут */}
      <g>
        {layout.nodes.filter(ln => !hidden.has(ln.node.id)).map(ln => {
          const id = ln.node.id;
          const st = nodeState(id);
          const ring = ln.isGod || st === 'sel' ? C.accent : KIND_RING[ln.node.kind];
          const glyphFill = ln.node.kind === 'Class' ? C.textHeading : KIND_COLOR[ln.node.kind];
          const labelFill = st === 'sel' || st === 'match' ? C.accent : C.textSecondary;
          const dimmed = st === 'dim';
          return (
            <g key={id} transform={`translate(${ln.x},${ln.y})`}
              opacity={dimmed ? 0.25 : 1}
              style={{ cursor: 'pointer', transition: 'opacity 0.15s' }}>
              {/* Невидимый круг-расширитель тач-цели (≥40px на мобиле) */}
              <circle r={Math.max(ln.r + 10, 20)} fill="transparent"
                onClick={ev => { ev.stopPropagation(); onSelect(id); }} />
              {/* god-node: пунктирный accent-нимб */}
              {ln.isGod && (
                <circle r={ln.r + 7} fill="none" stroke={C.accent}
                  strokeWidth={2} strokeDasharray="3 3" opacity={0.55} pointerEvents="none" />
              )}
              <circle r={ln.r} fill={C.bgCard} stroke={ring}
                strokeWidth={st === 'sel' ? 3.5 : 2.5}
                style={{ transition: 'stroke-width 0.15s' }}
                pointerEvents="none" />
              <text textAnchor="middle" dominantBaseline="central"
                fontFamily={FONT.mono} fontSize={GLYPH_FS} fontWeight={600}
                fill={glyphFill} pointerEvents="none">{KIND_GLYPH[ln.node.kind]}</text>
              <text textAnchor="middle" y={ln.r + 13}
                fontFamily={FONT.mono} fontSize={LABEL_FS}
                fill={labelFill} fontWeight={st === 'sel' || st === 'match' ? 600 : 400}
                style={{ transition: 'fill 0.15s' }} pointerEvents="none">
                {ln.node.label}
              </text>
            </g>
          );
        })}
      </g>
    </svg>
  );
}
