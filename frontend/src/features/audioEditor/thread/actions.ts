// Действия полосы и композера «Звук»: новый звук, снять выбор, ярлыки режимов и запуск из
// поля ввода (котировка → задача строго по quoteId, ADR-021 §2).

import { autoRevealGenerationPanel, requestStrip, revealWorkspacePanel, showToast } from 'aihome_shell/kit';
import { audioApi, type AudioMode, type AudioThread } from '../api';
import { opInfo } from '../ops';
import { resolveLaunch } from '../strip/summary';
import {
  focusThread, getCatalog, getPrefs, getShortcutMode, mutate, requestSoundMode, setShortcutMode, SOUND_PANEL, SOUND_STRIP,
} from './threadStore';

// «✦ Новый звук»: черновик в корне, его настройки — копия префов режима; поле — в режим «Звук»
export async function createDraft(scope: string, sessionId: string, mode: AudioMode): Promise<boolean> {
  const ok = await mutate(scope, sessionId, rev => audioApi.open(scope, sessionId, { draftFolder: '', mode, revision: rev }));
  if (ok) {
    requestSoundMode(sessionId);
    autoRevealGenerationPanel(SOUND_PANEL, sessionId, 'settings');
  }
  return ok;
}

export async function releaseFocus(scope: string, sessionId: string, thread: AudioThread | null) {
  if (thread) await focusThread(scope, sessionId, null);
}

// Ярлык «Голос» / «Музыка» (меню полос, «＋» композера, пустая лента): полоса «Звук», режим и
// панель «Звук» на «Настройках», если человек её в этом чате не закрывал (решение 2 по v2).
// Панель читает режим из стора (getShortcutMode)
export function openSoundShortcut(sessionId: string | null, mode: AudioMode) {
  // Чата ещё нет — закрыть панель в нём не могли: открываем всегда
  if (!sessionId) {
    revealWorkspacePanel(SOUND_PANEL, 'settings');
    return;
  }
  setShortcutMode(sessionId, mode);
  requestStrip(sessionId, SOUND_STRIP);
  autoRevealGenerationPanel(SOUND_PANEL, sessionId, 'settings');
}

// Выбор звука человеком (карточка в ленте): фокус и автооткрытие панели. Выбор агентом
// приходит событием нитей и сюда не попадает — панель сама не открывается
export async function selectThreadByHuman(scope: string, sessionId: string, threadId: string): Promise<boolean> {
  const ok = await focusThread(scope, sessionId, threadId);
  if (ok) autoRevealGenerationPanel(SOUND_PANEL, sessionId, 'settings');
  return ok;
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
