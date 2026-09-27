// Сигнал ленте чата «действие человека в этой вкладке — прыгни вниз и снова прилипни»:
// то же, что ChatPanel делает для своего сообщения, но для запусков из подсистем
// (генерация картинки из композера, попапа, карточки). Запуски агента и события из
// другой вкладки сюда не идут — им достаточно кнопки «↓».

type Listener = (sessionId: string) => void;
const _listeners = new Set<Listener>();

export function followChat(sessionId: string): void {
  _listeners.forEach(fn => fn(sessionId));
}

// Подписка ленты одного чата: чужие сессии отсеиваются здесь
export function onChatFollow(sessionId: string, fn: () => void): () => void {
  const l: Listener = id => { if (id === sessionId) fn(); };
  _listeners.add(l);
  return () => { _listeners.delete(l); };
}
