// Признак «сейчас открыт чат» для AI-палитры. Активная сессия проекта НЕ отражается
// в nav (в отличие от раздела «Чаты»), поэтому ChatPanel сам сообщает сюда, что чат
// открыт и есть ли в нём переписка — по этому палитра показывает действия чата
// («Извлечь задачи», «Итог сессии») и в проектных чатах, и в разделе «Чаты».
// tail — краткий хвост переписки (последние реплики) для локального ранжирования.

// Событие форс-пересчёта рекомендаций AI-хаба (диспатчит чат по завершении хода Claude).
export const AI_RECOMPUTE_EVENT = 'cc-ai-recompute';

interface ChatCtx { active: boolean; hasMessages: boolean; tail?: string }

let _state: ChatCtx = { active: false, hasMessages: false };

export function setChatContext(active: boolean, hasMessages: boolean, tail?: string): void {
  _state = { active, hasMessages, tail };
}

export function getChatContext(): ChatCtx {
  return _state;
}
