// Манифест recall (F3): что персона подтянула в последний ход из памяти/заметок/базы.
// Хранится per-session в модуле; обновляется из SignalR-сообщения recall_manifest,
// читается вкладкой «Контекст персоны» (раздел «использовано сейчас»).
import { useSyncExternalStore } from 'react';
import type { RecallItem } from '../types';

const _bySession = new Map<string, RecallItem[]>();
const _listeners = new Set<() => void>();

function emit() { _listeners.forEach(fn => fn()); }

// SignalR-обработчик вызывает это при приходе recall_manifest
export function setRecallManifest(sessionId: string, items: RecallItem[]): void {
  // Потолок LRU: манифест нужен открытым чатам — не копим на всю жизнь вкладки.
  // Переинсерт освежает позицию (Map хранит порядок вставки), лишнее — самое старое.
  _bySession.delete(sessionId);
  _bySession.set(sessionId, items);
  if (_bySession.size > 50) {
    const oldest = _bySession.keys().next().value;
    if (oldest !== undefined) _bySession.delete(oldest);
  }
  emit();
}

// Текущий манифест сессии (последний ход). [] — ничего не подтянулось/ещё не было хода.
export function useRecallManifest(sessionId: string | null): RecallItem[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => (sessionId ? _bySession.get(sessionId) : undefined) ?? EMPTY,
    () => EMPTY,
  );
}
const EMPTY: RecallItem[] = [];
