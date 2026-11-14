// Мини-превью графа маршрута шаблона (feature-workflows, G6b, разрез 2 §1.9).
// Колонки — previewColumns (глубина = длиннейший путь от корня), карточки фиксированного
// размера, рёбра — SVG-кривые между ними; библиотеки графов нет. Показывает то, что дано:
// валидация — дело сервера. На узком экране колонки встают вертикально, параллельные этапы
// одной колонки объединены рамкой «параллельно».

import { Lock } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { buildLadder } from '../../lib/workflowLadderModel';
import { STAGE_ROLE_LABEL } from './WorkflowStageRow';
import { normalizeStages } from './workflowGraphModel';
import { stageKeys } from './workflowStripModel';
import { isNonSkippable } from './workflowTemplateModel';
import type { WorkflowStage } from './types';

const LANE_LABEL: Record<string, string> = { back: 'бэк', front: 'фронт', other: 'другое' };

const NODE_H = 56;
const GAP = 12;
const GX = 44;
const PAD = 8;
const LABEL_H = 18;
const COL_GAP = 20;
const BOW = 28;
const BYPASS = 18;

export interface GraphNode {
  id: string; key: string; role: string; lane: string | null; title: string | null; presence: string;
  locked: boolean; col: number; x: number; y: number;
}
export interface GraphEdge { from: string; to: string; d: string; far: boolean }
export interface GraphGroup { x: number; y: number; w: number; h: number }
export interface GraphLayout {
  nodeW: number; nodeH: number; width: number; height: number;
  nodes: GraphNode[]; edges: GraphEdge[]; groups: GraphGroup[];
}

// Раскладка без DOM: колонки и рёбра — из лестницы маршрута (этапы presence=off в ней не
// участвуют, потомки наследуют их родителей), рамки «параллельно» — вокруг колонки из ≥2 этапов
// (на десктопе вертикальная колонка, на узком экране — блок), ребро через уровень — обход
// (на десктопе под графом, на узком — широкой дугой справа).
export function layoutGraph(stages: WorkflowStage[], compact: boolean): GraphLayout {
  const norm = normalizeStages(stages);
  const keys = stageKeys(stages);
  const keyOf = new Map<string, string>();
  norm.forEach((s, i) => { if (!keyOf.has(s.id ?? '')) keyOf.set(s.id ?? '', keys[i]); });
  const byId = new Map<string, WorkflowStage>();
  norm.forEach(s => { if (!byId.has(s.id ?? '')) byId.set(s.id ?? '', s); });

  const ladder = buildLadder(
    norm.map(s => ({
      id: s.id ?? '', after: s.after ?? [], presence: s.presence, skippable: !isNonSkippable((s.role ?? '').trim()),
    })),
    new Set<string>(),
  );
  const nodeW = compact ? 200 : 132;
  const hasFar = ladder.edges.some(e => e.far);
  const nodes: GraphNode[] = [];
  const groups: GraphGroup[] = [];
  const mk = (id: string, col: number, x: number, y: number): GraphNode => {
    const s = byId.get(id)!;
    return {
      id, key: keyOf.get(id) ?? id, role: s.role, lane: s.lane ?? null, title: (s.title ?? '').trim() || null,
      presence: s.presence, locked: ladder.locked[id] !== undefined, col, x, y,
    };
  };
  let width: number;
  let height = 0;

  if (compact) {
    let y = 0;
    ladder.columns.forEach((c, col) => {
      const parallel = c.parallel;
      const top = y;
      if (parallel) y += LABEL_H;
      c.stageIds.forEach((id, i) => {
        nodes.push(mk(id, col, PAD, y));
        y += NODE_H + (i < c.stageIds.length - 1 ? GAP : 0);
      });
      if (parallel) { y += PAD; groups.push({ x: 0, y: top, w: nodeW + PAD * 2, h: y - top }); }
      y += COL_GAP;
    });
    height = Math.max(0, y - COL_GAP);
    width = nodeW + PAD * 2 + BOW * (hasFar ? 2 : 1);
  } else {
    const anyParallel = ladder.columns.some(c => c.parallel);
    const top = anyParallel ? LABEL_H : 0;
    let maxRows = 0;
    ladder.columns.forEach((c, col) => {
      maxRows = Math.max(maxRows, c.stageIds.length);
      const x = PAD + col * (nodeW + GX);
      c.stageIds.forEach((id, i) => nodes.push(mk(id, col, x, top + i * (NODE_H + GAP))));
    });
    const rowsH = maxRows === 0 ? 0 : maxRows * NODE_H + (maxRows - 1) * GAP;
    ladder.columns.forEach((c, col) => {
      if (!c.parallel) return;
      const h = LABEL_H + c.stageIds.length * NODE_H + (c.stageIds.length - 1) * GAP + PAD;
      groups.push({ x: col * (nodeW + GX), y: 0, w: nodeW + PAD * 2, h });
    });
    const n = ladder.columns.length;
    width = n === 0 ? 0 : n * nodeW + (n - 1) * GX + PAD * 2;
    height = rowsH === 0 ? 0 : top + rowsH + (anyParallel ? PAD : 0) + (hasFar ? BYPASS : 0);
  }

  const at = new Map(nodes.map(n => [n.id, n]));
  const edges: GraphEdge[] = [];
  const bottom = height - BYPASS / 2;
  for (const e of ladder.edges) {
    const from = at.get(e.from);
    const to = at.get(e.to);
    if (!from || !to) continue;
    const x1 = from.x + nodeW;
    const y1 = from.y + NODE_H / 2;
    const y2 = to.y + NODE_H / 2;
    let d: string;
    if (compact) {
      const bow = e.far ? BOW * 1.8 : BOW;
      d = `M ${x1} ${y1} C ${x1 + bow} ${y1}, ${to.x + nodeW + bow} ${y2}, ${to.x + nodeW} ${y2}`;
    } else if (e.far) {
      d = `M ${x1} ${y1} C ${x1 + 10} ${y1}, ${x1 + 10} ${bottom}, ${x1 + 24} ${bottom} L ${to.x - 24} ${bottom} `
        + `C ${to.x - 10} ${bottom}, ${to.x - 10} ${y2}, ${to.x} ${y2}`;
    } else {
      d = `M ${x1} ${y1} C ${x1 + GX / 2} ${y1}, ${to.x - GX / 2} ${y2}, ${to.x} ${y2}`;
    }
    edges.push({ from: e.from, to: e.to, d, far: e.far });
  }
  return { nodeW, nodeH: NODE_H, width, height, nodes, edges, groups };
}

