import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { IconButton, Menu, MenuItem } from '../../components/ui';

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
      <IconButton onClick={() => setOpen(v => !v)} title="Действия" size="sm" color={color}>
        <svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor">
          <circle cx="12" cy="5" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="12" cy="19" r="1.6"/>
        </svg>
      </IconButton>
      {open && (
        <Menu onClose={() => setOpen(false)}>
          <MenuItem label="Переместить в группу" onClick={() => { setOpen(false); onMove(p); }}
            icon={<path d="M3 7a2 2 0 0 1 2-2h4l2 3h8a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>} />
          <MenuItem label="Редактировать" onClick={(e) => { setOpen(false); onEdit(p, e); }}
            icon={<><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/></>} />
          <MenuItem label="Удалить" danger onClick={() => { setOpen(false); onDelete(p); }}
            icon={<><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/></>} />
        </Menu>
      )}
    </div>
  );
}
