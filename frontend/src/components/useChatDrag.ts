// Состояние ручной группировки чатов перетаскиванием (провайдер — ChatGroupingDnd).
// Хук вынесен из компонентного файла: экспорт хука рядом с компонентом ломает
// fast refresh (см. eslint.config.js, примечание к react-refresh/only-export-components).
import { createContext, useContext } from 'react';

export interface DragState {
  draggingId: string | null;
  // Допустимая цель: любой чат, кроме потомков перетаскиваемого. Сам перетаскиваемый
  // чат допустим — drop на себя означает «вынести из группы».
  isValidTarget: (id: string) => boolean;
}

export const ChatDragContext = createContext<DragState>({ draggingId: null, isValidTarget: () => false });

// Состояние перетаскивания для строки списка (ChatTreeRow). Вне провайдера —
// нейтральный дефолт, поэтому строку можно рендерить и без DnD.
export function useChatDrag() {
  return useContext(ChatDragContext);
}
