// Мост поля ввода (ADR-023 §Д2, «Что заменяет слот composer-mode»): какой интерфейс режимов рисует
// поле для основного объекта. Вид с `actions` и включённый флаг — чипы действий, а слот
// `composer-mode` для этого объекта не читается; вид без действий — прежний сегмент «Чат | X»;
// без флага путь один — старый. Чистая функция: поле не решает это инлайном.

import { objectKey, resolveAction } from './actionMemory';
import type { ChatContextPrimary, ChatContextRef, ContextAction, ContextKindApi, ContextKindCtx } from './types';

export type ComposerSurface =
  | { surface: 'modes' }
  | {
      surface: 'actions';
      actions: readonly ContextAction[];
      // Выбранное run-действие; null — «Чат»
      action: ContextAction | null;
      // Ключ объекта `{kind:ref}`: память выбора и черновики текста
      objectKey: string;
    };

export interface SurfaceInput {
  flag: boolean;
  primary: ChatContextPrimary | null;
  refs: readonly ChatContextRef[];
  api: ContextKindApi | null;
  ctx: ContextKindCtx;
}

export function composerSurfaceFor(i: SurfaceInput): ComposerSurface {
  if (!i.flag || !i.primary || !i.api) return { surface: 'modes' };
  const actions = i.api.actions(i.ctx, { primary: i.primary, refs: i.refs });
  if (actions.length === 0) return { surface: 'modes' };
  const key = objectKey(i.primary);
  const { actionId } = resolveAction(i.ctx.sessionId, key, i.primary.by, actions, false);
  const action = actionId ? actions.find(a => a.id === actionId) ?? null : null;
  return { surface: 'actions', actions, action, objectKey: key };
}
