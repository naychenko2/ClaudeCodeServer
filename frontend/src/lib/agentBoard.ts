import { useSyncExternalStore } from 'react';
import type { BoardItem } from '../types';
import { api } from './api';
import { onMessage, onReconnected } from './signalr';

// Стор доски агентов: живые статусы исполнителей Claude/персона.
// Подписывается на task_changed и status_changed для реалтайм-обновления.

let _items: BoardItem[] = [];
let _loaded = false;
let _inFlight: Promise<void> | null = null;
const _listeners = new Set<() => void>();
let _realtimeWired = false;

// Ответ доски — самый крупный из регулярно опрашиваемых (замер: ~0,86 МБ на ответ),
// а поток status_changed идёт пачками на каждом ходе. Прежний debounce в 500 мс
// ограничивал только ЧАСТОТУ ВЫЗОВОВ, но не полёты: пока запрос летел, следующий
// событие успевало взвести новый — и доска читалась раз в 12 с, по 258 МБ за час,
// плюс столько же копий уходило в офлайн-кэш IndexedDB (см. api.board.agents live).
//
// Здесь две защиты: один полёт за раз (события, пришедшие в полёте, не теряются —
// взводят догон), и минимальный зазор между СТАРТАМИ запросов.
const MIN_GAP_MS = 5_000;

function emit() {
  _listeners.forEach(fn => fn());
}

let _tailTimer: ReturnType<typeof setTimeout> | null = null;
let _lastStartedAt = 0;
let _pending = false;

// Догон: взвести таймер на конец зазора. Действующий таймер не переставляется —
// пачка событий даёт один запрос, а не по одному на событие.
function scheduleFetch() {
  if (_tailTimer !== null) return;
  const wait = Math.max(0, MIN_GAP_MS - (Date.now() - _lastStartedAt));
  _tailTimer = setTimeout(() => {
    _tailTimer = null;
    void fetchBoard();
  }, wait);
}

// Один полёт на всех. Пока он летит, повод не начинает второй, а помечает себя
// догоном: без этого событие, пришедшее в полёте, потеряло бы обновление до
// следующего повода (реальтайм-событие одноразовое).
function fetchBoard(): Promise<void> {
  if (_inFlight) {
    _pending = true;
    return _inFlight;
  }
  _lastStartedAt = Date.now();
  const run = doFetch().finally(() => {
    _inFlight = null;
    if (_pending) {
      _pending = false;
      scheduleFetch();
    }
  });
  _inFlight = run;
  return run;
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
      // На случай, если за этим последует ещё событие — через троттл, не debounce
      scheduleFetch();
    }
  });

  onReconnected(() => {
    // После реконнекта — свежие данные (через троттл: реконнект часто ложится
    // на пачку status_changed, и два полёта на одну и ту же доску не нужны)
    scheduleFetch();
  });
}

export function ensureAgentsLoaded(): Promise<void> {
  wireRealtime();
  if (_loaded) return Promise.resolve();
  return fetchBoard();
}

// Принудительный рефетч доски — страховочный пуллинг дашборда «Домой»
// (статусы проектных исполнителей не приходят в user-группу SignalR).
// Через троттл: пуллинг и realtime-события сходятся в одну очередь.
export function refreshAgentBoard(): void {
  scheduleFetch();
}

export function useAgentBoard(): BoardItem[] {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => _listeners.delete(fn); },
    () => _items,
    () => _items,
  );
}

// Очистка (для тестов). Снимает и взведённый таймер, и флаг виртуальной подписки:
// иначе следующий тест не переподписался бы на signalr и видел бы пустой мок.
export function resetBoardStore() {
  _items = [];
  _loaded = false;
  _listeners.clear();
  _inFlight = null;
  _pending = false;
  _lastStartedAt = 0;
  _realtimeWired = false;
  if (_tailTimer !== null) { clearTimeout(_tailTimer); _tailTimer = null; }
}
