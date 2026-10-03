// Какое действие выбрано у основного объекта и есть ли у строки контекста чип «Чем».
// «Чем» рисуется только при выбранном run-действии (Р2): в «Чате» сообщение уходит Claude,
// исполнитель не нужен, и чипа в строке нет вовсе.
import { objectKey, resolveAction } from './actionMemory';
import { questionValue, runEntry } from './actionRun';
import type {
  ChatContextPrimary, ChatContextRef, ContextAction, ContextKindApi, ContextKindCtx, ExecutorListModel,
} from './types';

export interface RowAction {
  // null — «Чат»
  action: ContextAction | null;
  executors: ExecutorListModel | null;
}

export function selectRowAction(
  api: ContextKindApi | null, ctx: ContextKindCtx, primary: ChatContextPrimary, refs: readonly ChatContextRef[],
): RowAction {
  const actions = api?.actions(ctx, { primary, refs }) ?? [];
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
