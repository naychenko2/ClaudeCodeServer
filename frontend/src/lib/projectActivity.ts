import { useSyncExternalStore } from 'react';
import type { HomeSessionInfo } from '../types';
import { api } from './api';
import { onMessage, onReconnected } from './signalr';

// Стор активности проектов для переключателя в сайдбаре (флаг sidebar-project-switcher):
// агрегат «в каком проекте агент работает / ждет ответа» по live-сессиям всех проектов.
// Источник — GET /api/home/summary (active[]). Статусы ПРОЕКТНЫХ сессий не приходят
// в user-группу SignalR (только в группы session/project — см. useHomeSummary), поэтому
// добираем страховочным поллингом; он живет только пока есть подписчики (плашка видна
// только в открытом воркспейсе).

export interface ProjectActivity {
  status: 'waiting' | 'working';
  // id первой сессии проекта со статусом waiting — для диплинка сразу в ждущий чат
  waitingChatId?: string;
}

const POLL_MS = 15_000;

let _agg = new Map<string, ProjectActivity>();
// Отпечаток агрегата: не эмитим (и не пересоздаем Map) когда данные не изменились,
// иначе плашка ререндерилась бы каждый тик поллинга
let _fingerprint = '';
const _listeners = new Set<() => void>();

let _timer: ReturnType<typeof setInterval> | null = null;
let _offMessage: (() => void) | null = null;
let _offReconnected: (() => void) | null = null;

function aggregate(active: HomeSessionInfo[]): Map<string, ProjectActivity> {
  const next = new Map<string, ProjectActivity>();
  for (const s of active) {
    if (!s.projectId) continue; // чаты вне проектов в плашке проектов не участвуют
    const cur = next.get(s.projectId);
    if (s.status === 'waiting') {
      // waiting приоритетнее working; первый ждущий чат запоминаем для диплинка
      if (!cur || cur.status !== 'waiting') next.set(s.projectId, { status: 'waiting', waitingChatId: s.id });
    } else if (!cur) {
      // starting/working → «работает»
      next.set(s.projectId, { status: 'working' });
    }
  }
  return next;
}

function fetchNow() {
  api.home.summary(20)
    .then(d => {
      const next = aggregate(d.active);
      const fp = [...next.entries()]
        .map(([pid, a]) => `${pid}:${a.status}:${a.waitingChatId ?? ''}`)
        .sort()
        .join('|');
      if (fp === _fingerprint) return;
      _fingerprint = fp;
      _agg = next;
      _listeners.forEach(fn => fn());
    })
    .catch(() => { /* сервер недоступен — оставляем прошлый агрегат */ });
}

// Поллинг и realtime-подписки живут только при активных подписчиках
function start() {
  if (_timer) return;
  fetchNow();
  _timer = setInterval(fetchNow, POLL_MS);
  _offMessage = onMessage(msg => {
    if (msg.type === 'status_changed' || msg.type === 'chat_deleted') fetchNow();
  });
  _offReconnected = onReconnected(fetchNow);
}

function stop() {
  if (_timer) { clearInterval(_timer); _timer = null; }
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

// Активность проектов: Map<projectId, ProjectActivity>. Проекты без live-сессий в Map отсутствуют.
export function useProjectActivity(): Map<string, ProjectActivity> {
  return useSyncExternalStore(subscribe, () => _agg, () => _agg);
}
