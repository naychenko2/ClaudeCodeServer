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

// Единый гейт «запустить ход / ответить агенту» для входов вне композера (чипы-подсказки,
// «Повторить», «Зафиксировать», «Возобновить», ответы на план/вопрос/разрешение, механики
// команды, сброс режимов). Бэк откажет участнику (RunTurn только у владельца), поэтому
// запрос не шлём вовсе. null — можно; строка — текст отказа для тоста и подсказки кнопки
export function turnDeniedReason(project: Project | null | undefined): string | null {
  const gate = participantComposerGate(project);
  return gate.blocked ? participantSendTitle(gate).replace(/^Не отправится: /, 'Недоступно: ') : null;
}

// Оборачивает действие гейтом: у участника вместо вызова — onDenied(текст), результат undefined.
// Проект читается в момент вызова (getProject), чтобы обёртка могла быть стабильной по ссылке
export function guardTurnAction<A extends unknown[], R>(
  getProject: () => Project | null | undefined,
  action: (...args: A) => R,
  onDenied: (reason: string) => void,
): (...args: A) => R | undefined {
  return (...args: A) => {
    const reason = turnDeniedReason(getProject());
    if (reason) { onDenied(reason); return undefined; }
    return action(...args);
  };
}

// Блокировка отправки композера: проект есть и (ход недоступен по исполнению ИЛИ участник).
// Единственное место решения — из него же берётся disabled кнопки отправки и сброс режимов
export function composerExecBlocked(
  project: Project | null | undefined,
  execAvailable: boolean,
  gate: ParticipantComposerGate,
): boolean {
  return !!project && (!execAvailable || gate.blocked);
}
