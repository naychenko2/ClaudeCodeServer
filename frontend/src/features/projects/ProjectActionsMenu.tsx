import { useState } from 'react';
import type { CSSProperties, MouseEvent, ReactNode } from 'react';
import type { Project } from '../../types';
import { C, R, FONT, SHADOW, Z } from '../../lib/design';

interface Props {
  project: Project;
  color?: string;                 // цвет иконки-триггера
  onMove: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
}

// Меню действий карточки проекта: «⋯» → переместить / редактировать / удалить.
export function ProjectActionsMenu({ project: p, color = '#B0A697', onMove, onEdit, onDelete }: Props) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ position: 'relative', flexShrink: 0 }} onClick={e => e.stopPropagation()}>
      <button
        onClick={() => setOpen(v => !v)}
        title="Действия"
        style={{
          width: 26, height: 26, borderRadius: R.sm, display: 'flex', alignItems: 'center', justifyContent: 'center',
          color, background: 'none', border: 'none', cursor: 'pointer',
        }}
      >
        <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
          <circle cx="12" cy="5" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="12" cy="19" r="1.6"/>
        </svg>
      </button>
      {open && (
        <>
          <div style={{ position: 'fixed', inset: 0, zIndex: Z.dropdown }} onClick={() => setOpen(false)} />
          <div style={{
            position: 'absolute', top: 30, right: 0, zIndex: Z.dropdown + 1,
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
            boxShadow: SHADOW.dropdown, padding: 5, minWidth: 200, display: 'flex', flexDirection: 'column',
          }}>
            <MenuItem label="Переместить в группу" onClick={() => { setOpen(false); onMove(p); }}
              icon={<path d="M3 7a2 2 0 0 1 2-2h4l2 3h8a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>} />
            <MenuItem label="Редактировать" onClick={(e) => { setOpen(false); onEdit(p, e); }}
              icon={<><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></>} />
            <MenuItem label="Удалить" danger onClick={() => { setOpen(false); onDelete(p); }}
              icon={<><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/></>} />
          </div>
        </>
      )}
    </div>
  );
}

function MenuItem({ label, icon, onClick, danger }: { label: string; icon: ReactNode; onClick: (e: MouseEvent) => void; danger?: boolean }) {
  const [hover, setHover] = useState(false);
  const color = danger ? C.danger : C.textPrimary;
  const st: CSSProperties = {
    display: 'flex', alignItems: 'center', gap: 10, width: '100%', textAlign: 'left',
    background: hover ? C.bgSelected : 'none', border: 'none', borderRadius: R.md,
    padding: '9px 10px', cursor: 'pointer', color, fontSize: 13.5, fontFamily: FONT.sans,
  };
  return (
    <button onClick={onClick} onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)} style={st}>
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0 }}>
        {icon}
      </svg>
      {label}
    </button>
  );
}
