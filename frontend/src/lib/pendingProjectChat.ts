// Восстановление проектного чата из снимка истории и диплинка (cc_pending_project_chat).
//
// Восстановление асинхронное (ждёт список чатов проекта), поэтому опасны два исхода:
// ответ на устаревшее событие перебивает более свежее (или выбор пользователя), а ответ,
// пришедший после размонтирования инстанса, пишет в историю снимок чужого проекта поверх
// нового. Страж поколений отсекает оба.

/** Чат из снимка истории, который надо восстановить; null — восстанавливать нечего:
 *  снимок без чата или с тем же чатом, что уже открыт (так «назад» снимает запись
 *  шторки — запускать восстановление там значит гоняться с переходом из шторки). */
export function chatToRestore(snapshotChatId: string | null | undefined, activeChatId: string | null | undefined): string | null {
  if (!snapshotChatId || snapshotChatId === activeChatId) return null;
  return snapshotChatId;
}

export interface LatestGuard {
  /** Начать новое поколение; прежние после этого — устаревшие */
  next(): number;
  /** Поколение всё ещё последнее и страж не снят */
  isCurrent(gen: number): boolean;
  /** Снять страж (эффект размонтирован): любое поколение — устаревшее */
  dispose(): void;
}

export function createLatestGuard(): LatestGuard {
  let gen = 0;
  let disposed = false;
  return {
    next: () => ++gen,
    isCurrent: g => !disposed && g === gen,
    dispose: () => { disposed = true; },
  };
}