export interface WorkflowGraphPreviewProps {
  stages: WorkflowStage[];
  // Ключ выбранного этапа (тот же selectedStageId, что у ленты) и обработчик клика по узлу.
  selectedKey?: string | null;
  onSelectStage?: (key: string) => void;
}

export function WorkflowGraphPreview({ stages, selectedKey = null, onSelectStage }: WorkflowGraphPreviewProps): React.ReactElement | null {
  const compact = useIsMobile();
  const g = layoutGraph(stages, compact);
  if (g.nodes.length === 0) return null;

  return (
    <div
      role="group"
      aria-label="Маршрут, который увидит фича"
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
              data-far={e.far ? 'true' : undefined}
              style={{ stroke: C.textMuted, strokeWidth: 1.5, strokeDasharray: e.far ? '4 3' : undefined }}
            />
          ))}
        </svg>
        {g.groups.map((b, i) => (
          <div
            key={i}
            data-parallel-frame="true"
            style={{
              position: 'absolute', left: b.x, top: b.y, width: b.w, height: b.h, boxSizing: 'border-box',
              border: `1px dashed ${C.accent}`, borderRadius: R.lg, pointerEvents: 'none',
            }}
          >
            <span style={{
              position: 'absolute', left: PAD, top: 1, fontSize: FS.xs, fontFamily: FONT.sans,
              color: C.accent, fontWeight: 600,
            }}>⇉ параллельно</span>
          </div>
        ))}
        {g.nodes.map(n => {
          const label = STAGE_ROLE_LABEL[n.role] ?? n.role;
          const lane = n.lane ? LANE_LABEL[n.lane] ?? n.lane : null;
          // Имя узла — из шаблона (тот же порядок, что nodeTitle в FeatureProcessGraph):
          // Title, фолбэк — подпись роли с линией. Без этого параллельные этапы одной роли
          // (qa-plan и qa) дают две неотличимые «Проверки».
          const name = n.title || `${label}${lane ? ` · ${lane}` : ''}`;
          const selected = selectedKey === n.key;
          const optional = n.presence === 'optional';
          return (
            <button
              key={n.key}
              type="button"
              data-node={n.key}
              aria-pressed={selected}
              onClick={() => onSelectStage?.(n.key)}
              title={`${name} · ${n.id}`}
              style={{
                position: 'absolute', left: n.x, top: n.y, width: g.nodeW, height: g.nodeH,
                boxSizing: 'border-box', padding: `0 ${SP.sm}px`, background: C.bgWhite,
                border: `${selected ? 2 : 1}px ${optional ? 'dashed' : 'solid'} ${selected ? C.accent : C.border}`,
                borderRadius: R.lg, cursor: 'pointer', textAlign: 'left',
                display: 'flex', flexDirection: 'column', justifyContent: 'center', gap: 1, minWidth: 0,
                fontFamily: FONT.sans,
              }}
            >
              <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                <span style={{
                  flex: 1, minWidth: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}>{name}</span>
                {optional && <span aria-label="по решению" style={{ fontSize: FS.xs, color: C.textMuted }}>?</span>}
                {n.locked && (
                  <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-label="нельзя снять"
                    style={{ color: C.textMuted, flexShrink: 0 }} />
                )}
              </span>
              <span style={{
                display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, color: C.textMuted, minWidth: 0,
              }}>
                <span style={{ fontFamily: FONT.mono, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {n.role}
                </span>
                {lane && <span data-lane="true" style={{ flexShrink: 0 }}>· {lane}</span>}
              </span>
            </button>
          );
        })}
      </div>
    </div>
  );
}
