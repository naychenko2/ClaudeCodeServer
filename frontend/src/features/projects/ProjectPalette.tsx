import { useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Search, Pin, PinOff, Plus, LayoutGrid } from 'lucide-react';
import { C, R, SHADOW, Z, FONT, FS } from '../../lib/design';
import type { Project } from '../../types';
import { ProjectIcon } from './ProjectIcon';
import { useAllProjects, openProjectViaEvent, openAllProjects, openNewProjectFlow } from './useAllProjects';
import { usePinnedIds, useRecentIds, isPinned, togglePin } from '../../lib/pinnedProjects';

// Командная палитра переключения проектов: поиск + секции «Закреплённые» и «Недавние».
// Открывается лупой в зоне проектов (Ctrl+K занят AI-палитрой). Значки — projectColor/Initial.

function ProjectRowItem({ p, active, onOpen }: { p: Project; active: boolean; onOpen: (p: Project) => void }) {
  const [hover, setHover] = useState(false);
  const pinned = isPinned(p.id);
  return (
    <div
      role="button"
      onClick={() => onOpen(p)}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: 10, padding: '8px 14px', cursor: 'pointer',
        background: hover ? C.bgSelected : 'transparent',
      }}
    >
      <span style={{
        display: 'flex', borderRadius: R.md, flexShrink: 0,
        boxShadow: active ? `0 0 0 2px ${C.bgWhite}, 0 0 0 3px ${C.accent}` : undefined,
      }}>
        <ProjectIcon project={p} size={26} radius={R.md} />
      </span>
      <span style={{ flex: 1, minWidth: 0, fontSize: FS.md, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {p.name}
      </span>
      <button
        aria-label={pinned ? 'Открепить' : 'Закрепить'}
        title={pinned ? 'Открепить' : 'Закрепить'}
        onClick={e => { e.stopPropagation(); togglePin(p.id); }}
        style={{
          flexShrink: 0, width: 26, height: 26, borderRadius: R.md, border: 'none', cursor: 'pointer',
          background: 'none', color: pinned ? C.accent : C.textMuted,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          opacity: pinned || hover ? 1 : 0, transition: 'opacity 0.12s',
        }}
      >
        {pinned ? <Pin size={15} strokeWidth={2} /> : <PinOff size={15} strokeWidth={2} />}
      </button>
    </div>
  );
}

// Секция списка проектов. Живёт на уровне модуля (static-components): объявленная
// внутри ProjectPalette, она пересоздавалась бы на каждый рендер, а вместе с ней
// сбрасывалось бы состояние её поддерева.
function PaletteSection({ title, items, currentProjectId, onOpen }: {
  title: string; items: Project[]; currentProjectId?: string; onOpen: (p: Project) => void;
}) {
  if (!items.length) return null;
  return (
    <>
      <div style={{ padding: '8px 14px 4px', fontSize: FS.xs, color: C.textMuted, letterSpacing: '.04em', textTransform: 'uppercase' }}>{title}</div>
      {items.map(p => <ProjectRowItem key={p.id} p={p} active={p.id === currentProjectId} onOpen={onOpen} />)}
    </>
  );
}

