// Роли референсов основного объекта «звук» (ADR-023 §1, Дополнение 3): зеркало AcceptedRefs AudioContextKind
// на бэкенде — роль принимает он, чужую отвечает 400 role_not_accepted. Голос из библиотеки входит единственной
// ролью «голос» (одна роль — «В контекст» без вопроса); звук ленты и файл проекта — образцом голоса или куском
// склейки. Остальные виды основной звук не берёт.

import type { ContextRole } from 'aihome_shell/kit';

export const VOICE_ROLES: readonly ContextRole[] = [{ role: 'voice', label: 'Как голос' }];
export const SOUND_ROLES: readonly ContextRole[] = [
  { role: 'reference', label: 'Как образец голоса' },
  { role: 'piece', label: 'Как кусок склейки' },
];

export function audioRefRoles(candidateKind: string): readonly ContextRole[] {
  if (candidateKind === 'audio-voice') return VOICE_ROLES;
  if (candidateKind === 'audio' || candidateKind === 'project-file') return SOUND_ROLES;
  return [];
}
