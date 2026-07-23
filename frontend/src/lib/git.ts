// Git проекта: глобальный стор статуса/истории/веток + realtime. Паттерн — как lib/notes.ts.
// Realtime: бэк шлёт git_status_changed в группу user_{userId} после любой мутации
// (commit/stage/checkout/…, в т.ч. с другого устройства) — стор перечитывает статус.

import { useSyncExternalStore } from 'react';
import type { GitStatus, GitBranchInfo, GitLogEntry, GitStashEntry, GitRemoteInfo } from '../types';
import { api } from './api';
import { joinUser, onFilesChanged, onGitStatusChanged, onReconnected } from './signalr';

export interface GitProjectState {
  status: GitStatus | null;
  statusLoaded: boolean;   // статус хоть раз получен (для гейта сегментов пилюли)
  log: GitLogEntry[];
  logLoaded: boolean;
  unpushed: GitLogEntry[];     // незапушенные коммиты (стек скоупов панели «Изменения»)
  unpushedLoaded: boolean;
  branches: GitBranchInfo[];
  stashes: GitStashEntry[];
  remote: GitRemoteInfo | null;  // удалённый репозиторий + авто-коммит (null — не загружено)
  error: string | null;    // последняя ошибка операции (409 { error }) — компактная строка в UI
  busy: boolean;           // идёт сетевая git-операция (блокируем кнопки)
}

const EMPTY: GitProjectState = {
  status: null, statusLoaded: false,
  log: [], logLoaded: false,
  unpushed: [], unpushedLoaded: false,
  branches: [], stashes: [], remote: null, error: null, busy: false,
};

const _state = new Map<string, GitProjectState>();
const _listeners = new Set<() => void>();
let _realtimeWired = false;

function emit() { _listeners.forEach(fn => fn()); }

function get(projectId: string): GitProjectState {
  return _state.get(projectId) ?? EMPTY;
}

function patch(projectId: string, p: Partial<GitProjectState>) {
  _state.set(projectId, { ...get(projectId), ...p });
  emit();
}

function joinUserGroup() {
  const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id');
  if (uid) joinUser(uid).catch(() => {});
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onGitStatusChanged(({ projectId }) => {
    if (!_state.has(projectId)) return;   // проект не открывали — не дёргаем
    void loadGitStatus(projectId);
    if (get(projectId).logLoaded) void loadGitLog(projectId);
    // Стек незапушенных меняется коммитом/публикацией — держим свежим (иначе после
    // commit/push панель «Изменения» не обновится сама, лишь ahead в статусе)
    if (get(projectId).unpushedLoaded) void loadUnpushedLog(projectId);
    // Стэш меняется теми же мутациями (push/pop/drop, в т.ч. с другого устройства)
    void loadGitStash(projectId);
  });
  onReconnected(() => {
    joinUserGroup();
    for (const id of _state.keys()) void loadGitStatus(id);
  });
  // Правки файлов мимо git-операций (ход Claude, внешний редактор, watcher) тоже
  // меняют статус — перечитываем с дебаунсом, чтобы серия file_changed не спамила
  onFilesChanged(({ projectId }) => {
    if (!_state.has(projectId)) return;
    const prev = _fileDebounce.get(projectId);
    if (prev) clearTimeout(prev);
    _fileDebounce.set(projectId, setTimeout(() => {
      _fileDebounce.delete(projectId);
      void loadGitStatus(projectId);
    }, 1500));
  });
  // Возврат фокуса на вкладку/окно → перечитываем статус всех открытых проектов.
  // Закрывает дыру file-watcher'а: внешние правки (терминал, Rider, коммит/чекаут
  // вне приложения) меняют .git — а он исключён из watcher'а, так что realtime их
  // не ловит. Без этого приходилось обновлять страницу руками.
  const onFocus = () => {
    if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
    // focus + visibilitychange стреляют парой на один возврат — троттлим, чтобы не
    // дёргать git status дважды
    const now = Date.now();
    if (now - _lastFocusRefresh < 800) return;
    _lastFocusRefresh = now;
    for (const id of _state.keys()) {
      void loadGitStatus(id);
      if (get(id).unpushedLoaded) void loadUnpushedLog(id);
      void loadGitStash(id);
    }
  };
  if (typeof window !== 'undefined') {
    window.addEventListener('focus', onFocus);
    document.addEventListener('visibilitychange', onFocus);
  }
}

