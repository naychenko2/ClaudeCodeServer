// Открыть чат с преднаполненной затравкой из AI-хаба. Переиспользует рабочие каналы
// askClaude (заметки, NotesPage) и file.ask (FileViewer): затравка кладётся в
// sessionStorage['cc_pending_chat_prompt'], а её забирает композер при монтировании
// (Composer.consume) или по событию 'cc-compose-prefill'.
//
// Чтобы затравка ГАРАНТИРОВАННО попала в поле ввода (пустой раздел «Чаты» и проект без
// активного чата НЕ монтируют композер), различаем два случая:
//  • композер уже открыт (ctx.chat.active) — просто наполняем его событием prefill;
//  • композера нет — создаём свежий чат и открываем его тем же каналом, что диплинки:
//    проект → cc-pending-project-chat (WorkspacePage), глобально → cc-open-chat (App/ChatsPage).
//    Новый ChatPanel монтируется и потребляет затравку на маунте.

import type { AiActionCtx } from './actions';
import { api } from '../api';
import { showToast } from '../toast';

const PENDING_KEY = 'cc_pending_chat_prompt';

export async function startChatWithPrompt(text: string, ctx: AiActionCtx): Promise<void> {
  // Проект берём только на его экране — глобальные действия всегда идут в чат вне проекта
  const project = ctx.nav?.screen === 'project' ? ctx.nav.project : undefined;
  sessionStorage.setItem(PENDING_KEY, text);
  try {
    // Композер уже смонтирован (текущий чат — проектный сплит-вид или раздел «Чаты»):
    // наполняем его напрямую, не создавая лишний чат. Заполнится, только если пусто.
    if (ctx.chat.active) {
      window.dispatchEvent(new Event('cc-compose-prefill'));
      return;
    }
    // Подбираем максимально релевантную персону под задачу (best-effort: ошибка/нет персоны —
    // создаём обычный чат, как раньше). Так чат-действие сразу ведёт нужный специалист.
    let personaId: string | null = null;
    try { personaId = (await api.personas.match(text, project?.id)).personaId; } catch { /* без персоны */ }
    if (project) {
      // Чат В ПРОЕКТЕ: создаём сессию (от лица персоны, если подобрана) и открываем каналом диплинка.
      const s = personaId
        ? await api.personas.createChat(personaId, { projectId: project.id })
        : await api.sessions.create(project.id);
      sessionStorage.setItem('cc_pending_project_chat', `${project.id}|${s.id}`);
      window.dispatchEvent(new Event('cc-pending-project-chat'));
    } else {
      // ГЛОБАЛЬНЫЙ чат: от лица персоны (если подобрана) либо обычный чат вне проекта.
      const chat = personaId
        ? await api.personas.createChat(personaId, {})
        : await api.chats.create();
      window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
    }
  } catch (e) {
    sessionStorage.removeItem(PENDING_KEY);
    showToast('Не удалось открыть чат', e instanceof Error ? e.message : 'Сервер недоступен', 'info');
  }
}
