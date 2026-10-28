// Логика офлайн-синхронизации:
// - runOfflineSnapshot — проактивная докачка метаданных всех проектов и содержимого
//   синхронизированных файлов в IndexedDB-кэш (режим «всё заранее»).
// - реактивный стор меток (useSyncMarks/toggleSyncMark) — общий источник для дерева и тулбара.
// - toggleSyncMark — оптимистичная пометка + фоновая докачка с состоянием «синхронизируется».
// - computeSyncState — клиентский расчёт состояния (зеркало бэкенда; корневая метка = весь проект).

import { useSyncExternalStore } from 'react';
import type { FileEntry, Session, SyncMark } from '../types';
import { api } from './api';
import {
  isOnline, passiveCacheKey, fileContentCachePrefix, flushFileEviction,
  onPassiveEvict, parseFileContentCacheKey, setKeptFilePaths, keepFilePath, unkeepFilePath,
  writePassiveRangeEntry,
} from './offline';
import { idbGet, idbSet, idbKeys, idbDelete, idbRangeStamps } from './idb';
import { isSubsystemEnabled } from './subsystems';
import { warmNote, drainNotesOutbox } from './notesOffline';
import { drainTaskOutbox } from './taskOutbox';
import './tasks';   // side-effect: configureOutbox регистрирует store-хуки очереди задач

function parentDir(path: string): string {
  const i = path.lastIndexOf('/');
  return i < 0 ? '' : path.slice(0, i);
}

// Раскладывает плоское дерево по per-directory кэшу под теми же URL, что использует
// api.files.list — чтобы FileExplorer (грузит папки по одной) работал офлайн.
// Ключ — общий с пассивным кэшем request(): offline:<owner>:<url>. Без владельца
// (не вошли) кэша нет — писать некуда.
//
// Запись идёт через writePassiveRangeEntry: листинги снапшота — это сотни записей
// за один проход, и прямой записью в IDB они проходили мимо счётчика диапазона
// (потолок 200 записей на владельца держал установившееся состояние, а на пике
// после снапшота в диапазоне лежало 957 записей). Через общий учёт потолок держится
// и на пике: место под листинг освобождается ДО его записи.
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
    const key = passiveCacheKey(url);
    if (key === null) continue;
    await writePassiveRangeEntry(key, entries);
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
// Верх диапазона ключей в сторе (для курсора по диапазону).
const KEY_RANGE_END = String.fromCharCode(0xffff);
const norm = (p: string) => (p ?? '').replace(/\\/g, '/');

// --- Состояние синхронизации ---
// Пути файлов, помеченных к офлайн-синхронизации (прямо помеченные и помеченные по
// наследству от папки). Список живёт в offline.ts: там же решается, чьё содержимое
// защищено от вытеснения содержимым непомеченных файлов.

// Метки синхронизации проекта → помеченные файлы и каталоги. Метки — источник правды
// о том, что человек пометил; дерево помечает лишь файлы, дошедшие в ответе.
function publishKeptPaths(projectId: string): void {
  const marks = _marks.get(projectId) ?? [];
  const files: string[] = [];
  const dirs: string[] = [];
  for (const m of marks) {
    if (m.isDirectory) dirs.push(norm(m.path));
    else files.push(norm(m.path));
  }
  setKeptFilePaths(projectId, files, dirs);
}

// Состояние синхронизации по ПОЛНОМУ дереву проекта: заменяет прежнее.
function rememberSyncedFiles(projectId: string, tree: FileEntry[]): void {
  const paths: string[] = [];
  for (const e of tree) {
    if (e.isDirectory) continue;
    if (!e.synced) continue;
    paths.push(norm(e.path));
  }
  const marks = _marks.get(projectId) ?? [];
  const dirs = marks.filter(m => m.isDirectory).map(m => norm(m.path));
  setKeptFilePaths(projectId, paths, dirs);
}

