// Авторство сообщений человека в общем проекте (S2a-F1): чистая логика без React.
// Над чужим пузырём — аватар, имя и время; свой пузырь без подписи. В личном проекте и у
// сообщений без senderUserId (реплика агента, старые записи) подписи нет — вид прежний.

import type { Project, ProjectMembersView } from '../../../types';
import { isParticipant } from './participantGate';

export interface MessageAuthor {
  userId: string;
  name: string;
  // Человека уже нет в проекте: серый аватар и пометка «больше не в проекте»
  gone: boolean;
}

export const AUTHOR_GONE_LABEL = 'больше не в проекте';
const UNKNOWN_NAME = 'Участник';

// Проект общий, если у вызывающего в нём есть роль (в личном проекте myRole нет)
export function isSharedProject(project: Pick<Project, 'myRole'> | null | undefined): boolean {
  return !!project?.myRole;
}

function nameOf(p: { displayName?: string | null; username?: string | null } | undefined): string {
  return p?.displayName?.trim() || p?.username?.trim() || UNKNOWN_NAME;
}

// Кого подписывать над пузырём. null — подпись не нужна: личный проект, нет senderUserId,
// свой пузырь. view — полный список участников (может быть ещё не загружен: тогда имя
// «Участник», а не пустота)
export function resolveMessageAuthor(
  project: Pick<Project, 'myRole' | 'formerMembers'> | null | undefined,
  senderUserId: string | null | undefined,
  myUserId: string | null | undefined,
  view: Pick<ProjectMembersView, 'members' | 'owner'> | null | undefined,
): MessageAuthor | null {
  if (!isSharedProject(project) || !senderUserId || senderUserId === myUserId) return null;
  if (view?.owner.userId === senderUserId) return { userId: senderUserId, name: nameOf(view.owner), gone: false };
  const member = view?.members.find(m => m.userId === senderUserId);
  if (member) return { userId: senderUserId, name: nameOf(member), gone: false };
  const former = project?.formerMembers?.find(m => m.userId === senderUserId);
  if (former) return { userId: senderUserId, name: nameOf(former), gone: true };
  // «Больше не в проекте» — только по явному попаданию в formerMembers. Неизвестный (список
  // не догружен или участник добавлен офлайн) — запасное имя, не «ушёл»
  return { userId: senderUserId, name: UNKNOWN_NAME, gone: false };
}

// «Не доставлять» в очереди: владелец проекта и отправитель. Участник (редактор/читатель)
// отменяет только своё; чьё сообщение — неизвестно (агентское, старый снимок) — не отменяет.
// Серверная проверка not_sender (S2a-10b) остаётся главной, это лишь не обещание зря
export function canCancelPending(
  project: Pick<Project, 'myRole'> | null | undefined,
  senderUserId: string | null | undefined,
  myUserId: string | null | undefined,
): boolean {
  if (!isParticipant(project)) return true;
  return !!senderUserId && senderUserId === myUserId;
}
