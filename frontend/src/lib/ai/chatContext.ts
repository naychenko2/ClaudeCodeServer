// Признак «сейчас открыт чат» для AI-палитры. Активная сессия проекта НЕ отражается
// в nav (в отличие от раздела «Чаты»), поэтому ChatPanel сам сообщает сюда, что чат
// открыт и есть ли в нём переписка — по этому палитра показывает действия чата
// («Извлечь задачи», «Итог сессии») и в проектных чатах, и в разделе «Чаты».
// tail — краткий хвост переписки (последние реплики) для локального ранжирования.

// personaId — персона открытого чата: резолвер релевантной персоны (useContextPersona)
// красит ею лицо AI-хаба, пока чат на экране.

import { useSyncExternalStore } from 'react';

// Событие форс-пересчёта рекомендаций AI-хаба (диспатчит чат по завершении хода Claude).
export const AI_RECOMPUTE_EVENT = 'cc-ai-recompute';

interface ChatCtx { active: boolean; hasMessages: boolean; tail?: string; personaId?: string }

let _state: ChatCtx = { active: false, hasMessages: false };
const _listeners = new Set<() => void>();

export function setChatContext(active: boolean, hasMessages: boolean, tail?: string, personaId?: string): void {
  _state = { active, hasMessages, tail, personaId };
  _listeners.forEach(fn => fn());
}

export function getChatContext(): ChatCtx {
  return _state;
}

// Реактивная подписка на персону открытого чата (для резолвера аватаров вне чата)
export function useChatPersonaId(): string | undefined {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _state.personaId,
    () => _state.personaId,
  );
}
