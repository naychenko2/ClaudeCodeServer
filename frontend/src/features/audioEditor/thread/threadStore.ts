// Стор нитей звука по чатам (ADR-021 §3): состояние с сервера (GET state + audio_thread_changed),
// каталог и префы области (audio_prefs_changed), ход запусков (audio_edit_progress → completed /
// failed) и локальное состояние экрана — режим, запрошенный ярлыком «Голос» / «Музыка», и просьбы
// включить режим композера «Звук». Выбор звука старше запомненной полосы: фокус просит полосу
// «Звук» у стора полос ядра, снятие — отпускает (как у картинок).

import { useEffect, useSyncExternalStore } from 'react';
import {
  dropAgentPickOf, noteAgentPick, notifyComposer, onReconnected, releaseStrip, requestStrip, revealWorkspacePanel, showToast,
} from 'aihome_shell/kit';
import {
  audioApi, conflictState, EMPTY_THREADS,
  type AudioCatalog, type AudioEvent, type AudioMode, type AudioOp, type AudioPrefs, type AudioStage, type AudioThread,
  type AudioThreadsState,
} from '../api';
import type { AudioSelection } from '../player/selection';
import type { ConcatPiece } from '../panel/inputs';
import { isCreateMode } from '../ops';
import type { ChosenMode, PendingSettings } from './modeState';
import { threadName } from './model';

export const SOUND_STRIP = 'sound';
export const SOUND_PANEL = 'sound';
// Ключ элемента для черновиков и выбора: панель + нить
export const soundDraftKey = (threadId: string) => `${SOUND_PANEL}:${threadId}`;

interface Entry { scope: string; state: AudioThreadsState; loaded: boolean; loading: boolean }

// Ход задачи по последнему audio_edit_progress; уходит с completed / failed
export interface JobProgress {
  jobId: string;
  sessionId: string | null;
  threadId: string | null;
  stage: AudioStage;
  queuePosition: number | null;
  etaSeconds: number | null;
  variant: number;
  count: number;
}

const _entries = new Map<string, Entry>();
// Каталог и префы — на область (у личной — одни на владельца)
const _catalogs = new Map<string, AudioCatalog>();
const _prefs = new Map<string, AudioPrefs>();
const _jobs = new Map<string, JobProgress>();
// Режим, выбранный человеком в чате, и последний режим создания: из них modeState выводит
// действующий режим для полосы, панели и запуска
const _chosenMode = new Map<string, ChosenMode>();
const _lastCreate = new Map<string, AudioMode>();
// Выбор человека, ещё не доехавший до сервера, — общий для панели и полосы
const _pending = new Map<string, PendingSettings>();
const _modeRequests = new Map<string, number>();
// Текст поля режима «Звук» по чатам: панель считает по нему цену и запускает с ним
const _composerText = new Map<string, string>();
// Промпт из карточки агента «Вставить в промпт»: n — повод для затравки поля режима «Звук»
const _suggested = new Map<string, { n: number; text: string }>();
let _version = 0;
const _listeners = new Set<() => void>();
let _unsub: (() => void) | null = null;

const NO_PREFS: AudioPrefs = { voice: null, music: null, process: null };

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

function subscribe(fn: () => void) {
  _listeners.add(fn);
  ensureLive();
  return () => { _listeners.delete(fn); };
}
const getVersion = () => _version;
// Подписка вне React (вид контекста хода узнаёт о смене нитей, каталога и выделения)
export const subscribeAudioStore = subscribe;

// Фокус меняет полосу над композером: выбрали звук — «Звук», сняли — прежняя
function syncStrip(sessionId: string, prev: string | null, next: string | null) {
  if (next && next !== prev) requestStrip(sessionId, SOUND_STRIP);
  else if (!next && prev) releaseStrip(sessionId, SOUND_STRIP);
}

function apply(sessionId: string, scope: string, state: AudioThreadsState) {
  const e = _entries.get(sessionId);
  // Событие старше того, что уже знаем, — пропускаем
  if (e?.loaded && e.state.revision > state.revision) return;
  const prevFocus = e?.loaded ? e.state.focus : null;
  _entries.set(sessionId, { scope, state, loaded: true, loading: false });
  syncStrip(sessionId, prevFocus, state.focus);
  emit();
  // Поле ввода пересчитывает режим «Звук» по сигналу стора полос
  notifyComposer();
}

