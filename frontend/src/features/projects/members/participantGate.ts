// Режим участника общего проекта: чистая логика «что можно в композере» и «чей это проект».
// Без React — проверяется юнит-тестами (S1-F6). Право хода решает бэк (RunTurn у владельца
// и редактора, S2a-10a; ответы на разрешения/планы — только Configure владельца); фронт лишь
// не обещает того, что бэк откажет.

import type { Project } from '../../../types';

// Участник — не владелец общего проекта. myRole авторитетна, когда она есть
export function isParticipant(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return project?.myRole === 'editor' || project?.myRole === 'reader';
}

export type ParticipantComposerGate =
  | { blocked: false }
  | { blocked: true; role: 'reader'; ownerName: string };

// Имя владельца для текстов: displayName, запасной username; без обоих — «владелец»
export function ownerNameOf(project: Pick<Project, 'ownerDisplayName' | 'ownerUsername'> | null | undefined): string {
  return project?.ownerDisplayName?.trim() || project?.ownerUsername?.trim() || 'владелец';
}

// Можно ли участнику запускать ход из композера: редактор может (право RunTurn, S2a-10a),
// читатель — нет
export function participantComposerGate(project: Project | null | undefined): ParticipantComposerGate {
  if (project?.myRole !== 'reader') return { blocked: false };
  return { blocked: true, role: 'reader', ownerName: ownerNameOf(project) };
}

// Текст плашки над полем: читателю — про смену роли
export function participantComposerText(gate: Extract<ParticipantComposerGate, { blocked: true }>): string {
  return `Вы читатель — писать в чаты нельзя. Попросите ${gate.ownerName} сделать вас редактором.`;
}

// Подсказка кнопки отправки
export function participantSendTitle(_gate: Extract<ParticipantComposerGate, { blocked: true }>): string {
  return 'Не отправится: вы читатель проекта';
}

// Переименовать, архивировать и удалить чат может только его автор (в S1 — владелец проекта).
// null — можно; строка — причина для подсказки выключенного пункта и тоста
export const CHAT_AUTHOR_ONLY = 'Только автор чата';
export function chatAuthorOnlyReason(project: Pick<Project, 'myRole'> | null | undefined): string | null {
  return isParticipant(project) ? CHAT_AUTHOR_ONLY : null;
}

// Причина блокировки действия шапки чата для участника: только rename/archive/delete
// принадлежат автору; остальные пункты («Теги», «Срок» и др.) гейт не трогает
const AUTHOR_ONLY_ACTIONS: readonly string[] = ['rename', 'archive', 'delete'];
export function chatActionAuthorLock(key: string, project: Pick<Project, 'myRole'> | null | undefined): string | null {
  return AUTHOR_ONLY_ACTIONS.includes(key) ? chatAuthorOnlyReason(project) : null;
}

// Сообщение об отзыве доступа на списке проектов (QA-1): базовый текст фиксирован; строка
// с именем владельца добавляется только когда сервер его принёс (у проекта без владельца
// ownerDisplayName — null). Формулировка та же, что у плашки в воркспейсе.
export const ACCESS_REVOKED_TITLE = 'Доступ к проекту отозван';
export function accessRevokedOwnerLine(ownerDisplayName?: string | null): string | null {
  const name = ownerDisplayName?.trim();
  return name ? `${name} закрыл вам доступ к проекту.` : null;
}

// Бэк мог всё же отказать (устаревшая роль на клиенте): 403 с code not_chat_author
export function isNotChatAuthorError(e: unknown): boolean {
  const body = (e as { status?: number; body?: { code?: string } } | null);
  return body?.status === 403 && body.body?.code === 'not_chat_author';
}

// Создавать чаты в проекте читателю нельзя: входы создания скрыты
export function canCreateChats(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return project?.myRole !== 'reader';
}

// Гейт «запустить ход / ответить на вопрос» для входов вне композера (чипы-подсказки,
// «Повторить», «Зафиксировать», «Возобновить», сброс режимов). RunTurn есть у редактора,
// читателю бэк откажет — запрос не шлём вовсе. null — можно; строка — текст отказа для тоста и подсказки кнопки
export function turnDeniedReason(project: Project | null | undefined): string | null {
  const gate = participantComposerGate(project);
  return gate.blocked ? participantSendTitle(gate).replace(/^Не отправится: /, 'Недоступно: ') : null;
}

// Решения владельца (разрешить/запретить инструмент, ответ на план, эскалацию команды):
// на сервере это Configure, редактору закрыто так же, как читателю. Читателю — причина про
// чтение, редактору — про владельца. null — можно (владелец и обычный проект)
export const OWNER_DECISION = 'Недоступно: решает владелец проекта';
export function ownerDecisionDeniedReason(project: Project | null | undefined): string | null {
  return turnDeniedReason(project) ?? (isParticipant(project) ? OWNER_DECISION : null);
}

// Оборачивает действие гейтом: у участника вместо вызова — onDenied(текст), результат undefined.
// Проект читается в момент вызова (getProject), чтобы обёртка могла быть стабильной по ссылке
export function guardTurnAction<A extends unknown[], R>(
  getProject: () => Project | null | undefined,
  action: (...args: A) => R,
  onDenied: (reason: string) => void,
  reasonOf: (project: Project | null | undefined) => string | null = turnDeniedReason,
): (...args: A) => R | undefined {
  return (...args: A) => {
    const reason = reasonOf(getProject());
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
