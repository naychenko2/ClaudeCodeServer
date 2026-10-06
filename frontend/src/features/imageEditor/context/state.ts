// Чтение состояния картинки для вида контекста: основной объект → нить → вход каталога действий.
// Вне React: хост зовёт `actions` на каждый рендер, поэтому всё берётся из кэшей сторов.

import type { ChatContextPrimary, ContextAction, ContextKindCtx, ChatContextRef } from 'aihome_shell/kit';
import { getChatContextState } from 'aihome_shell/kit';
import { quickOffered } from '../editorInputs';
import { hasMaskMark } from '../marks';
import { enterScope } from '../scope';
import { getCatalog } from '../thread/catalog';
import { launchMarks } from './ops';
import { threadHasImage } from '../thread/model';
import { getThreadMarks, getThreadsState, isWholeImage, openEditor } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';
import { buildImageActions, type ImageActionInput } from './actions';

export const IMAGE_KIND = 'image';

// Нить основного объекта: ref.threadId; нет её в сторе чата (ещё грузится) — null
export function threadOfPrimary(sessionId: string, primary: ChatContextPrimary | null): ImageThread | null {
  const id = primary?.kind === IMAGE_KIND ? primary.ref.threadId : null;
  return typeof id === 'string' ? getThreadsState(sessionId).threads.find(t => t.id === id) ?? null : null;
}

export function inputOf(ctx: ContextKindCtx, thread: ImageThread): ImageActionInput {
  const catalog = getCatalog(enterScope(ctx.projectId, ctx.sessionId));
  const { marks } = getThreadMarks(thread.id);
  const hasFile = threadHasImage(thread);
  return {
    hasFile,
    marks: marks.length,
    hasMask: hasFile && hasMaskMark(launchMarks(marks, isWholeImage(thread.id))),
    offered: {
      removeBackground: quickOffered('removeBackground', catalog),
      upscale: quickOffered('upscale', catalog),
      outpaint: quickOffered('outpaint', catalog),
    },
    openBrush: () => openEditor(ctx.sessionId, thread.id, null, { tool: 'mask' }),
  };
}

// Нить не нашли (ещё грузится) — действий нет, поле остаётся на прежнем «Чат | Картинка»
export function imageActions(
  ctx: ContextKindCtx, s: { primary: ChatContextPrimary; refs: readonly ChatContextRef[] },
): readonly ContextAction[] {
  const thread = threadOfPrimary(ctx.sessionId, s.primary);
  return thread ? buildImageActions(inputOf(ctx, thread)) : [];
}

// Действие по id для текущего основного объекта чата: «Чем», параметры и цена зовутся с actionId
export function actionOf(ctx: ContextKindCtx, actionId: string) {
  const primary = getChatContextState(ctx.sessionId).primary;
  const thread = threadOfPrimary(ctx.sessionId, primary);
  if (!primary || !thread) return null;
  const input = inputOf(ctx, thread);
  const action = buildImageActions(input).find(a => a.id === actionId);
  return action ? { action, thread, input, scope: enterScope(ctx.projectId, ctx.sessionId) } : null;
}
