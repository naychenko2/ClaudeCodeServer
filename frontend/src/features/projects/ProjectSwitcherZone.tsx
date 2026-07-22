import { useMemo, useState } from 'react';
import { Search } from 'lucide-react';
import { C, R, FS } from '../../lib/design';
import { projectColor, projectInitial } from '../../lib/tasks';
import type { Project } from '../../types';
import { useAllProjects, openProjectViaEvent } from './useAllProjects';
import { usePinnedIds, PINNED_ZONE_MAX } from '../../lib/pinnedProjects';
import { ProjectPalette } from './ProjectPalette';

// Зона переключения проектов внутри активной вкладки «Проекты»: единая пилюля в тонкой
// рамке (navInk) — тёмная «голова» с подписью + значки закреплённых проектов на сером теле
// + лупа (палитра). Показывается только в разделе «Проекты» на десктопе (см. HubTabs).

function ProjectIcon({ p, active, onOpen }: { p: Project; active: boolean; onOpen: (p: Project) => void }) {
  const col = projectColor(p.id);
  return (
    <button
      title={p.name}
      onClick={e => { e.stopPropagation(); onOpen(p); }}
      style={{
        width: 22, height: 22, borderRadius: R.sm, border: `0.5px solid ${C.border}`, cursor: 'pointer',
        background: col.soft, color: col.main, fontSize: FS.sm, fontWeight: 600, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        boxShadow: active ? `0 0 0 2px ${C.bgSelected}, 0 0 0 3px ${C.accent}` : undefined,
      }}
    >{projectInitial(p.name)}</button>
  );
}

export function ProjectSwitcherZone({ currentProjectId }: { currentProjectId?: string }) {
  const projects = useAllProjects();
  const pinnedIds = usePinnedIds();
  const [paletteOpen, setPaletteOpen] = useState(false);

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
      {/* Тёмная голова с подписью раздела */}
      <span style={{ display: 'inline-flex', alignItems: 'center', fontSize: FS.base, fontWeight: 600, color: C.onNavInk, background: C.navInk, padding: '0 11px', whiteSpace: 'nowrap' }}>
        Проекты
      </span>
      {/* Серое тело: значки закреплённых + лупа (палитра) */}
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 5, background: C.bgSelected, padding: '0 8px' }}>
        {zoneProjects.map(p => (
          <ProjectIcon key={p.id} p={p} active={p.id === currentProjectId} onOpen={openProject} />
        ))}
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
      {paletteOpen && <ProjectPalette currentProjectId={currentProjectId} onClose={() => setPaletteOpen(false)} />}
    </div>
  );
}
