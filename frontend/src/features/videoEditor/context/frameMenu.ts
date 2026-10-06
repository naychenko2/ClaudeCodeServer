// Меню чипа «Кадр A ▾» / «Кадр B ▾» (макет composer-actions-v1, сценарий 8): шапка с именем кадра и пункты
// источника — «Править» или «Нарисовать в „Картинках“», «Из проекта», «Кадр B прошлой сцены», «Убрать кадр».
// Здесь только состав и серость пунктов; что пункт делает, подставляет вызывающий (kind.tsx).

import type { ContextMenuItem } from 'aihome_shell/kit';
import type { FrameSlot } from '../store/frameRefs';

export interface FrameMenuInput {
  slot: FrameSlot;
  // Кадр слота сейчас; null — слот пуст
  frame: { label: string } | null;
  personal: boolean;
  // Предыдущая сцена чата: есть ли у неё кадр B, который можно взять в контекст
  prev: { exists: boolean; hasFrameB: boolean; usable: boolean };
  editInImages: () => void;
  drawInImages: () => void;
  fromProject: () => void;
  fromPrevious: () => void;
  clear: () => void;
}

export function buildFrameMenu(i: FrameMenuInput): readonly ContextMenuItem[] {
  const prevReason = !i.prev.exists ? 'У этой сцены нет предыдущей'
    : !i.prev.hasFrameB ? 'У прошлой сцены нет кадра B'
      : !i.prev.usable ? 'Кадр прошлой сцены нельзя взять в контекст' : undefined;
  return [
    {
      id: 'head', label: `Кадр ${i.slot}${i.frame ? ` · ${i.frame.label}` : ' · не выбран'}`,
      disabledReason: i.frame ? 'Сейчас в контексте' : 'Выберите источник ниже', run: () => {},
    },
    i.frame
      ? { id: 'edit', label: 'Править в «Картинках»', run: i.editInImages }
      : { id: 'draw', label: 'Нарисовать в «Картинках»', run: i.drawInImages },
    {
      id: 'project', label: 'Из проекта', run: i.fromProject,
      ...(i.personal ? { disabledReason: 'В личном чате нет проекта' } : {}),
    },
    { id: 'prev', label: 'Кадр B прошлой сцены', run: i.fromPrevious, ...(prevReason ? { disabledReason: prevReason } : {}) },
    ...(i.frame ? [{ id: 'clear', label: 'Убрать кадр', run: i.clear }] : []),
  ];
}