// Фокус сменился событием с сервера, а не ответом на свой клик, — его выбрал агент
// (audio_focus): панель он не двигает, каркас покажет подсказку в панели другого раздела
function noteAgentFocus(sessionId: string, prev: AudioThreadsState | null, next: AudioThreadsState) {
  if (!prev || prev.focus === next.focus || next.revision < prev.revision) return;
  if (!next.focus) { dropAgentPickOf(sessionId, SOUND_PANEL); return; }
  const t = next.threads.find(x => x.id === next.focus);
  noteAgentPick(sessionId, { panelKey: SOUND_PANEL, target: soundDraftKey(next.focus), label: t ? threadName(t) : 'звук', tab: 'settings' });
}

// Событие модуля → состояние. Нити — только у уже показанного чата: чужой чат догрузится сам при входе
export function handleEvent(ev: AudioEvent) {
  switch (ev.type) {
    case 'audio_thread_changed': {
      const e = _entries.get(ev.sessionId);
      if (!e) return;
      noteAgentFocus(ev.sessionId, e.loaded ? e.state : null, ev.state);
      apply(ev.sessionId, ev.scopeKey, ev.state);
      return;
    }
    case 'audio_prefs_changed': {
      const prev = _prefs.get(ev.scopeKey) ?? NO_PREFS;
      _prefs.set(ev.scopeKey, { ...prev, [ev.mode]: ev.prefs });
      emit();
      return;
    }
    case 'audio_edit_progress':
      _jobs.set(ev.jobId, {
        jobId: ev.jobId, sessionId: ev.chatSessionId, threadId: ev.threadId, stage: ev.stage,
        queuePosition: ev.queuePosition, etaSeconds: ev.etaSeconds, variant: ev.variant, count: ev.count,
      });
      emit();
      return;
    case 'audio_edit_completed':
    case 'audio_edit_failed':
      if (_jobs.delete(ev.jobId)) emit();
      return;
  }
}

function ensureLive() {
  if (_unsub) return;
  const offEvents = audioApi.subscribe(handleEvent);
  // После обрыва события могли потеряться — перечитываем всё, что показано
  const offRe = onReconnected(() => {
    _jobs.clear();
    _entries.forEach((e, sid) => { if (e.loaded) void load(e.scope, sid, true); });
  });
  _unsub = () => { offEvents(); offRe(); };
}

async function load(scope: string, sessionId: string, force = false) {
  const e = _entries.get(sessionId);
  if (e && (e.loading || (e.loaded && !force))) return;
  _entries.set(sessionId, { scope, state: e?.state ?? EMPTY_THREADS, loaded: e?.loaded ?? false, loading: true });
  try {
    const st = await audioApi.state(scope, sessionId);
    _catalogs.set(scope, st.catalog);
    _prefs.set(scope, st.prefs);
    apply(sessionId, scope, st.threads);
  } catch {
    // Модуль выключен или чат не свой — звуков просто нет
    _entries.set(sessionId, { scope, state: EMPTY_THREADS, loaded: true, loading: false });
    emit();
  }
}

// Состояние чата до первой мутации (вход в ещё не открытый чат): без него мутация ушла бы с ревизией 0
export async function ensureAudioThreads(scope: string, sessionId: string): Promise<void> {
  if (_entries.get(sessionId)?.loaded) return;
  await load(scope, sessionId);
}

// Вход в чат: владелец полосы заново просит «Звук» по серверному фокусу
export function enterChat(sessionId: string) {
  const e = _entries.get(sessionId);
  if (e?.loaded && e.state.focus) requestStrip(sessionId, SOUND_STRIP);
}

export function getThreadsState(sessionId: string | null): AudioThreadsState {
  return (sessionId && _entries.get(sessionId)?.state) || EMPTY_THREADS;
}

export function getFocusedThread(sessionId: string | null): AudioThread | null {
  const st = getThreadsState(sessionId);
  return st.focus ? st.threads.find(t => t.id === st.focus) ?? null : null;
}

export const getCatalog = (scope: string): AudioCatalog | null => _catalogs.get(scope) ?? null;
export const getPrefs = (scope: string): AudioPrefs => _prefs.get(scope) ?? NO_PREFS;