export function ProjectPalette({ currentProjectId, onClose }: { currentProjectId?: string; onClose: () => void }) {
  const projects = useAllProjects();
  const pinnedIds = usePinnedIds();
  const recentIds = useRecentIds();
  const [q, setQ] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    inputRef.current?.focus();
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const byId = useMemo(() => new Map(projects.map(p => [p.id, p])), [projects]);
  const match = (p: Project) => p.name.toLowerCase().includes(q.trim().toLowerCase());

  // Текущий проект — отдельной секцией сверху (незакреплённый иначе не попадал бы
  // в палитру вовсе, и закрепить его было бы негде). Закреплённый уже виден в
  // «Закреплённых» с обводкой — не дублируем.
  const currentProject = currentProjectId ? byId.get(currentProjectId) : undefined;
  const currentList = currentProject && !isPinned(currentProject.id) && match(currentProject)
    ? [currentProject] : [];
  // Закреплённые — по порядку закрепления; недавние — по MRU, исключая уже закреплённые
  const pinnedList = pinnedIds.map(id => byId.get(id)).filter((p): p is Project => !!p && match(p));
  const recentList = recentIds
    .map(id => byId.get(id))
    .filter((p): p is Project => !!p && !isPinned(p.id) && p.id !== currentProjectId && match(p));
  // Прочие проекты (не закреплённые, не недавние) — чтобы палитра давала доступ ко всем
  const restList = projects
    .filter(p => !isPinned(p.id) && !recentIds.includes(p.id) && p.id !== currentProjectId && match(p))
    .sort((a, b) => (b.updatedAt || '').localeCompare(a.updatedAt || ''));

  const open = (p: Project) => { openProjectViaEvent(p); onClose(); };

  // «Все проекты» — к списку проектов (из открытого проекта диплинк #/projects выводит
  // к списку через switchHubTab); «Новый проект» — туда же + флаг, по которому
  // ProjectListPage сразу откроет диалог создания
  const goAllProjects = () => { onClose(); openAllProjects(); };
  const goNewProject = () => { onClose(); openNewProjectFlow(); };

  const empty = !currentList.length && !pinnedList.length && !recentList.length && !restList.length;

  // Портал в body обязателен: палитру открывают в том числе из панели-карточки,
  // а та несёт transform (анимация появления) и overflow:hidden — внутри неё
  // position:fixed отсчитывается от карточки и обрезается по её краю.
  return createPortal(
    <div
      onClick={onClose}
      style={{
        position: 'fixed', inset: 0, zIndex: Z.modal, background: C.overlay,
        display: 'flex', alignItems: 'flex-start', justifyContent: 'center', paddingTop: '14vh',
        fontFamily: FONT.sans,
      }}
    >
      <div
        onClick={e => e.stopPropagation()}
        style={{
          width: 480, maxWidth: '92vw', maxHeight: '70vh', display: 'flex', flexDirection: 'column',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.modal,
          boxShadow: SHADOW.modal, overflow: 'hidden',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '12px 16px', borderBottom: `1px solid ${C.border}` }}>
          <Search size={18} strokeWidth={2} color={C.textMuted} />
          <input
            autoComplete="off"
            ref={inputRef}
            value={q}
            onChange={e => setQ(e.target.value)}
            placeholder="Перейти к проекту…"
            style={{ flex: 1, border: 'none', outline: 'none', background: 'transparent', fontSize: FS.md, color: C.textHeading, fontFamily: 'inherit' }}
          />
        </div>
        <div style={{ overflowY: 'auto', padding: '4px 0 8px' }}>
          <PaletteSection title="Текущий" items={currentList} currentProjectId={currentProjectId} onOpen={open} />
          <PaletteSection title="Закреплённые" items={pinnedList} currentProjectId={currentProjectId} onOpen={open} />
          <PaletteSection title="Недавние" items={recentList} currentProjectId={currentProjectId} onOpen={open} />
          <PaletteSection title={pinnedList.length || recentList.length ? 'Все проекты' : 'Проекты'} items={restList} currentProjectId={currentProjectId} onOpen={open} />
          {empty && (
            <div style={{ padding: 20, textAlign: 'center', color: C.textMuted, fontSize: FS.base }}>
              Ничего не найдено
            </div>
          )}
        </div>
        {/* Футер: создание проекта + переход к списку проектов */}
        <div style={{ display: 'flex', gap: 6, padding: '8px 10px', borderTop: `1px solid ${C.border}`, flexShrink: 0 }}>
          <FooterButton icon={<Plus size={15} strokeWidth={2} />} label="Новый проект" onClick={goNewProject} />
          <span style={{ flex: 1 }} />
          <FooterButton icon={<LayoutGrid size={15} strokeWidth={2} />} label="Все проекты" onClick={goAllProjects} />
        </div>
      </div>
    </div>,
    document.body,
  );
}

function FooterButton({ icon, label, onClick }: { icon: React.ReactNode; label: string; onClick: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6, padding: '6px 10px',
        border: 'none', borderRadius: R.md, cursor: 'pointer',
        background: hover ? C.bgSelected : 'transparent',
        fontFamily: 'inherit', fontSize: FS.base, color: C.textPrimary,
        transition: 'background 0.12s',
      }}
    >
      {icon}
      {label}
    </button>
  );
}
