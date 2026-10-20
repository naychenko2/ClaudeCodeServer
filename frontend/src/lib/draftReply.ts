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
