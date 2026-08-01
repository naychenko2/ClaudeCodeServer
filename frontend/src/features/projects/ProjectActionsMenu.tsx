import { useEffect, useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { C } from '../../lib/design';
import { IconButton, Menu, MenuItem } from '../../components/ui';
import { MoreVertical, Folder, SquarePen, Trash2, Pin, PinOff } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { isPinned, togglePin } from '../../lib/pinnedProjects';

interface Props {
  project: Project;
  color?: string;                 // цвет иконки-триггера
  onMove: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
}

// Высота меню (4 пункта) — по ней Menu решает, раскрываться вниз или вверх
const MENU_H = 4 * 34 + 10;

// Меню действий карточки проекта: «⋯» → переместить / редактировать / удалить.
export function ProjectActionsMenu({ project: p, color = C.textMuted, onMove, onEdit, onDelete }: Props) {
  // rect кнопки, а не булев флаг: absolute-меню всегда росло ВНИЗ от триггера, и у
  // карточек нижнего ряда уезжало за край экрана — на узких мобиле/планшете туда
  // попадает почти любая карточка. Anchor-режим рисует меню порталом по rect и сам
  // разворачивает его вверх, когда снизу места нет (как в карточке чата).
  const [menu, setMenu] = useState<DOMRect | null>(null);
  // Меню нарисовано fixed по rect кнопки и при скролле списка осталось бы висеть
  // на месте, оторвавшись от своей карточки: закрываем его вместе с прокруткой
  // (и по Esc) — контракт anchor-режима Menu отдаёт это вызывающей стороне.
  useEffect(() => {
    if (!menu) return;
    const close = () => setMenu(null);
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(null); };
    window.addEventListener('scroll', close, true);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('keydown', onKey);
    };
  }, [menu]);
  const pinned = isPinned(p.id);
  return (
    <div style={{ position: 'relative', flexShrink: 0 }} onClick={e => e.stopPropagation()}>
      <IconButton
        onClick={e => {
          const r = e.currentTarget.getBoundingClientRect();
          setMenu(prev => (prev ? null : r));
        }}
        title="Действия" size="sm" color={color} active={!!menu}
      >
        <MoreVertical size={ICON_SIZE.sm} fill="currentColor" />
      </IconButton>
      {menu && (
        <Menu anchor={menu} onClose={() => setMenu(null)} maxHeight={MENU_H} gap={4}>
          <MenuItem label={pinned ? 'Открепить' : 'Закрепить'} onClick={() => { setMenu(null); togglePin(p.id); }}
            icon={pinned ? <PinOff size={15} strokeWidth={ICON_STROKE} /> : <Pin size={15} strokeWidth={ICON_STROKE} />} />
          <MenuItem label="Переместить в группу" onClick={() => { setMenu(null); onMove(p); }}
            icon={<Folder size={15} strokeWidth={ICON_STROKE} />} />
          <MenuItem label="Редактировать" onClick={(e) => { setMenu(null); onEdit(p, e); }}
            icon={<SquarePen size={15} strokeWidth={ICON_STROKE} />} />
          <MenuItem label="Удалить" danger onClick={() => { setMenu(null); onDelete(p); }}
            icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />} />
        </Menu>
      )}
    </div>
  );
}
