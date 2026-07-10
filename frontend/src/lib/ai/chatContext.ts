// Признак «сейчас открыт чат» для AI-палитры. Активная сессия проекта НЕ отражается
// в nav (в отличие от раздела «Чаты»), поэтому ChatPanel сам сообщает сюда, что чат
// открыт и есть ли в нём переписка — по этому палитра показывает действия чата
// («Извлечь задачи», «Итог сессии») и в проектных чатах, и в разделе «Чаты».

let _state: { active: boolean; hasMessages: boolean } = { active: false, hasMessages: false };

export function setChatContext(active: boolean, hasMessages: boolean): void {
  _state = { active, hasMessages };
}

export function getChatContext(): { active: boolean; hasMessages: boolean } {
  return _state;
}