// Состояние синхронизации по ПОДДЕРЕВУ (докачка помеченной папки): дополняет
// прежнее, а не заменяет — поддерево не знает о пометках в других ветках.
// Помечаются ВСЕ файлы поддерева: человек только что включил синхронизацию этой
// папки, и её содержимое должно лежать в защищённой части класса (даже если сервер
// ещё не проставил метки «по наследству» в дереве).
function addSyncedFiles(projectId: string, tree: FileEntry[]): void {
  for (const e of tree) {
    if (e.isDirectory) continue;
    keepFilePath(projectId, norm(e.path));
  }
}

// Снять пометку: путь и всё вложенное больше не помечены.
function forgetSyncedFiles(projectId: string, path: string): void {
  unkeepFilePath(projectId, norm(path));
}

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
  const prefix = fileContentCachePrefix(projectId);
  const set = _downloaded.get(projectId);
  if (prefix !== null) {
    const keys = await idbKeys().catch(() => [] as string[]);
    for (const k of keys) {
      if (!k.startsWith(prefix)) continue;
      const parsed = parseFileContentCacheKey(k);
      if (!parsed || parsed.projectId !== projectId) continue;
      if (p === '' || parsed.path === p || parsed.path.startsWith(p + '/')) {
        await idbDelete(k).catch(() => {});
        set?.delete(parsed.path);
      }
    }
  }
  // Чистим записи mtime, чтобы при повторной синхронизации файлы скачались заново
  const mtimes = await loadMtimes(projectId);
  const changes: Record<string, string | null> = {};
  for (const fp of Object.keys(mtimes)) {
    if (p === '' || fp === p || fp.startsWith(p + '/')) changes[fp] = null;
  }
  await applyMtimeChanges(projectId, changes);
  notifyMarks();
}

// Снести содержимое файла: все его записи в классе содержимого (помеченная «k»,
// непомеченная «f» и варианты с «extra» — файл, прочитанный в дереве чата).
async function deleteFileContent(projectId: string, path: string): Promise<void> {
  const prefix = fileContentCachePrefix(projectId);
  if (prefix === null) return;
  const keys = await idbKeys().catch(() => [] as string[]);
  for (const k of keys) {
    if (!k.startsWith(prefix)) continue;
    const parsed = parseFileContentCacheKey(k);
    if (!parsed || parsed.projectId !== projectId || parsed.path !== path) continue;
    await idbDelete(k).catch(() => {});
  }
}

// Финальная запись mtime прогона — по диффу, из которого вычёркнуты снесённые вытеснением.
//
// Вытеснение класса содержимого может пройти ПОСРЕДИ прогона и снести часть только
// что скачанных файлов. Если такой файл получит «свежий» mtime, авто-докачка сочтёт
// его скачанным (mtime совпал) и больше не тронет, пока файл не изменится на диске —
// содержимое пропало бы из офлайна навсегда. Поэтому вытеснение запоминает путь и
// время (см. _evictedAt), и финальная запись такие пути пропускает: файл остаётся
// «не скачанным», и следующий снапшот его докачает.
//
// Почему не сверка со стором (содержимое лежит в IDB): у локальных проектов
// содержимое живёт на устройстве, а не в IDB, — сверка сняла бы mtime у всех файлов
// локального проекта и заставила бы снапшот качать их заново каждый раз.
async function applyRunMtimeChanges(projectId: string, changes: Record<string, string | null>, runStart: number): Promise<void> {
  const evicted = _evictedAt.get(projectId);
  const filtered: Record<string, string | null> = {};
  for (const [path, value] of Object.entries(changes)) {
    // null — снять запись (удалённый/рассинхронизированный файл): пропускаем всегда
    if (value !== null) {
      const at = evicted?.get(path);
      // Снесён вытеснением В ЭТОМ ПРОГОНЕ — mtime не ставим (файл не скачан)
      if (at !== undefined && at >= runStart) continue;
    }
    filtered[path] = value;
  }
  await applyMtimeChanges(projectId, filtered);
}

