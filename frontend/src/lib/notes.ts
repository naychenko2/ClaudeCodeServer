// Заметки: глобальный стор списка + realtime. Паттерн — как lib/tasks.ts.
// Realtime: бэк шлёт notes_changed в группу user_{userId} (Claude создал/изменил
// заметку через MCP или другое устройство) — стор перечитывает список и бампает
// версию, по которой граф и открытая заметка перезапрашиваются.

import { useEffect, useMemo, useSyncExternalStore } from 'react';
import type { NoteSummary, NoteFolder } from '../types';
import { api } from './api';
import { joinUser, onMessage, onReconnected } from './signalr';
import { clearResolveCache } from '../components/MarkdownViewer';
import { isOnline, OfflineError, subscribeOnline } from './offline';
import { isSubsystemEnabled } from './subsystems';
import { drainNotesOutbox, overlayNotesList } from './notesOffline';

let _notes: NoteSummary[] = [];
let _folders: NoteFolder[] = [];
let _loaded = false;
let _loading: Promise<void> | null = null;
let _version = 0;   // бампается на любое изменение — deps для рефетча графа/детали
const _listeners = new Set<() => void>();
let _realtimeWired = false;

function emit() {
  _version++;
  clearResolveCache();   // hover/embed-кэш резолва протухает вместе со списком
  _listeners.forEach(fn => fn());
}

function joinUserGroup() {
  const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id');
  if (uid) joinUser(uid).catch(() => {});
}

let _fileChangeTimer: number | null = null;
const offlineEnabled = () => true;

// Во время дренажа очереди подавляем перечитку списка от realtime (иначе она сбросит
// оптимистичные записи середины синхронизации); одна перечитка — в конце дренажа.
let _syncing = false;

async function syncNotes(): Promise<void> {
  if (!offlineEnabled()) { void reloadNotes(); return; }
  _syncing = true;
  try { await drainNotesOutbox(); }
  finally { _syncing = false; }
  await reloadNotes();
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onMessage(msg => {
    if (_syncing) return;   // идёт дренаж — не дёргаем перечитку
    if (msg.type === 'notes_changed') { void reloadNotes(); return; }
    // Правки файлов vault мимо notes-API (Claude в ходе сессии и т.п.) — дебаунс-перечитка
    if (msg.type === 'file_changed' && /(^|[\\/])notes[\\/]/i.test(msg.path)) {
      if (_fileChangeTimer) window.clearTimeout(_fileChangeTimer);
      _fileChangeTimer = window.setTimeout(() => { _fileChangeTimer = null; void reloadNotes(); }, 800);
    }
  });
  // После реконнекта — сперва проиграть офлайн-очередь, затем перечитать список
  onReconnected(() => { joinUserGroup(); void syncNotes(); });
  // Связь может подняться через probe (без WS-reconnected) — тоже дренажим
  if (offlineEnabled()) subscribeOnline(() => { if (isOnline()) void syncNotes(); });
}

export async function reloadNotes(): Promise<void> {
  let list: NoteSummary[];
  let folders: NoteFolder[];
  try {
    // Папки грузим параллельно; их сбой не должен ронять список заметок
    [list, folders] = await Promise.all([
      api.notes.list(),                                   // офлайн вернёт из GET-кэша
      api.notes.folders().catch(() => [] as NoteFolder[]),
    ]);
  } catch (e) {
    if (offlineEnabled() && e instanceof OfflineError) { list = []; folders = _folders; }
    else throw e;
  }
  // Поверх серверного списка — офлайн-создания/удаления
  _notes = offlineEnabled() ? await overlayNotesList(list) : list;
  _folders = folders;
  _loaded = true;
  emit();
}

export function ensureNotesLoaded(): Promise<void> {
  // Гейт подсистемы: при выключенных заметках НЕ подписываемся на WS,
  // НЕ идём в joinUserGroup, НЕ дренажим очередь, НЕ дёргаем /api/notes*.
  // Иначе при выключенной подсистеме открытие файла вызывало до 14 ошибок
  // и отклонений промисов в консоли (см. Д-4/Д-6 отчёта QA). Гейт ДО запроса,
  // а не .catch(() => {}) — закрываем вызов.
  if (!isSubsystemEnabled('notes')) return Promise.resolve();
  wireRealtime();
  joinUserGroup();
  if (offlineEnabled() && isOnline()) void syncNotes();   // подхватить незасинканное с прошлого офлайна
  if (_loaded) return Promise.resolve();
  if (!_loading) _loading = reloadNotes().finally(() => { _loading = null; });
  return _loading;
}

// Список заметок (все источники владельца)
export function useNotes(): NoteSummary[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _notes,
    () => _notes,
  );
}

// Физические папки владельца (в т.ч. пустые) — для дерева и datalist «куда создать»
export function useNoteFolders(): NoteFolder[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _folders,
    () => _folders,
  );
}

// Снимок списка вне React — для колбэков (напр. rename заметки в дереве файлов)
export function getNotesSnapshot(): NoteSummary[] { return _notes; }

// Счётчик изменений — для инвалидации графа/детали (включай в deps эффекта рефетча)
export function useNotesVersion(): number {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _version,
    () => _version,
  );
}

