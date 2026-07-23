// Резолв контекста происхождения чата (задача/автоматизация) — единая точка для
// плашек списка чатов, шапки чата и панели артефактов. Полагается на уже загруженные
// сторы задач/персон (ensureTasksLoaded/ensurePersonasLoaded должны быть вызваны
// где-то выше в дереве компонентов).
import type { Session } from '../types';
import { getTaskById, dueLabel, isDueUrgent } from './tasks';
import { getPersonaById } from './personas';

export interface ChatOriginInfo {
  kind: 'task' | 'automation';
  label: string;
  tone: 'info' | 'warning';
}

// Бейдж происхождения чата — чисто информационный контекст. Клик по нему НЕ уводит с
// карточки чата в раздел (задачи/проактивности): переход намеренно убран, бейдж
// показывает лишь, откуда пришёл чат (см. ChatOriginBadge — всегда некликабельный span).
export function resolveChatOrigin(session: Session): ChatOriginInfo | null {
  if (session.origin === 'task') {
    const task = session.taskId ? getTaskById(session.taskId) : undefined;
    if (!task) return { kind: 'task', label: 'Задача (удалена)', tone: 'info' };
    return { kind: 'task', label: `Задача: ${task.title}`, tone: 'info' };
  }

  if (session.origin === 'automation') {
    const persona = session.personaId ? getPersonaById(session.personaId) : undefined;
    const rule = persona?.automationRules?.find(r => r.id === session.automationRuleId);
    if (!persona || !rule) return { kind: 'automation', label: 'Автоматизация (правило удалено)', tone: 'warning' };
    return { kind: 'automation', label: `Автоматизация: ${rule.name}`, tone: 'warning' };
  }

  return null;
}

// === Компактная карточка чата-задачи (вариант A) ===
// Карточка чата-исполнителя задачи не дублирует заголовок трижды (имя чата +
// плашка происхождения + технический промпт в превью), а показывает его один раз
// и заменяет превью на живой статус выполнения. Данные для этого собирает
// describeTaskChat — используется ТОЛЬКО в ChatCard, шапка/артефакты по-прежнему
// берут «Задача: …» из resolveChatOrigin.

export type TaskChatStatusKind = 'run' | 'wait' | 'done' | 'todo' | 'error' | 'deleted';

export interface TaskChatInfo {
  // Чистый заголовок задачи (без служебного префикса «Задача:») — звучит один раз
  title: string;
  // Полная подпись «Задача: …» — в тултип признака-ключа
  fullLabel: string;
  status: { kind: TaskChatStatusKind; label: string; spinner: boolean };
  subDone: number;
  subTotal: number;
  // Готовая подпись срока («Сегодня · 18:00») или null
  dueText: string | null;
  dueUrgent: boolean;
}

// Ведущее «Задача:» в имени чата убираем — заголовок несёт сам task.title
function stripTaskPrefix(name: string): string {
  return name.replace(/^\s*задача\s*:\s*/i, '').trim();
}

function resolveTaskChatStatus(
  session: Session,
  task: ReturnType<typeof getTaskById>,
): TaskChatInfo['status'] {
  if (!task) return { kind: 'deleted', label: 'Задача удалена', spinner: false };
  // Живые состояния чата приоритетнее — говорят, что происходит прямо сейчас
  switch (session.status) {
    case 'starting':
    case 'working':  return { kind: 'run',  label: 'Выполняется', spinner: true };
    case 'waiting':  return { kind: 'wait', label: 'Ждёт ответа', spinner: false };
    case 'error':    return { kind: 'error', label: 'Ошибка',     spinner: false };
    case 'orphaned': return { kind: 'error', label: 'Прервана',   spinner: false };
  }
  // Спокойный чат — показываем статус самой задачи
  if (task.status === 'done')       return { kind: 'done', label: 'Готово',     spinner: false };
  if (task.status === 'inProgress') return { kind: 'run',  label: 'В работе',   spinner: false };
  return { kind: 'todo', label: 'В очереди', spinner: false };
}

// Данные компактной карточки чата-задачи; null — чат не порождён задачей.
export function describeTaskChat(session: Session): TaskChatInfo | null {
  if (session.origin !== 'task') return null;
  const task = session.taskId ? getTaskById(session.taskId) : undefined;
  const title = task?.title ?? (session.name ? stripTaskPrefix(session.name) : '');
  const fullLabel = task ? `Задача: ${task.title}` : 'Задача (удалена)';
  const subTotal = task?.subtasks.length ?? 0;
  const subDone = task?.subtasks.filter(st => st.isDone).length ?? 0;
  const dueText = task?.dueDate
    ? dueLabel(task.dueDate) + (task.dueTime ? ` · ${task.dueTime}` : '')
    : null;
  return {
    title,
    fullLabel,
    status: resolveTaskChatStatus(session, task),
    subDone,
    subTotal,
    dueText,
    dueUrgent: task ? isDueUrgent(task) : false,
  };
}
