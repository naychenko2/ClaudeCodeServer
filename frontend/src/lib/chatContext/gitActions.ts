// Поручения «зафиксировать» git-меню: их собирает ChatPanel (commitViaChat уходит сообщением в чат),
// а панель «Контекст» живёт на странице выше и до них не дотягивается. Строка контекста регистрирует
// обработчики своего чата, пока смонтирована, — панель берёт их отсюда.
export interface GitActions { commitOwn: () => void; commitAll: () => void }

const _actions = new Map<string, GitActions>();

// Возвращает снятие; повторная регистрация чата заменяет прежнюю
export function registerGitActions(sessionId: string, a: GitActions): () => void {
  _actions.set(sessionId, a);
  return () => { if (_actions.get(sessionId) === a) _actions.delete(sessionId); };
}

export const getGitActions = (sessionId: string): GitActions | null => _actions.get(sessionId) ?? null;