// Ответ PUT prefs — сразу в стор, не дожидаясь audio_prefs_changed
export function setScopePrefs(scope: string, prefs: AudioPrefs) {
  _prefs.set(scope, prefs);
  emit();
}

// Идущие задачи нити (или чата, если нить не задана) — для бейджа очереди GPU
export function getJobsOf(sessionId: string | null, threadId: string | null): JobProgress[] {
  if (!sessionId) return [];
  return [..._jobs.values()].filter(j => j.sessionId === sessionId && (threadId === null || j.threadId === threadId));
}

export function useAudioThreads(scope: string, sessionId: string | null): AudioThreadsState {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  useEffect(() => { if (sessionId) void load(scope, sessionId); }, [scope, sessionId]);
  return getThreadsState(sessionId);
}

export function useAudioStoreVersion(): number {
  return useSyncExternalStore(subscribe, getVersion, getVersion);
}

// Мутация от текущей ревизии. 409 — применяем состояние из ответа и говорим об этом;
// false — мутация не прошла
export async function mutate(
  scope: string, sessionId: string,
  run: (revision: number) => Promise<AudioThreadsState>,
): Promise<boolean> {
  try {
    apply(sessionId, scope, await run(getThreadsState(sessionId).revision));
    return true;
  } catch (e) {
    const st = conflictState(e);
    if (st) {
      apply(sessionId, scope, st);
      showToast('Звук только что поменяли в другом месте — показываем свежее состояние', '', 'info');
    } else {
      showToast((e as Error).message || 'Не удалось', '', 'error');
    }
    return false;
  }
}

export const focusThread = (scope: string, sessionId: string, threadId: string | null) =>
  mutate(scope, sessionId, rev => audioApi.focus(scope, sessionId, threadId, rev));

// ── Выделение куска: волна в ленте ↔ поле «Кусок» панели ──
// Одно выделение на нить (макет v2, «Поле «Кусок» и волна в ленте»): карточка в ленте и панель
// читают и пишут его здесь, подписка — через useAudioStoreVersion. versionId — версия, по волне
// которой выделяли: перелистнули версию — выделение обрезается по её длине у того, кто рисует волну.

export interface ThreadSelection extends AudioSelection { versionId: string }

const selKey = (sessionId: string, threadId: string) => `${sessionId}\n${threadId}`;
const _selections = new Map<string, ThreadSelection>();
// Нить, у которой в панели открыта операция с полем «Кусок»: карточка пишет «= «Кусок» в панели»
const _pieceField = new Map<string, string>();
// Просьба карточки к панели переключить операцию («Обрезать», «Перегенерировать кусок»); seq растёт
// на каждую просьбу — панель отрабатывает каждую ровно раз
// piece — у склейки «с другим звуком»: этот звук встаёт первым куском
export interface OperationRequest { threadId: string; op: AudioOp; seq: number; piece?: ConcatPiece }
const _opRequests = new Map<string, OperationRequest>();

export function getSelection(sessionId: string | null, threadId: string | null): ThreadSelection | null {
  return (sessionId && threadId && _selections.get(selKey(sessionId, threadId))) || null;
}

export function setSelection(sessionId: string, threadId: string, sel: ThreadSelection | null) {
  const k = selKey(sessionId, threadId);
  const prev = _selections.get(k) ?? null;
  if (prev === sel || (prev && sel && prev.start === sel.start && prev.end === sel.end && prev.versionId === sel.versionId)) return;
  if (sel) _selections.set(k, sel);
  else _selections.delete(k);
  emit();
}

export function setPieceFieldOpen(sessionId: string, threadId: string | null) {
  if ((_pieceField.get(sessionId) ?? null) === threadId) return;
  if (threadId) _pieceField.set(sessionId, threadId);
  else _pieceField.delete(sessionId);
  emit();
}

export const getPieceFieldOpen = (sessionId: string | null): string | null => (sessionId && _pieceField.get(sessionId)) || null;

// Кнопка под выделением: панель «Звук» на «Настройках», операция — по просьбе
export function requestOperation(sessionId: string, threadId: string, op: AudioOp, piece?: ConcatPiece) {
  const seq = (_opRequests.get(sessionId)?.seq ?? 0) + 1;
  _opRequests.set(sessionId, { threadId, op, seq, ...(piece ? { piece } : {}) });
  emit();
  revealWorkspacePanel(SOUND_PANEL, 'settings');
}

