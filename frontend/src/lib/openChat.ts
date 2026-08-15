// Открыть чат по id из любого места приложения (детали задачи, карточка доклада,
// заметка). Чат бывает двух видов, и раньше каждое место умело только второй:
//   - сессия проекта (чат-исполнитель проектной задачи) — открывается в воркспейсе
//     своего проекта диплинком #/project/{pid}/chat/{chatId} через универсальный
//     обработчик cc-open-url в App: он смонтирует проект, даже если тот не открыт
//     (канал cc-open-project-session на это не годится — он будит только уже
//     смонтированный воркспейс);
//   - чат вне проектов — прежний канал cc-open-chat (раздел «Чаты»).
// Чата нет или запрос упал — тост: молча проглоченный клик человек жмёт ещё и ещё.
// Возвращает, случился ли переход: вызов из модалки закрывает её только на true,
// иначе на пропавшем чате карточка исчезла бы вместе с тостом.

import { api } from './api';
import { showToast } from './toast';

export interface OpenChatOptions {
  // Слова тоста, когда чат не нашёлся: у карточки доклада они про исполнителя,
  // у задачи и заметки — нейтральные
  missingTitle?: string;
  missingBody?: string;
}

export async function openChatById(sessionId: string, opts: OpenChatOptions = {}): Promise<boolean> {
  const missingTitle = opts.missingTitle ?? 'Чат';
  const missingBody = opts.missingBody ?? 'Чат не найден — возможно, он удалён';
  let chat;
  try {
    chat = await api.chats.get(sessionId);
  } catch {
    showToast(missingTitle, missingBody, 'info');
    return false;
  }
  if (!chat) {
    showToast(missingTitle, missingBody, 'info');
    return false;
  }
  if (chat.projectId) {
    const url = `#/project/${encodeURIComponent(chat.projectId)}/chat/${encodeURIComponent(chat.id)}`;
    window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } }));
    return true;
  }
  // Ключ cc_open_chat пишет сам обработчик cc-open-chat в App (для монтирования ChatsPage)
  window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
  return true;
}
