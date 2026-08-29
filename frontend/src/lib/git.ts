// Git проекта: глобальный стор статуса/истории/веток + realtime. Паттерн — как lib/notes.ts.
// Realtime: бэк шлёт git_status_changed в группу user_{userId} после любой мутации
// (commit/stage/checkout/…, в т.ч. с другого устройства) — стор перечитывает статус.

import { useSyncExternalStore } from 'react';
import type { GitStatus, GitBranchInfo, GitLogEntry, GitStashEntry, GitRemoteInfo, ChangedBySession } from '../types';
import { api, getGitSessionContext } from './api';
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
  // Последняя ошибка — расхождение с origin (409 { diverged: true }): у неё есть лекарство,
  // и UI предлагает «Подтянуть и опубликовать» вместо простого текста ошибки
  diverged: boolean;
  // Файлы, на которых споткнулось автоматическое слияние (409 { conflictFiles }): UI
  // показывает их поимённо и предлагает разобрать конфликт в чате
  conflictFiles: string[];
  busy: boolean;           // идёт сетевая git-операция (блокируем кнопки)
  // Путь git status (как он пришёл от сервера) → чужие чаты, менявшие файл — панель
  // «Изменения» (бейдж строки) и шапка диффа («Также меняли»). Пусто — не загружено,
  // гейт worktree-контекста активного чата или у файлов правда нет чужих чатов.
  // Только записи с external=false: правка вне заявленного хода к чату не привязана.
  changedBy: Map<string, ChangedBySession[]>;
  // Пути АКТИВНОГО чата (lowercase, включая external=true) — фильтр «только файлы
  // чата» и признак mine у бейджа. Источник — тот же серверный changed-by (история
  // чата минус зафиксированное в git), фронтовый дубль по живой ленте снесён.
  // undefined — фильтр недоступен (нет активного чата / worktree-контекст панели /
  // changed-by не загрузился); пустой Set — чат ничего не менял.
  myChangedPaths: Set<string> | undefined;
  // Чаты проекта, за которыми числятся НЕзафиксированные в git правки — значок в списке
  // чатов (SessionList). Тот же сырой changed-by: запрос уходит РОВНО по грязным путям
  // git status, а сервер уже вычел Session.CommittedFilePaths, поэтому sessionId в
  // ответе = у чата есть незакоммиченное.
  // Записи external=true ОТБРАСЫВАЮТСЯ, как и в changedBy. Причина не косметическая:
  // пока в чате идёт ход, файловый вотчер пишет в его историю КАЖДУЮ правку файлов репы —
  // в том числе чужие (человек в IDE, соседний чат), и они оседают там как external.
  // На боевых данных это давало 39 ложных совпадений из 48: чат про CSS шапки был
  // помечен правками PreviewController/DevServerService, которых в глаза не видел.
  // От changedBy отличие ровно одно — активный чат НЕ исключается: свой значок он носит
  // наравне с прочими.
  // undefined — достоверно неизвестно (не загружено / worktree-контекст / ошибка
  // запроса); пустой Set — знание достоверно и незакоммиченного нет ни у кого.
  dirtySessionIds: Set<string> | undefined;
}