// Пути файлов, чьё содержимое действительно лежит в сторе (класс содержимого).
// Источник правды — стор: «скачано» в памяти может разойтись с ним после вытеснения.
async function loadDownloadedPaths(projectId: string): Promise<Set<string>> {
  const prefix = fileContentCachePrefix(projectId);
  const set = new Set<string>();
  if (prefix === null) return set;
  const stamps = await idbRangeStamps(prefix, prefix + KEY_RANGE_END).catch(() => []);
  for (const s of stamps) {
    const parsed = parseFileContentCacheKey(s.key);
    // extra ≠ пусто — это файл, прочитанный в дереве чата (sessionId), а не файл
    // проекта: его содержимое не заменяет содержимое файла проекта.
    if (!parsed || parsed.projectId !== projectId || parsed.extra) continue;
    set.add(parsed.path);
  }
  return set;
}

// Заполнить набор скачанных файлов из IndexedDB (по ключам content-запросов проекта)
export async function loadDownloadedSet(projectId: string): Promise<void> {
  _downloaded.set(projectId, await loadDownloadedPaths(projectId));
  notifyMarks();
}

// Загрузить метки проекта в стор (GET кэшируется → работает и офлайн)
export async function loadSyncMarks(projectId: string): Promise<void> {
  const marks = await api.sync.list(projectId).catch(() => null);
  if (marks) {
    _marks.set(projectId, marks);
    // Метки — источник правды о помеченном: публикуем их содержимому (оно решает,
    // чьё содержимое защищено от вытеснения содержимым непомеченных файлов).
    publishKeptPaths(projectId);
    notifyMarks();
  }
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

// --- mtime скачанных файлов (для инкрементальной синхронизации) ---
// Храним время модификации (mtime) на момент скачивания содержимого — чтобы качать
// заново только реально изменившиеся файлы. Запись на проект в IndexedDB.

async function loadMtimes(projectId: string): Promise<Record<string, string>> {
  const e = await idbGet<Record<string, string>>('synced-mtimes:' + projectId).catch(() => undefined);
  return e?.data ?? {};
}

async function saveMtimes(projectId: string, map: Record<string, string>): Promise<void> {
  await idbSet('synced-mtimes:' + projectId, { data: map, savedAt: Date.now() }).catch(() => {});
}

// Применить изменения mtime поверх ТЕКУЩЕГО содержимого записи (чтение → правка → запись),
// а не сохранить карту целиком: иначе более поздний снапшот со своей (устаревшей) картой
// вернул бы сброшенные при вытеснении записи — и авто-докачка не вернула бы файл.
// null в changes — снять запись пути.
//
// Правки по одному проекту выстроены в очередь: и слушатель вытеснения, и снапшот
// пишут в одну и ту же карту, а «прочитать → изменить → сохранить» без очереди
// теряло бы изменение одного из них (прочитали одну и ту же старую карту — и каждый
// сохранил свою версию).
const _mtimeQueues = new Map<string, Promise<void>>();

async function applyMtimeChanges(projectId: string, changes: Record<string, string | null>): Promise<void> {
  const prev = _mtimeQueues.get(projectId) ?? Promise.resolve();
  const next = prev.then(async () => {
    const cur = await loadMtimes(projectId);
    let changed = false;
    for (const [path, value] of Object.entries(changes)) {
      if (value === null) {
        if (path in cur) { delete cur[path]; changed = true; }
      } else if (cur[path] !== value) {
        cur[path] = value;
        changed = true;
      }
    }
    if (changed) await saveMtimes(projectId, cur);
  }).catch(() => { /* кэш недоступен — не критично */ });
  _mtimeQueues.set(projectId, next);
  await next;
}

// Вытеснение содержимого сбрасывает mtime вытесненных путей. mtime-запись лежит вне
// диапазона вытеснения (ключ synced-mtimes:<pid>), поэтому сама вытеснение её не трогает:
// без сброса авто-докачка считала бы файл свежим (mtime совпал) и больше его не качала —
// содержимое пропало бы из офлайна до следующего изменения файла на диске.
//
// Время вытеснения запоминается: прогон, идущий в это время, не должен ставить mtime
// снесённому пути (см. applyRunMtimeChanges).
const _evictedAt = new Map<string, Map<string, number>>();
// Сколько живёт отметка о вытеснении: нужна только для прогона, который идёт сейчас
// (файл вытеснен → его не считаем скачанным). Старые отметки убираем, иначе карта
// растёт без предела.
const EVICTED_AT_TTL_MS = 60 * 60_000;

function pruneEvictedAt(): void {
  const cutoff = Date.now() - EVICTED_AT_TTL_MS;
  for (const [projectId, paths] of _evictedAt) {
    for (const [path, at] of paths) {
      if (at < cutoff) paths.delete(path);
    }
    if (!paths.size) _evictedAt.delete(projectId);
  }
}

onPassiveEvict(async keys => {
  const byProject = new Map<string, Set<string>>();
  for (const key of keys) {
    const parsed = parseFileContentCacheKey(key);
    if (!parsed) continue;
    // extra ≠ пусто — это файл, прочитанный в дереве чата (sessionId): он не заменяет
    // содержимое файла проекта, и mtime проектного файла сбрасывать нельзя.
    if (parsed.extra) continue;
    const paths = byProject.get(parsed.projectId) ?? new Set<string>();
    paths.add(parsed.path);
    byProject.set(parsed.projectId, paths);
  }
  const now = Date.now();
  for (const [projectId, paths] of byProject) {
    // Содержимое могло уцелеть под другой пометкой (у файла две записи: помеченная
    // «k» и непомеченная «f»): сбрасываем mtime только тем путям, у которых в сторе
    // не осталось НИ ОДНОЙ записи содержимого — иначе файл, живой в офлайне,
    // считался бы ненужным и качался бы заново.
    const present = await loadDownloadedPaths(projectId);
    const changes: Record<string, string | null> = {};
    const evicted = _evictedAt.get(projectId) ?? new Map<string, number>();
    for (const path of paths) {
      if (present.has(path)) continue;
      changes[path] = null;
      evicted.set(path, now);
    }
    _evictedAt.set(projectId, evicted);
    pruneEvictedAt();
    if (Object.keys(changes).length) await applyMtimeChanges(projectId, changes);
    // Содержимого больше нет — снимаем и отметку «скачано» в сторе.
    const set = _downloaded.get(projectId);
    if (set) for (const path of paths) if (!present.has(path)) set.delete(norm(path));
  }
  notifyMarks();
});

// --- Ревизии скачанных историй чатов ---
// История чата — самый тяжёлый объект снапшота (у активных чатов это мегабайты), а
// меняется она только когда в чате прошёл ход. Храним ревизию (updatedAt|messageCount)
// на момент скачивания и повторно качаем ТОЛЬКО изменившиеся: иначе каждый снапшот
// тянул истории всех чатов всех проектов заново (десятки МБ), забивал пул соединений
// браузера и упирался в CPU разбором JSON — ровно в те секунды, когда пользователь
// переключает чаты, отчего переключение и вставало на секунды.

const chatRevision = (s: Session): string => `${s.updatedAt}|${s.messageCount}`;

// Потолок прогрева историй на проект за снимок: по-чатовой класс кэша держит последние
// чаты, гонять больше — трафик впустую.
const CHAT_WARM_MAX = 8;

async function loadChatRevisions(projectId: string): Promise<Record<string, string>> {
  const e = await idbGet<Record<string, string>>('synced-chats:' + projectId).catch(() => undefined);
  return e?.data ?? {};
}

async function saveChatRevisions(projectId: string, map: Record<string, string>): Promise<void> {
  await idbSet('synced-chats:' + projectId, { data: map, savedAt: Date.now() }).catch(() => {});
}

// --- Докачка содержимого ---

// Минимальное время показа спиннера: на localhost докачка мгновенна, без этого спиннер незаметен.
const MIN_SPINNER_MS = 500;

// Пометить файл скачанным с небольшой задержкой — чтобы спиннер успел стать заметным.
function markDownloadedSoon(projectId: string, path: string) {
  setTimeout(() => markDownloaded(projectId, path), MIN_SPINNER_MS);
}

// Файл — один запрос, папка/корень — все файлы рекурсивно. token — для отмены на лету.
// Запоминаем mtime скачанных файлов для последующей инкрементальной синхронизации.
async function downloadSyncedContent(projectId: string, entry: { path: string; isDirectory: boolean; modified?: string }, token?: { cancelled: boolean }): Promise<void> {
  const updates: Record<string, string> = {};
  // Файл помечен человеком прямо сейчас: его содержимое защищено от вытеснения
  // содержимым непомеченных файлов, пока пометка не снята.
  if (!entry.isDirectory) keepFilePath(projectId, norm(entry.path));
  if (entry.isDirectory) {
    const tree = await api.files.tree(projectId, entry.path).catch(() => [] as FileEntry[]);
    // Состояние синхронизации по поддереву: файлы помеченной папки помечены «по
    // наследству» — их содержимое тоже защищено.
    addSyncedFiles(projectId, tree);
    const files = tree.filter(e => !e.isDirectory);
    await mapLimit(files, 4, async f => {
      if (token?.cancelled) return;
      try { await api.files.getContent(projectId, f.path); markDownloadedSoon(projectId, f.path); updates[f.path] = f.modified; }
      catch { /* пропускаем сбойный файл */ }
    });
  } else {
    if (token?.cancelled) return;
    try { await api.files.getContent(projectId, entry.path); markDownloadedSoon(projectId, entry.path); if (entry.modified) updates[entry.path] = entry.modified; }
    catch { /* пропускаем */ }
  }
  if (Object.keys(updates).length) {
    const cur = await loadMtimes(projectId);
    await saveMtimes(projectId, { ...cur, ...updates });
  }
}

// --- Снапшот ---

// Проиграть офлайн-очереди задач и заметок на сервер. Вызывается из App при переходе
// в онлайн — независимо от того, какая страница смонтирована (иначе синк заметок/задач
// сработал бы только если открыт их раздел, где подписка на online заводится при монтировании).
export async function drainOfflineQueues(): Promise<void> {
  if (!isOnline()) return;
  // Без гейта по флагам: если в очереди есть накопленные офлайн-правки, их всегда
  // корректно проиграть (гейт создавал бы гонку с асинхронной загрузкой флагов из
  // /api/auth/me — эффект online мог сработать раньше и молча пропустить дренаж).
  await drainTaskOutbox().catch(() => {});
  await drainNotesOutbox().catch(() => {});
}

let _snapshotRunning = false;

// Прогон офлайн-снапшота. Идемпотентен: повторный вызов во время работы игнорируется.
// priorityProjectId — проект, с которого начать (текущий открытый при выходе из офлайна).
export async function runOfflineSnapshot(priorityProjectId?: string): Promise<void> {
  if (_snapshotRunning || !isOnline()) return;
  _snapshotRunning = true;
  // Начало прогона: вытеснения, случившиеся после этой отметки, относятся к нему
  const runStart = Date.now();
  setProgress({ active: true, done: 0, total: 0 });

  try {
    // Прогрев задач: GET /tasks оседает в IDB-кэш (offline.request), чтобы после
    // снапшота первый офлайн-заход имел данные ещё до гидрации стора.
    await api.tasks.listAll().catch(() => {});
    const projects = await api.projects.list();
    // Начинаем с текущего проекта, остальные — следом
    const ordered = priorityProjectId
      ? [...projects].sort((a, b) => (a.id === priorityProjectId ? -1 : b.id === priorityProjectId ? 1 : 0))
      : projects;
    // Задачи на докачку (только новые/изменённые файлы); mtime и валидные пути по проектам
    const contentTasks: { projectId: string; path: string; mtime: string }[] = [];
    const mtimeByProject = new Map<string, Record<string, string>>();
    // Изменения mtime за этот прогон (null — снять запись): сохраняются поверх текущей
    // карты, а не вместо неё — иначе снапшот вернул бы сброшенные при вытеснении записи.
    const mtimeChanges = new Map<string, Record<string, string | null>>();
    const validByProject = new Map<string, Set<string>>();
    // Дерево приоритетного проекта — для повторной записи листингов в конце цикла
    let priorityTree: FileEntry[] | null = null;

    for (const p of ordered) {
      const [sessions, treeEntries] = await Promise.all([
        api.sessions.list(p.id).catch(() => []),
        api.sync.list(p.id).catch(() => []), // прогрев кэша меток
        // null (а не []) при ошибке: пустой список стёр бы весь офлайн-кэш проекта
        // на этапе чистки ниже. Отличаем «дерево не пришло» от «проект реально пуст».
        api.files.tree(p.id).catch(() => null),
      ]).then(([s, , t]) => [s, t] as const);

      // Истории — только изменившиеся с прошлого снапшота (см. loadChatRevisions).
      // Параллельность 2, а не 4: снапшот фоновый, и живым запросам открытого чата
      // нужны свободные сокеты (HTTP/1.1 даёт ~6 на origin).
      //
      // Два изменения против прежнего поведения:
      // 1) По ТОМУ ЖЕ ключу, что читает лента (?after=-1). Прежде прогревался ключ без
      //    параметров — его никто не читал: лента при загрузке берёт ?after=-1, то есть
      //    история чата лежала в кэше впустую (мегабайты на чат).
      // 2) С потолком по числу чатов: по-чатовой класс кэша всё равно держит последние
      //    чаты, а прогрев без потолка тянул истории всех чатов всех проектов — десятки
      //    МБ трафика впустую. Слагированные чаты просто достанутся следующим снимкам.
      const knownRevs = await loadChatRevisions(p.id);
      const staleChats = sessions
        .filter(s => knownRevs[s.id] !== chatRevision(s))
        .sort((a, b) => (a.updatedAt < b.updatedAt ? 1 : -1))
        .slice(0, CHAT_WARM_MAX);
      const freshRevs: Record<string, string> = {};
      await mapLimit(staleChats, 2, async s => {
        await api.sessions.getHistoryTail(p.id, s.id, { after: -1 });
        freshRevs[s.id] = chatRevision(s);
      });
      // Метки живых чатов + успешно скачанных; удалённые чаты уходят сами (нет в sessions)
      const nextRevs: Record<string, string> = {};
      for (const s of sessions) {
        const rev = freshRevs[s.id] ?? knownRevs[s.id];
        if (rev) nextRevs[s.id] = rev;
      }
      await saveChatRevisions(p.id, nextRevs);

      // Дерево не получено (сервер недоступен/ошибка) — НЕ трогаем кэш этого проекта:
      // ни листинги, ни валидные пути, ни чистку. Проект просто не участвует в снапшоте.
      // Иначе гонка health-ping'а с флапающей сетью стирала бы офлайн-контент.
      if (treeEntries === null) continue;

      // Обновляем дерево в кэше — новые файлы появляются, удалённые исчезают из листингов
      await cacheTreeAsListings(p.id, treeEntries);
      if (p.id === priorityProjectId) priorityTree = treeEntries;
      // Состояние синхронизации по дереву: помеченные файлы известны ДО докачки —
      // их содержимое попадает в защищённую часть класса содержимого.
      rememberSyncedFiles(p.id, treeEntries);

      const mtimes = await loadMtimes(p.id);
      mtimeByProject.set(p.id, mtimes);
      mtimeChanges.set(p.id, {});
      const valid = new Set<string>();
      for (const e of treeEntries) {
        if (e.isDirectory || !e.synced) continue;
        valid.add(e.path);
        // качаем только если файл новый или изменился (mtime отличается)
        if (mtimes[e.path] !== e.modified) contentTasks.push({ projectId: p.id, path: e.path, mtime: e.modified });
      }
      validByProject.set(p.id, valid);
    }

    // Приоритетный проект обходится первым, а значит его листинги записаны раньше всех
    // и под потолком записей вытеснялись бы первыми (LRU по usedAt). Перезаписываем их
    // последними: вытеснение остаётся LRU, но офлайн-дерево текущего проекта не зависит
    // от порядка проектов в списке.
    if (priorityTree) await cacheTreeAsListings(priorityProjectId!, priorityTree);

    setProgress({ total: contentTasks.length, done: 0 });
    let done = 0;
    await mapLimit(contentTasks, 4, async t => {
      try {
        await api.files.getContent(t.projectId, t.path);
        markDownloaded(t.projectId, t.path);
        mtimeByProject.get(t.projectId)![t.path] = t.mtime;
        mtimeChanges.get(t.projectId)![t.path] = t.mtime;
        // Файл скачан заново — прежняя отметка о вытеснении больше не мешает
        _evictedAt.get(t.projectId)?.delete(t.path);
      }
      catch { /* сбой — оставим на следующий снапшот */ }
      setProgress({ done: ++done });
    });

    // Вытеснение могло снести часть только что скачанного содержимого (класс
    // содержимого имеет свой бюджет по весу). Перед финальной записью mtime ждём
    // завершения вытеснения — иначе снесённый файл получил бы «свежий» mtime и
    // потерялся бы из офлайна навсегда.
    await flushFileEviction();

    // Чистим устаревшее: содержимое файлов, удалённых с диска или больше не синхронизированных
    for (const p of ordered) {
      const mtimes = mtimeByProject.get(p.id) ?? {};
      const valid = validByProject.get(p.id) ?? new Set<string>();
      for (const fp of Object.keys(mtimes)) {
        if (valid.has(fp)) continue;
        await deleteFileContent(p.id, fp);
        _downloaded.get(p.id)?.delete(fp);
        delete mtimes[fp];
        mtimeChanges.get(p.id)![fp] = null;
      }
      // Финальная запись mtime — по диффу без путей, снесённых вытеснением в этом
      // прогоне (см. applyRunMtimeChanges).
      await applyRunMtimeChanges(p.id, mtimeChanges.get(p.id) ?? {}, runStart);
    }

    // Прогрев заметок: список + folders/graph в GET-кэш, контент изменившихся —
    // в редактируемый локальный слой (с mtime-диффом; не затираем черновики).
    await warmNotes();

    notifyMarks();
  } catch {
    /* офлайн или сбой — снапшот частичный, не критично */
  } finally {
    _snapshotRunning = false;
    setProgress({ active: false });
  }
}

// --- Прогрев заметок для офлайна ---

const NOTES_MTIMES_KEY = 'notes-mtimes';
const NOTES_WARM_CAP = 500;   // потолок по свежести — vault может быть большим

async function loadNoteMtimes(): Promise<Record<string, string>> {
  const e = await idbGet<Record<string, string>>(NOTES_MTIMES_KEY).catch(() => undefined);
  return e?.data ?? {};
}

async function warmNotes(): Promise<void> {
  // Гейт подсистемы: при выключенных заметках не дёргаем /api/notes* — маршрута
  // в MVC нет, и вызов упирается в общий фолбэк приложения: на стенде это 200
  // `text/html` (`response.ok === true`, а `JSON.parse` падает синтаксической
  // ошибкой), в тестовом хосте 404. Шум в консоли — см. Д-4/Д-6 отчёта QA.
  // Закрываем сам вызов, а не try/catch: пустой прогрев при выключенной
  // подсистеме бессмысленен.
  if (!isSubsystemEnabled('notes')) return;
  try {
    const list = await api.notes.list();               // заполнит GET-кэш /notes
    await api.notes.folders().catch(() => {});
    await api.notes.graph().catch(() => {});
    const known = await loadNoteMtimes();
    // Самые свежие первыми, ограничиваем объём
    const recent = [...list].sort((a, b) => (a.updatedAt < b.updatedAt ? 1 : -1)).slice(0, NOTES_WARM_CAP);
    const stale = recent.filter(n => known[n.id] !== n.updatedAt);
    await mapLimit(stale, 4, async n => {
      try { const d = await api.notes.get(n.id); await warmNote(d); known[n.id] = n.updatedAt; }
      catch { /* сбойную заметку пропускаем */ }
    });
    await idbSet(NOTES_MTIMES_KEY, { data: known, savedAt: Date.now() }).catch(() => {});
  } catch {
    /* офлайн/сбой — частичный прогрев */
  }
}

// --- Ре-синхронизация одного проекта (для watcher'а изменений файлов) ---

const _projectSyncing = new Set<string>();
const _projectResync = new Set<string>();

// Инкрементальный ре-синк одного проекта: обновляет дерево/листинги, докачивает
// изменённые синхронизированные файлы по mtime, чистит удалённые. Защищён от наложений.
export async function syncProjectFiles(projectId: string): Promise<void> {
  if (!isOnline()) return;
  if (_projectSyncing.has(projectId)) { _projectResync.add(projectId); return; }
  _projectSyncing.add(projectId);
  try {
    await doSyncProjectFiles(projectId);
  } finally {
    _projectSyncing.delete(projectId);
    if (_projectResync.delete(projectId)) syncProjectFiles(projectId); // пришла ещё пачка изменений
  }
}

async function doSyncProjectFiles(projectId: string): Promise<void> {
  const tree = await api.files.tree(projectId).catch(() => null);
  if (!tree) return;
  await cacheTreeAsListings(projectId, tree);
  rememberSyncedFiles(projectId, tree);

  const mtimes = await loadMtimes(projectId);
  const changes: Record<string, string | null> = {};
  const valid = new Set<string>();
  const tasks: { path: string; mtime: string }[] = [];
  for (const e of tree) {
    if (e.isDirectory || !e.synced) continue;
    valid.add(e.path);
    if (mtimes[e.path] !== e.modified) tasks.push({ path: e.path, mtime: e.modified });
  }

  if (tasks.length) setProgress({ active: true, done: 0, total: tasks.length });
  let done = 0;
  const runStart = Date.now();
  await mapLimit(tasks, 4, async t => {
    try {
      await api.files.getContent(projectId, t.path);
      markDownloaded(projectId, t.path);
      mtimes[t.path] = t.mtime;
      changes[t.path] = t.mtime;
      // Файл скачан заново — прежняя отметка о вытеснении больше не мешает
      _evictedAt.get(projectId)?.delete(t.path);
    }
    catch { /* сбой — на следующий раз */ }
    if (tasks.length) setProgress({ done: ++done });
  });
  if (tasks.length) setProgress({ active: false });

  // Вытеснение могло снести часть только что скачанного содержимого — перед
  // финальной записью mtime ждём его завершения (иначе снесённый файл получил бы
  // «свежий» mtime и потерялся бы из офлайна навсегда).
  await flushFileEviction();

  // Чистим удалённые/рассинхронизированные
  for (const fp of Object.keys(mtimes)) {
    if (valid.has(fp)) continue;
    await deleteFileContent(projectId, fp);
    _downloaded.get(projectId)?.delete(fp);
    delete mtimes[fp];
    changes[fp] = null;
  }
  // Финальная запись mtime — по диффу без путей, снесённых вытеснением в этом прогоне.
  await applyRunMtimeChanges(projectId, changes, runStart);
  notifyMarks();
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
    // Пометка снята — содержимое файла больше не защищено от вытеснения.
    forgetSyncedFiles(projectId, entry.path);
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
  // Пометка включена — содержимое файла (а для папки — всего вложенного) защищено
  // от вытеснения содержимым непомеченных файлов.
  if (entry.isDirectory) keepFilePath(projectId, norm(entry.path), true);
  else keepFilePath(projectId, norm(entry.path));
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
