// Действия звука: новый звук, запуск текста из карточки агента (котировка → задача строго по quoteId,
// ADR-021 §2), версии нити — взять, свести, сохранить, скачать.

import { autoRevealGenerationPanel, clearGenDraft, refreshChatContext, showToast } from 'aihome_shell/kit';
import { audioApi, nameTakenSuggestion, type AudioMode, type AudioOp, type AudioThread } from '../api';
import type { MixPlan } from '../player/mix';
import { isPersonalScope } from '../scope';
import { draftStem, mixRequest } from './model';
import { opInfo } from '../ops';
import { resolveLaunch } from '../panel/launch';
import { soundSource } from './modeState';
import { getCatalog, mutate, soundDraftKey, SOUND_PANEL } from './threadStore';

// «Новый звук»: черновик в корне, его настройки — копия префов режима; становится основным объектом
export async function createDraft(scope: string, sessionId: string, mode: AudioMode): Promise<boolean> {
  const ok = await mutate(scope, sessionId, rev => audioApi.open(scope, sessionId, { draftFolder: '', mode, revision: rev }));
  if (ok) {
    // Черновик становится основным объектом на сервере; состояние контекста не ждёт события рассылки
    void refreshChatContext(sessionId);
    autoRevealGenerationPanel(SOUND_PANEL, sessionId);
  }
  return ok;
}

// Запуск текста: настройки сервер разрешает сам по цепочке нити, текст — в поле операции.
// false — не запущено (причина уже показана тостом)
// override — явный запуск с режимом и моделью агента (карточка «Текст для звука»)
export async function launchFromComposer(
  scope: string, sessionId: string, thread: AudioThread, text: string,
  override?: { mode: AudioMode; operation: AudioOp; provider: string | null; model: string | null } | null,
): Promise<boolean> {
  const src = soundSource(scope, sessionId, thread);
  const L = resolveLaunch(src.thread, src.prefs, getCatalog(scope), src.mode);
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
