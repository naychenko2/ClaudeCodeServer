// Заметки: глобальный стор списка + realtime. Паттерн — как lib/tasks.ts.
// Realtime: бэк шлёт notes_changed в группу user_{userId} (Claude создал/изменил
// заметку через MCP или другое устройство) — стор перечитывает список и бампает
// версию, по которой граф и открытая заметка перезапрашиваются.

import { useSyncExternalStore } from 'react';
import type { NoteSummary } from '../types';
import { api } from './api';
import { joinUser, onMessage, onReconnected } from './signalr';

let _notes: NoteSummary[] = [];
let _loaded = false;
let _loading: Promise<void> | null = null;
let _version = 0;   // бампается на любое изменение — deps для рефетча графа/детали
const _listeners = new Set<() => void>();
let _realtimeWired = false;

function emit() {
  _version++;
  _listeners.forEach(fn => fn());
}

function joinUserGroup() {
  const uid = localStorage.getItem('cc_user_id') || sessionStorage.getItem('cc_user_id');
  if (uid) joinUser(uid).catch(() => {});
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;
  onMessage(msg => {
    if (msg.type !== 'notes_changed') return;
    void reloadNotes();
  });
  onReconnected(() => { joinUserGroup(); void reloadNotes(); });
}

export async function reloadNotes(): Promise<void> {
  const list = await api.notes.list();
  _notes = list;
  _loaded = true;
  emit();
}

export function ensureNotesLoaded(): Promise<void> {
  wireRealtime();
  joinUserGroup();
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