export const getOperationRequest = (sessionId: string | null): OperationRequest | null =>
  (sessionId && _opRequests.get(sessionId)) || null;

// ── Режим ярлыков и поля ввода ──

export function getShortcutMode(sessionId: string | null): AudioMode | null {
  return getChosenMode(sessionId)?.mode ?? null;
}

// Чата ещё нет (панель открыта ярлыком) — выбор держится под пустым ключом
const modeKey = (sessionId: string | null) => sessionId ?? '';
export const getChosenMode = (sessionId: string | null): ChosenMode | null => _chosenMode.get(modeKey(sessionId)) ?? null;
export const getLastCreateMode = (sessionId: string | null): AudioMode | null => _lastCreate.get(modeKey(sessionId)) ?? null;

// Выбор режима человеком. leaving — режим, из которого ушли: режим создания запоминается, чтобы
// «Обработка» без звука вернулась к нему
export function setChosenMode(sessionId: string | null, chosen: ChosenMode, leaving: AudioMode | null = null) {
  _chosenMode.set(modeKey(sessionId), chosen);
  const create = isCreateMode(chosen.mode) ? chosen.mode : leaving && isCreateMode(leaving) ? leaving : null;
  if (create) _lastCreate.set(modeKey(sessionId), create);
  emit();
}

export function setShortcutMode(sessionId: string, mode: AudioMode) {
  setChosenMode(sessionId, { mode, withSound: false });
}

export function getPendingSettings(sessionId: string | null, threadId: string | null): PendingSettings | null {
  const p = _pending.get(modeKey(sessionId));
  return p && p.threadId === threadId ? p : null;
}

export function setPendingSettings(sessionId: string | null, p: PendingSettings) {
  _pending.set(modeKey(sessionId), p);
  emit();
}

// Сохранение доехало: снимаем только свою правку — более свежая остаётся поверх
export function dropPendingSettings(sessionId: string | null, p: PendingSettings) {
  if (_pending.get(modeKey(sessionId)) !== p) return;
  _pending.delete(modeKey(sessionId));
  emit();
}

export function requestSoundMode(sessionId: string) {
  _modeRequests.set(sessionId, (_modeRequests.get(sessionId) ?? 0) + 1);
  notifyComposer();
}

export function getSoundModeRequest(sessionId: string | null): number {
  return (sessionId && _modeRequests.get(sessionId)) || 0;
}

export function getComposerText(sessionId: string | null): string {
  return (sessionId && _composerText.get(sessionId)) || '';
}

export function setComposerText(sessionId: string, text: string) {
  if ((_composerText.get(sessionId) ?? '') === text) return;
  if (text) _composerText.set(sessionId, text);
  else _composerText.delete(sessionId);
  emit();
}

// «Вставить в промпт» из карточки агента: текст ложится в поле режима «Звук» (затравка
// soundMode.prefill — только в нетронутое поле), режим включается сам
export function suggestPrompt(sessionId: string, text: string) {
  _suggested.set(sessionId, { n: (_suggested.get(sessionId)?.n ?? 0) + 1, text });
  requestSoundMode(sessionId);
}

export function getSuggestedPrompt(sessionId: string | null): { n: number; text: string } | null {
  return (sessionId && _suggested.get(sessionId)) || null;
}

// Сброс — только для тестов
export function __resetAudioStore() {
  _entries.clear();
  _catalogs.clear();
  _prefs.clear();
  _jobs.clear();
  _chosenMode.clear();
  _lastCreate.clear();
  _pending.clear();
  _modeRequests.clear();
  _composerText.clear();
  _selections.clear();
  _pieceField.clear();
  _opRequests.clear();
  _suggested.clear();
  emit();
}

// Для тестов: подставить состояние чата, как будто оно пришло с сервера
export function __applyThreads(sessionId: string, scope: string, state: AudioThreadsState) {
  apply(sessionId, scope, state);
}

export function __setScopeData(scope: string, catalog: AudioCatalog, prefs: AudioPrefs) {
  _catalogs.set(scope, catalog);
  _prefs.set(scope, prefs);
  emit();
}