const _fileDebounce = new Map<string, ReturnType<typeof setTimeout>>();
let _lastFocusRefresh = 0;   // троттл refresh по фокусу окна

// Подключение стора для проекта: realtime + первичная загрузка статуса
export function ensureGit(projectId: string): void {
  wireRealtime();
  joinUserGroup();
  if (!_state.has(projectId)) {
    _state.set(projectId, { ...EMPTY });
    void loadGitStatus(projectId);
  }
}

export async function loadGitStatus(projectId: string): Promise<void> {
  try {
    const status = await api.git.status(projectId);
    patch(projectId, { status, statusLoaded: true });
  } catch {
    // офлайн/ошибка — считаем «не репо», сегменты git скрыты
    patch(projectId, { statusLoaded: true });
  }
}

export async function loadGitLog(projectId: string, limit = 100): Promise<void> {
  try {
    const log = await api.git.log(projectId, limit);
    patch(projectId, { log, logLoaded: true });
  } catch (e) {
    patch(projectId, { logLoaded: true, error: e instanceof Error ? e.message : 'Не удалось загрузить историю' });
  }
}

export async function loadUnpushedLog(projectId: string, limit = 100): Promise<void> {
  try {
    const unpushed = await api.git.unpushed(projectId, limit);
    patch(projectId, { unpushed, unpushedLoaded: true });
  } catch {
    // без upstream/ошибка — стек пуст (панель покажет только «Не зафиксировано»)
    patch(projectId, { unpushed: [], unpushedLoaded: true });
  }
}

export async function loadGitBranches(projectId: string): Promise<void> {
  try {
    const branches = await api.git.branches(projectId);
    patch(projectId, { branches });
  } catch { /* меню веток просто останется пустым */ }
}

export async function loadGitStash(projectId: string): Promise<void> {
  try {
    const stashes = await api.git.stashList(projectId);
    patch(projectId, { stashes });
  } catch { /* секция стэшей просто останется пустой */ }
}

export async function loadGitRemote(projectId: string): Promise<void> {
  try {
    const remote = await api.git.remote(projectId);
    patch(projectId, { remote });
  } catch { /* без remote-инфо скрываем кнопку Forgejo и настройки авто-коммита */ }
}

// Общая обёртка мутации: busy + сброс ошибки → операция → свежий статус либо ошибка в стор
async function mutate(projectId: string, op: () => Promise<GitStatus>): Promise<boolean> {
  patch(projectId, { busy: true, error: null });
  try {
    const status = await op();
    patch(projectId, { status, statusLoaded: true, busy: false });
    return true;
  } catch (e) {
    patch(projectId, { busy: false, error: e instanceof Error ? e.message : 'Ошибка git-операции' });
    return false;
  }
}

export const gitStage = (projectId: string, path: string) =>
  mutate(projectId, () => api.git.stage(projectId, path));
export const gitUnstage = (projectId: string, path: string) =>
  mutate(projectId, () => api.git.unstage(projectId, path));
export const gitStageAll = (projectId: string) =>
  mutate(projectId, () => api.git.stageAll(projectId));
export const gitDiscard = (projectId: string, path: string) =>
  mutate(projectId, () => api.git.discard(projectId, path));

export const gitDiscardAll = (projectId: string) =>
  mutate(projectId, () => api.git.discardAll(projectId));
export const gitCheckout = (projectId: string, branch: string) =>
  mutate(projectId, () => api.git.checkout(projectId, branch)).then(ok => {
    if (ok) void loadGitBranches(projectId);
    return ok;
  });
export const gitCreateBranch = (projectId: string, name: string, from?: string) =>
  mutate(projectId, () => api.git.createBranch(projectId, name, from)).then(ok => {
    if (ok) void loadGitBranches(projectId);
    return ok;
  });
export const gitStashPush = (projectId: string, message?: string) =>
  mutate(projectId, () => api.git.stashPush(projectId, message)).then(ok => {
    if (ok) void loadGitStash(projectId);
    return ok;
  });
export const gitStashPop = (projectId: string, index: number) =>
  mutate(projectId, () => api.git.stashPop(projectId, index)).then(ok => {
    if (ok) void loadGitStash(projectId);
    return ok;
  });
