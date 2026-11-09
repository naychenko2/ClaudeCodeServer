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

/** Файл и задача снимка истории, которые едут вместе с его чатом */
export interface PendingChatView { file: string | null; task: string | null }

/** keepFeature — метка запасного перехода «← к фиче»: чат выбирается ПОД открытой страницей
 *  фичи, и страницу закрывать не надо. Ставит только он; остальные писатели канала
 *  (уведомление, диплинк, «ждёт ответа», инцидент) метку не несут — их выбор страницу закрывает */
export interface PendingChat { pid: string; chatId: string; view: PendingChatView | null; keepFeature: boolean }

// Вид снимка дописывается к «projectId|chatId» после перевода строки: в id его нет,
// а значение остаётся одной строкой одного канала — страж поколений и возврат
// незавершённого pending на перемонтаже StrictMode работают с ним как раньше
const VIEW_SEP = '\n';

/** Значение cc_pending_project_chat; view — вид снимка, который надо восстановить вместе с чатом,
 *  keepFeature — метка «оставить страницу фичи» (см. PendingChat) */
export function encodePendingChat(pid: string, chatId: string, view?: PendingChatView, keepFeature?: boolean): string {
  const base = `${pid}|${chatId}`;
  if (!view && !keepFeature) return base;
  return `${base}${VIEW_SEP}${JSON.stringify(keepFeature ? { ...view, keepFeature: true } : view)}`;
}

/** Разбор cc_pending_project_chat; без «projectId|» значение относится к fallbackPid.
 *  Битый вид не роняет восстановление чата — чат откроется без него */
export function parsePendingChat(raw: string, fallbackPid: string): PendingChat {
  const viewAt = raw.indexOf(VIEW_SEP);
  const head = viewAt === -1 ? raw : raw.slice(0, viewAt);
  const sep = head.indexOf('|');
  const [pid, chatId] = sep === -1 ? [fallbackPid, head] : [head.slice(0, sep), head.slice(sep + 1)];
  let view: PendingChatView | null = null;
  let keepFeature = false;
  if (viewAt !== -1) {
    try {
      const v = JSON.parse(raw.slice(viewAt + 1)) as Partial<PendingChatView> & { keepFeature?: unknown };
      keepFeature = v.keepFeature === true;
      // значение с одной меткой вида не несёт: файл и задачу из снимка тогда не трогаем
      if ('file' in v || 'task' in v) {
        view = { file: typeof v.file === 'string' ? v.file : null, task: typeof v.task === 'string' ? v.task : null };
      }
    } catch { /* вид не разобрался — восстанавливаем только чат */ }
  }
  return { pid, chatId, view, keepFeature };
}

/** Значение cc_pending_project_chat для «назад/вперёд» на снимок проектного чата, когда
 *  WorkspacePage смонтируется заново: проект другой или он «спал» в другом разделе.
 *  Свой popstate новый инстанс уже пропустил, а чат снимка ему передаёт только pending —
 *  вместе с файлом и задачей снимка: иначе новый инстанс поднял бы файл из своего
 *  сохранённого состояния, и «назад» вернул бы чат не с тем файлом в сплите.
 *  null — восстанавливать нечего или этим займётся смонтированный WorkspacePage сам. */
export function pendingChatOnPop(
  snapshot: { project?: { id: string } | null; chatId?: string | null; file?: string | null; task?: string | null; feature?: string | null },
  openProjectId: string | null | undefined,
  hubTab: string,
): string | null {
  if (!snapshot.project || !snapshot.chatId) return null;
  const remount = openProjectId !== snapshot.project.id || hubTab !== 'projects';
  return remount
    ? encodePendingChat(snapshot.project.id, snapshot.chatId, { file: snapshot.file ?? null, task: snapshot.task ?? null }, !!snapshot.feature)
    : null;
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
