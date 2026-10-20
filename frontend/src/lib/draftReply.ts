// Условие приёма cc-draft-reply по chatId: реплика принадлежит ТОМУ чату, который сейчас
// открыт. Карточка черновика в ленте шлёт реплику «в свой» чат — тот, в чьей ленте она
// нарисована (chatId = id этого чата), и слушатели (ChatsPage, WorkspacePage) принимают её,
// только если chatId совпадает с их активным чатом.
//
// Проект НЕ сравниваем: черновик может быть из проекта, а чат — вне проекта (и наоборот),
// и сравнение проектов роняло реплику в чате вне проекта (дефект 289505d9). Постороннее
// событие (чужой chatId, пустой, не строка) не принимается.

// chatId — id чата, в чьей ленте нарисована карточка (не строка/пусто — нет идентификатора);
// activeChatId — id активного чата слушателя; reply — текст реплики.
export function acceptDraftReplyByChat(
  chatId: unknown,
  activeChatId: string | null | undefined,
  reply: string | undefined,
): boolean {
  if (!reply) return false;
  if (typeof chatId !== 'string' || chatId.length === 0) return false;
  if (typeof activeChatId !== 'string' || activeChatId.length === 0) return false;
  return chatId === activeChatId;
}

// Глеб MAJOR-3: решение WorkspacePage (два источника события cc-draft-reply):
//  (1) карточка черновика в ленте — chatId = активный чат воркспейса (acceptDraftReplyByChat,
//      проект НЕ сравниваем: черновик может быть из проекта, а чат — вне проекта);
//  (2) настройки проекта — project события = проект страницы (eventProjectId === pageProjectId).
// Пустая реплика — нет. Источники независимы: приём, если сработал ЛЮБОЙ.
export function acceptDraftReplyInWorkspace(
  chatId: unknown,
  activeChatId: string | null | undefined,
  reply: string | undefined,
  eventProjectId: string | null | undefined,
  pageProjectId: string | null | undefined,
): boolean {
  if (!reply) return false;
  const byChat = acceptDraftReplyByChat(chatId, activeChatId, reply);
  const bySettings = eventProjectId != null && eventProjectId === pageProjectId;
  return byChat || bySettings;
}
