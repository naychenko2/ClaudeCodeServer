import { useEffect, useMemo, useRef, useState } from 'react';
import type { NoteGraph, NoteGraphNode } from '../../types';
import { api } from '../../lib/api';
import { useNotesVersion } from '../../lib/notes';
import { C, FONT } from '../../lib/design';
import { sourceColor } from './shared';

interface Pos { id: string; x: number; y: number; dx: number; dy: number }

// Простая force-directed раскладка (Fruchterman–Reingold), считается синхронно
// при изменении данных/фильтра. Без внешних библиотек — рендер собственным SVG,
// чтобы цвета шли из токенов темы и работали в тёмной теме.
function layout(nodes: NoteGraphNode[], edges: { source: string; target: string }[]): Map<string, { x: number; y: number }> {
  const N = nodes.length;
  const W = 1000, H = 700;
  const pos: Pos[] = nodes.map((n, i) => {
    const a = (i / Math.max(1, N)) * Math.PI * 2;
    const r = 120 + (i % 9) * 24;
    return { id: n.id, x: W / 2 + Math.cos(a) * r, y: H / 2 + Math.sin(a) * r, dx: 0, dy: 0 };
  });
  const idx = new Map(pos.map((p, i) => [p.id, i]));
  const k = Math.sqrt((W * H) / Math.max(1, N)) * 0.72;
  const iters = 300;
  for (let it = 0; it < iters; it++) {
    const temp = (1 - it / iters) * 42;
    for (let i = 0; i < N; i++) { pos[i].dx = 0; pos[i].dy = 0; }
    // Отталкивание всех пар
    for (let i = 0; i < N; i++)
      for (let j = i + 1; j < N; j++) {
        let dx = pos[i].x - pos[j].x, dy = pos[i].y - pos[j].y;
        let d = Math.hypot(dx, dy) || 0.01;
        const rep = (k * k) / d;
        const ux = dx / d, uy = dy / d;
        pos[i].dx += ux * rep; pos[i].dy += uy * rep;
        pos[j].dx -= ux * rep; pos[j].dy -= uy * rep;
      }
    // Притяжение по рёбрам
    for (const e of edges) {
      const a = idx.get(e.source), b = idx.get(e.target);
      if (a == null || b == null) continue;
      let dx = pos[a].x - pos[b].x, dy = pos[a].y - pos[b].y;
      let d = Math.hypot(dx, dy) || 0.01;
      const att = (d * d) / k;
      const fx = (dx / d) * att, fy = (dy / d) * att;
      pos[a].dx -= fx; pos[a].dy -= fy;
      pos[b].dx += fx; pos[b].dy += fy;
    }
    // Гравитация к центру + смещение с охлаждением
    for (let i = 0; i < N; i++) {
      pos[i].dx += (W / 2 - pos[i].x) * 0.02;
      pos[i].dy += (H / 2 - pos[i].y) * 0.02;
      const dl = Math.hypot(pos[i].dx, pos[i].dy) || 0.01;
      pos[i].x += (pos[i].dx / dl) * Math.min(dl, temp);
      pos[i].y += (pos[i].dy / dl) * Math.min(dl, temp);
    }
  }
  return new Map(pos.map(p => [p.id, { x: p.x, y: p.y }]));
}

