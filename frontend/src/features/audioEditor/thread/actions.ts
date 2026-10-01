// Действия полосы и композера «Звук»: новый звук, снять выбор, ярлыки режимов и запуск из
// поля ввода (котировка → задача строго по quoteId, ADR-021 §2).

import {
  autoRevealGenerationPanel, clearGenDraft, dropAgentPick, followSelection, requestStrip, revealWorkspacePanel, showToast,
} from 'aihome_shell/kit';
import { audioApi, nameTakenSuggestion, type AudioMode, type AudioOp, type AudioThread } from '../api';
import type { MixPlan } from '../player/mix';
import { isPersonalScope } from '../scope';
import { draftStem, mixRequest } from './model';
import { opInfo } from '../ops';
import { resolveLaunch } from '../strip/summary';
import {
  focusThread, getCatalog, getPrefs, getShortcutMode, mutate, requestSoundMode, setShortcutMode, soundDraftKey, SOUND_PANEL, SOUND_STRIP,
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

// Клик человека по карточке в ленте: звук — в работу, открытая панель переключается на
// «Звук → Настройки», закрытая не открывается («Панель следует за выбором», правило 1).
// Выбор агентом приходит событием нитей и сюда не попадает — панель он не двигает
export async function selectThreadByHuman(scope: string, sessionId: string, threadId: string, focused = false): Promise<boolean> {
  const ok = focused || await focusThread(scope, sessionId, threadId);
  if (ok) followSelection(SOUND_PANEL, sessionId, soundDraftKey(threadId));
  return ok;
}

// Запуск из композера: настройки сервер разрешает сам по цепочке нити, текст — в поле операции.
// false — не запущено (причина уже показана тостом)
// override — явный запуск не по полосе (карточка «Текст для звука» с режимом и моделью агента)
export async function launchFromComposer(
  scope: string, sessionId: string, thread: AudioThread, text: string,
  override?: { mode: AudioMode; operation: AudioOp; provider: string | null; model: string | null } | null,
): Promise<boolean> {
  const L = resolveLaunch(thread, getPrefs(scope), getCatalog(scope), getShortcutMode(sessionId) ?? 'voice');
  const field = opInfo(override?.operation ?? L.op)?.field ?? 'prompt';
  const trimmed = text.trim();
  try {
    const quote = await audioApi.quote(scope, sessionId, {
      mode: override?.mode ?? L.mode, sessionId, threadId: thread.id, text: field === 'text' ? trimmed : null,
      prompt: field === 'prompt' && trimmed ? trimmed : null,
      ...(override ? { operation: override.operation, provider: override.provider, model: override.model } : {}),
    });
    await audioApi.startJob(scope, sessionId, {
      quoteId: quote.quoteId, sessionId, threadId: thread.id,
      text: field === 'text' ? trimmed : null,
      prompt: field === 'prompt' && trimmed ? trimmed : null,
    });
    // Запуск забрал черновик элемента — пометка «черновик» уходит
    clearGenDraft(soundDraftKey(thread.id));
    return true;
  } catch (e) {
    showToast((e as Error).message || 'Звук не запущен', '', 'error');
    return false;
  }
}

// ── Карточка нити в ленте ──

// «Взять» вариант / «Работать с этой»: версия становится текущей, нить — в работе (сервер ставит фокус сам).
// Кнопка — просьба открыть: панель «Звук» открывается и закрытая (правило 4)
export async function takeVersion(scope: string, sessionId: string, thread: AudioThread, versionId: string): Promise<boolean> {
  const ok = await mutate(scope, sessionId, rev => audioApi.current(scope, sessionId, thread.id, versionId, rev));
  if (ok) {
    dropAgentPick(sessionId, soundDraftKey(thread.id));
    revealWorkspacePanel(SOUND_PANEL, 'settings', { sessionId, target: soundDraftKey(thread.id) });
  }
  return ok;
}

// «Свести N из M в новую версию»: без ИИ, итог — новая версия той же нити
export async function mixStems(scope: string, sessionId: string, thread: AudioThread, versionId: string, plan: MixPlan): Promise<boolean> {
  if (!plan.canMix) return false;
  const ok = await mutate(scope, sessionId, async rev =>
    (await audioApi.mix(scope, sessionId, thread.id, mixRequest(plan, versionId, rev))).state);
  if (ok) showToast(`Сведено: ${plan.description}`, 'Новая версия — своей карточкой в ленте', 'info');
  return ok;
}

export type SaveOutcome =
  | { ok: true; path: string }
  // suggestion — свободное имя рядом (409 name_taken), его подставляет «Сохранить как…»
  | { ok: false; error: string; suggestion: string | null };

// «Сохранить в проект» — следующей версией рядом с исходником; as — «Сохранить как…» в папку и под имя.
// В личном чате проекта нет: сюда не доходит, вместо кнопки — «Скачать»
export async function saveVersion(
  scope: string, sessionId: string, thread: AudioThread, versionId: string,
  as?: { folder: string; fileName: string },
): Promise<SaveOutcome> {
  if (isPersonalScope(scope)) return { ok: false, error: 'У личного чата нет проекта — версию можно только скачать', suggestion: null };
  try {
    const r = await audioApi.save(scope, sessionId, thread.id, as
      ? { versionId, mode: 'as', folder: as.folder, fileName: as.fileName }
      : { versionId, mode: 'nextVersion', ...(thread.file ? {} : { fileName: draftStem(thread) }) });
    showToast(`Сохранено: ${r.path}`, r.files.length > 1 ? `файлов: ${r.files.length}` : '', 'info');
    return { ok: true, path: r.path };
  } catch (e) {
    const suggestion = nameTakenSuggestion(e);
    const error = (e as Error).message || 'Не удалось сохранить';
    // Занятое имя показывает диалог рядом с полем; остальное — тостом
    if (!suggestion) showToast(error, '', 'error');
    return { ok: false, error, suggestion };
  }
}

// «Скачать» файл версии: сервер отдаёт его с именем «intro.version3.vocals.mp3» (download=true)
export function downloadFile(scope: string, sessionId: string, thread: AudioThread, versionId: string, role = 'main') {
  const a = document.createElement('a');
  a.href = audioApi.versionFileUrl(scope, sessionId, thread.id, versionId, role, true);
  a.download = '';
  // Firefox кликает только по ссылке в документе; после клика она не нужна
  document.body.appendChild(a);
  try { a.click(); } finally { a.remove(); }
}
