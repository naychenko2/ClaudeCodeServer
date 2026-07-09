import { useEffect, useMemo, useRef, useState } from 'react';
import type { NoteGraph, NoteGraphNode } from '../../types';
import { api } from '../../lib/api';
import { useNotesVersion } from '../../lib/notes';
import { C, FONT } from '../../lib/design';
import { sourceColor } from './shared';

type Pt = { x: number; y: number };

// Синхронная force-directed раскладка (Fruchterman–Reingold) с охлаждением и
// клампом шага — устойчива и считается за единицы мс на типичной базе. Позиции
// потом можно двигать мышью (drag-pin). SVG-рендер красится токенами темы.
function layout(nodes: NoteGraphNode[], edges: { source: string; target: string }[], keep: Map<string, Pt>): Map<string, Pt> {
  const N = nodes.length;
  const W = 1000, H = 700;
  const pos = nodes.map((n, i) => {
    const k = keep.get(n.id);
    if (k) return { id: n.id, x: k.x, y: k.y, dx: 0, dy: 0 };
    const a = (i / Math.max(1, N)) * Math.PI * 2, r = 120 + (i % 9) * 24;
    return { id: n.id, x: W / 2 + Math.cos(a) * r, y: H / 2 + Math.sin(a) * r, dx: 0, dy: 0 };
  });
  const idx = new Map(pos.map((p, i) => [p.id, i]));
  // Фиксированная идеальная длина ребра: при малом N формула area/N даёт огромный k
  // и узлы разлетаются. ~78px держит граф компактным независимо от числа узлов.
  const k = 78;
  const iters = 300;
  for (let it = 0; it < iters; it++) {
    const temp = (1 - it / iters) * 42;
    for (let i = 0; i < N; i++) { pos[i].dx = 0; pos[i].dy = 0; }
    for (let i = 0; i < N; i++)
      for (let j = i + 1; j < N; j++) {
        let dx = pos[i].x - pos[j].x, dy = pos[i].y - pos[j].y;
        let d = Math.hypot(dx, dy) || 0.01;
        const rep = (k * k) / d, ux = dx / d, uy = dy / d;
        pos[i].dx += ux * rep; pos[i].dy += uy * rep; pos[j].dx -= ux * rep; pos[j].dy -= uy * rep;
      }
    for (const e of edges) {
      const a = idx.get(e.source), b = idx.get(e.target);
      if (a == null || b == null) continue;
      let dx = pos[a].x - pos[b].x, dy = pos[a].y - pos[b].y;
      let d = Math.hypot(dx, dy) || 0.01;
      const att = (d * d) / k, fx = (dx / d) * att, fy = (dy / d) * att;
      pos[a].dx -= fx; pos[a].dy -= fy; pos[b].dx += fx; pos[b].dy += fy;
    }
    for (let i = 0; i < N; i++) {
      // Сильная гравитация к центру держит несвязанные компоненты вместе (иначе разлетаются)
      pos[i].dx += (W / 2 - pos[i].x) * 0.08; pos[i].dy += (H / 2 - pos[i].y) * 0.08;
      const dl = Math.hypot(pos[i].dx, pos[i].dy) || 0.01;
      pos[i].x += (pos[i].dx / dl) * Math.min(dl, temp);
      pos[i].y += (pos[i].dy / dl) * Math.min(dl, temp);
    }
  }
  return new Map(pos.map(p => [p.id, { x: p.x, y: p.y }]));
}