const EMPTY: GitProjectState = {
  status: null, statusLoaded: false,
  log: [], logLoaded: false,
  unpushed: [], unpushedLoaded: false,
  branches: [], stashes: [], remote: null, error: null, diverged: false, conflictFiles: [], busy: false,
  changedBy: new Map(),
  myChangedPaths: undefined,
  dirtySessionIds: undefined,
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
// refresh — перечитать статус даже при уже инициализированном сторе (смена
// worktree-контекста активного чата: закешированный статус — от другого дерева)
export function ensureGit(projectId: string, refresh = false): void {
  wireRealtime();
  joinUserGroup();
  if (!_state.has(projectId)) {
    _state.set(projectId, { ...EMPTY });
    void loadGitStatus(projectId);
  } else if (refresh) {
    void loadGitStatus(projectId);
  }
}

export async function loadGitStatus(projectId: string): Promise<void> {
  try {
    const status = await api.git.status(projectId);
    patch(projectId, { status, statusLoaded: true });
    void loadChangedBy(projectId, status);
  } catch {
    // офлайн/ошибка — считаем «не репо», сегменты git скрыты.
    // changedBy/myChangedPaths/dirtySessionIds намеренно НЕ сбрасываем: упавший статус
    // ничего не сообщает о фиксации правок, а гашение значков в списке чатов при каждом
    // сетевом сбое читалось бы как «всё закоммичено». Держим последнее известное до
    // первого удачного статуса — realtime и возврат фокуса перечитают его сами.
    patch(projectId, { statusLoaded: true });
  }
}

// Активный чат для фильтра changedBy: панель «Также меняли»/бейдж не должны включать
// сам открытый чат в свой же список «другие чаты». Задаётся вызывающим кодом
// (WorkspacePage) при смене активного чата — стору самому её взять неоткуда.
const _activeSessionForChangedBy = new Map<string, string | null>();
// Сырой (нефильтрованный по активному чату) последний ответ сервера — чтобы переключение
// чата могло перефильтровать уже загруженные данные БЕЗ нового запроса и не оставляло в
// сторе устаревший фильтр до следующего git status (иначе после смены чата «Также меняли»
// на миг показывал бы сам новый активный чат и терял прежний — до следующего рефреша).
const _rawChangedBy = new Map<string, Record<string, ChangedBySession[]>>();

export function setActiveSessionForChangedBy(projectId: string, sessionId: string | null): void {
  _activeSessionForChangedBy.set(projectId, sessionId);
  applyChangedByFilter(projectId);
}

// Перефильтровывает и укладывает в стор последний сырой ответ по текущему активному чату —
// ЕДИНСТВЕННАЯ точка фильтрации changed-by. Не грузило — нечего перефильтровывать
// (первый git status ещё не пришёл). Из сырого ответа собираются обе поверхности:
// - changedBy (бейдж строки/«Также меняли»): без активного чата и без external=true —
//   внешняя правка к чату не привязана, поведение бейджей прежнее;
// - myChangedPaths (фильтр «только файлы чата»): пути активного чата, ВКЛЮЧАЯ
//   external=true; ключи — lowercase (GitChangesRail сравнивает по lowercase).
//   Нет активного чата — undefined (фильтр недоступен, тумблер скрыт).
// - dirtySessionIds (значок «правки не зафиксированы» в списке чатов): чаты записей
//   external=false — как в changedBy, но активный чат остаётся (он не «другой себе»,
//   а такой же владелец незафиксированных правок).
function applyChangedByFilter(projectId: string): void {
  const raw = _rawChangedBy.get(projectId);
  if (!raw) return;
  const activeSessionId = _activeSessionForChangedBy.get(projectId) ?? null;
  const changedBy = new Map<string, ChangedBySession[]>();
  const myChangedPaths = activeSessionId ? new Set<string>() : undefined;
  const dirtySessionIds = new Set<string>();
  for (const [path, entries] of Object.entries(raw)) {
    const others = entries.filter(e => !e.external && (!activeSessionId || e.sessionId !== activeSessionId));
    if (others.length > 0) changedBy.set(path, others);
    if (myChangedPaths && entries.some(e => e.sessionId === activeSessionId))
      myChangedPaths.add(path.toLowerCase());
    for (const e of entries) if (!e.external) dirtySessionIds.add(e.sessionId);
  }
  patch(projectId, { changedBy, myChangedPaths, dirtySessionIds });
}

// Догружает changedBy по путям текущего git status — общей цепочкой с loadGitStatus,
// поэтому срабатывает после любого её вызова (realtime, focus, дебаунс file_changed).
// Гейт: пока панель смотрит git worktree активного чата (getGitSessionContext), пути
// status относятся к дереву чата, а не к rootPath проекта — индекс их не знает.
// Трёхзначность myChangedPaths по веткам:
// - worktree-контекст / ошибка запроса → undefined (фильтр недоступен, тумблер скрыт);
// - пустой git status → пустой Set при активном чате (чат ничего не менял из видимого),
//   undefined без него;
// - штатный ответ → собирает applyChangedByFilter.
// dirtySessionIds идёт теми же ветками, но без оглядки на активный чат: пустой git
// status — достоверное «ни у кого нет незакоммиченного» (пустой Set), а недоступность
// данных (worktree-контекст, ошибка) — undefined, и значки в списке просто не рисуются.
async function loadChangedBy(projectId: string, status: GitStatus): Promise<void> {
  const ctx = getGitSessionContext();
  if (ctx?.projectId === projectId) {
    _rawChangedBy.delete(projectId);
    patch(projectId, { changedBy: new Map(), myChangedPaths: undefined, dirtySessionIds: undefined });
    return;
  }
  const paths = [...status.staged, ...status.unstaged, ...status.untracked].map(f => f.path);
  if (paths.length === 0) {
    _rawChangedBy.delete(projectId);
    const active = _activeSessionForChangedBy.get(projectId) ?? null;
    patch(projectId, {
      changedBy: new Map(),
      myChangedPaths: active ? new Set() : undefined,
      dirtySessionIds: new Set(),
    });
    return;
  }
  try {
    const { files } = await api.files.changedBy(projectId, paths);
    _rawChangedBy.set(projectId, files);
    applyChangedByFilter(projectId);
  } catch {
    _rawChangedBy.delete(projectId);
    patch(projectId, { changedBy: new Map(), myChangedPaths: undefined, dirtySessionIds: undefined });
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

// Данные из тела 409 сверх текста ошибки: расхождение с origin (лечится «Подтянуть и
// опубликовать») и файлы неразрешённого конфликта. request() вешает распарсенное тело
// ошибки на поле body (см. lib/offline.ts).
function errorBody(e: unknown): { diverged?: boolean; conflictFiles?: string[] } {
  return (e as { body?: { diverged?: boolean; conflictFiles?: string[] } } | null)?.body ?? {};
}

// Общая обёртка мутации: busy + сброс ошибки → операция → свежий статус либо ошибка в стор
async function mutate(projectId: string, op: () => Promise<GitStatus>): Promise<boolean> {
  patch(projectId, { busy: true, error: null, diverged: false, conflictFiles: [] });
  try {
    const status = await op();
    patch(projectId, { status, statusLoaded: true, busy: false });
    return true;
  } catch (e) {
    const body = errorBody(e);
    patch(projectId, {
      busy: false,
      error: e instanceof Error ? e.message : 'Ошибка git-операции',
      diverged: body.diverged === true,
      conflictFiles: body.conflictFiles ?? [],
    });
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
// «Подтянуть и опубликовать»: rebase на origin + push одним действием — для ветки,
// разошедшейся с origin (обычный push там отклоняется, pull --ff-only не проходит)
export const gitSync = (projectId: string) =>
  mutate(projectId, () => api.git.sync(projectId));

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

// Сводная статистика рабочих изменений: суммарные +added/−deleted и число файлов.
// Мерж по пути (файл может быть и в staged, и в unstaged — не задваиваем; numstat
// tracked-файлов считается vs HEAD, значения по группам совпадают). Untracked
// добавляем только если пути ещё нет. Повторяет логику mergeWorking из GitChangesRail.
export function workingDiffStat(status: GitStatus | null): { added: number; deleted: number; files: number } {
  if (!status) return { added: 0, deleted: 0, files: 0 };
  const seen = new Map<string, { added: number; deleted: number }>();
  const put = (path: string, added: number | null | undefined, deleted: number | null | undefined) => {
    if (seen.has(path)) return;
    seen.set(path, { added: added ?? 0, deleted: deleted ?? 0 });
  };
  for (const f of status.staged) put(f.path, f.added, f.deleted);
  for (const f of status.unstaged) put(f.path, f.added, f.deleted);
  for (const f of status.untracked) put(f.path, f.added, f.deleted);
  let added = 0, deleted = 0;
  for (const v of seen.values()) { added += v.added; deleted += v.deleted; }
  return { added, deleted, files: seen.size };
}

// Состояние git проекта (статус/история/ветки/busy/ошибка)
export function useGitState(projectId: string): GitProjectState {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _state.get(projectId) ?? EMPTY,
    () => _state.get(projectId) ?? EMPTY,
  );
}

// Снимок состояния без подписки (для кода вне React-рендера — напр. тестов): та же
// семантика, что у useGitState, без useSyncExternalStore. Аналог getNotesSnapshot (notes.ts).
export function getGitState(projectId: string): GitProjectState {
  return get(projectId);
}
