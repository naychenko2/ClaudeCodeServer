// Логика офлайн-синхронизации:
// - runOfflineSnapshot — проактивная докачка метаданных всех проектов и содержимого
//   синхронизированных файлов в IndexedDB-кэш (режим «всё заранее»).
// - toggleSync — пометить/снять файл или папку + докачать содержимое помеченного.
// - computeSyncState — клиентский расчёт состояния синхронизации по меткам (зеркало бэкенда).

import type { FileEntry, SyncMark } from '../types';
import { api } from './api';
import { isOnline } from './offline';
import { idbSet } from './idb';

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

// --- Снапшот ---

let _snapshotRunning = false;

// Прогон офлайн-снапшота. Идемпотентен: повторный вызов во время работы игнорируется.
export async function runOfflineSnapshot(): Promise<void> {
  if (_snapshotRunning || !isOnline()) return;
  _snapshotRunning = true;
  setProgress({ active: true, done: 0, total: 0 });

  try {
    const projects = await api.projects.list();
    const contentTasks: { projectId: string; path: string }[] = [];

    for (const p of projects) {
      const [sessions, treeEntries] = await Promise.all([
        api.sessions.list(p.id).catch(() => []),
        api.sync.list(p.id).catch(() => []), // прогрев кэша меток
        api.files.tree(p.id).catch(() => [] as FileEntry[]),
      ]).then(([s, , t]) => [s, t] as const);

      // История всех чатов проекта
      await mapLimit(sessions, 4, async s => { await api.sessions.getHistory(p.id, s.id).catch(() => {}); });

      // Раскладываем дерево по per-directory кэшу для офлайн-навигации
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
      setProgress({ done: ++done });
    });
  } catch {
    /* офлайн или сбой — снапшот частичный, не критично */
  } finally {
    _snapshotRunning = false;
    setProgress({ active: false });
  }
}

// --- Переключение синхронизации ---

// Пометить/снять синхронизацию файла или папки.
// При включении сразу докачивает содержимое (файл — один запрос, папка — все файлы рекурсивно).
export async function toggleSync(projectId: string, entry: FileEntry): Promise<void> {
  if (entry.synced === 'direct') {
    await api.sync.remove(projectId, entry.path);
    return;
  }

  await api.sync.add(projectId, entry.path, entry.isDirectory);

  if (entry.isDirectory) {
    const tree = await api.files.tree(projectId, entry.path).catch(() => [] as FileEntry[]);
    const files = tree.filter(e => !e.isDirectory);
    await mapLimit(files, 4, async f => { await api.files.getContent(projectId, f.path).catch(() => {}); });
  } else {
    await api.files.getContent(projectId, entry.path).catch(() => {});
  }
}

// Клиентский расчёт состояния синхронизации пути (зеркало SyncService.GetSyncState)
export function computeSyncState(marks: SyncMark[], path: string): 'direct' | 'inherited' | null {
  const norm = path.replace(/\\/g, '/');
  if (marks.some(m => m.path === norm)) return 'direct';
  if (marks.some(m => m.isDirectory && norm.startsWith(m.path + '/'))) return 'inherited';
  return null;
}
