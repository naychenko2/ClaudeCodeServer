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
  // Слот СЛЕВА, сразу за названием панели. Нужен переключателям вида: они
  // относятся к самой панели («что показываем»), и у её имени читаются как часть
  // заголовка, а не как ещё одна кнопка в правой группе действий.
  elLeft: HTMLElement | null;
  // Закреплённый слот справа — контролы, которые НЕ гаснут без курсора. Сюда
  // кладётся главное действие панели («+ Чат», «+ Задача», «+ Проект»): оно
  // должно быть видно всегда, иначе на пустой панели непонятно, чем её наполнить.
  elPinned: HTMLElement | null;
}

const EMPTY: PanelHeaderSlotValue = { hasHeader: false, el: null, elLeft: null, elPinned: null };

export const PanelHeaderSlotContext = createContext<PanelHeaderSlotValue>(EMPTY);

// Есть ли у панели шапка, в которую можно положить контролы. Панель с двумя
// раскладками (компактная в шапке / полноразмерная в теле) выбирает по этому
// флагу — как раньше делал проп hideViewSwitcher, только без участия владельца.
export function useHasPanelHeader(): boolean {
  return useContext(PanelHeaderSlotContext).hasHeader;
}
