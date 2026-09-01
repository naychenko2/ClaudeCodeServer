import { useSyncExternalStore } from 'react';
import { api } from './api';
import { onMessage, onReconnected } from './signalr';

// Сторожа чатов (chat-watchdogs) — «чат ждёт условие на сервере»: watch_start декларирует
// «дождись и разбуди этот чат», цикл живёт в бэкенде и переживает ходы и рестарты. Снаружи
// такой чат обязан выглядеть живым (значок будильника, статус «как у активного», точки рельсы
// и стены), хотя ход в нём давно завершён — по той же причине, что и у agentsPresence: у
// статуса Active нет ни свечения, ни движения. Признак живёт, пока жив сторож, поэтому в
// саму сессию его делать нечего.
//
// Источник — событие watchdogs_changed (приходит ПОЛНЫМ составом чатов и проектов, а не
// диффом: потребитель заменяет им состояние целиком) плюс снимок GET /api/watchdogs:
// открывший список после постановки сторожа иначе не узнал бы о нём до терминала. Снимок
// снимается при первом подписчике и на переподключении. Удаление чата (chat_deleted)
// вычитает его: сторож на сервере гаснет сам, а состав проектов пересоберёт ближайшее
// событие — проекта по id чата клиент не знает.

// Один объект на оба множества: снимок useSyncExternalStore сравнивается ПО ССЫЛКЕ, и
// свежий объект на каждый вызов гнета бы бесконечный ререндер
interface WatchdogPresence {
  sessions: ReadonlySet<string>;
  projects: ReadonlySet<string>;
}

let _state: WatchdogPresence = { sessions: new Set(), projects: new Set() };
const _listeners = new Set<() => void>();

let _offMessage: (() => void) | null = null;
let _offReconnected: (() => void) | null = null;

function emit() {
  for (const fn of _listeners) fn();
}

// Set пересоздаём только при реальном изменении: useSyncExternalStore сравнивает
// снимок по ссылке, и новый Set с тем же составом дал бы ререндер списка чатов вхолостую
function withIds(set: ReadonlySet<string>, ids: string[]): ReadonlySet<string> {
  if (ids.length === set.size && ids.every(id => set.has(id))) return set;
  return new Set(ids);
}

function applySnapshot(sessions: string[], projects: string[]) {
  const nextSessions = withIds(_state.sessions, sessions);
  const nextProjects = withIds(_state.projects, projects);
  if (nextSessions === _state.sessions && nextProjects === _state.projects) return;
  _state = { sessions: nextSessions, projects: nextProjects };
  emit();
}

// Чат удалили — его присутствие больше не про что. Проектное множество не трогаем: id
// проекта по чату клиент не знает, состав пересоберёт ближайшее watchdogs_changed
function dropChat(sessionId: string) {
  if (!_state.sessions.has(sessionId)) return;
  const next = new Set(_state.sessions);
  next.delete(sessionId);
  _state = { sessions: next, projects: _state.projects };
  emit();
}

async function fetchSnapshot() {
  try {
    const snapshot = await api.watchdogs.snapshot();
    applySnapshot(snapshot.sessions ?? [], snapshot.projects ?? []);
  } catch {
    // Снимок — уточнение поверх realtime: не вышло, живём на событиях до следующей попытки
  }
}

function start() {
  void fetchSnapshot();
  _offMessage = onMessage(msg => {
    if (msg.type === 'watchdogs_changed') applySnapshot(msg.sessions, msg.projects);
    // Чат удалили — его присутствие больше не про что
    else if (msg.type === 'chat_deleted' && msg.sessionId) dropChat(msg.sessionId);
  });
  // За время обрыва состав мог смениться (терминал, отмена, постановка) — пересобираем снимком
  _offReconnected = onReconnected(() => void fetchSnapshot());
}

function stop() {
  _offMessage?.(); _offMessage = null;
  _offReconnected?.(); _offReconnected = null;
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  if (_listeners.size === 1) start();
  return () => {
    _listeners.delete(fn);
    if (_listeners.size === 0) stop();
  };
}

const stateSnapshot = () => _state;
const sessionsSnapshot = () => _state.sessions;
const projectsSnapshot = () => _state.projects;

/**
 * Чаты ({ sessions }) и проекты ({ projects }) владельца с АКТИВНЫМИ сторожами. Пустые
 * множества — сторожей нет нигде. Кто ждёт чего именно — знает только бэкенд: наружу
 * выставляется сам факт ожидания.
 */
export function useWatchdogPresence(): WatchdogPresence {
  return useSyncExternalStore(subscribe, stateSnapshot, stateSnapshot);
}

/** Ждёт ли в этом чате активный сторож прямо сейчас. */
export function useChatWatchdogs(sessionId: string): boolean {
  return useWatchdogPresence().sessions.has(sessionId);
}

/** Проекты, в чатах которых есть активные сторожа, — источник точек рельсы и стены. */
export function useWatchdogProjects(): ReadonlySet<string> {
  return useSyncExternalStore(subscribe, projectsSnapshot, projectsSnapshot);
}

/**
 * Подписка для потребителей ВНЕ React (те держат собственные агрегаты и обязаны
 * пересчитываться, когда сторож поставлен или погас). Возвращает функцию отписки;
 * первый подписчик поднимает снимок и realtime.
 */
export const subscribeWatchdogPresence = subscribe;

/** Текущее множество чатов с активными сторожами — для не-React потребителей. */
export const watchdogSessionsSnapshot = sessionsSnapshot;

/** Текущее множество проектов с активными сторожами — для не-React потребителей. */
export const watchdogProjectsSnapshot = projectsSnapshot;

// Только для тестов: сбросить состояние между кейсами
export function __resetWatchdogPresence() {
  _state = { sessions: new Set(), projects: new Set() };
  _listeners.clear();
  stop();
}
