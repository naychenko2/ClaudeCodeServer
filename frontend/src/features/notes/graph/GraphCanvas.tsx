import { useEffect, useRef } from 'react';
import { Maximize } from 'lucide-react';
import { C, FONT, R } from '../../../lib/design';
import { useIsMobile } from '../../../lib/breakpoints';
import { ICON_SIZE } from '../../../components/ui/icons';
import type { GraphSettings } from './graphSettings';
import type { SimApi, SimNode } from './useForceSimulation';
import type { ThemeColors } from './useThemeColors';

// Canvas-рендер графа (стиль Obsidian): собственный rAF draw loop поверх живой
// d3-симуляции. Позиции узлов мутируются симуляцией, React в кадре не участвует.
// Взаимодействие: зум к курсору (колесо/pinch), pan, drag узла (reheat, без
// закрепления), hover-подсветка соседей с плавным затуханием остальных,
// затухание подписей от зума, автофит после первой стабилизации.

const TAU = Math.PI * 2;
const K_MIN = 0.05, K_MAX = 8;

interface View { k: number; tx: number; ty: number }

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

export function GraphCanvas({ api, display, selectedId, focusId, colors, onSelectNode, redrawKey }: {
  api: SimApi;
  display: GraphSettings['display'];
  selectedId: string | null;
  focusId?: string;
  colors: ThemeColors;
  onSelectNode: (id: string) => void;
  redrawKey: unknown;   // смена значения = перерисовать (цвета/радиусы узлов пересчитаны снаружи)
}) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const viewRef = useRef<View>({ k: 1, tx: 0, ty: 0 });
  const dirtyRef = useRef(true);
  const hoverRef = useRef<SimNode | null>(null);
  const highlightRef = useRef<Set<string> | null>(null);
  const scheduleRef = useRef<() => void>(() => {});
  const fitRef = useRef<() => void>(() => {});

  // Пропсы в ref: draw loop живёт в mount-эффекте и читает всегда свежие значения
  const propsRef = useRef({ display, selectedId, focusId, onSelectNode });
  propsRef.current = { display, selectedId, focusId, onSelectNode };

  // Резолвленные цвета темы для canvas (var(--…) он не понимает)
  const themeRef = useRef({ border: '#ccc', accent: '#D97757', text: '#666', muted: '#999', ghostFill: '#eee' });
  useEffect(() => {
    themeRef.current = {
      border: colors.resolve(C.border),
      accent: colors.resolve(C.accent),
      text: colors.resolve(C.textSecondary),
      muted: colors.resolve(C.textMuted),
      ghostFill: colors.resolve(C.bgPanel),
    };
    dirtyRef.current = true;
    scheduleRef.current();
  }, [colors]);

  // Любая смена пропсов (слайдеры отображения, выбор, пересчитанные цвета) — перерисовка
  useEffect(() => {
    dirtyRef.current = true;
    scheduleRef.current();
  }, [redrawKey, display.arrows, display.textFade, display.lineWidth, display.nodeSize, selectedId, focusId]);

  useEffect(() => {
    const wrap = wrapRef.current!;
    const canvas = canvasRef.current!;
    const ctx = canvas.getContext('2d')!;
    const view = viewRef.current;
    let raf: number | null = null;
    let lastTs = 0;
    let viewInit = false;
    let autoFitDone = false;
    let interacted = false;

    // --- отрисовка ---

    const draw = () => {
      const { display, selectedId, focusId } = propsRef.current;
      const th = themeRef.current;
      const dpr = window.devicePixelRatio || 1;
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.setTransform(dpr * view.k, 0, 0, dpr * view.k, dpr * view.tx, dpr * view.ty);

      const nodes = api.nodes();
      const links = api.links();
      const hoverId = hoverRef.current?.id ?? null;

      // Рёбра (+ стрелки направления wikilink source→target)
      for (const l of links) {
        const s = l.source as SimNode, t = l.target as SimNode;
        if (typeof s !== 'object' || typeof t !== 'object' || s.x == null || t.x == null) continue;
        const active = hoverId != null && (s.id === hoverId || t.id === hoverId);
        ctx.globalAlpha = l.fade;
        ctx.strokeStyle = active ? th.accent : th.border;
        ctx.lineWidth = display.lineWidth;
        ctx.beginPath();
        ctx.moveTo(s.x, s.y!);
        ctx.lineTo(t.x, t.y!);
        ctx.stroke();
        if (display.arrows) {
          const dx = t.x - s.x, dy = t.y! - s.y!;
          const d = Math.hypot(dx, dy) || 1;
          const ux = dx / d, uy = dy / d;
          const size = 4 + display.lineWidth * 2;
          const tipX = t.x - ux * (t.r + 2), tipY = t.y! - uy * (t.r + 2);
          const bx = tipX - ux * size, by = tipY - uy * size;
          ctx.fillStyle = active ? th.accent : th.border;
          ctx.beginPath();
          ctx.moveTo(tipX, tipY);
          ctx.lineTo(bx - uy * size * 0.5, by + ux * size * 0.5);
          ctx.lineTo(bx + uy * size * 0.5, by - ux * size * 0.5);
          ctx.closePath();
          ctx.fill();
        }
      }

      // Затухание подписей от зума: порог из настроек (textFade), формула даёт
      // невидимость при отдалении и полную читаемость вблизи
      const tf = display.textFade;
      const labelAlpha = clamp((view.k - 0.6 * tf) / (0.4 * tf), 0, 1);
      ctx.font = `12px ${FONT.sans}`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'top';

      for (const n of nodes) {
        if (n.x == null || n.y == null) continue;
        const selected = n.id === selectedId || n.id === focusId;
        ctx.globalAlpha = n.fade;
        ctx.beginPath();
        ctx.arc(n.x, n.y, n.r, 0, TAU);
        if (n.ghost) {
          // Призрачная заметка: приглушённая заливка + пунктирное кольцо
          ctx.fillStyle = th.ghostFill;
          ctx.fill();
          ctx.setLineDash([4, 3]);
          ctx.strokeStyle = th.muted;
          ctx.lineWidth = 1.2;
          ctx.stroke();
          ctx.setLineDash([]);
        } else {
          ctx.fillStyle = n.color;
          ctx.fill();
        }
        if (selected) {
          ctx.strokeStyle = th.accent;
          ctx.lineWidth = 2;
          ctx.beginPath();
          ctx.arc(n.x, n.y, n.r + 3, 0, TAU);
          ctx.stroke();
        }
        const la = (n.id === hoverId || selected) ? 1 : labelAlpha;
        const alpha = la * n.fade;
        if (alpha > 0.02) {
          ctx.globalAlpha = alpha;
          ctx.fillStyle = n.ghost ? th.muted : th.text;
          ctx.fillText(n.title, n.x, n.y + n.r + 4);
        }
      }
      ctx.globalAlpha = 1;
    };

    // Плавная hover-анимация: экспоненциальный lerp альф к целям (~120 мс)
    const stepFades = (dt: number): boolean => {
      const hl = highlightRef.current;
      const hoverId = hoverRef.current?.id ?? null;
      const ease = 1 - Math.exp(-dt / 120);
      let animating = false;
      for (const n of api.nodes()) {
        const target = !hl ? 1 : (hl.has(n.id) ? 1 : 0.15);
        const d = target - n.fade;
        if (Math.abs(d) > 0.01) { n.fade += d * ease; animating = true; }
        else n.fade = target;
      }
      for (const l of api.links()) {
        const s = l.source as SimNode, t = l.target as SimNode;
        const incident = hoverId != null && typeof s === 'object' && (s.id === hoverId || t.id === hoverId);
        const target = !hl ? 1 : (incident ? 1 : 0.1);
        const d = target - l.fade;
        if (Math.abs(d) > 0.01) { l.fade += d * ease; animating = true; }
        else l.fade = target;
      }
      return animating;
    };

    const loop = (ts: number) => {
      raf = null;
      const dt = Math.min(64, ts - (lastTs || ts));
      lastTs = ts;
      const animating = stepFades(dt);
      if (dirtyRef.current || animating) { dirtyRef.current = false; draw(); }
      const sim = api.sim();
      if (animating || sim.alpha() >= sim.alphaMin()) raf = requestAnimationFrame(loop);
      else lastTs = 0;
    };
    const schedule = () => { if (raf == null) raf = requestAnimationFrame(loop); };
    scheduleRef.current = schedule;

    // --- вид: фит, зум, координаты ---

    const fit = () => {
      const nodes = api.nodes();
      if (!nodes.length) return;
      let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
      for (const n of nodes) {
        if (n.x == null || n.y == null) continue;
        minX = Math.min(minX, n.x - n.r); maxX = Math.max(maxX, n.x + n.r);
        minY = Math.min(minY, n.y - n.r); maxY = Math.max(maxY, n.y + n.r);
      }
      if (!Number.isFinite(minX)) return;
      const w = wrap.clientWidth, h = wrap.clientHeight;
      const pad = 40;
      const k = clamp(Math.min(w / (maxX - minX + pad * 2), h / (maxY - minY + pad * 2)), K_MIN, 2);
      view.k = k;
      view.tx = w / 2 - ((minX + maxX) / 2) * k;
      view.ty = h / 2 - ((minY + maxY) / 2) * k;
      dirtyRef.current = true;
      schedule();
    };
    fitRef.current = fit;

    const pos = (x: number, y: number) => {
      const rect = canvas.getBoundingClientRect();
      return { sx: x - rect.left, sy: y - rect.top };
    };
    const toWorld = (sx: number, sy: number) => ({ wx: (sx - view.tx) / view.k, wy: (sy - view.ty) / view.k });

    const pick = (wx: number, wy: number, slop: number): SimNode | null => {
      const nodes = api.nodes();
      for (let i = nodes.length - 1; i >= 0; i--) {
        const n = nodes[i];
        if (n.x == null || n.y == null) continue;
        const dx = wx - n.x, dy = wy - n.y, rr = n.r + slop;
        if (dx * dx + dy * dy <= rr * rr) return n;
      }
      return null;
    };

    const setHover = (n: SimNode | null) => {
      if (hoverRef.current === n) return;
      hoverRef.current = n;
      if (!n) highlightRef.current = null;
      else {
        const s = new Set([n.id]);
        for (const l of api.links()) {
          const a = l.source as SimNode, b = l.target as SimNode;
          if (typeof a !== 'object' || typeof b !== 'object') continue;
          if (a.id === n.id) s.add(b.id);
          else if (b.id === n.id) s.add(a.id);
        }
        highlightRef.current = s;
      }
      canvas.style.cursor = n ? 'pointer' : 'grab';
      dirtyRef.current = true;
      schedule();
    };

    // --- указатели: pan / drag узла / pinch / hover / клик ---

    type Drag =
      | { kind: 'node'; node: SimNode; startX: number; startY: number; moved: boolean }
      | { kind: 'pan'; startX: number; startY: number; tx0: number; ty0: number };
    let drag: Drag | null = null;
    let pinch: { d0: number; k0: number; mx0: number; my0: number; tx0: number; ty0: number } | null = null;
    const pointers = new Map<number, { x: number; y: number }>();

    const onPointerDown = (e: PointerEvent) => {
      canvas.setPointerCapture(e.pointerId);
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (pointers.size === 2) {
        // Второй палец: гасим pan/drag, входим в pinch
        if (drag?.kind === 'node' && drag.moved) api.dragEnd(drag.node);
        drag = null;
        setHover(null);
        const [p1, p2] = [...pointers.values()];
        const m = pos((p1.x + p2.x) / 2, (p1.y + p2.y) / 2);
        pinch = { d0: Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1, k0: view.k, mx0: m.sx, my0: m.sy, tx0: view.tx, ty0: view.ty };
        interacted = true;
        return;
      }
      const { sx, sy } = pos(e.clientX, e.clientY);
      const { wx, wy } = toWorld(sx, sy);
      const n = pick(wx, wy, (e.pointerType === 'touch' ? 10 : 3) / view.k);
      drag = n
        ? { kind: 'node', node: n, startX: e.clientX, startY: e.clientY, moved: false }
        : { kind: 'pan', startX: e.clientX, startY: e.clientY, tx0: view.tx, ty0: view.ty };
    };

    const onPointerMove = (e: PointerEvent) => {
      if (pointers.has(e.pointerId)) pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (pinch && pointers.size >= 2) {
        const [p1, p2] = [...pointers.values()];
        const d = Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1;
        const m = pos((p1.x + p2.x) / 2, (p1.y + p2.y) / 2);
        const k = clamp(pinch.k0 * (d / pinch.d0), K_MIN, K_MAX);
        // Якорь — midpoint: мировая точка под ним остаётся на месте (+ pan за пальцами)
        view.k = k;
        view.tx = m.sx - (pinch.mx0 - pinch.tx0) * (k / pinch.k0);
        view.ty = m.sy - (pinch.my0 - pinch.ty0) * (k / pinch.k0);
        dirtyRef.current = true;
        schedule();
        return;
      }
      if (!drag) {
        if (e.pointerType === 'mouse') {
          const { sx, sy } = pos(e.clientX, e.clientY);
          const { wx, wy } = toWorld(sx, sy);
          setHover(pick(wx, wy, 3 / view.k));
        }
        return;
      }
      if (drag.kind === 'pan') {
        view.tx = drag.tx0 + (e.clientX - drag.startX);
        view.ty = drag.ty0 + (e.clientY - drag.startY);
        canvas.style.cursor = 'grabbing';
        interacted = true;
        dirtyRef.current = true;
        schedule();
      } else {
        if (!drag.moved && Math.hypot(e.clientX - drag.startX, e.clientY - drag.startY) > 4) {
          drag.moved = true;
          api.dragStart(drag.node);
        }
        if (drag.moved) {
          const { sx, sy } = pos(e.clientX, e.clientY);
          const { wx, wy } = toWorld(sx, sy);
          api.dragMove(drag.node, wx, wy);
          dirtyRef.current = true;
          schedule();
        }
      }
    };

    const onPointerUp = (e: PointerEvent) => {
      pointers.delete(e.pointerId);
      if (pinch) {
        if (pointers.size < 2) pinch = null;
        drag = null;
        return;
      }
      if (drag?.kind === 'node') {
        if (drag.moved) api.dragEnd(drag.node);
        else if (!drag.node.ghost) propsRef.current.onSelectNode(drag.node.id);
      }
      drag = null;
      canvas.style.cursor = hoverRef.current ? 'pointer' : 'grab';
    };

    const onPointerLeave = () => setHover(null);

    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const { sx, sy } = pos(e.clientX, e.clientY);
      const k = clamp(view.k * (e.deltaY > 0 ? 1 / 1.12 : 1.12), K_MIN, K_MAX);
      view.tx = sx - (sx - view.tx) * (k / view.k);
      view.ty = sy - (sy - view.ty) * (k / view.k);
      view.k = k;
      interacted = true;
      dirtyRef.current = true;
      schedule();
    };

    const onDblClick = () => fit();

    // --- размер и hi-DPI ---

    const resize = () => {
      const dpr = window.devicePixelRatio || 1;
      const w = wrap.clientWidth, h = wrap.clientHeight;
      canvas.width = Math.max(1, Math.round(w * dpr));
      canvas.height = Math.max(1, Math.round(h * dpr));
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
      if (!viewInit && w > 0) { viewInit = true; view.tx = w / 2; view.ty = h / 2; }
      dirtyRef.current = true;
      schedule();
    };
    const ro = new ResizeObserver(resize);
    ro.observe(wrap);
    resize();

    // Тик симуляции: пометить кадр грязным; автофит один раз после стабилизации
    api.sim().on('tick.canvas', () => {
      dirtyRef.current = true;
      if (!autoFitDone && !interacted && api.nodes().length > 0 && api.sim().alpha() < 0.3) {
        autoFitDone = true;
        fit();
      }
      schedule();
    });

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    canvas.addEventListener('pointerleave', onPointerLeave);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('dblclick', onDblClick);
    schedule();

    return () => {
      if (raf != null) cancelAnimationFrame(raf);
      ro.disconnect();
      api.sim().on('tick.canvas', null);
      canvas.removeEventListener('pointerdown', onPointerDown);
      canvas.removeEventListener('pointermove', onPointerMove);
      canvas.removeEventListener('pointerup', onPointerUp);
      canvas.removeEventListener('pointercancel', onPointerUp);
      canvas.removeEventListener('pointerleave', onPointerLeave);
      canvas.removeEventListener('wheel', onWheel);
      canvas.removeEventListener('dblclick', onDblClick);
      scheduleRef.current = () => {};
    };
  }, [api]);

  const m = useIsMobile();
  return (
    <div ref={wrapRef} style={{ position: 'absolute', inset: 0, overflow: 'hidden' }}>
      <canvas ref={canvasRef} style={{ display: 'block', touchAction: 'none', cursor: 'grab' }} />
      <button
        onClick={() => fitRef.current()}
        title="Показать весь граф (двойной клик по фону)"
        aria-label="Показать весь граф"
        style={{
          position: 'absolute', right: 10, bottom: 10, width: m ? 40 : 30, height: m ? 40 : 30,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.md,
          color: C.textMuted, cursor: 'pointer', padding: 0,
        }}>
        <Maximize size={ICON_SIZE.sm} strokeWidth={2} />
      </button>
    </div>
  );
}
