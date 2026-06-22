// Логика офлайн-синхронизации:
// - runOfflineSnapshot — проактивная докачка метаданных всех проектов и содержимого
//   синхронизированных файлов в IndexedDB-кэш (режим «всё заранее»).
// - реактивный стор меток (useSyncMarks/toggleSyncMark) — общий источник для дерева и тулбара.
// - toggleSyncMark — оптимистичная пометка + фоновая докачка с состоянием «синхронизируется».
// - computeSyncState — клиентский расчёт состояния (зеркало бэкенда; корневая метка = весь проект).

import { useSyncExternalStore } from 'react';
import type { FileEntry, SyncMark } from '../types';
import { api } from './api';
import { isOnline } from './offline';
import { idbSet, idbKeys, idbDelete } from './idb';

function parentDir(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

// Раскладывает плоское дерево по per-directory кэшу под теми же URL, что использует
// api.files.list — чтобы FileExplorer (грузит папки по одной) работал офлайн.
async function cacheTreeAsListings(projectId: string, tree: FileEntry[]): Promise<void> {
  const groups = new Map<string, FileEntry[]>();
  // Корень и каждая папка должны иметь запись, даже пустые
  groups.set('', []);
  for (const e of tree) if (e.isDirectory) groups.set(e.path, groups.get(e.path) ?? []);
  for (const e of tree) {
    const parent = parentDir(e.path);
    (groups.get(parent) ?? groups.set(parent, []).get(parent)!).push(e);
  }
  for (const [dir, entries] of groups) {
    // Порядок как в FileService.List: сначала папки, потом файлы, по алфавиту
    entries.sort((a, b) => (a.isDirectory !== b.isDirectory ? (a.isDirectory ? -1 : 1) : a.path.localeCompare(b.path)));
    const url = `/projects/${projectId}/files?path=${encodeURIComponent(dir)}`;
    await idbSet(url, { data: entries, savedAt: Date.now() }).catch(() => {});
  }
}

// --- Прогресс снапшота (для индикатора) ---

interface SyncProgress {
  active: boolean;
  done: number;
  total: number;
}

let _progress: SyncProgress = { active: false, done: 0, total: 0 };
const _listeners = new Set<() => void>();

export function getSyncProgress(): SyncProgress {
  return _progress;
}

export function subscribeSyncProgress(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

function setProgress(patch: Partial<SyncProgress>) {
  _progress = { ..._progress, ...patch };
  _listeners.forEach(fn => fn());
}

// Компактный счётчик прогресса «done/total» (для badge — всегда виден целиком).
// Пока total неизвестен — многоточие.
export function syncCount(p: SyncProgress): string {
  return p.total > 0 ? `${p.done}/${p.total}` : '…';
}

// Полная подпись «Синхронизация done/total» (для footer, где места достаточно).
export function syncLabel(p: SyncProgress): string {
  return p.total > 0 ? `Синхронизация ${p.done}/${p.total}` : 'Синхронизация…';
}

// Параллельный обход с ограничением одновременных задач
async function mapLimit<T>(items: T[], limit: number, fn: (item: T) => Promise<void>): Promise<void> {
  let i = 0;
  const n = Math.min(limit, items.length);
  const workers = Array.from({ length: n }, async () => {
    while (i < items.length) {
      const idx = i++;
      try { await fn(items[idx]); } catch { /* сбойную задачу пропускаем */ }
    }
  });
  await Promise.all(workers);
}

// --- Реактивный стор: метки, активные докачки, скачанные файлы, токены отмены ---
// Общий источник правды для дерева, тулбара FileViewer и тоггла проекта.

const _marks = new Map<string, SyncMark[]>();          // projectId -> метки
const _syncing = new Set<string>();                    // `${projectId}:${path}` — идёт докачка (для подписей)
const _downloaded = new Map<string, Set<string>>();    // projectId -> пути файлов, чьё содержимое уже в кэше
const _cancelTokens = new Map<string, { cancelled: boolean }>(); // `${projectId}:${path}` -> токен отмены
const _markListeners = new Set<() => void>();
let _marksVersion = 0;
let _flushTimer: ReturnType<typeof setTimeout> | null = null;

// Уведомление с throttle (leading): мгновенно + не чаще раза в 100мс — чтобы массовая
// докачка (весь проект) не вызывала шторм перерисовок.
function notifyMarks() {
  _marksVersion++;
  if (_flushTimer) return;
  _markListeners.forEach(fn => fn());
  _flushTimer = setTimeout(() => { _flushTimer = null; _markListeners.forEach(fn => fn()); }, 100);
}

const skey = (projectId: string, path: string) => projectId + ':' + path;
const norm = (p: string) => (p ?? '').replace(/\\/g, '/');

export function getMarks(projectId: string): SyncMark[] {
  return _marks.get(projectId) ?? [];
}

// Идёт ли докачка по этому пути (для подписи «синхронизируется»)
export function isSyncing(projectId: string, path: string): boolean {
  return _syncing.has(skey(projectId, path));
}

// Содержимое файла уже скачано (есть в кэше)
export function isDownloaded(projectId: string, path: string): boolean {
  return _downloaded.get(projectId)?.has(norm(path)) ?? false;
}

function markDownloaded(projectId: string, path: string) {
  const set = _downloaded.get(projectId) ?? new Set<string>();
  set.add(norm(path));
  _downloaded.set(projectId, set);
  notifyMarks();
}

// Удалить локальные копии (содержимое из IDB) при снятии синхронизации.
// path === '' → весь проект; иначе сам путь и всё вложенное.
async function dropSyncedContent(projectId: string, path: string): Promise<void> {
  const p = norm(path);
  const prefix = `/projects/${projectId}/files/content?path=`;
  const keys = await idbKeys().catch(() => [] as string[]);
  const set = _downloaded.get(projectId);
  for (const k of keys) {
    if (!k.startsWith(prefix)) continue;
    let fp: string;
    try { fp = decodeURIComponent(k.slice(prefix.length)); } catch { continue; }
    if (p === '' || fp === p || fp.startsWith(p + '/')) {
      await idbDelete(k).catch(() => {});
      set?.delete(fp);
    }
  }
  notifyMarks();
}

// Заполнить набор скачанных файлов из IndexedDB (по ключам content-запросов проекта)
export async function loadDownloadedSet(projectId: string): Promise<void> {
  const keys = await idbKeys().catch(() => [] as string[]);
  const prefix = `/projects/${projectId}/files/content?path=`;
  const set = new Set<string>();
  for (const k of keys) {
    if (k.startsWith(prefix)) {
      try { set.add(decodeURIComponent(k.slice(prefix.length))); } catch { /* битый ключ */ }
    }
  }
  _downloaded.set(projectId, set);
  notifyMarks();
}

// Загрузить метки проекта в стор (GET кэшируется → работает и офлайн)
export async function loadSyncMarks(projectId: string): Promise<void> {
  const marks = await api.sync.list(projectId).catch(() => null);
  if (marks) { _marks.set(projectId, marks); notifyMarks(); }
}

// React-хук подписки на стор. Возвращает метки проекта; перерисовка при любом изменении стора.
export function useSyncMarks(projectId: string): SyncMark[] {
  useSyncExternalStore(
    cb => { _markListeners.add(cb); return () => { _markListeners.delete(cb); }; },
    () => _marksVersion,
    () => _marksVersion,
  );
  return getMarks(projectId);
}

// --- Клиентский расчёт состояния (зеркало SyncService.GetSyncState) ---
// Корневая метка (path === '') = синхронизация всего проекта → всё вложенное inherited.
export function computeSyncState(marks: SyncMark[], path: string): 'direct' | 'inherited' | null {
  const p = norm(path);
  if (marks.some(m => m.path === p)) return 'direct';
  if (p !== '' && marks.some(m => m.isDirectory && m.path === '')) return 'inherited';
  if (marks.some(m => m.isDirectory && m.path !== '' && p.startsWith(m.path + '/'))) return 'inherited';
  return null;
}

// --- Докачка содержимого ---

// Минимальное время показа спиннера: на localhost докачка мгновенна, без этого спиннер незаметен.
const MIN_SPINNER_MS = 500;

// Пометить файл скачанным с небольшой задержкой — чтобы спиннер успел стать заметным.
function markDownloadedSoon(projectId: string, path: string) {
  setTimeout(() => markDownloaded(projectId, path), MIN_SPINNER_MS);
}

// Файл — один запрос, папка/корень — все файлы рекурсивно. token — для отмены на лету.
async function downloadSyncedContent(projectId: string, entry: { path: string; isDirectory: boolean }, token?: { cancelled: boolean }): Promise<void> {
  if (entry.isDirectory) {
    const tree = await api.files.tree(projectId, entry.path).catch(() => [] as FileEntry[]);
    const files = tree.filter(e => !e.isDirectory);
    await mapLimit(files, 4, async f => {
      if (token?.cancelled) return;
      await api.files.getContent(projectId, f.path).catch(() => {});
      markDownloadedSoon(projectId, f.path);
    });
  } else {
    if (token?.cancelled) return;
    await api.files.getContent(projectId, entry.path).catch(() => {});
    markDownloadedSoon(projectId, entry.path);
  }
}

// --- Снапшот ---

let _snapshotRunning = false;

// Прогон офлайн-снапшота. Идемпотентен: повторный вызов во время работы игнорируется.
// priorityProjectId — проект, с которого начать (текущий открытый при выходе из офлайна).
export async function runOfflineSnapshot(priorityProjectId?: string): Promise<void> {
  if (_snapshotRunning || !isOnline()) return;
  _snapshotRunning = true;
  setProgress({ active: true, done: 0, total: 0 });

  try {
    const projects = await api.projects.list();
    // Начинаем с текущего проекта, остальные — следом
    const ordered = priorityProjectId
      ? [...projects].sort((a, b) => (a.id === priorityProjectId ? -1 : b.id === priorityProjectId ? 1 : 0))
      : projects;
    const contentTasks: { projectId: string; path: string }[] = [];

    for (const p of ordered) {
      const [sessions, treeEntries] = await Promise.all([
        api.sessions.list(p.id).catch(() => []),
        api.sync.list(p.id).catch(() => []), // прогрев кэша меток
        api.files.tree(p.id).catch(() => [] as FileEntry[]),
      ]).then(([s, , t]) => [s, t] as const);

      await mapLimit(sessions, 4, async s => { await api.sessions.getHistory(p.id, s.id).catch(() => {}); });
      await cacheTreeAsListings(p.id, treeEntries);

      // Содержимое синхронизированных файлов (по флагу synced из дерева)
      for (const e of treeEntries) {
        if (!e.isDirectory && e.synced) contentTasks.push({ projectId: p.id, path: e.path });
      }
    }

    setProgress({ total: contentTasks.length, done: 0 });
    let done = 0;
    await mapLimit(contentTasks, 4, async t => {
      await api.files.getContent(t.projectId, t.path).catch(() => {});
      markDownloaded(t.projectId, t.path); // помечаем файл как скачанный → спиннер в дереве гаснет
      setProgress({ done: ++done });
    });
  } catch {
    /* офлайн или сбой — снапшот частичный, не критично */
  } finally {
    _snapshotRunning = false;
    setProgress({ active: false });
  }
}

// --- Переключение метки ---
// Оптимистично обновляем стор (маркеры появляются сразу). При выключении — отменяем
// активную докачку этого пути (через токен), чтобы синхронизацию можно было прервать в любой момент.
export async function toggleSyncMark(projectId: string, entry: FileEntry): Promise<void> {
  const enabling = computeSyncState(getMarks(projectId), entry.path) !== 'direct';
  const k = skey(projectId, entry.path);
  const rest = getMarks(projectId).filter(m => m.path !== entry.path);

  // --- Выключение / отмена ---
  if (!enabling) {
    const token = _cancelTokens.get(k);
    if (token) token.cancelled = true; // прерываем идущую докачку
    _syncing.delete(k);
    _marks.set(projectId, rest);
    notifyMarks();
    try { await api.sync.remove(projectId, entry.path); } catch { /* сеть — согласуем ниже */ }
    await dropSyncedContent(projectId, entry.path); // удаляем локальные копии содержимого
    await loadSyncMarks(projectId);
    return;
  }

  // --- Включение ---
  // Токен создаём СРАЗУ (до запроса), чтобы отмена сработала в любой момент, в т.ч. во время api.sync.add.
  const token = { cancelled: false };
  _cancelTokens.set(k, token);
  _marks.set(projectId, [...rest, { path: entry.path, isDirectory: entry.isDirectory }]);
  _syncing.add(k); notifyMarks();

  try {
    if (token.cancelled) return;
    await api.sync.add(projectId, entry.path, entry.isDirectory);
    if (token.cancelled) { await api.sync.remove(projectId, entry.path).catch(() => {}); return; }
    await downloadSyncedContent(projectId, entry, token);
  } catch {
    /* сетевой сбой — состояние согласуем ниже */
  } finally {
    _syncing.delete(k);
    _cancelTokens.delete(k);
    notifyMarks();
  }

  await loadSyncMarks(projectId); // согласование с сервером (дедуп вложенных меток)
}
