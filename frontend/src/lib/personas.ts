// Персоны (олицетворённые ИИ-собеседники): глобальный стор списка + realtime.
// Паттерн — как lib/notes.ts, но проще (без офлайна): модульное состояние,
// подписки, useSyncExternalStore.
// Realtime: бэк шлёт personas_changed в группу user_{userId} (персона создана/
// изменена/удалена другим устройством или Claude) — стор перечитывает список.

import { useSyncExternalStore } from 'react';
import type { Persona } from '../types';
import { api } from './api';
import { joinUser, onMessage, onReconnected } from './signalr';

let _personas: Persona[] = [];
let _loaded = false;
let _loading: Promise<void> | null = null;
let _version = 0;   // бампается на любое изменение — deps для рефетча деталей
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
    if (msg.type === 'personas_changed') void reloadPersonas();
  });
  // После реконнекта — заново вступаем в группу и перечитываем список
  onReconnected(() => { joinUserGroup(); void reloadPersonas(); });
}

export async function reloadPersonas(): Promise<void> {
  _personas = await api.personas.list();
  _loaded = true;
  emit();
}

export function ensurePersonasLoaded(): Promise<void> {
  wireRealtime();
  joinUserGroup();
  if (_loaded) return Promise.resolve();
  if (!_loading) _loading = reloadPersonas().finally(() => { _loading = null; });
  return _loading;
}

// Список персон владельца
export function usePersonas(): Persona[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _personas,
    () => _personas,
  );
}

// Счётчик изменений — для инвалидации детали (включай в deps эффекта рефетча)
export function usePersonasVersion(): number {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _version,
    () => _version,
  );
}

// Снимок списка вне React — для колбэков
export function getPersonasSnapshot(): Persona[] { return _personas; }

// Найти персону по id в текущем снимке (списки чатов, шапка чата и пр.).
// Не реактивен сам по себе — для авто-перерисовки подписывайся на usePersonas/usePersonasVersion.
export function getPersonaById(id: string): Persona | undefined {
  return _personas.find(p => p.id === id);
}

// Локально применить изменения после собственных мутаций (realtime продублирует)
export function bumpPersonas(): void { void reloadPersonas(); }

// Единый источник форматирования подписи персоны: роль — главная, имя в скобках.
// «Роль (Имя)», например «Дизайнер (Светлана)». Если роль пустая — просто имя.
// Для узких инлайн-мест (плашки чатов, дропдаун выбора).
export function personaLabel(p: { role?: string; name: string }): string {
  return p.role && p.role.trim() ? `${p.role.trim()} (${p.name})` : p.name;
}

// Инициалы из имени: две первые буквы (по словам, иначе первые две буквы слова)
export function personaInitials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

// Две строки подписи для мест с аватаром: роль сверху (главная), имя под ней.
// Если роль пустая — одна строка (primary = имя, secondary отсутствует).
export function personaTitleLines(p: { role?: string; name: string }): { primary: string; secondary?: string } {
  const role = p.role?.trim();
  return role ? { primary: role, secondary: p.name } : { primary: p.name };
}
