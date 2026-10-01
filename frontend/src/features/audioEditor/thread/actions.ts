// Действия полосы и композера «Звук»: новый звук, снять выбор, ярлыки режимов и запуск из
// поля ввода (котировка → задача строго по quoteId, ADR-021 §2).

import { requestStrip, revealWorkspacePanel, showToast } from 'aihome_shell/kit';
import { audioApi, type AudioMode, type AudioThread } from '../api';
import { opInfo } from '../ops';
import { resolveLaunch } from '../strip/summary';
import {
  focusThread, getCatalog, getPrefs, getShortcutMode, mutate, requestSoundMode, setShortcutMode, SOUND_PANEL, SOUND_STRIP,
} from './threadStore';

// «✦ Новый звук»: черновик в корне, его настройки — копия префов режима; поле — в режим «Звук»
export async function createDraft(scope: string, sessionId: string, mode: AudioMode): Promise<boolean> {
  const ok = await mutate(scope, sessionId, rev => audioApi.open(scope, sessionId, { draftFolder: '', mode, revision: rev }));
  if (ok) requestSoundMode(sessionId);
  return ok;
}

export async function releaseFocus(scope: string, sessionId: string, thread: AudioThread | null) {
  if (thread) await focusThread(scope, sessionId, null);
}

// Ярлык «Голос» / «Музыка» (меню полос, «＋» композера, пустая лента): полоса «Звук», режим и
// панель «Звук» на «Настройках». Панель читает режим из стора (getShortcutMode)
export function openSoundShortcut(sessionId: string | null, mode: AudioMode) {
  if (sessionId) {
    setShortcutMode(sessionId, mode);
    requestStrip(sessionId, SOUND_STRIP);
  }
  revealWorkspacePanel(SOUND_PANEL, 'settings');
}

// Запуск из композера: настройки сервер разрешает сам по цепочке нити, текст — в поле операции.
// false — не запущено (причина уже показана тостом)
export async function launchFromComposer(scope: string, sessionId: string, thread: AudioThread, text: string): Promise<boolean> {
  const L = resolveLaunch(thread, getPrefs(scope), getCatalog(scope), getShortcutMode(sessionId) ?? 'voice');
  const field = opInfo(L.op)?.field ?? 'prompt';
  const trimmed = text.trim();
  try {
    const quote = await audioApi.quote(scope, sessionId, {
      mode: L.mode, sessionId, threadId: thread.id, text: field === 'text' ? trimmed : null,
    });
    await audioApi.startJob(scope, sessionId, {
      quoteId: quote.quoteId, sessionId, threadId: thread.id,
      text: field === 'text' ? trimmed : null,
      prompt: field === 'prompt' && trimmed ? trimmed : null,
    });
    return true;
  } catch (e) {
    showToast((e as Error).message || 'Звук не запущен', '', 'error');
    return false;
  }
}
