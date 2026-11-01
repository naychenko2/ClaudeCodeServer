// Фильтры календаря: чистые функции без UI.
import type { Project, Task } from '../../types';
import { isSharedTask } from './bits';
import { isParticipant } from '../projects/members/participantGate';

// Ключ фильтра-чипа «Общие» (группы проектов — их id, 'none' — без группы)
export const SHARED_FILTER_KEY = 'shared';

// Фильтр календаря: 'all' | groupId | 'none' | 'shared'. Личные задачи — в «Все» и «Без группы»;
// «Общие» оставляет только задачи чужих общих проектов (предикат isSharedTask)
export function filterCalendarTasks(allTasks: Task[], filter: string, projectsById: Map<string, Project>): Task[] {
  if (filter === 'all') return allTasks;
  if (filter === SHARED_FILTER_KEY) return allTasks.filter(t => isSharedTask(t, projectsById));
  return allTasks.filter(t => {
    if (!t.projectId) return filter === 'none';
    const p = projectsById.get(t.projectId);
    if (!p) return false;
    return filter === 'none' ? !p.groupId : p.groupId === filter;
  });
}

// Чип «Общие» показываем, только если есть чужие общие проекты (я — участник)
export function hasSharedProjects(projects: Project[]): boolean {
  return projects.some(p => isParticipant(p));
}
