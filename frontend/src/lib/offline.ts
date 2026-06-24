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
  if (_online === value) {
    return;
  }
  _online = value;
  // Пока офлайн — активно зондируем сервер: иначе при «мигнувшей» сети
  // (упал один запрос, но событие window 'online' не пришло) UI застрянет в офлайне
  if (value) stopConnectivityProbe(); else startConnectivityProbe();
  _listeners.forEach(fn => fn());
}

// --- Probe восстановления связи ---
// Если _online опустился в false из-за единичного сбоя fetch, а ОС-событие 'online'
// не приходит (сеть на уровне ОС не пропадала) и фоновых GET нет — без зонда UI
// остался бы офлайн навсегда. Зонд бьёт по API раз в N секунд, пока не получит ответ.
let _probeTimer: ReturnType<typeof setInterval> | null = null;
const PROBE_INTERVAL_MS = 4_000;

function startConnectivityProbe() {
  if (_probeTimer !== null || typeof window === 'undefined') return;
  _probeTimer = setInterval(async () => {
    try {
      const token = typeof localStorage !== 'undefined'
        ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
        : null;
      // HEAD к API: важен сам факт ответа (сеть жива), тело не нужно. Не идёт через
      // request(), чтобы не триггерить IDB-fallback/логаут. SW не кэширует /api.
      const res = await fetch(BASE + '/projects', {
        method: 'HEAD',
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      });
      // Любой HTTP-ответ (включая 401) означает, что сеть восстановлена
      if (res) setOnline(true);
    } catch {
      /* всё ещё нет сети — ждём следующего тика */
    }
  }, PROBE_INTERVAL_MS);
}

function stopConnectivityProbe() {
  if (_probeTimer !== null) {
    clearInterval(_probeTimer);
    _probeTimer = null;
  }
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

// Таймаут fetch: на зависшей сети без него запрос может ждать минутами
const FETCH_TIMEOUT_MS = 30_000;

// --- Запрос ---

export async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const method = (options?.method ?? 'GET').toUpperCase();
  const isGet = method === 'GET';

  // Мутации офлайн запрещены
  if (!isGet && !_online) {
    throw new OfflineError();
  }

  const token = typeof localStorage !== 'undefined'
    ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
    : null;

  // AbortController для таймаута: если сеть «зависла» (пакеты идут, но ответа нет),
  // мы не ждём браузерного TCP-таймаута (может быть минуты)
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);

  try {
    const res = await fetch(BASE + url, {
      ...options,
      signal: controller.signal,
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(options?.headers as Record<string, string> | undefined),
      },
    });
    clearTimeout(timeoutId);
    // Сервер ответил (даже ошибкой) → мы онлайн
    setOnline(true);

    // Ключ отвергнут сервером — уводим на экран входа
    if (res.status === 401) {
      if (token && typeof window !== 'undefined') {
        window.dispatchEvent(new Event('cc-unauthorized'));
      }
      const err = await res.json().catch(() => ({ error: 'Неверный API-ключ' }));
      throw new Error(err.error ?? 'Неверный API-ключ');
    }

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
    clearTimeout(timeoutId);
    // AbortError от нашего таймаута трактуем как сетевую проблему
    if (isNetworkError(e) || (e instanceof DOMException && e.name === 'AbortError')) {
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

  // Стартанули в офлайне (navigator.onLine=false): setOnline не вызывался,
  // поэтому зонд не активен — запускаем его вручную, чтобы поймать восстановление
  if (!_online) startConnectivityProbe();
}
