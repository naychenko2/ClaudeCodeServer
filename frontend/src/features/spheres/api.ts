// Сферы и группы проектов (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// CRUD групп и сводка/память страницы сферы. Тонкая обёртка над живым REST;
// DTO-типы — types/index.ts (переедут в эту папку в S5-P3).
import { request } from '../../lib/offline';
import type {
  DeleteSphereResponse,
  ProjectGroup,
  SphereMemoryEntry,
  SphereMemoryResponse,
  SphereMemoryType,
  SphereOverview,
} from '../../types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.projectGroups.* и api.spheres.* остаются прежними).
export const spheresApi = {
  // Группы проектов
  projectGroups: {
    list: () => request<ProjectGroup[]>('/project-groups'),
    // icon и charter необязательны — сфера создаётся сразу со значком и уставом
    create: (name: string, color: string, extra?: { icon?: string; charter?: string }) =>
      request<ProjectGroup>('/project-groups', { method: 'POST', body: JSON.stringify({ name, color, ...extra }) }),
    // icon: '' снимает значок; charter — устав сферы (до 4000 символов)
    update: (id: string, data: { name?: string; color?: string; icon?: string; charter?: string }) =>
      request<ProjectGroup>(`/project-groups/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    reorder: (orderedIds: string[]) =>
      request<ProjectGroup[]>('/project-groups/reorder', { method: 'POST', body: JSON.stringify({ orderedIds }) }),
    // 409 — в сфере есть персоны (err.body.personas — сколько); память удалению не мешает
    delete: (id: string) => request<DeleteSphereResponse>(`/project-groups/${id}`, { method: 'DELETE' }),
  },

  // Сферы: сводка страницы сферы
  spheres: {
    overview: (id: string) => request<SphereOverview>(`/spheres/${encodeURIComponent(id)}/overview`),
    memory: (id: string) => request<SphereMemoryResponse>(`/spheres/${encodeURIComponent(id)}/memory`),
    addMemory: (id: string, text: string, type?: SphereMemoryType) =>
      request<SphereMemoryEntry>(`/spheres/${encodeURIComponent(id)}/memory`, { method: 'POST', body: JSON.stringify({ text, type }) }),
    removeMemory: (id: string, entryId: string) =>
      request<void>(`/spheres/${encodeURIComponent(id)}/memory/${encodeURIComponent(entryId)}`, { method: 'DELETE' }),
    // Перенос записи проекта на полку сферы; ответ — созданная запись сферы
    adoptMemory: (id: string, projectId: string, entryId: string) =>
      request<SphereMemoryEntry>(`/spheres/${encodeURIComponent(id)}/memory/adopt`, { method: 'POST', body: JSON.stringify({ projectId, entryId }) }),
  },
};
