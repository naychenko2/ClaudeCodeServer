// Мост поля ввода (ADR-023 §Д2): какой интерфейс рисует поле для основного объекта. Вид с `actions` —
// чипы действий; без объекта или без действий — обычный «Чат». Чистая функция: поле не решает это инлайном.

import { objectKey, resolveAction } from './actionMemory';
import type { ChatContextPrimary, ChatContextRef, ContextAction, ContextKindApi, ContextKindCtx } from './types';

export type ComposerSurface =
  | { surface: 'chat' }
  | {
      surface: 'actions';
      actions: readonly ContextAction[];
      // Выбранное run-действие; null — «Чат»
      action: ContextAction | null;
      // Ключ объекта `{kind:ref}`: память выбора и черновики текста
      objectKey: string;
    };

export interface SurfaceInput {
  primary: ChatContextPrimary | null;
  refs: readonly ChatContextRef[];
  api: ContextKindApi | null;
  ctx: ContextKindCtx;
}

export function composerSurfaceFor(i: SurfaceInput): ComposerSurface {
  if (!i.primary || !i.api) return { surface: 'chat' };
  const actions = i.api.actions(i.ctx, { primary: i.primary, refs: i.refs });
  if (actions.length === 0) return { surface: 'chat' };
  const key = objectKey(i.primary);
  const { actionId } = resolveAction(i.ctx.sessionId, key, i.primary.by, actions, false);
  const action = actionId ? actions.find(a => a.id === actionId) ?? null : null;
  return { surface: 'actions', actions, action, objectKey: key };
}
