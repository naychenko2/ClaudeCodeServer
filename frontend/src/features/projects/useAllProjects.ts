import { useEffect, useState } from 'react';
import { api } from '../../lib/api';
import type { Project } from '../../types';
import { recordRecentProject } from '../../lib/pinnedProjects';

// Открыть проект из шапки (зона/палитра): переиспользуем событие cc-open-session, которое
// App уже слушает и переводит в раздел «Проекты» + setProject. Заодно отмечаем в «недавних».
export function openProjectViaEvent(p: Project) {
  recordRecentProject(p.id);
  window.dispatchEvent(new CustomEvent('cc-open-session', { detail: { project: p } }));
}

// Общий кэш списка проектов для зоны переключения и палитры: чтобы два компонента шапки
// не дёргали /projects по отдельности. Мягкий TTL — список проектов меняется редко.

const TTL_MS = 60_000;
let cache: Project[] = [];
let fetchedAt = 0;
let inflight: Promise<Project[]> | null = null;
const listeners = new Set<() => void>();

function load(force = false): Promise<Project[]> {
  const fresh = Date.now() - fetchedAt < TTL_MS;
  if (!force && fresh && cache.length) return Promise.resolve(cache);
  if (inflight) return inflight;
  inflight = api.projects.list()
    .then(list => {
      cache = list;
      fetchedAt = Date.now();
      listeners.forEach(l => l());
      return list;
    })
    .catch(() => cache)
    .finally(() => { inflight = null; });
  return inflight;
}

// Сбросить кэш и оповестить подписчиков (напр. после создания/удаления проекта)
export function invalidateProjectsCache() { fetchedAt = 0; void load(true); }

export function useAllProjects(): Project[] {
  const [list, setList] = useState<Project[]>(cache);
  useEffect(() => {
    const update = () => setList(cache);
    listeners.add(update);
    void load().then(update);
    return () => { listeners.delete(update); };
  }, []);
  return list;
}
