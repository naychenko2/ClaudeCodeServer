// Заметки: глобальный стор списка + realtime. Паттерн — как lib/tasks.ts.
// Realtime: бэк шлёт notes_changed в группу user_{userId} (Claude создал/изменил
// заметку через MCP или другое устройство) — стор перечитывает список и бампает
// версию, по которой граф и открытая заметка перезапрашиваются.

import { useSyncExternalStore } from 'react';
import type { NoteSummary, NoteFolder } from '../types';
import { api } from './api';
import { joinUser, onMessage, onReconnected } from './signalr';
import { clearResolveCache } from '../components/MarkdownViewer';
import { isOnline, OfflineError, subscribeOnline } from './offline';
import { getFlag, FLAGS } from './featureFlags';
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
const offlineEnabled = () => getFlag(FLAGS.offline);

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

// Локально применить изменения после собственных мутаций (realtime продублирует).
export function bumpNotes(): void { void reloadNotes(); }
