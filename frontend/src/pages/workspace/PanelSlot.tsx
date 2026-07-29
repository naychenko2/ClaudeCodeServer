import type { ReactNode } from 'react';
import { PANEL_MIN_H } from './panelStackState';

// Место панели в колонке — общее для левой и правой зон: высота по весу слота,
// кламп снизу и плавное перераспределение при открытии/закрытии соседей.
//
// Вес — свойство СЛОТА, а не панели: при перестановке панели меняются местами
// вместе с весами (см. swapWith в panelStackState), поэтому раскладка не «прыгает».
export function PanelSlot({ weight = 1, resizing, slotRef, children }: {
  weight?: number;
  // Идёт ручной drag границы — transition выключается, иначе слот отстаёт от курсора
  resizing: boolean;
  slotRef?: (el: HTMLDivElement | null) => void;
  children: ReactNode;
}) {
  return (
    // overflow НЕ hidden: контент клипает сама карточка-остров (PanelShell),
    // а обёртке нельзя — иначе она срезает тень острова (ISLAND.shadow)
    <div
      ref={slotRef}
      style={{
        flex: `${weight} 1 0`, minHeight: PANEL_MIN_H,
        display: 'flex', flexDirection: 'column', minWidth: 0,
        transition: resizing ? 'none' : 'flex-grow 0.15s ease-out',
      }}
    >
      {children}
    </div>
  );
}
