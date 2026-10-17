// Перенос в новый чат, холодный путь (docs/features/chat-handoff.md): когда кнопка
// «Перенести в новый чат» доступна. Зеркало серверных отказов
// (SessionManager.EnsureHandoffAllowed) — чтобы человек видел причину в подсказке, а не
// получал 409 после клика. Сервер остаётся единственной точкой правды: расхождение здесь
// даст лишний клик с тостом, но не перенос, который сервер запретил.
import type { Session } from '../types';

export interface HandoffAvailability {
  enabled: boolean;
  // Почему выключена — текст подсказки кнопки
  reason?: string;
}

export function handoffAvailability(
  session: Pick<Session, 'participants' | 'teamImplement' | 'worktreePath'>,
  isWaiting: boolean,
): HandoffAvailability {
  if (session.participants && session.participants.length > 0)
    return { enabled: false, reason: 'Групповой чат перенести нельзя: состав спикеров живёт в самом чате' };
  if (session.teamImplement)
    return { enabled: false, reason: 'Чат «Командной реализации» перенести нельзя: состояние волн живёт в самом чате' };
  if (session.worktreePath)
    return { enabled: false, reason: 'Чат в отдельном рабочем дереве перенести нельзя: дерево уйдёт вместе с ним' };
  // Записка по незаконченному ходу потеряла бы его развязку
  if (isWaiting)
    return { enabled: false, reason: 'Дождитесь конца хода — записка соберётся по завершённому разговору' };
  return { enabled: true };
}
