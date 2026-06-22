// Офлайн-слой: обёртка над fetch с network-first → IndexedDB-fallback,
// глобальное состояние online/offline и блокировка мутаций офлайн.
//
// GET:    онлайн → сеть + запись в кэш; офлайн/сетевая ошибка → отдаём из кэша.
// Мутации (POST/PUT/DELETE): офлайн → ошибка; онлайн → как обычно.

import { idbGet, idbSet } from './idb';

const BASE = '/api';

// --- Состояние связи ---

let _online = typeof navigator !== 'undefined' ? navigator.onLine : true;
const _listeners = new Set<() => void>();

export function isOnline(): boolean {
  return _online;
}

export function subscribeOnline(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

function setOnline(value: boolean) {
  if (_online === value) return;
  _online = value;
  _listeners.forEach(fn => fn());
}

// Вызываются из signalr.ts по событиям соединения
export function notifyOnline() { setOnline(true); }
export function notifyOffline() { setOnline(false); }

// Ошибка офлайн-операции — UI может отличить её от прочих
export class OfflineError extends Error {
  constructor(message = 'Действие недоступно офлайн') {
    super(message);
    this.name = 'OfflineError';
  }
}

// fetch при сетевом сбое реджектит с TypeError; HTTP-ошибки (4xx/5xx) — это res.ok=false (сервер доступен)
function isNetworkError(e: unknown): boolean {
  return e instanceof TypeError;
}

// --- Запрос ---

export async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const method = (options?.method ?? 'GET').toUpperCase();
  const isGet = method === 'GET';

  // Мутации офлайн запрещены
  if (!isGet && !_online) {
    throw new OfflineError();
  }

  try {
    const res = await fetch(BASE + url, {
      headers: { 'Content-Type': 'application/json' },
      ...options,
    });
    // Сервер ответил (даже ошибкой) → мы онлайн
    setOnline(true);

    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: res.statusText }));
      throw new Error(err.error ?? res.statusText);
    }

    // Тело может быть пустым (Ok() без контента у мутаций) — не парсим пустую строку как JSON
    const text = res.status === 204 ? '' : await res.text();
    const data = (text ? JSON.parse(text) : undefined) as T;
    if (isGet) {
      idbSet(url, { data, savedAt: Date.now() }).catch(() => { /* кэш недоступен — не критично */ });
    }
    return data;
  } catch (e) {
    if (isNetworkError(e)) {
      setOnline(false);
      if (isGet) {
        const cached = await idbGet<T>(url).catch(() => undefined);
        if (cached) return cached.data;
        throw new OfflineError('Нет сохранённых данных для офлайн-доступа');
      }
      throw new OfflineError();
    }
    throw e; // HTTP-ошибка или прочее — пробрасываем как есть
  }
}

// --- Инициализация детекции связи (вызвать один раз при старте) ---

let _initialized = false;

export function initConnectivity() {
  if (_initialized || typeof window === 'undefined') return;
  _initialized = true;

  window.addEventListener('offline', () => setOnline(false));
  // 'online' — оптимистично считаем себя онлайн; ближайший fetch/SignalR подтвердит или откатит
  window.addEventListener('online', () => setOnline(true));
}
