import { useCallback } from 'react';
import { useAllProjects } from '../features/projects/useAllProjects';
import type { SphereOf } from './personaZone';

// Резолвер «проект → сфера» по общему кэшу проектов (сфера = группа проекта)
export function useSphereOf(): SphereOf {
  const projects = useAllProjects();
  return useCallback(id => projects.find(p => p.id === id)?.groupId, [projects]);
}
