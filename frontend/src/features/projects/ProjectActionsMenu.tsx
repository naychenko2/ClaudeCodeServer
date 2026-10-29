import { useEffect, useState } from 'react';
import type { MouseEvent } from 'react';
import type { Project } from '../../types';
import { C } from '../../lib/design';
import { IconButton, Menu, MenuItem } from '../../components/ui';
import { MoreVertical, Folder, FolderInput, FolderOutput, SquarePen, Trash2, Pin, PinOff, Settings, UserMinus } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { isPinned, togglePin } from '../../lib/pinnedProjects';
import { useUserScrollGate } from '../../lib/userScrollGate';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { useMe } from '../../lib/defaultPersona';

interface Props {
  project: Project;
  color?: string;                 // цвет иконки-триггера
  onMove: (p: Project) => void;
  onEdit: (p: Project, e: MouseEvent) => void;
  onDelete: (p: Project) => void;
  // Флаг spheres: «Убрать из сферы» (показывается у проекта в сфере)
  onRemoveFromSphere?: (p: Project) => void;
  // «Покинуть проект» для участника (карта п.7). Вызывающий не задал — пункта нет:
  // обновить список после ухода решает вызывающий (row/card), подтверждение ухода —
  // отдельный шаг (карта п.42, S5).
  onLeave?: (p: Project) => void;
}

// Взгляд участника (карта п.7): myRole авторитетна, когда бэк её отдал; в списке
// проектов он её ещё не отдаёт — пока «чужой проект» по ownerId против меня.
// То же решение, что в SharedBadge.kindOf (S1-F4): пока продублировано здесь,
// сводить в один хелпер — когда бэк начнёт отдавать myRole в список.
function isMemberView(p: Project, meId: string | null): boolean {
  if (p.myRole) return p.myRole !== 'owner';
  return !!meId && !!p.ownerId && p.ownerId !== meId;
}

// Меню действий карточки проекта: «⋯» → переместить / редактировать / удалить.
// Участнику чужого проекта — только «Закрепить · Открыть настройки · Покинуть
// проект» (карта п.7): сфера и удаление — решения владельца, не участника.
export function ProjectActionsMenu({ project: p, color = C.textMuted, onMove, onEdit, onDelete, onRemoveFromSphere, onLeave }: Props) {
  const spheres = useFeature(FLAGS.spheres);
  const member = isMemberView(p, useMe().userId);
  // rect кнопки, а не булев флаг: absolute-меню всегда росло ВНИЗ от триггера, и у
  // карточек нижнего ряда уезжало за край экрана — на узких мобиле/планшете туда
  // попадает почти любая карточка. Anchor-режим рисует меню порталом по rect и сам
  // разворачивает его вверх, когда снизу места нет (как в карточке чата).
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const isUserScroll = useUserScrollGate();
  // Меню нарисовано fixed по rect кнопки и при скролле списка осталось бы висеть
  // на месте, оторвавшись от своей карточки: закрываем его вместе с прокруткой
  // (и по Esc) — контракт anchor-режима Menu отдаёт это вызывающей стороне. Гейт
  // «только пользовательский скролл»: программный прижим ленты чата (активность Claude)
  // тоже порождает scroll, но меню, с которым работает человек, схлопывать не должен.
  useEffect(() => {
    if (!menu) return;
    const close = () => setMenu(null);
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(null); };
    const onScroll = () => { if (isUserScroll()) close(); };
    window.addEventListener('scroll', onScroll, true);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('scroll', onScroll, true);
      window.removeEventListener('keydown', onKey);
    };
  }, [menu, isUserScroll]);
  const pinned = isPinned(p.id);
  // Высота меню — по числу пунктов данного взгляда (4 у владельца без сферы, 5 с
  // «Убрать из сферы», 2–3 у участника); по ней Menu решает, раскрываться вниз или вверх
  const menuH = (member ? (onLeave ? 3 : 2) : 4 + (spheres && p.groupId && onRemoveFromSphere ? 1 : 0)) * 34 + 10;
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
        <Menu anchor={menu} onClose={() => setMenu(null)} maxHeight={menuH} gap={4}>
          <MenuItem label={pinned ? 'Открепить' : 'Закрепить'} onClick={() => { setMenu(null); togglePin(p.id); }}
            icon={pinned ? <PinOff size={15} strokeWidth={ICON_STROKE} /> : <Pin size={15} strokeWidth={ICON_STROKE} />} />
          {member ? (
            <>
              {/* Карта п.7: участнику — «Открыть настройки» (тот же диалог, вид только
                  чтения — его решает EditDialog) и «Покинуть проект» вместо удаления:
                  выйти участник может сам, удалить проект — нет. */}
              <MenuItem label="Открыть настройки" onClick={(e) => { setMenu(null); onEdit(p, e); }}
                icon={<Settings size={15} strokeWidth={ICON_STROKE} />} />
              {onLeave && (
                <MenuItem label="Покинуть проект" danger onClick={() => { setMenu(null); onLeave(p); }}
                  icon={<UserMinus size={15} strokeWidth={ICON_STROKE} />} />
              )}
            </>
          ) : (
            <>
              <MenuItem label={spheres ? 'Перенести в сферу…' : 'Переместить в группу'} onClick={() => { setMenu(null); onMove(p); }}
                icon={spheres ? <FolderInput size={15} strokeWidth={ICON_STROKE} /> : <Folder size={15} strokeWidth={ICON_STROKE} />} />
              {spheres && p.groupId && onRemoveFromSphere && (
                <MenuItem label="Убрать из сферы" onClick={() => { setMenu(null); onRemoveFromSphere(p); }}
                  icon={<FolderOutput size={15} strokeWidth={ICON_STROKE} />} />
              )}
              <MenuItem label="Редактировать" onClick={(e) => { setMenu(null); onEdit(p, e); }}
                icon={<SquarePen size={15} strokeWidth={ICON_STROKE} />} />
              <MenuItem label="Удалить" danger onClick={() => { setMenu(null); onDelete(p); }}
                icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />} />
            </>
          )}
        </Menu>
      )}
    </div>
  );
}
