import type { Task } from '../../types';

// Состояние загрузки списка задач у потребителей, где «0 задач» — утверждение о мире.
// live-запрос при сетевом сбое реджектится (OfflineError), а не отдаёт старый ответ:
// сбой не должен превращаться в пустой список и ложные нули.
export interface TasksLoad {
  /** null — ещё ни одной успешной загрузки */
  tasks: Task[] | null;
  /** последний запрос упал: tasks (если есть) — прежние, не обновлённые */
  failed: boolean;
}

export const TASKS_LOAD_INITIAL: TasksLoad = { tasks: null, failed: false };

export function settleTasks(prev: TasksLoad, result: { ok: Task[] } | { err: unknown }): TasksLoad {
  if ('ok' in result) return { tasks: result.ok, failed: false };
  return { tasks: prev.tasks, failed: true };
}

/** Число для hero: без данных — прочерк, а не «0». */
export function heroCount(tasks: Task[] | null, count: number): string {
  return tasks === null ? '—' : String(count);
}
