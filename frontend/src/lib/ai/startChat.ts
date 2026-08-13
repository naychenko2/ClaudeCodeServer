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
import type { Project } from '../../types';
import { api } from '../api';
import { createChatWithContextPersona } from '../defaultPersona';
import { showToast } from '../toast';
import { getNav } from '../nav';
import { getFlag } from '../featureFlags';
import { getChatContext } from './chatContext';

const PENDING_KEY = 'cc_pending_chat_prompt';

// Положить затравку в УЖЕ открытый композер (тот же канал, что «Про файл …» в FileViewer
// и цитата раздела в DocsPanel). Текст ложится только в пустое поле — набранный черновик
// важнее. Композера нет — затравка дождётся его в sessionStorage (Composer.consume на маунте).
export function prefillComposer(text: string): void {
  sessionStorage.setItem(PENDING_KEY, text);
  window.dispatchEvent(new Event('cc-compose-prefill'));
}

// Затравка из места, где AiActionCtx неоткуда взять: панель комментариев живёт и в файлах
// проекта, и в заметках, а полный контекст собирает только AI-хаб. Из него здесь нужны
// ровно два поля — nav (в каком проекте открывать чат) и признак смонтированного композера;
// оба доступны теми же снимками, что и в AiLauncher.buildCtx.
export function startChatFromPanel(text: string, opts?: { requiredTool?: string }): Promise<void> {
  return startChatWithPrompt(text, {
    nav: getNav(), online: true, flag: getFlag, caps: { semantic: false }, chat: getChatContext(),
  }, opts);
}

// Новый чат в ЯВНО выбранном проекте с затравкой в композере (UI-инспектор «элемент в чат»).
// Рецепт — AiLauncher.openWaitingChat: ключ cc_pending_project_chat пишем ВСЕГДА до выбора
// канала — смонтированный воркспейс того же проекта иначе восстановил бы СВОЙ прошлый чат,
// и его композер съел бы затравку. openProject — канал открытия другого проекта/раздела:
// вызывающий передаёт openProjectViaEvent (features-слой, из lib его не импортируем).
// Возврат: true — чат создан и открыт; false — ошибка (тост показан, форма вызывающего
// может остаться открытой с набранным текстом).
export async function startChatInProject(
  text: string, project: Project, openProject: (p: Project) => void,
): Promise<boolean> {
  sessionStorage.setItem(PENDING_KEY, text);
  try {
    // Подбор персоны под текст — best-effort, как в startChatWithPrompt
    let personaId: string | null = null;
    try { personaId = (await api.personas.match(text, project.id)).personaId; } catch { /* без персоны */ }
    const s = personaId
      ? await api.personas.createChat(personaId, { projectId: project.id })
      : await createChatWithContextPersona(project, { mode: 'acceptEdits' });
    sessionStorage.setItem('cc_pending_project_chat', `${project.id}|${s.id}`);
    // Дальше — только выбор КАНАЛА открытия: проект уже на экране — cc-open-session не
    // перемонтирует WorkspacePage (тот же key), дёргаем его слушатель напрямую; иначе
    // открываем проект — App смонтирует воркспейс, тот подхватит pending-ключ.
    const n = getNav();
    if (n?.screen === 'project' && n.project?.id === project.id) {
      window.dispatchEvent(new Event('cc-pending-project-chat'));
    } else {
      openProject(project);
    }
    return true;
  } catch (e) {
    // Одноразовый глобальный ключ: не убрать — выстрелит позже в другом чате
    sessionStorage.removeItem(PENDING_KEY);
    showToast('Не удалось открыть чат', e instanceof Error ? e.message : 'Сервер недоступен', 'info');
    return false;
  }
}

// requiredTool — ключ инструментов, без которого действие не выполнить (напр. notes-annotations
// у разбора комментариев). Подбор персоны его учитывает: без ключа персона в выбор не идёт,
// а пустой остаток даёт обычный чат — у него инструменты есть все.
export async function startChatWithPrompt(
  text: string, ctx: AiActionCtx, opts?: { requiredTool?: string },
): Promise<void> {
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
    try { personaId = (await api.personas.match(text, project?.id, opts?.requiredTool)).personaId; } catch { /* без персоны */ }
    if (project) {
      // Чат В ПРОЕКТЕ: создаём сессию (от лица персоны, если подобрана) и открываем каналом
      // диплинка. Без подобранной персоны — дефолт-персона контекста (хелпер инварианта;
      // без флага — прежний api.sessions.create с его дефолтным режимом acceptEdits).
      const s = personaId
        ? await api.personas.createChat(personaId, { projectId: project.id })
        : await createChatWithContextPersona(project, { mode: 'acceptEdits' });
      sessionStorage.setItem('cc_pending_project_chat', `${project.id}|${s.id}`);
      window.dispatchEvent(new Event('cc-pending-project-chat'));
    } else {
      // ГЛОБАЛЬНЫЙ чат: от лица персоны (если подобрана) либо дефолт-персоны контекста.
      const chat = personaId
        ? await api.personas.createChat(personaId, {})
        : await createChatWithContextPersona();
      window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
    }
  } catch (e) {
    sessionStorage.removeItem(PENDING_KEY);
    showToast('Не удалось открыть чат', e instanceof Error ? e.message : 'Сервер недоступен', 'info');
  }
}
