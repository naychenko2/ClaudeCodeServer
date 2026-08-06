import { createContext, useContext, useEffect } from 'react';

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
  // Удержать контролы шапки видимыми, пока панель этого просит. Нужно попапам:
  // меню открывается порталом в body, курсор уходит с карточки — и шапка гасила
  // контролы вместе с кнопкой, которой это меню открыли.
  hold: (held: boolean) => void;
}

const EMPTY: PanelHeaderSlotValue = { hasHeader: false, el: null, elLeft: null, elPinned: null, hold: () => {} };

export const PanelHeaderSlotContext = createContext<PanelHeaderSlotValue>(EMPTY);

// Есть ли у панели шапка, в которую можно положить контролы. Панель с двумя
// раскладками (компактная в шапке / полноразмерная в теле) выбирает по этому
// флагу — как раньше делал проп hideViewSwitcher, только без участия владельца.
export function useHasPanelHeader(): boolean {
  return useContext(PanelHeaderSlotContext).hasHeader;
}

// Пока active — контролы шапки не гаснут, даже если курсор ушёл с карточки.
// Панель вызывает это на время жизни своего попапа (меню, поповера): открыть меню
// кнопкой в шапке и увидеть, как эта кнопка исчезает, — не поведение, а дефект.
export function usePanelHeaderHold(active: boolean): void {
  const { hold } = useContext(PanelHeaderSlotContext);
  useEffect(() => {
    if (!active) return;
    hold(true);
    return () => hold(false);
  }, [active, hold]);
}
