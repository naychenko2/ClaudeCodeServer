// Стор нитей звука по чатам (ADR-021 §3): состояние с сервера (GET state + audio_thread_changed),
// каталог и префы области (audio_prefs_changed), ход запусков (audio_edit_progress → completed /
// failed) и локальное состояние экрана — выделение куска на волне и открытый редактор.

import { useEffect, useSyncExternalStore } from 'react';
import { onReconnected, showToast } from 'aihome_shell/kit';
import {
  audioApi, conflictState, EMPTY_THREADS,
  type AudioCatalog, type AudioEvent, type AudioPrefs, type AudioStage, type AudioThread,
  type AudioThreadsState,
} from '../api';
import type { AudioSelection } from '../player/selection';
import { SOUND_PANEL } from './panelKey';

export { SOUND_PANEL };
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
// Открытый редактор звука (на весь экран): один на вкладку, как попап «Редактор» у картинок
export interface AudioEditorOpen { sessionId: string; threadId: string; versionId: string | null }
let _editor: AudioEditorOpen | null = null;
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

function apply(sessionId: string, scope: string, state: AudioThreadsState) {
  const e = _entries.get(sessionId);
  // Событие старше того, что уже знаем, — пропускаем
  if (e?.loaded && e.state.revision > state.revision) return;
  _entries.set(sessionId, { scope, state, loaded: true, loading: false });
  emit();
}

// Событие модуля → состояние. Нити — только у уже показанного чата: чужой чат догрузится сам при входе
export function handleEvent(ev: AudioEvent) {
  switch (ev.type) {
    case 'audio_thread_changed': {
      const e = _entries.get(ev.sessionId);
      if (!e) return;
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

export function getThreadsState(sessionId: string | null): AudioThreadsState {
  return (sessionId && _entries.get(sessionId)?.state) || EMPTY_THREADS;
}

export function getFocusedThread(sessionId: string | null): AudioThread | null {
  const st = getThreadsState(sessionId);
  return st.focus ? st.threads.find(t => t.id === st.focus) ?? null : null;
}

export const getCatalog = (scope: string): AudioCatalog | null => _catalogs.get(scope) ?? null;
export const getPrefs = (scope: string): AudioPrefs => _prefs.get(scope) ?? NO_PREFS;

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

// ── Выделение куска на волне ──
// Одно выделение на нить: карточка в ленте, редактор и панель «Контекст» читают и пишут его здесь, подписка — через useAudioStoreVersion. versionId — версия, по волне
// которой выделяли: перелистнули версию — выделение обрезается по её длине у того, кто рисует волну.

export interface ThreadSelection extends AudioSelection { versionId: string }

const selKey = (sessionId: string, threadId: string) => `${sessionId}\n${threadId}`;
const _selections = new Map<string, ThreadSelection>();

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

// ── Редактор звука ──

export const getEditor = (): AudioEditorOpen | null => _editor;

export function openEditor(sessionId: string, threadId: string, versionId: string | null = null) {
  _editor = { sessionId, threadId, versionId };
  emit();
}

export function closeEditor() {
  if (!_editor) return;
  _editor = null;
  emit();
}

// Сброс — только для тестов
export function __resetAudioStore() {
  _entries.clear();
  _catalogs.clear();
  _prefs.clear();
  _jobs.clear();
  _selections.clear();
  _editor = null;
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
