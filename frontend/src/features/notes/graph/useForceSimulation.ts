import { useEffect, useMemo, useRef } from 'react';
import {
  forceCollide, forceLink, forceManyBody, forceSimulation, forceX, forceY,
  type ForceCollide, type ForceLink, type ForceManyBody, type ForceX, type ForceY,
  type Simulation, type SimulationLinkDatum, type SimulationNodeDatum,
} from 'd3-force';
import type { NoteGraph } from '../../../types';
import type { GraphSettings } from './graphSettings';

// Живая d3-force симуляция — та же физическая модель, что в Obsidian Graph View:
// forceX/forceY (центральная сила), forceManyBody (отталкивание), forceLink
// (сила и длина связей) + forceCollide против наложений. Узлы и связи —
// мутабельные массивы в refs: позиции меняются каждый тик, React в кадре
// не участвует (отрисовка — GraphCanvas).

export interface SimNode extends SimulationNodeDatum {
  id: string;
  title: string;
  source: string;
  sourceLabel: string;
  degree: number;
  ghost: boolean;
  tags?: string[];
  r: number;       // радиус (мировые единицы); назначает NotesGraph по degree и слайдеру
  color: string;   // резолвленный цвет заливки (группа или источник)
  fade: number;    // текущая альфа hover-подсветки (анимируется в draw loop)
}

export interface SimLink extends SimulationLinkDatum<SimNode> {
  fade: number;
}

export interface SimApi {
  nodes: () => SimNode[];
  links: () => SimLink[];
  sim: () => Simulation<SimNode, SimLink>;
  // Пересчитать радиусы collide после смены n.r (слайдер размера) + мягкий reheat
  refreshCollide: () => void;
  dragStart: (n: SimNode) => void;
  dragMove: (n: SimNode, x: number, y: number) => void;
  dragEnd: (n: SimNode) => void;
}

const linkKey = (l: SimLink) => {
  const id = (e: SimLink['source']) => typeof e === 'object' ? e.id : String(e);
  return `${id(l.source)}→${id(l.target)}`;
};

