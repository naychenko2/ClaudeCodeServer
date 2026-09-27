// Стор нитей картинок по чатам: состояние с сервера (GET + image_thread_changed),
// мутации с ревизией и локальное состояние экрана — пометки выбранной картинки,
// режим композера, открытый попап «Редактор». Выбор картинки старше запомненной
// полосы: фокус просит полосу «Картинки» у стора полос ядра, снятие — отпускает.

import { useEffect, useSyncExternalStore } from 'react';
import { onReconnected, releaseStrip, requestStrip, showToast } from 'aihome_shell/kit';
import type { Mark } from '../marks';
import { conflictState, EMPTY_THREADS, threadsApi, type ImageThread, type ImageThreadsState } from './threadsApi';

export const IMAGES_STRIP = 'images';

interface Entry { projectId: string; state: ImageThreadsState; loaded: boolean; loading: boolean }

const _entries = new Map<string, Entry>();
// Пометки редактора на нить: уходят со следующим запуском, ✕ на чипе — не прикладывать
const _marks = new Map<string, { marks: Mark[]; size: { w: number; h: number } | null }>();
// Попап «Редактор»: какой чат и какая нить открыты
let _editor: { sessionId: string; threadId: string } | null = null;
let _version = 0;
const _listeners = new Set<() => void>();
let _unsub: (() => void) | null = null;

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

// Фокус меняет полосу над композером: выбрали картинку — «Картинки», сняли — прежняя
function syncStrip(sessionId: string, prev: string | null, next: string | null) {
  if (next && next !== prev) requestStrip(sessionId, IMAGES_STRIP);
  else if (!next && prev) releaseStrip(sessionId, IMAGES_STRIP);
}

function apply(sessionId: string, projectId: string, state: ImageThreadsState) {
  const e = _entries.get(sessionId);
  // Событие старше того, что уже знаем, — пропускаем
  if (e?.loaded && e.state.revision > state.revision) return;
  const prevFocus = e?.loaded ? e.state.focus : null;
  _entries.set(sessionId, { projectId, state, loaded: true, loading: false });
  syncStrip(sessionId, prevFocus, state.focus);
  emit();
}

function ensureLive() {
  if (_unsub) return;
  const offEvents = threadsApi.subscribe(ev => {
    if (!_entries.has(ev.sessionId)) return;
    apply(ev.sessionId, ev.projectId, ev.state);
  });
  // После обрыва события могли потеряться — перечитываем всё, что показано
  const offRe = onReconnected(() => {
    _entries.forEach((e, sid) => { if (e.loaded) void load(e.projectId, sid, true); });
  });
  _unsub = () => { offEvents(); offRe(); };
}

async function load(projectId: string, sessionId: string, force = false) {
  const e = _entries.get(sessionId);
  if (e && (e.loading || (e.loaded && !force))) return;
  _entries.set(sessionId, { projectId, state: e?.state ?? EMPTY_THREADS, loaded: e?.loaded ?? false, loading: true });
  try {
    apply(sessionId, projectId, await threadsApi.get(projectId, sessionId));
  } catch {
    // Модуль выключен или чат не свой — нитей просто нет
    _entries.set(sessionId, { projectId, state: EMPTY_THREADS, loaded: true, loading: false });
    emit();
  }
}

// Состояние чата до первой мутации (вход из дерева в ещё не открытый чат): без него
// мутация ушла бы с ревизией 0 и словила 409. Сбой запроса — наружу, вызывающему
export async function ensureThreads(projectId: string, sessionId: string): Promise<void> {
  if (_entries.get(sessionId)?.loaded) return;
  apply(sessionId, projectId, await threadsApi.get(projectId, sessionId));
}

// Вход в чат: владелец полосы заново просит «Картинки» по серверному фокусу — ручной уход
// на другую полосу держится только до выхода из чата, как и после перезагрузки. Ещё не
// загруженный чат попросит полосу сам, когда придёт его состояние
export function enterChat(sessionId: string) {
  const e = _entries.get(sessionId);
  if (e?.loaded && e.state.focus) requestStrip(sessionId, IMAGES_STRIP);
}

export function getThreadsState(sessionId: string | null): ImageThreadsState {
  return (sessionId && _entries.get(sessionId)?.state) || EMPTY_THREADS;
}

export function getFocusedThread(sessionId: string | null): ImageThread | null {
  const st = getThreadsState(sessionId);
  return st.focus ? st.threads.find(t => t.id === st.focus) ?? null : null;
}

export function useThreads(projectId: string | null, sessionId: string | null): ImageThreadsState {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  useEffect(() => { if (projectId && sessionId) void load(projectId, sessionId); }, [projectId, sessionId]);
  return getThreadsState(sessionId);
}

// Подписка на локальную часть стора (пометки, попап) без загрузки нитей
export function useThreadStoreVersion(): number {
  return useSyncExternalStore(subscribe, getVersion, getVersion);
}

// Мутация от текущей ревизии. 409 — применяем состояние из ответа и говорим об этом;
// false — мутация не прошла
export async function mutate(
  projectId: string, sessionId: string,
  run: (revision: number) => Promise<ImageThreadsState>,
): Promise<boolean> {
  try {
    apply(sessionId, projectId, await run(getThreadsState(sessionId).revision));
    return true;
  } catch (e) {
    const st = conflictState(e);
    if (st) {
      apply(sessionId, projectId, st);
      showToast('Картинку только что поменяли в другом месте — показываем свежее состояние', '', 'info');
    } else {
      showToast((e as Error).message || 'Не удалось', '', 'error');
    }
    return false;
  }
}

export const focusThread = (projectId: string, sessionId: string, threadId: string | null) =>
  mutate(projectId, sessionId, rev => threadsApi.focus(projectId, sessionId, threadId, rev));

// ── Пометки ──

export function getThreadMarks(threadId: string | null) {
  return (threadId && _marks.get(threadId)) || { marks: [], size: null };
}

export function setThreadMarks(threadId: string, marks: Mark[], size: { w: number; h: number } | null) {
  if (marks.length) _marks.set(threadId, { marks, size });
  else _marks.delete(threadId);
  emit();
}

// ── Попап «Редактор» ──

export function getEditor() { return _editor; }

export function openEditor(sessionId: string, threadId: string) {
  _editor = { sessionId, threadId };
  emit();
}

export function closeEditor() {
  _editor = null;
  emit();
}

// Сброс — только для тестов
export function __resetThreadStore() {
  _entries.clear();
  _marks.clear();
  _editor = null;
  emit();
}

// Для тестов: подставить состояние чата, как будто оно пришло с сервера
export function __applyThreads(sessionId: string, projectId: string, state: ImageThreadsState) {
  apply(sessionId, projectId, state);
}
