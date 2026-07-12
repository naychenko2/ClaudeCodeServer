import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { C } from '../../lib/design';
import { IconButton, Menu, MenuItem } from '../../components/ui';
import { MoreVertical, Folder, SquarePen, Trash2 } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';

interface Props {
  project: Project;
  color?: string;                 // цвет иконки-триггера
  onMove: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
}

// Меню действий карточки проекта: «⋯» → переместить / редактировать / удалить.
export function ProjectActionsMenu({ project: p, color = C.textMuted, onMove, onEdit, onDelete }: Props) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ position: 'relative', flexShrink: 0 }} onClick={e => e.stopPropagation()}>
      <IconButton onClick={() => setOpen(v => !v)} title="Действия" size="sm" color={color}>
        <MoreVertical size={ICON_SIZE.sm} fill="currentColor" />
      </IconButton>
      {open && (
        <Menu onClose={() => setOpen(false)}>
          <MenuItem label="Переместить в группу" onClick={() => { setOpen(false); onMove(p); }}
            icon={<Folder size={15} strokeWidth={ICON_STROKE} />} />
          <MenuItem label="Редактировать" onClick={(e) => { setOpen(false); onEdit(p, e); }}
            icon={<SquarePen size={15} strokeWidth={ICON_STROKE} />} />
          <MenuItem label="Удалить" danger onClick={() => { setOpen(false); onDelete(p); }}
            icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />} />
        </Menu>
      )}
    </div>
  );
}
