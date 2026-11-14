// Какое действие выбрано у основного объекта и есть ли у строки контекста чип «Чем».
// «Чем» рисуется при выбранном run-действии (Р2). В «Чате» сообщение уходит Claude, и чип есть, только
// если вид отдаёт `chatExecutors` — с чем пойдут запуски агента (картинка: «Править» и «Создать»).
import { withThumb } from './thumbs';
import { objectKey, resolveAction } from './actionMemory';
import { questionValue, runEntry } from './actionRun';
import type {
  ChatContextPrimary, ChatContextRef, ChatExecutorSection, ContextAction, ContextKindApi, ContextKindCtx, ExecutorListModel,
} from './types';

export interface RowAction {
  // null — «Чат»
  action: ContextAction | null;
  executors: ExecutorListModel | null;
}

export function selectRowAction(
  api: ContextKindApi | null, ctx: ContextKindCtx, primary: ChatContextPrimary, refs: readonly ChatContextRef[],
): RowAction {
  const actions = api?.actions(ctx, { primary, refs: refs.map(r => withThumb(ctx, r)) }) ?? [];
  const { actionId } = resolveAction(ctx.sessionId, objectKey(primary), primary.by, actions, false);
  const action = actionId ? actions.find(a => a.id === actionId) ?? null : null;
  // Ответ вопроса действия читаем из состояния запуска той же области, что у кнопки и панели
  const answer = action?.question ? questionValue(action, runEntry(ctx.sessionId, `${objectKey(primary)}:${action.id}`).values) : null;
  const executors = action ? api?.executors?.(ctx, action.id, answer) ?? null : null;
  return { action, executors };
}

// Заголовок меню «Чем» по макету: «Чем выполнить «Стемы»»
export const execMenuTitle = (action: ContextAction | null): string =>
  action ? `Чем выполнить «${action.label}»` : 'Чем выполнить';

// Модель чипа «Чем»: у run-действия — его исполнители; в «Чате» — разделы вида (чип — первый, меню — все);
// нет ни того, ни другого — чипа нет
export interface RowExecModel extends ExecutorListModel {
  title: string;
  sections?: readonly ChatExecutorSection[];
}

export function rowExecOf(api: ContextKindApi | null, ctx: ContextKindCtx, sel: RowAction | null): RowExecModel | null {
  if (!sel) return null;
  const { action, executors } = sel;
  if (executors) return { rows: executors.rows, value: executors.value, onChange: executors.onChange, title: execMenuTitle(action) };
  const chat = action ? [] : api?.chatExecutors?.(ctx) ?? [];
  return chat.length
    ? { rows: chat[0].rows, value: chat[0].value, onChange: chat[0].onChange, title: execMenuTitle(null), sections: chat }
    : null;
}
