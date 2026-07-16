import { useSyncExternalStore } from 'react';
import type { BoardItem } from '../types';
import { api } from './api';
import { onMessage, onReconnected } from './signalr';

// Стор доски агентов: живые статусы исполнителей Claude/персона.
// Подписывается на task_changed и status_changed для реалтайм-обновления.

let _items: BoardItem[] = [];
let _loaded = false;
let _loading: Promise<void> | null = null;
const _listeners = new Set<() => void>();
let _realtimeWired = false;
const DEBOUNCE_MS = 500;

function emit() {
  _listeners.forEach(fn => fn());
}

// Debounced fetch: при батче событий перезапрашиваем не чаще DEBOUNCE_MS
let _fetchTimer: ReturnType<typeof setTimeout> | null = null;
function scheduleFetch() {
  if (_fetchTimer) return;
  _fetchTimer = setTimeout(() => {
    _fetchTimer = null;
    void fetchBoard();
  }, DEBOUNCE_MS);
}

async function fetchBoard(): Promise<void> {
  // При первичной загрузке (не debounce из реальтайма) — делаем полноценным
  if (!_loaded) {
    _loading = doFetch();
    await _loading;
    _loading = null;
    return;
  }
  await doFetch();
}

async function doFetch(): Promise<void> {
  try {
    const res = await api.board.agents();
    _items = res.items;
    _loaded = true;
    emit();
  } catch {
    // Не обновляем — оставляем старые данные
  }
}

function wireRealtime() {
  if (_realtimeWired) return;
  _realtimeWired = true;

  onMessage(msg => {
    // task_changed — задача обновилась или удалилась → обновляем доску
    if (msg.type === 'task_changed') {
      scheduleFetch();
      return;
    }
    // status_changed — изменился статус сессии → обновляем доску
    if (msg.type === 'status_changed' && msg.sessionId) {
      // Быстрый оптимистичный апдейт: обновляем статус карточки в текущем снепшоте
      // без полного рефетча
      const sessionId = msg.sessionId as string;
      const status = msg.status as string;
      let changed = false;

      _items = _items.map(item => {
        if (item.sessionId !== sessionId) return item;
        changed = true;
        // Маппинг статуса в колонку
        if (status === 'waiting') {
          return { ...item, column: 'waiting' as const, sessionStatus: 'waiting', permissionPending: true };
        }
        if (status === 'working' || status === 'starting') {
          return { ...item, column: 'working' as const, sessionStatus: status, permissionPending: false };
        }
        // finished/active/error → queue или done
        if (status === 'finished' || status === 'error') {
          return { ...item, column: 'done' as const, sessionStatus: status, permissionPending: false };
        }
        return item;
      });

      if (changed) emit();
      // Debounce на случай, если за этим последует ещё событие
      scheduleFetch();
    }
  });

  onReconnected(() => {
    // После реконнекта — свежие данные
    void fetchBoard();
  });
}

export function ensureAgentsLoaded(): Promise<void> {
  wireRealtime();
  if (_loaded) return Promise.resolve();
  if (!_loading) {
    _loading = doFetch().finally(() => { _loading = null; });
  }
  return _loading;
}

export function useAgentBoard(): BoardItem[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _items,
    () => _items,
  );
}

// Очистка (для тестов)
export function resetBoardStore() {
  _items = [];
  _loaded = false;
  _listeners.clear();
}
