import { useSyncExternalStore } from 'react';
import { api } from './api';
import { onMessage, onReconnected } from './signalr';

// Чаты, в которых прямо сейчас работают ФОНОВЫЕ агенты (Agent run_in_background / Workflow).
//
// Зачем отдельный стор, а не поле статуса сессии: пока фоновый агент работает, ход чата уже
// завершён и статус сессии — Active, у которого нет ни свечения, ни движения. Снаружи такой
// чат выглядел остывшим, хотя внутри процесса CLI идёт работа. Признак живёт ровно столько,
// сколько живёт процесс, поэтому в sessions.json ему делать нечего.
//
// Источник — событие bg_agents_presence (приходит ТОЛЬКО на переходе 0↔N) плюс снимок
// GET /api/chats/agents-presence: открывший список уже после старта агентов иначе не узнал
// бы о них до самого конца работы. Снимок снимается при первом подписчике и на переподключении.

let _ids: ReadonlySet<string> = new Set();
const _listeners = new Set<() => void>();

let _offMessage: (() => void) | null = null;
let _offReconnected: (() => void) | null = null;

function emit() {
  for (const fn of _listeners) fn();
}

// Set пересоздаём только при реальном изменении: useSyncExternalStore сравнивает
// снимок по ссылке, и новый Set с тем же составом дал бы ререндер всего списка чатов
function setPresence(sessionId: string, active: boolean) {
  if (_ids.has(sessionId) === active) return;
  const next = new Set(_ids);
  if (active) next.add(sessionId);
  else next.delete(sessionId);
  _ids = next;
  emit();
}

function applySnapshot(ids: string[]) {
  if (ids.length === _ids.size && ids.every(id => _ids.has(id))) return;
  _ids = new Set(ids);
  emit();
}

async function fetchSnapshot() {
  try {
    applySnapshot(await api.chats.agentsPresence());
  } catch {
    // Снимок — уточнение поверх realtime: не вышло, живём на событиях до следующей попытки
  }
}

function start() {
  void fetchSnapshot();
  _offMessage = onMessage(msg => {
    if (msg.type === 'bg_agents_presence' && msg.sessionId) setPresence(msg.sessionId, msg.active);
    // Чат удалили — его присутствие больше не про что
    else if (msg.type === 'chat_deleted' && msg.sessionId) setPresence(msg.sessionId, false);
  });
  // За время обрыва переходы 0↔N прошли мимо — состав пересобираем снимком
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

const snapshot = () => _ids;

/** Чаты с живыми фоновыми агентами. Пустое множество — фона нет нигде. */
export function useAgentsPresence(): ReadonlySet<string> {
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

/** Работают ли в этом чате фоновые агенты прямо сейчас. */
export function useAgentsRunning(sessionId: string): boolean {
  return useAgentsPresence().has(sessionId);
}

/**
 * Подписка для потребителей ВНЕ React (сторы projectActivity, wallStore): те держат
 * собственные агрегаты и обязаны пересчитываться, когда фон появился или закончился.
 * Возвращает функцию отписки; первый подписчик поднимает снимок и realtime.
 */
export const subscribeAgentsPresence = subscribe;

/** Текущее множество чатов с живым фоном — для тех же не-React потребителей. */
export const agentsPresenceSnapshot = snapshot;

// Только для тестов: сбросить состояние между кейсами
export function __resetAgentsPresence() {
  _ids = new Set();
  _listeners.clear();
  stop();
}
