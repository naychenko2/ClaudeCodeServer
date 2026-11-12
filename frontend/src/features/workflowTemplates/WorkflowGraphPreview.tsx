// Мини-превью графа маршрута шаблона (feature-workflows, G6b, разрез 2 §1.9).
// Колонки — previewColumns (глубина = длиннейший путь от корня), карточки фиксированного
// размера, рёбра — SVG-кривые между ними; библиотеки графов нет. Показывает то, что дано:
// валидация — дело сервера. На узком экране колонки встают вертикально, параллельные этапы
// одной колонки объединены рамкой «параллельно».

import { C, FONT, FS, R, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { normalizeStages, previewColumns } from './workflowGraphModel';
import type { WorkflowStage } from './types';

const ROLE_LABEL: Record<string, string> = {
  cut: 'Нарезка', design: 'Дизайн', plan: 'План', step: 'Шаг', review: 'Ревью',
  qa: 'Проверка', final: 'Финал', merge: 'Слияние',
};
const LANE_LABEL: Record<string, string> = { back: 'бэк', front: 'фронт', other: 'другое' };

const NODE_H = 44;
const GAP = 12;
const GX = 44;
const PAD = 8;
const LABEL_H = 18;
const COL_GAP = 20;
const BOW = 28;

export interface GraphNode { id: string; role: string; lane: string | null; presence: string; col: number; x: number; y: number }
export interface GraphEdge { from: string; to: string; d: string }
export interface GraphGroup { x: number; y: number; w: number; h: number }
export interface GraphLayout {
  nodeW: number; nodeH: number; width: number; height: number;
  nodes: GraphNode[]; edges: GraphEdge[]; groups: GraphGroup[];
}

// Раскладка без DOM: координаты карточек, рёбер и рамок «параллельно» (только compact).
export function layoutGraph(stages: WorkflowStage[], compact: boolean): GraphLayout {
  const norm = normalizeStages(stages);
  const cols = previewColumns(stages);
  const nodeW = compact ? 200 : 132;
  const colCount = norm.reduce((m, s) => Math.max(m, (cols[s.id ?? ''] ?? 0) + 1), 0);
  const byCol: WorkflowStage[][] = Array.from({ length: colCount }, () => []);
  norm.forEach(s => byCol[cols[s.id ?? ''] ?? 0].push(s));

  const nodes: GraphNode[] = [];
  const groups: GraphGroup[] = [];
  const mk = (s: WorkflowStage, col: number, x: number, y: number): GraphNode => ({
    id: s.id ?? '', role: s.role, lane: s.lane ?? null, presence: s.presence, col, x, y,
  });
  let width: number;
  let height = 0;

  if (compact) {
    let y = 0;
    byCol.forEach((list, col) => {
      const parallel = list.length > 1;
      const top = y;
      if (parallel) y += LABEL_H;
      list.forEach((s, i) => {
        nodes.push(mk(s, col, PAD, y));
        y += NODE_H + (i < list.length - 1 ? GAP : 0);
      });
      if (parallel) { y += PAD; groups.push({ x: 0, y: top, w: nodeW + PAD * 2, h: y - top }); }
      y += COL_GAP;
    });
    height = Math.max(0, y - COL_GAP);
    width = nodeW + PAD * 2 + BOW;
  } else {
    let maxRows = 0;
    byCol.forEach((list, col) => {
      maxRows = Math.max(maxRows, list.length);
      list.forEach((s, i) => nodes.push(mk(s, col, col * (nodeW + GX), i * (NODE_H + GAP))));
    });
    width = colCount === 0 ? 0 : colCount * nodeW + (colCount - 1) * GX;
    height = maxRows === 0 ? 0 : maxRows * NODE_H + (maxRows - 1) * GAP;
  }

  const at = new Map(nodes.map(n => [n.id, n]));
  const edges: GraphEdge[] = [];
  for (const s of norm) {
    const to = at.get(s.id ?? '');
    for (const parent of s.after ?? []) {
      const from = at.get(parent);
      // Только вперёд по колонкам: висящая ссылка и ссылка «назад» рёбер не рисуют.
      if (!from || !to || from.col >= to.col) continue;
      const x1 = from.x + nodeW;
      const y1 = from.y + NODE_H / 2;
      const y2 = to.y + NODE_H / 2;
      const d = compact
        ? `M ${x1} ${y1} C ${x1 + BOW} ${y1}, ${to.x + nodeW + BOW} ${y2}, ${to.x + nodeW} ${y2}`
        : `M ${x1} ${y1} C ${x1 + GX / 2} ${y1}, ${to.x - GX / 2} ${y2}, ${to.x} ${y2}`;
      edges.push({ from: parent, to: to.id, d });
    }
  }
  return { nodeW, nodeH: NODE_H, width, height, nodes, edges, groups };
}

export function WorkflowGraphPreview({ stages }: { stages: WorkflowStage[] }): React.ReactElement | null {
  const compact = useIsMobile();
  const g = layoutGraph(stages, compact);
  if (g.nodes.length === 0) return null;

  return (
    <div
      role="group"
      aria-label="Схема маршрута"
      style={{
        overflowX: 'auto', padding: SP.md, background: C.bgPanel,
        border: `1px solid ${C.border}`, borderRadius: R.xl,
      }}
    >
      <div style={{ position: 'relative', width: g.width, height: g.height, margin: compact ? '0 auto' : 0 }}>
        <svg
          width={g.width} height={g.height} aria-hidden="true"
          style={{ position: 'absolute', left: 0, top: 0, overflow: 'visible', pointerEvents: 'none' }}
        >
          <defs>
            <marker id="wf-arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto">
              <path d="M 0 0 L 8 4 L 0 8 z" style={{ fill: C.textMuted }} />
            </marker>
          </defs>
          {g.edges.map(e => (
            <path
              key={`${e.from}>${e.to}`} d={e.d} fill="none" markerEnd="url(#wf-arrow)"
              style={{ stroke: C.textMuted, strokeWidth: 1.5 }}
            />
          ))}
        </svg>
        {g.groups.map((b, i) => (
          <div
            key={i}
            style={{
              position: 'absolute', left: b.x, top: b.y, width: b.w, height: b.h, boxSizing: 'border-box',
              border: `1px dashed ${C.accent}`, borderRadius: R.lg,
            }}
          >
            <span style={{
              position: 'absolute', left: PAD, top: 1, fontSize: FS.xs, fontFamily: FONT.sans,
              color: C.accent, fontWeight: 600,
            }}>параллельно</span>
          </div>
        ))}
        {g.nodes.map(n => {
          const label = ROLE_LABEL[n.role] ?? n.role;
          const lane = n.lane ? LANE_LABEL[n.lane] ?? n.lane : null;
          return (
            <div
              key={n.id}
              title={`${label} · ${n.id}`}
              style={{
                position: 'absolute', left: n.x, top: n.y, width: g.nodeW, height: g.nodeH,
                boxSizing: 'border-box', padding: `0 ${SP.sm}px`, background: C.bgWhite,
                border: `1px ${n.presence === 'optional' ? 'dashed' : 'solid'} ${C.border}`,
                borderRadius: R.lg, opacity: n.presence === 'off' ? 0.5 : 1,
                display: 'flex', flexDirection: 'column', justifyContent: 'center', minWidth: 0,
                fontFamily: FONT.sans,
              }}
            >
              <span style={{
                fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
              }}>{label}{lane ? ` · ${lane}` : ''}</span>
              {n.id !== n.role && (
                <span style={{
                  fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>{n.id}</span>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
