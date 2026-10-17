// Перенос в новый чат (docs/features/chat-handoff.md): когда кнопка
// «Перенести в новый чат» доступна. Зеркало серверных отказов
// (SessionManager.EnsureHandoffAllowed) — чтобы человек видел причину в подсказке, а не
// получал 409 после клика. Сервер остаётся единственной точкой правды: расхождение здесь
// даст лишний клик с тостом, но не перенос, который сервер запретил.
import type { Session } from '../types';
import type { PromptCacheStatus } from './promptCache';

// Какой путь просить у сервера. Тёплый — записку пишет сам чат своим ходом: пока кэш жив,
// префикс читается по 0.1x, а агент видит выводы инструментов и файлы. Остывший или
// неизвестный кэш — холодный: ход старого чата переписал бы весь префикс заново.
// Сервер при невыполненном предусловии (идёт ход, цикл, архив…) сам уйдёт на холодный
export function handoffWarm(status: PromptCacheStatus): boolean {
  return status === 'warm' || status === 'expiring';
}

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