export function useForceSimulation(graph: NoteGraph | null, forces: GraphSettings['forces']): SimApi {
  const nodesRef = useRef<SimNode[]>([]);
  const linksRef = useRef<SimLink[]>([]);
  const simRef = useRef<Simulation<SimNode, SimLink> | null>(null);
  const forcesRef = useRef(forces);
  // Число связей узла в текущем наборе — для d3-дефолтной силы связи 1/min(cnt)
  const countsRef = useRef(new Map<string, number>());

  const api = useMemo<SimApi>(() => {
    const getSim = (): Simulation<SimNode, SimLink> => {
      if (!simRef.current) {
        simRef.current = forceSimulation<SimNode>([])
          .force('x', forceX<SimNode>(0))
          .force('y', forceY<SimNode>(0))
          .force('charge', forceManyBody<SimNode>())
          .force('link', forceLink<SimNode, SimLink>([]).id(n => n.id))
          .force('collide', forceCollide<SimNode>())
          .alphaDecay(0.02)
          .velocityDecay(0.4)
          .alphaMin(0.001);
      }
      return simRef.current;
    };
    const cnt = (e: SimLink['source']) =>
      countsRef.current.get(typeof e === 'object' ? e.id : String(e)) ?? 1;
    const applyForces = () => {
      const sim = getSim();
      const f = forcesRef.current;
      (sim.force('x') as ForceX<SimNode>).strength(f.center * 0.1);
      (sim.force('y') as ForceY<SimNode>).strength(f.center * 0.1);
      (sim.force('charge') as ForceManyBody<SimNode>).strength(-f.repel * 20).distanceMax(1000).theta(0.9);
      (sim.force('link') as ForceLink<SimNode, SimLink>)
        .distance(f.linkDistance)
        .strength(l => f.link / Math.min(cnt(l.source), cnt(l.target)));
      (sim.force('collide') as ForceCollide<SimNode>).radius(n => n.r + 2).strength(0.6);
    };
    return {
      nodes: () => nodesRef.current,
      links: () => linksRef.current,
      sim: getSim,
      refreshCollide() {
        (getSim().force('collide') as ForceCollide<SimNode>).radius(n => n.r + 2);
        getSim().alpha(Math.max(getSim().alpha(), 0.3)).restart();
      },
      dragStart(n) {
        getSim().alphaTarget(0.3).restart();
        n.fx = n.x; n.fy = n.y;
      },
      dragMove(n, x, y) { n.fx = x; n.fy = y; },
      // Как в Obsidian: отпущенный узел снова свободен, никакого закрепления
      dragEnd(n) {
        getSim().alphaTarget(0);
        n.fx = null; n.fy = null;
      },
      // приватное для эффектов ниже
      _applyForces: applyForces,
    } as SimApi & { _applyForces: () => void };
  }, []);
  const applyForces = (api as SimApi & { _applyForces: () => void })._applyForces;

  // Слайдеры сил: живое обновление параметров + reheat
  useEffect(() => {
    forcesRef.current = forces;
    applyForces();
    api.sim().alpha(0.5).restart();
  }, [forces.center, forces.repel, forces.link, forces.linkDistance, api, applyForces, forces]);

  // Смена данных/фильтров: инкрементальный merge — выжившие узлы сохраняют
  // позицию и скорость, новые появляются возле связанного соседа и плавно вливаются
  useEffect(() => {
    if (!graph) return;
    const sim = api.sim();
    const prevNodes = new Map(nodesRef.current.map(n => [n.id, n]));
    const prevLinks = new Map(linksRef.current.map(l => [linkKey(l), l]));

    const nodes: SimNode[] = graph.nodes.map(g => {
      const old = prevNodes.get(g.id);
      if (old) {
        // обновляем данные, позиция/скорость остаются
        old.title = g.title; old.source = g.source; old.sourceLabel = g.sourceLabel;
        old.degree = g.degree; old.ghost = g.ghost; old.tags = g.tags;
        return old;
      }
      return { ...g, r: 6, color: '#888888', fade: 1 };
    });
    const byId = new Map(nodes.map(n => [n.id, n]));

    const counts = new Map<string, number>();
    const adj = new Map<string, string[]>();
    const links: SimLink[] = graph.edges.map(e => {
      counts.set(e.source, (counts.get(e.source) ?? 0) + 1);
      counts.set(e.target, (counts.get(e.target) ?? 0) + 1);
      (adj.get(e.source) ?? adj.set(e.source, []).get(e.source)!).push(e.target);
      (adj.get(e.target) ?? adj.set(e.target, []).get(e.target)!).push(e.source);
      const old = prevLinks.get(`${e.source}→${e.target}`);
      return { source: e.source, target: e.target, fade: old?.fade ?? 1 };
    });
    countsRef.current = counts;

    // Стартовые позиции новых узлов: рядом с уже размещённым соседом, иначе у центроида
    let cx = 0, cy = 0, placed = 0;
    for (const n of nodes) if (n.x != null) { cx += n.x; cy += n.y ?? 0; placed++; }
    if (placed) { cx /= placed; cy /= placed; }
    for (const n of nodes) {
      if (n.x != null) continue;
      const nb = (adj.get(n.id) ?? []).map(id => byId.get(id)).find(m => m && m.x != null);
      const bx = nb?.x ?? cx, by = nb?.y ?? cy;
      n.x = bx + (Math.random() - 0.5) * 60;
      n.y = by + (Math.random() - 0.5) * 60;
    }

    nodesRef.current = nodes;
    linksRef.current = links;
    sim.nodes(nodes);
    (sim.force('link') as ForceLink<SimNode, SimLink>).links(links);
    applyForces();
    sim.alpha(prevNodes.size ? 0.5 : 1).restart();
  }, [graph, api, applyForces]);

  // Остановка симуляции при размонтировании (и пересоздание при remount в StrictMode)
  useEffect(() => () => {
    simRef.current?.stop();
    simRef.current = null;
    nodesRef.current = [];
    linksRef.current = [];
  }, []);

  return api;
}