export function NotesGraph({ sourceFilter, selectedId, onSelectNode, focusId }: {
  sourceFilter: Set<string> | null;
  selectedId: string | null;
  onSelectNode: (id: string) => void;
  focusId?: string;   // локальный режим: только эта заметка и её соседи
}) {
  const version = useNotesVersion();
  const [graph, setGraph] = useState<NoteGraph | null>(null);
  const [hover, setHover] = useState<string | null>(null);
  const [pos, setPos] = useState<Map<string, Pt>>(new Map());
  const [pinned, setPinned] = useState<Set<string>>(new Set());
  const [view, setView] = useState({ x: 0, y: 0, w: 1000, h: 700 });
  const svgRef = useRef<SVGSVGElement>(null);
  const drag = useRef<{ kind: 'pan'; x: number; y: number; vx: number; vy: number }
    | { kind: 'node'; id: string; x: number; y: number } | null>(null);

  useEffect(() => {
    let alive = true;
    api.notes.graph().then(g => { if (alive) setGraph(g); }).catch(() => {});
    return () => { alive = false; };
  }, [version]);

  const filtered = useMemo(() => {
    if (!graph) return null;
    let { nodes, edges } = graph;
    if (focusId) {
      const nb = new Set<string>([focusId]);
      edges.forEach(e => { if (e.source === focusId) nb.add(e.target); if (e.target === focusId) nb.add(e.source); });
      nodes = nodes.filter(n => nb.has(n.id));
      edges = edges.filter(e => nb.has(e.source) && nb.has(e.target));
    } else {
      const vis = new Set(nodes.filter(n => n.ghost || !sourceFilter || sourceFilter.has(n.source)).map(n => n.id));
      nodes = nodes.filter(n => vis.has(n.id));
      edges = edges.filter(e => vis.has(e.source) && vis.has(e.target));
    }
    return { nodes, edges };
  }, [graph, sourceFilter, focusId]);

  // Пересчёт раскладки при изменении набора узлов; закреплённые узлы сохраняют место
  useEffect(() => {
    if (!filtered) return;
    const next = layout(filtered.nodes, filtered.edges, pinnedPositions());
    setPos(next);
    // Подгоняем viewBox под облако
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    next.forEach(p => { minX = Math.min(minX, p.x); minY = Math.min(minY, p.y); maxX = Math.max(maxX, p.x); maxY = Math.max(maxY, p.y); });
    if (Number.isFinite(minX)) {
      const pad = 90;
      setView({ x: minX - pad, y: minY - pad, w: (maxX - minX) + pad * 2 || 1000, h: (maxY - minY) + pad * 2 || 700 });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filtered]);

  function pinnedPositions(): Map<string, Pt> {
    const m = new Map<string, Pt>();
    pinned.forEach(id => { const p = pos.get(id); if (p) m.set(id, p); });
    return m;
  }

  // Зум колесом — нативный listener с passive:false (иначе preventDefault → ошибка)
  useEffect(() => {
    const el = svgRef.current; if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const f = e.deltaY > 0 ? 1.12 : 0.89;
      setView(v => { const nw = Math.max(150, Math.min(5000, v.w * f)); const nh = nw * (v.h / v.w); return { x: v.x + (v.w - nw) / 2, y: v.y + (v.h - nh) / 2, w: nw, h: nh }; });
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  const adjacency = useMemo(() => {
    const m = new Map<string, Set<string>>();
    filtered?.edges.forEach(e => {
      (m.get(e.source) ?? m.set(e.source, new Set()).get(e.source)!).add(e.target);
      (m.get(e.target) ?? m.set(e.target, new Set()).get(e.target)!).add(e.source);
    });
    return m;
  }, [filtered]);

  if (!filtered) return <div style={box}>Загрузка графа…</div>;
  if (filtered.nodes.length === 0) return <div style={box}>Нет заметок для графа</div>;

  const showLabels = filtered.nodes.length <= 45 || !!focusId;
  const radius = (n: NoteGraphNode) => Math.min(focusId ? 20 : 26, (focusId && n.id === focusId ? 14 : 7) + (n.degree ?? 0) * 2.2);
  const dim = (id: string) => hover != null && hover !== id && !(adjacency.get(hover)?.has(id));
  const scale = (e: React.PointerEvent) => { const r = (e.currentTarget as SVGElement).getBoundingClientRect(); return { sx: view.w / r.width, sy: view.h / r.height }; };

  const onDown = (e: React.PointerEvent) => { drag.current = { kind: 'pan', x: e.clientX, y: e.clientY, vx: view.x, vy: view.y }; (e.target as Element).setPointerCapture(e.pointerId); };
  const onNodeDown = (e: React.PointerEvent, id: string) => {
    e.stopPropagation();
    drag.current = { kind: 'node', id, x: e.clientX, y: e.clientY };
    (e.target as Element).setPointerCapture(e.pointerId);
  };
  const onMove = (e: React.PointerEvent) => {
    const d = drag.current; if (!d) return;
    const { sx, sy } = scale(e);
    if (d.kind === 'pan') setView(v => ({ ...v, x: d.vx - (e.clientX - d.x) * sx, y: d.vy - (e.clientY - d.y) * sy }));
    else {
      setPos(prev => { const n = new Map(prev); const p = n.get(d.id); if (p) n.set(d.id, { x: p.x + (e.clientX - d.x) * sx, y: p.y + (e.clientY - d.y) * sy }); return n; });
      drag.current = { ...d, x: e.clientX, y: e.clientY };
    }
  };
  const onUp = (e: React.PointerEvent) => {
    if (drag.current?.kind === 'node') {
      const moved = Math.abs(e.clientX - drag.current.x) + Math.abs(e.clientY - drag.current.y);
      // закрепляем перетащенный узел (drag-pin); клик без сдвига — навигация
      if (moved > 2) setPinned(prev => new Set(prev).add(drag.current!.kind === 'node' ? (drag.current as { id: string }).id : ''));
    }
    drag.current = null;
  };

  return (
    <svg ref={svgRef} width="100%" height="100%" viewBox={`${view.x} ${view.y} ${view.w} ${view.h}`}
      style={{ display: 'block', cursor: drag.current ? 'grabbing' : 'grab', touchAction: 'none' }}
      onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerLeave={onUp}>
      {filtered.edges.map((e, i) => {
        const a = pos.get(e.source), b = pos.get(e.target);
        if (!a || !b) return null;
        const active = hover != null && (e.source === hover || e.target === hover);
        return <line key={i} x1={a.x} y1={a.y} x2={b.x} y2={b.y}
          stroke={active ? C.accent : C.border} strokeWidth={active ? 2 : 1.2}
          opacity={hover != null && !active ? 0.25 : 1} />;
      })}
      {filtered.nodes.map(n => {
        const p = pos.get(n.id); if (!p) return null;
        const r = radius(n);
        const selected = n.id === selectedId || n.id === focusId;
        return (
          <g key={n.id} opacity={dim(n.id) ? 0.28 : 1} style={{ cursor: 'pointer' }}
            onPointerEnter={() => setHover(n.id)} onPointerLeave={() => setHover(null)}
            onPointerDown={e => onNodeDown(e, n.id)}
            onClick={() => !n.ghost && onSelectNode(n.id)}>
            {selected && <circle cx={p.x} cy={p.y} r={r + 4} fill="none" stroke={C.accent} strokeWidth={2.5} />}
            <circle cx={p.x} cy={p.y} r={r}
              fill={n.ghost ? C.bgPanel : sourceColor(n.source)}
              stroke={n.ghost ? C.textMuted : (pinned.has(n.id) ? C.textHeading : 'none')}
              strokeWidth={n.ghost ? 1.5 : (pinned.has(n.id) ? 1.5 : 0)}
              strokeDasharray={n.ghost ? '4 3' : undefined} />
            {(showLabels || hover === n.id || selected) && (
              <text x={p.x} y={p.y + r + 13} textAnchor="middle" fontFamily={FONT.sans} fontSize={12}
                fill={n.ghost ? C.textMuted : C.textSecondary}>{n.title}</text>
            )}
          </g>
        );
      })}
    </svg>
  );
}

const box: React.CSSProperties = { padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans };
