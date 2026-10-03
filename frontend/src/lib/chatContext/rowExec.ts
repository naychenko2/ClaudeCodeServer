// Какое действие выбрано у основного объекта и есть ли у строки контекста чип «Чем».
// «Чем» рисуется только при выбранном run-действии (Р2): в «Чате» сообщение уходит Claude,
// исполнитель не нужен, и чипа в строке нет вовсе.
import { objectKey, resolveAction } from './actionMemory';
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
  const { actionId } = resolveAction(ctx.sessionId, objectKey(primary), primary.by, actions);
  const action = actionId ? actions.find(a => a.id === actionId) ?? null : null;
  const executors = action ? api?.executors?.(ctx, action.id) ?? null : null;
  return { action, executors };
}
