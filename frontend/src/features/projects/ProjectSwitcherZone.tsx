import { useMemo, useState } from 'react';
import { Search } from 'lucide-react';
import { C, R, FS } from '../../lib/design';
import type { Project } from '../../types';
import { ProjectIcon as ProjectIconTile } from './ProjectIcon';
import { useAllProjects, openProjectViaEvent } from './useAllProjects';
import { usePinnedIds, PINNED_ZONE_MAX, movePinned } from '../../lib/pinnedProjects';
import { ProjectPalette } from './ProjectPalette';

// Зона переключения проектов внутри активной вкладки «Проекты»: единая пилюля в тонкой
// рамке (navInk) — тёмная «голова» с подписью + значки закреплённых проектов на сером теле
// + лупа (палитра). Показывается только в разделе «Проекты» на десктопе (см. HubTabs).

function PinnedCell({ p, active, dragging, over, onOpen, onDragStart, onDragOver, onDrop, onDragEnd }: {
  p: Project; active: boolean; dragging: boolean; over: boolean;
  onOpen: (p: Project) => void;
  onDragStart: () => void; onDragOver: () => void; onDrop: () => void; onDragEnd: () => void;
}) {
  return (
    // Ячейка на всю высоту тела с ровными краями: активный проект выделяется тёмной
    // прямоугольной подложкой (без скруглений), цель переноса — кольцом accent.
    <span
      onDragOver={e => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; onDragOver(); }}
      onDrop={e => { e.preventDefault(); e.stopPropagation(); onDrop(); }}
      style={{
        alignSelf: 'stretch', display: 'flex', alignItems: 'center', padding: '0 6px',
        background: active ? C.track : 'transparent',
      }}
    >
      <button
        title={p.name}
        draggable
        onClick={e => { e.stopPropagation(); onOpen(p); }}
        onDragStart={e => { e.stopPropagation(); e.dataTransfer.effectAllowed = 'move'; onDragStart(); }}
        onDragEnd={onDragEnd}
        style={{
          padding: 0, border: 'none', background: 'transparent', cursor: 'pointer', flexShrink: 0,
          display: 'flex', borderRadius: R.sm,
          opacity: dragging ? 0.4 : 1,
          boxShadow: over ? `0 0 0 2px ${C.bgSelected}, 0 0 0 3px ${C.accent}` : undefined,
          transition: 'opacity 0.12s',
        }}
      >
        <ProjectIconTile project={p} size={22} radius={R.sm} />
      </button>
    </span>
  );
}

export function ProjectSwitcherZone({ currentProjectId, onOpenHub }: { currentProjectId?: string; onOpenHub?: () => void }) {
  const projects = useAllProjects();
  const pinnedIds = usePinnedIds();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [headHover, setHeadHover] = useState(false);
  const [dragId, setDragId] = useState<string | null>(null);
  const [overId, setOverId] = useState<string | null>(null);

  const byId = useMemo(() => new Map(projects.map(p => [p.id, p])), [projects]);
  // Закреплённые для зоны: по порядку закрепления, только существующие, максимум PINNED_ZONE_MAX
  const zoneProjects = pinnedIds
    .map(id => byId.get(id))
    .filter((p): p is Project => !!p)
    .slice(0, PINNED_ZONE_MAX);

  const openProject = (p: Project) => openProjectViaEvent(p);
  // Клик по зоне не должен инициировать drag трека PillSwitch
  const stop = (e: React.PointerEvent) => e.stopPropagation();

  return (
    <div onPointerDown={stop} style={{ display: 'inline-flex', alignItems: 'stretch', height: 32, boxSizing: 'border-box', border: `1px solid ${C.navInk}`, borderRadius: R.md, overflow: 'hidden' }}>
      {/* Тёмная голова с подписью раздела — клик ведёт в хаб проектов (к списку) */}
      <button
        title="К списку проектов"
        onClick={e => { e.stopPropagation(); onOpenHub?.(); }}
        onMouseEnter={() => setHeadHover(true)}
        onMouseLeave={() => setHeadHover(false)}
        style={{
          display: 'inline-flex', alignItems: 'center', border: 'none', cursor: 'pointer',
          fontSize: FS.base, fontWeight: 600, fontFamily: 'inherit', color: C.onNavInk,
          background: C.navInk, padding: '0 11px', whiteSpace: 'nowrap',
          opacity: headHover ? 0.85 : 1, transition: 'opacity 0.12s',
        }}
      >
        Проекты
      </button>
      {/* Серое тело: значки закреплённых (ячейки без зазора — подложка активного примыкает ровно) + лупа */}
      <span style={{ display: 'inline-flex', alignItems: 'stretch', background: C.bgSelected }}>
        {zoneProjects.map(p => (
          <PinnedCell
            key={p.id}
            p={p}
            active={p.id === currentProjectId}
            dragging={dragId === p.id}
            over={!!dragId && overId === p.id && dragId !== p.id}
            onOpen={openProject}
            onDragStart={() => setDragId(p.id)}
            onDragOver={() => setOverId(p.id)}
            onDrop={() => { if (dragId && dragId !== p.id) movePinned(dragId, p.id); setDragId(null); setOverId(null); }}
            onDragEnd={() => { setDragId(null); setOverId(null); }}
          />
        ))}
        <span style={{ alignSelf: 'stretch', display: 'flex', alignItems: 'center', padding: '0 8px' }}>
          <button
            aria-label="Все проекты"
            title="Все проекты"
            onClick={e => { e.stopPropagation(); setPaletteOpen(true); }}
            style={{
              width: 22, height: 22, borderRadius: R.sm, border: 'none', background: 'transparent',
              color: C.textSecondary, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
            }}
          >
            <Search size={15} strokeWidth={2} />
          </button>
        </span>
      </span>
      {paletteOpen && <ProjectPalette currentProjectId={currentProjectId} onClose={() => setPaletteOpen(false)} />}
    </div>
  );
}