export function NotesGraph({ sourceFilter, selectedId, onSelectNode }: {
  sourceFilter: Set<string> | null;
  selectedId: string | null;
  onSelectNode: (id: string) => void;
}) {
  const version = useNotesVersion();
  const [graph, setGraph] = useState<NoteGraph | null>(null);
  const [hover, setHover] = useState<string | null>(null);
  const [view, setView] = useState({ x: 0, y: 0, w: 1000, h: 700 });
  const drag = useRef<{ x: number; y: number; vx: number; vy: number } | null>(null);

  useEffect(() => {
    let alive = true;
    api.notes.graph().then(g => { if (alive) setGraph(g); }).catch(() => {});
    return () => { alive = false; };
  }, [version]);

  // Фильтрация по источникам (ghost-узлы source='' показываем всегда)
  const filtered = useMemo(() => {
    if (!graph) return null;
    const visible = new Set(graph.nodes
      .filter(n => n.ghost || !sourceFilter || sourceFilter.has(n.source))
      .map(n => n.id));
    const nodes = graph.nodes.filter(n => visible.has(n.id));
    const edges = graph.edges.filter(e => visible.has(e.source) && visible.has(e.target));
    return { nodes, edges };
  }, [graph, sourceFilter]);

  const positions = useMemo(
    () => filtered ? layout(filtered.nodes, filtered.edges) : new Map(),
    [filtered],
  );

  // Пере-центрируем viewBox под облако узлов при пересчёте раскладки
  useEffect(() => {
    if (!positions.size) return;
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    positions.forEach(p => { minX = Math.min(minX, p.x); minY = Math.min(minY, p.y); maxX = Math.max(maxX, p.x); maxY = Math.max(maxY, p.y); });
    const pad = 80;
    setView({ x: minX - pad, y: minY - pad, w: (maxX - minX) + pad * 2 || 1000, h: (maxY - minY) + pad * 2 || 700 });
  }, [positions]);

  // Соседи для подсветки при наведении
  const adjacency = useMemo(() => {
    const m = new Map<string, Set<string>>();
    filtered?.edges.forEach(e => {
      (m.get(e.source) ?? m.set(e.source, new Set()).get(e.source)!).add(e.target);
      (m.get(e.target) ?? m.set(e.target, new Set()).get(e.target)!).add(e.source);
    });
    return m;
  }, [filtered]);

  const degree = useMemo(() => {
    const d = new Map<string, number>();
    filtered?.nodes.forEach(n => d.set(n.id, n.degree));
    return d;
  }, [filtered]);

  if (!filtered) return <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans }}>Загрузка графа…</div>;
  if (filtered.nodes.length === 0)
    return <div style={{ padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans }}>Нет заметок для графа</div>;

  const showLabels = filtered.nodes.length <= 40;
  const nodeRadius = (n: NoteGraphNode) => Math.min(26, 7 + (degree.get(n.id) ?? 0) * 2.2);

  const isDim = (id: string) => hover != null && hover !== id && !(adjacency.get(hover)?.has(id));

  const onWheel = (e: React.WheelEvent) => {
    e.preventDefault();
    const factor = e.deltaY > 0 ? 1.12 : 0.89;
    setView(v => {
      const nw = Math.max(200, Math.min(4000, v.w * factor));
      const nh = nw * (v.h / v.w);
      return { x: v.x + (v.w - nw) / 2, y: v.y + (v.h - nh) / 2, w: nw, h: nh };
    });
  };
  const onDown = (e: React.PointerEvent) => { drag.current = { x: e.clientX, y: e.clientY, vx: view.x, vy: view.y }; (e.target as Element).setPointerCapture(e.pointerId); };
  const onMove = (e: React.PointerEvent) => {
    if (!drag.current) return;
    const rect = (e.currentTarget as SVGElement).getBoundingClientRect();
    const sx = view.w / rect.width, sy = view.h / rect.height;
    setView(v => ({ ...v, x: drag.current!.vx - (e.clientX - drag.current!.x) * sx, y: drag.current!.vy - (e.clientY - drag.current!.y) * sy }));
  };
  const onUp = () => { drag.current = null; };

  return (
    <svg
      width="100%" height="100%"
      viewBox={`${view.x} ${view.y} ${view.w} ${view.h}`}
      style={{ display: 'block', cursor: drag.current ? 'grabbing' : 'grab', touchAction: 'none' }}
      onWheel={onWheel} onPointerDown={onDown} onPointerMove={onMove} onPointerUp={onUp} onPointerLeave={onUp}
    >
      {/* Рёбра */}
      {filtered.edges.map((e, i) => {
        const a = positions.get(e.source), b = positions.get(e.target);
        if (!a || !b) return null;
        const active = hover != null && (e.source === hover || e.target === hover);
        return <line key={i} x1={a.x} y1={a.y} x2={b.x} y2={b.y}
          stroke={active ? C.accent : C.border}
          strokeWidth={active ? 2 : 1.2}
          opacity={hover != null && !active ? 0.25 : 1} />;
      })}
      {/* Узлы */}
      {filtered.nodes.map(n => {
        const p = positions.get(n.id);
        if (!p) return null;
        const r = nodeRadius(n);
        const dim = isDim(n.id);
        const selected = n.id === selectedId;
        return (
          <g key={n.id} opacity={dim ? 0.28 : 1} style={{ cursor: 'pointer' }}
            onPointerEnter={() => setHover(n.id)} onPointerLeave={() => setHover(null)}
            onClick={() => !n.ghost && onSelectNode(n.id)}>
            {selected && <circle cx={p.x} cy={p.y} r={r + 4} fill="none" stroke={C.accent} strokeWidth={2.5} />}
            <circle
              cx={p.x} cy={p.y} r={r}
              fill={n.ghost ? C.bgPanel : sourceColor(n.source)}
              stroke={n.ghost ? C.textMuted : 'none'}
              strokeWidth={n.ghost ? 1.5 : 0}
              strokeDasharray={n.ghost ? '4 3' : undefined}
            />
            {(showLabels || hover === n.id || selected) && (
              <text x={p.x} y={p.y + r + 13} textAnchor="middle"
                fontFamily={FONT.sans} fontSize={12}
                fill={n.ghost ? C.textMuted : C.textSecondary}>{n.title}</text>
            )}
          </g>
        );
      })}
    </svg>
  );
}
