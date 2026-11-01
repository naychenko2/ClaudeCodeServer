// S2b-F8: фильтр-чип «Общие» в календаре оставляет только задачи чужих общих проектов.
import { describe, it, expect, vi } from 'vitest';
import type { Project, Task } from '../../../types';

vi.mock('../../../lib/api', () => ({ api: { tasks: {} } }));
vi.mock('../../../lib/signalr', () => ({
  joinUser: async () => {}, onMessage: () => () => {}, onReconnected: () => () => {}, onProjectMembersChanged: () => () => {},
}));

const { filterCalendarTasks, hasSharedProjects, SHARED_FILTER_KEY } = await import('../calendarFilter');

const t = (id: string, projectId?: string) => ({ id, projectId } as Task);
const projects = new Map<string, Project>([
  ['p-foreign', { id: 'p-foreign', myRole: 'editor', groupId: 'g1' } as Project],
  ['p-own', { id: 'p-own', myRole: 'owner', groupId: 'g1' } as Project],
]);
const all = [t('a', 'p-foreign'), t('b', 'p-own'), t('c')];

describe('Календарь: фильтр «Общие»', () => {
  it('оставляет только задачи чужих общих проектов', () => {
    expect(filterCalendarTasks(all, SHARED_FILTER_KEY, projects).map(x => x.id)).toEqual(['a']);
  });
  it('чип виден только при наличии общих проектов', () => {
    expect(hasSharedProjects([...projects.values()])).toBe(true);
    expect(hasSharedProjects([projects.get('p-own')!])).toBe(false);
  });
  it('«Все» и группы работают как прежде', () => {
    expect(filterCalendarTasks(all, 'all', projects)).toHaveLength(3);
    expect(filterCalendarTasks(all, 'g1', projects).map(x => x.id)).toEqual(['a', 'b']);
    expect(filterCalendarTasks(all, 'none', projects).map(x => x.id)).toEqual(['c']);
  });
});
