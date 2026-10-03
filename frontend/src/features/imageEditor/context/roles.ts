// Роли референсов основного объекта «картинка» (ADR-023 §1, Дополнение 3): зеркало AcceptedRefs
// ImageContextKind на бэкенде — роль принимает он, чужую отвечает 400 role_not_accepted. Картинка и
// файл проекта входят образцом (стиль, объект, персонаж), персонаж проекта — единственной ролью «персонаж»
// (одна роль — «В контекст» без вопроса). Остальные виды основной картинка не берёт.

import type { ContextRole } from 'aihome_shell/kit';

export const IMAGE_SAMPLE_ROLES: readonly ContextRole[] = [
  { role: 'style', label: 'Как образец стиля' },
  { role: 'object', label: 'Как объект' },
  { role: 'character', label: 'Как персонажа' },
];
export const CHARACTER_ROLES: readonly ContextRole[] = [{ role: 'character', label: 'Как персонажа' }];

export function imageRefRoles(candidateKind: string): readonly ContextRole[] {
  if (candidateKind === 'image' || candidateKind === 'project-file') return IMAGE_SAMPLE_ROLES;
  if (candidateKind === 'image-character') return CHARACTER_ROLES;
  return [];
}
