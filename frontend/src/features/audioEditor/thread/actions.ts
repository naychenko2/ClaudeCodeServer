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
import { rememberMode, saveSettings } from '../panel/inputs';
import { nextSettings, resolvePanel, type PanelState, type SettingsPatch } from '../panel/model';
import { resolveLaunch } from '../strip/summary';
import { hasSound, soundSource, type PendingSettings } from './modeState';
import {
  dropPendingSettings, focusThread, getCatalog, getFocusedThread, getPrefs, mutate, requestSoundMode, setChosenMode,
  setPendingSettings, soundDraftKey, SOUND_PANEL, SOUND_STRIP,
} from './threadStore';

// ── Настройки и режим звука: одна цепочка для панели и полосы ──

const SAVE_DELAY = 500;
// Отложенное сохранение правки по чату: уходит само по таймеру, а раньше — при смене звука,
// закрытии панели или смене режима
const _later = new Map<string, { threadId: string | null; timer: ReturnType<typeof setTimeout>; flush: () => void }>();

// Настройки, которые видит человек: нить → префы режима → умолчание, плюс невыехавший выбор
export function soundPanelState(scope: string, sessionId: string | null, focus: AudioThread | null = getFocusedThread(sessionId)): PanelState {
  const src = soundSource(scope, sessionId, focus);
  return resolvePanel(src.thread, src.prefs, getCatalog(scope), src.mode);
}

export function flushSoundSettings(sessionId: string | null) {
  const key = sessionId ?? '';
  const l = _later.get(key);
  if (!l) return;
  _later.delete(key);
  clearTimeout(l.timer);
  l.flush();
}

// Правка настроек звука: сразу поверх экрана, на сервер — сейчас или через SAVE_DELAY.
// Пишется в нить в фокусе, без нити — в префы режима. false — правка не принята
export function changeSoundSettings(scope: string, sessionId: string | null, patch: SettingsPatch, debounced = false): boolean {
  const key = sessionId ?? '';
  const thread = getFocusedThread(sessionId);
  const threadId = thread?.id ?? null;
  // Недосохранённое другой нити уходит в СВОЮ нить (flush держит её в замыкании)
  if (_later.get(key)?.threadId !== threadId) flushSoundSettings(sessionId);
  const state = soundPanelState(scope, sessionId, thread);
  const prefs = getPrefs(scope);
  const next = nextSettings(state, patch, prefs, getCatalog(scope));
  if (next.mode !== state.mode) {
    // Черновику без звука «Обработка» не нужна: обрабатывать нечего
    if (next.mode === 'process' && thread && !hasSound(thread)) return false;
    // Выбор уходящего режима: у нити — в его префы, без нити — недосохранённая правка в те же префы
    if (thread) void rememberMode(scope, sessionId, nextSettings(state, {}), prefs[state.mode]);
    else flushSoundSettings(sessionId);
    setChosenMode(sessionId, { mode: next.mode, withSound: hasSound(thread) }, state.mode);
  }
  const p: PendingSettings = { threadId, settings: next };
  setPendingSettings(sessionId, p);
  const l = _later.get(key);
  if (l) clearTimeout(l.timer);
  _later.delete(key);
  const flush = () => { void saveSettings(scope, sessionId, thread, next).then(() => dropPendingSettings(sessionId, p)); };
  if (debounced) _later.set(key, { threadId, flush, timer: setTimeout(() => { _later.delete(key); flush(); }, SAVE_DELAY) });
  else flush();
  return true;
}

// Смена режима — из панели и (шаг K2) из полосы
export const setSoundMode = (scope: string, sessionId: string | null, mode: AudioMode): boolean =>
  changeSoundSettings(scope, sessionId, { mode });

// Сброс — только для тестов
export function __resetSoundSettings() {
  _later.forEach(l => clearTimeout(l.timer));
  _later.clear();
}

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

// Ярлык «Звук» (меню полос, «＋» композера, пустая лента): полоса «Звук» и панель «Звук» на
// «Настройках», если человек её в этом чате не закрывал (решение 2 по v2). Режим — последний
// действующий (modeState, по умолчанию «Голос»)
export function openSoundShortcut(sessionId: string | null) {
  // Чата ещё нет — закрыть панель в нём не могли: открываем всегда
  if (!sessionId) {
    revealWorkspacePanel(SOUND_PANEL, 'settings');
    return;
  }
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