// Нормализованные заголовки существующих заметок — для отличия «живых»
// вики-ссылок от «призрачных» при рендере markdown.
export function existingTitleSet(notes: NoteSummary[]): Set<string> {
  return new Set(notes.map(n => n.title.trim().toLowerCase()));
}

// Группировка заметок проекта по привязанному файлу (frontmatter file:) —
// чистая функция, покрыта notesByFile.test.ts. Ключ — путь от корня проекта
// (forward slashes, как отдаёт бэкенд); только заметки source === projectId,
// чтобы одинаковые пути разных проектов не склеивались.
export function groupNotesByFile(notes: NoteSummary[], projectId: string): Map<string, NoteSummary[]> {
  const map = new Map<string, NoteSummary[]>();
  for (const n of notes) {
    if (n.source !== projectId || !n.file) continue;
    const key = n.file.replace(/\\/g, '/');
    const arr = map.get(key);
    if (arr) arr.push(n); else map.set(key, [n]);
  }
  return map;
}

// Привязки «файл проекта → заметки о нём» — обратный индикатор для дерева файлов,
// FileViewer и панели «Заметки». ensureNotesLoaded — внутри (идемпотентный): хук
// работает и там, где раздел заметок ещё не открывали (стена проекта, воркспейс).
export function useNotesByFile(projectId: string): Map<string, NoteSummary[]> {
  useEffect(() => { void ensureNotesLoaded(); }, []);
  const notes = useNotes();
  return useMemo(() => groupNotesByFile(notes, projectId), [notes, projectId]);
}

// Локально применить изменения после собственных мутаций (realtime продублирует).
export function bumpNotes(): void { void reloadNotes(); }

// --- Избранное: тег #избранное в самой заметке, без отдельного хранилища ---
// Звёздочка в списке и в просмотре дописывает/убирает этот тег в тексте, поэтому
// избранное переживает синк с Obsidian и ищется обычным оператором tag:.

export const FAVORITE_TAG = 'избранное';

export function isFavorite(tags: string[]): boolean {
  return tags.some(t => t.trim().toLowerCase() === FAVORITE_TAG);
}

// Граница тега — как у разбора на бэкенде; хвостовой пробел съедаем, чтобы не оставалось двойных
const FAV_INLINE = new RegExp(`(?<=^|\\s)#${FAVORITE_TAG}(?![\\p{L}\\p{N}_/-])[ \\t]*`, 'giu');

export function withFavoriteTag(content: string): string {
  return isFavoriteContent(content) ? content : `${content.trimEnd()} #${FAVORITE_TAG}\n`;
}

// Тег мог попасть и в тело, и во frontmatter — снимаем из обоих мест, иначе
// звёздочка гаснет в интерфейсе, а список тегов её возвращает.
export function withoutFavoriteTag(content: string): string {
  const fm = splitFrontmatter(content);
  const body = fm.body.replace(FAV_INLINE, '').replace(/[ \t]+$/gm, '');
  return fm.head === null ? body : `${stripFavoriteFromFrontmatter(fm.head)}${body}`;
}

// Есть ли тег в тексте (а не в разобранном списке note.tags) — для идемпотентности
function isFavoriteContent(content: string): boolean {
  const fm = splitFrontmatter(content);
  FAV_INLINE.lastIndex = 0;
  if (FAV_INLINE.test(fm.body)) return true;
  return fm.head !== null && stripFavoriteFromFrontmatter(fm.head) !== fm.head;
}

// head — frontmatter вместе с обрамляющими «---» (null, если его нет)
function splitFrontmatter(content: string): { head: string | null; body: string } {
  const m = /^---\r?\n[\s\S]*?\r?\n---\r?\n?/.exec(content);
  return m ? { head: m[0], body: content.slice(m[0].length) } : { head: null, body: content };
}

// tags: [a, избранное] и список в столбик «  - избранное»; опустевший ключ убираем
function stripFavoriteFromFrontmatter(head: string): string {
  const eq = (s: string) => s.trim().replace(/^#+/, '').replace(/^["']|["']$/g, '').toLowerCase() === FAVORITE_TAG;
  const lines = head.split('\n');
  const out: string[] = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const inline = /^(\s*tags:\s*)\[(.*)\]\s*$/i.exec(line);
    if (inline) {
      const rest = inline[2].split(',').map(x => x.trim()).filter(x => x.length > 0 && !eq(x));
      if (rest.length > 0) out.push(`${inline[1]}[${rest.join(', ')}]`);
      continue;
    }
    if (/^\s*tags:\s*$/i.test(line)) {
      const items: string[] = [];
      let j = i + 1;
      for (; j < lines.length; j++) {
        const item = /^\s*-\s*(.+?)\s*$/.exec(lines[j]);
        if (!item) break;
        if (!eq(item[1])) items.push(lines[j]);
      }
      if (items.length > 0) { out.push(line, ...items); }
      i = j - 1;
      continue;
    }
    out.push(line);
  }
  return out.join('\n');
}

// Переключение звёздочки: список отдаёт только NoteSummary, поэтому тело
// дочитываем перед правкой. Возвращает новое состояние избранности.
export async function toggleFavorite(id: string): Promise<boolean> {
  const note = await api.notes.get(id);
  const fav = isFavorite(note.tags);
  const content = fav ? withoutFavoriteTag(note.content) : withFavoriteTag(note.content);
  await api.notes.update(id, { content });
  bumpNotes();
  return !fav;
}
