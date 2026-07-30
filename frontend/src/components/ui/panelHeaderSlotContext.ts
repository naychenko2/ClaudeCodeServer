import { createContext, useContext } from 'react';

// Связь «панель ↔ шапка её карточки» для PanelHeaderSlot. Вынесено из .tsx
// отдельным модулем: файл с компонентом не должен экспортировать ничего кроме
// компонентов, иначе ломается fast refresh (правило react-refresh).

export interface PanelHeaderSlotValue {
  // Есть ли над панелью шапка. Известно СРАЗУ, в первом же рендере — в отличие
  // от el, который доезжает через ref только к следующему кадру. Иначе панель,
  // выбирающая раскладку по useHasPanelHeader, успела бы моргнуть телесным
  // вариантом контролов.
  hasHeader: boolean;
  el: HTMLElement | null;
}

const EMPTY: PanelHeaderSlotValue = { hasHeader: false, el: null };

export const PanelHeaderSlotContext = createContext<PanelHeaderSlotValue>(EMPTY);

// Есть ли у панели шапка, в которую можно положить контролы. Панель с двумя
// раскладками (компактная в шапке / полноразмерная в теле) выбирает по этому
// флагу — как раньше делал проп hideViewSwitcher, только без участия владельца.
export function useHasPanelHeader(): boolean {
  return useContext(PanelHeaderSlotContext).hasHeader;
}
