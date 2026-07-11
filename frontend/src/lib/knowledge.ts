// Знания: глобальный стор списка баз + realtime. Паттерн — как lib/notes.ts.
// Realtime: бэк шлёт knowledge_changed в группу user_{userId} (создание/удаление
// базы, добавление/удаление документа) — стор перечитывает список и бампает версию,
// по которой KnowledgeView перезапрашивает состав открытой базы.

import { useSyncExternalStore } from 'react';
import type { KnowledgeBaseSummary } from '../types';
import { api } from './api';
import { joinUser, onMessage, onReconnected } from './signalr';

let _items: KnowledgeBaseSummary[] = [];
let _configured = true;
let _loaded = false;
let _loading: Promise<void> | null = null;
let _version = 0;   // бампается на любое изменение — deps для рефетча деталей базы
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
    if (msg.type === 'knowledge_changed') void reloadKnowledge();
  });
  onReconnected(() => { joinUserGroup(); void reloadKnowledge(); });
}

export async function reloadKnowledge(): Promise<void> {
  try {
    const res = await api.knowledgeBases.list();
    _items = res.items;
    _configured = res.configured;
    _loaded = true;
    emit();
  } catch {
    // Сервер/Dify недоступны — не роняем раздел; покажет пустой список / повторит на реконнекте
  }
}

export function ensureKnowledgeLoaded(): Promise<void> {
  wireRealtime();
  joinUserGroup();
  if (_loaded) return Promise.resolve();
  if (!_loading) _loading = reloadKnowledge().finally(() => { _loading = null; });
  return _loading;
}

// Список релевантных пользователю баз (личные + публичные)
export function useKnowledge(): KnowledgeBaseSummary[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _items,
    () => _items,
  );
}

// Настроен ли Dify (для empty-state «раздел недоступен»)
export function useKnowledgeConfigured(): boolean {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _configured,
    () => _configured,
  );
}

// Счётчик изменений — для инвалидации состава открытой базы (включай в deps эффекта рефетча)
export function useKnowledgeVersion(): number {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version,
    () => _version,
  );
}

// Локально применить изменения после собственных мутаций (realtime продублирует).
export function bumpKnowledge(): void { void reloadKnowledge(); }
