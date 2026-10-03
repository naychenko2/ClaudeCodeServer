// Наполнение контекста из ленты, «Файлов» и «Персонажей» (ADR-023, шаг 2к-2): чистые функции над DTO.
// Кнопки «Работать с этой» и «В контекст ▾» разных экранов спрашивают одно и то же: уже ли объект в
// контексте и под какими ролями его можно туда положить. Роли знает вид основного объекта (refRoles),
// хост их не выдумывает.

import { objectKey } from './actionMemory';
import { getKindApi } from './registry';
import type { ChatContextDto, ChatContextRef, ContextKindCtx, ContextRole } from './types';

export interface ContextCandidate { kind: string; ref: Record<string, unknown> }

// Референс, уже лежащий в контексте: тот же вид и та же ссылка (роль не в счёте — объект один)
export const refOf = (state: ChatContextDto, candidate: ContextCandidate): ChatContextRef | null =>
  state.refs.find(r => objectKey(r) === objectKey(candidate)) ?? null;

// Роли, под которыми кандидата можно положить в контекст. Нет основного объекта или вид о нём не знает —
// пусто: сервер всё равно ответил бы 400 role_not_accepted
export function rolesFor(ctx: ContextKindCtx, state: ChatContextDto, candidateKind: string): readonly ContextRole[] {
  const primary = state.primary;
  if (!primary) return [];
  return getKindApi(primary.kind)?.refRoles?.(ctx, primary, candidateKind) ?? [];
}
