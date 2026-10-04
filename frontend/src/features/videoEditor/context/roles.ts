// Роли референсов основного объекта «сцена» (ADR-023 §1, Дополнение 3): зеркало AcceptedRefs VideoContextKind
// на бэкенде — роль принимает он, чужую отвечает 400 role_not_accepted. Картинка и файл проекта входят кадром
// A или кадром B (роль спрашивается при «В контекст ▾»). Фильм референсов не берёт, остальные виды сцена тоже.

import type { ContextRole } from 'aihome_shell/kit';

export const FRAME_ROLES: readonly ContextRole[] = [
  { role: 'frame-a', label: 'Как кадр A' },
  { role: 'frame-b', label: 'Как кадр B' },
];

export function videoRefRoles(candidateKind: string): readonly ContextRole[] {
  return candidateKind === 'image' || candidateKind === 'project-file' ? FRAME_ROLES : [];
}
