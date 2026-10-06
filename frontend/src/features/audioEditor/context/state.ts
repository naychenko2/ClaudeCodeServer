// Чтение состояния звука для вида контекста: основной объект → нить и версия → вход каталога действий.
// Вне React: хост зовёт `actions` на каждый рендер, поэтому всё берётся из кэшей сторов.

import { getChatContextState, type ChatContextPrimary, type ChatContextRef, type ContextAction, type ContextKindCtx } from 'aihome_shell/kit';
import type { AudioStemSet, AudioThread, AudioThreadVersion } from '../api';
import { audioScope } from '../scope';
import { hasMain, isStem } from '../thread/model';
import { getCatalog, getSelection, getThreadsState } from '../thread/threadStore';
import { audioState, buildAudioActions, type AudioActionInput } from './actions';

export const AUDIO_KIND = 'audio';

// Нить основного объекта: ref.threadId; нет её в сторе чата (ещё грузится) — null
export function threadOfPrimary(sessionId: string, primary: ChatContextPrimary | null): AudioThread | null {
  const id = primary?.kind === AUDIO_KIND ? primary.ref.threadId : null;
  return typeof id === 'string' ? getThreadsState(sessionId).threads.find(t => t.id === id) ?? null : null;
}

// Версия основного объекта: ref.versionId, иначе текущая позиция нити
export function versionOfPrimary(thread: AudioThread, primary: ChatContextPrimary): AudioThreadVersion | null {
  const id = typeof primary.ref.versionId === 'string' ? primary.ref.versionId : thread.currentVersionId;
  return id ? thread.versions.find(v => v.id === id) ?? null : null;
}

// Наборы стемов, которые умеет хоть один доступный поставщик; нет каталога — null
function stemSetsOf(scope: string): readonly AudioStemSet[] | null {
  const catalog = getCatalog(scope);
  if (!catalog) return null;
  const sets = new Set<AudioStemSet>();
  for (const p of catalog.providers) {
    if (!p.available) continue;
    for (const m of p.models) if (m.caps.ops.includes('separate') && m.caps.stemSet) sets.add(m.caps.stemSet);
  }
  return [...sets];
}

export function inputOf(
  ctx: ContextKindCtx, primary: ChatContextPrimary, thread: AudioThread, refs: readonly ChatContextRef[],
): AudioActionInput {
  const version = versionOfPrimary(thread, primary);
  const selection = getSelection(ctx.sessionId, thread.id);
  return {
    state: audioState({
      hasSound: hasMain(version),
      hasStems: !!version?.files.some(isStem),
      mode: thread.settings?.mode,
    }),
    refs,
    hasSelection: !!selection && !!version && selection.versionId === version.id,
    stemSets: stemSetsOf(audioScope(ctx.projectId)),
  };
}

// Нить не нашли (ещё грузится) — действий нет, поле остаётся на прежнем «Чат | Звук»
export function audioActions(
  ctx: ContextKindCtx, s: { primary: ChatContextPrimary; refs: readonly ChatContextRef[] },
): readonly ContextAction[] {
  const thread = threadOfPrimary(ctx.sessionId, s.primary);
  return thread ? buildAudioActions(inputOf(ctx, s.primary, thread, s.refs)) : [];
}

// Действие по id для текущего основного объекта чата: «Чем», параметры и цена зовутся с actionId
export function actionOf(ctx: ContextKindCtx, actionId: string) {
  const { primary, refs } = getChatContextState(ctx.sessionId);
  const thread = threadOfPrimary(ctx.sessionId, primary);
  if (!primary || !thread) return null;
  const input = inputOf(ctx, primary, thread, refs);
  const action = buildAudioActions(input).find(a => a.id === actionId);
  return action ? { action, thread, primary, input, scope: audioScope(ctx.projectId) } : null;
}
