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

/** Значение cc_pending_project_chat для «назад/вперёд» на снимок проектного чата, когда
 *  WorkspacePage смонтируется заново: проект другой или он «спал» в другом разделе.
 *  Свой popstate новый инстанс уже пропустил, а чат снимка ему передаёт только pending.
 *  null — восстанавливать нечего или этим займётся смонтированный WorkspacePage сам. */
export function pendingChatOnPop(
  snapshot: { project?: { id: string } | null; chatId?: string | null },
  openProjectId: string | null | undefined,
  hubTab: string,
): string | null {
  if (!snapshot.project || !snapshot.chatId) return null;
  const remount = openProjectId !== snapshot.project.id || hubTab !== 'projects';
  return remount ? `${snapshot.project.id}|${snapshot.chatId}` : null;
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
