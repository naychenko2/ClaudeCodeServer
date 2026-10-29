// Режим участника общего проекта: чистая логика «что можно в композере» и «чей это проект».
// Без React — проверяется юнит-тестами (S1-F6). Право хода решает бэк (RunTurn только у
// владельца, S1-7a); фронт лишь не обещает того, что бэк откажет.

import type { Project } from '../../../types';

// Ходы редактора включит этап S2a-10 (рамка хода). Пока выключатель один и константа:
// композер заблокирован у ЛЮБОГО участника. Заводить фич-флаг в каталоге ради этого рано.
export const EDITOR_TURNS_ENABLED = false;

// Участник — не владелец общего проекта. myRole авторитетна, когда она есть
export function isParticipant(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return project?.myRole === 'editor' || project?.myRole === 'reader';
}

export type ParticipantComposerGate =
  | { blocked: false }
  | { blocked: true; role: 'editor' | 'reader'; ownerName: string };

// Имя владельца для текстов: displayName, запасной username; без обоих — «владелец»
export function ownerNameOf(project: Pick<Project, 'ownerDisplayName' | 'ownerUsername'> | null | undefined): string {
  return project?.ownerDisplayName?.trim() || project?.ownerUsername?.trim() || 'владелец';
}

// Можно ли участнику запускать ход из композера
export function participantComposerGate(project: Project | null | undefined): ParticipantComposerGate {
  const role = project?.myRole;
  if (role !== 'editor' && role !== 'reader') return { blocked: false };
  if (role === 'editor' && EDITOR_TURNS_ENABLED) return { blocked: false };
  return { blocked: true, role, ownerName: ownerNameOf(project) };
}

// Текст плашки над полем. Читателю — про смену роли; редактору — что ходы включат позже
export function participantComposerText(gate: Extract<ParticipantComposerGate, { blocked: true }>): string {
  return gate.role === 'reader'
    ? `Вы читатель — писать в чаты нельзя. Попросите ${gate.ownerName} сделать вас редактором.`
    : 'Ходы редактора включит следующий этап. Пока чаты проекта доступны только для чтения.';
}

// Подсказка кнопки отправки
export function participantSendTitle(gate: Extract<ParticipantComposerGate, { blocked: true }>): string {
  return gate.role === 'reader' ? 'Не отправится: вы читатель проекта' : 'Не отправится: ходы редактора пока выключены';
}

// Создавать чаты в проекте читателю нельзя: входы создания скрыты
export function canCreateChats(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return project?.myRole !== 'reader';
}