export const gitStashDrop = (projectId: string, index: number) =>
  mutate(projectId, () => api.git.stashDrop(projectId, index)).then(ok => {
    if (ok) void loadGitStash(projectId);
    return ok;
  });
export const gitFetch = (projectId: string) =>
  mutate(projectId, () => api.git.fetch(projectId));
export const gitPull = (projectId: string) =>
  mutate(projectId, () => api.git.pull(projectId));
export const gitPush = (projectId: string) =>
  mutate(projectId, () => api.git.push(projectId));

// Документный режим: вернуть файл к версии коммита (в авто-режиме сразу фиксируется)
export const gitRestoreFile = (projectId: string, sha: string, path: string) =>
  mutate(projectId, () => api.git.restoreFile(projectId, sha, path)).then(ok => {
    if (ok && get(projectId).logLoaded) void loadGitLog(projectId);
    return ok;
  });

// Документный режим: «Сохранить сейчас» — commit всего с ✨-сообщением (+push при авто-пуше)
export async function gitSaveNow(projectId: string): Promise<boolean> {
  patch(projectId, { busy: true, error: null });
  try {
    await api.git.saveNow(projectId);
    patch(projectId, { busy: false });
    await loadGitStatus(projectId);
    if (get(projectId).logLoaded) void loadGitLog(projectId);
    return true;
  } catch (e) {
    patch(projectId, { busy: false, error: e instanceof Error ? e.message : 'Не удалось сохранить' });
    return false;
  }
}

// Коммит: message = summary + описание; после успеха обновляем статус и историю
export async function gitCommit(projectId: string, message: string, amend = false): Promise<boolean> {
  patch(projectId, { busy: true, error: null });
  try {
    await api.git.commit(projectId, message, amend);
    patch(projectId, { busy: false });
    await loadGitStatus(projectId);
    if (get(projectId).logLoaded) void loadGitLog(projectId);
    return true;
  } catch (e) {
    patch(projectId, { busy: false, error: e instanceof Error ? e.message : 'Не удалось создать коммит' });
    return false;
  }
}

// Откат коммита (revert): null — успех, строка — текст ошибки (409 при конфликте)
export async function gitRevertCommit(projectId: string, sha: string): Promise<string | null> {
  patch(projectId, { busy: true, error: null });
  try {
    const status = await api.git.revertCommit(projectId, sha);
    patch(projectId, { status, statusLoaded: true, busy: false });
    if (get(projectId).logLoaded) void loadGitLog(projectId);
    return null;
  } catch (e) {
    const msg = e instanceof Error ? e.message : 'Не удалось откатить коммит';
    patch(projectId, { busy: false, error: msg });
    return msg;
  }
}

// git init (+ remote на Forgejo, если настроен): после успеха статус и remote в сторе свежие
export async function gitInit(projectId: string): Promise<{ ok: boolean; htmlUrl: string | null; error?: string }> {
  patch(projectId, { busy: true, error: null });
  try {
    const r = await api.git.init(projectId);
    patch(projectId, { status: r.status, statusLoaded: true, busy: false });
    void loadGitRemote(projectId);
    return { ok: true, htmlUrl: r.htmlUrl };
  } catch (e) {
    const msg = e instanceof Error ? e.message : 'Не удалось создать git-репозиторий';
    patch(projectId, { busy: false, error: msg });
    return { ok: false, htmlUrl: null, error: msg };
  }
}

// Настройки авто-коммита после хода ИИ (enabled) и авто-пуша (push)
export async function gitSetAutoCommit(projectId: string, enabled: boolean, push: boolean): Promise<boolean> {
  try {
    const r = await api.git.setAutoCommit(projectId, enabled, push);
    const remote = get(projectId).remote;
    if (remote) patch(projectId, { remote: { ...remote, autoCommit: r.autoCommit, autoPush: r.autoPush } });
    else void loadGitRemote(projectId);
    return true;
  } catch (e) {
    patch(projectId, { error: e instanceof Error ? e.message : 'Не удалось сохранить настройку' });
    return false;
  }
}

export function clearGitError(projectId: string): void {
  if (get(projectId).error) patch(projectId, { error: null });
}

// Состояние git проекта (статус/история/ветки/busy/ошибка)
export function useGitState(projectId: string): GitProjectState {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _state.get(projectId) ?? EMPTY,
    () => _state.get(projectId) ?? EMPTY,
  );
}
