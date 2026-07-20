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
  // Успех (обычный запрос ответил, зонд достучался) сбрасывает счётчик промахов —
  // иначе накопленные фейлы могли бы тут же снова увести в офлайн.
  if (value) _consecutiveFailures = 0;
  if (_online === value) {
    return;
  }
  _online = value;
  // Состояние сменилось — пересчитаем каденс монитора немедленно
  // (offline → частый зонд возврата; online → спокойный heartbeat).
  rescheduleMonitor();
  _listeners.forEach(fn => fn());
}

// --- Монитор связи (health-ping) ---
// Активно проверяем достижимость сервера лёгким пингом. Один цикл, два режима:
//   online  — heartbeat раз в HEARTBEAT_INTERVAL; OFFLINE_FAIL_THRESHOLD промахов
//             подряд уводят в офлайн. Это ловит «зависшую» сеть (мобильный интернет
//             то есть, то нет): сокет цел, navigator.onLine=true, обычных запросов
//             нет — раньше UI узнавал о пропаже только через server-timeout SignalR
//             (~60с) или упёршись в 30-сек таймаут ручного действия.
//   offline — probe раз в PROBE_INTERVAL; первый же ответ возвращает в онлайн.
// Пинг гейтится видимостью вкладки: в фоне сеть/батарею не жжём, при возврате —
// немедленная проверка (см. initConnectivity).
let _monitorTimer: ReturnType<typeof setTimeout> | null = null;
let _consecutiveFailures = 0;
const HEARTBEAT_INTERVAL_MS = 15_000; // спокойный опрос, когда связь есть
const FAST_RETRY_MS = 3_000;          // добор до порога после первого промаха
const PROBE_INTERVAL_MS = 4_000;      // частый зонд возврата из офлайна
const PING_TIMEOUT_MS = 6_000;        // короткий таймаут пинга — не ждём 30с
const OFFLINE_FAIL_THRESHOLD = 2;     // столько промахов подряд уводят в офлайн

// Один пинг сервера. true = сервер достижим (любой HTTP-ответ, включая 401/404/500),
// false = сеть недоступна или ответ не пришёл за PING_TIMEOUT_MS.
async function pingServer(): Promise<boolean> {
  const token = typeof localStorage !== 'undefined'
    ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
    : null;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), PING_TIMEOUT_MS);
  try {
    // Лёгкий health-эндпоинт. Не через request() — чтобы не триггерить IDB-fallback/логаут.
    // На старом сервере без /health вернётся 404 — это тоже «достижим». SW не кэширует /api.
    await fetch(BASE + '/health', {
      method: 'GET',
      cache: 'no-store',
      signal: controller.signal,
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });
    return true;
  } catch {
    return false; // reject (сетевой сбой) или abort по таймауту
  } finally {
    clearTimeout(timer);
  }
}

// Задержка до следующего тика — по текущему состоянию связи и серии промахов.
function nextMonitorDelay(): number {
  if (!_online) return PROBE_INTERVAL_MS;
  return _consecutiveFailures > 0 ? FAST_RETRY_MS : HEARTBEAT_INTERVAL_MS;
}

function scheduleNextTick() {
  if (_monitorTimer !== null || typeof window === 'undefined') return;
  _monitorTimer = setTimeout(runMonitorTick, nextMonitorDelay());
}

// Пересобрать таймер под новый каденс (после смены _online). Немедленного пинга не шлём.
function rescheduleMonitor() {
  if (_monitorTimer !== null) { clearTimeout(_monitorTimer); _monitorTimer = null; }
  scheduleNextTick();
}

async function runMonitorTick() {
  _monitorTimer = null;
  // Вкладка в фоне — пропускаем пинг (возврат на вкладку форсит проверку сам)
  if (typeof document !== 'undefined' && document.visibilityState === 'hidden') {
    scheduleNextTick();
    return;
  }
  const reachable = await pingServer();
  if (reachable) {
    _consecutiveFailures = 0;
    if (!_online) setOnline(true);
  } else {
    _consecutiveFailures++;
    if (_online && _consecutiveFailures >= OFFLINE_FAIL_THRESHOLD) setOnline(false);
  }
  scheduleNextTick();
}

// Немедленная внеплановая проверка (возврат на вкладку, ОС-событие online, фокус окна).
function forceConnectivityCheck() {
  if (typeof window === 'undefined') return;
  if (_monitorTimer !== null) { clearTimeout(_monitorTimer); _monitorTimer = null; }
  void runMonitorTick();
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

export async function request<T>(url: string, options?: RequestInit & { timeoutMs?: number }): Promise<T> {
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
  // мы не ждём браузерного TCP-таймаута (может быть минуты).
  // timeoutMs — оверрайд для заведомо долгих запросов (AI-генерация и т.п.)
  const { timeoutMs, ...fetchOptions } = options ?? {};
  const controller = new AbortController();
  const timeoutId = setTimeout(() => controller.abort(), timeoutMs ?? FETCH_TIMEOUT_MS);

  // FormData (multipart-загрузки) — Content-Type ставит сам браузер (с boundary)
  const isFormData = typeof FormData !== 'undefined' && fetchOptions.body instanceof FormData;

  try {
    const res = await fetch(BASE + url, {
      ...fetchOptions,
      signal: controller.signal,
      headers: {
        ...(isFormData ? {} : { 'Content-Type': 'application/json' }),
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
      // Статус прикрепляем к ошибке — потребители (offline-очередь) отличают 404/4xx
      // (перманентно) от 5xx/сетевых (стоит повторить)
      const httpErr = new Error(err.error ?? res.statusText) as Error & { status?: number };
      httpErr.status = res.status;
      throw httpErr;
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

  // ОС однозначно потеряла сеть — сразу офлайн, без ожидания пинга
  window.addEventListener('offline', () => setOnline(false));
  // ОС сообщает о сети, но до НАШЕГО сервера она может не дойти — не верим слепо,
  // а тут же проверяем пингом (за ≤6с подтвердит или оставит офлайн).
  window.addEventListener('online', () => forceConnectivityCheck());
  // Возврат на вкладку/фокус окна — момент, когда точность статуса важнее всего
  window.addEventListener('focus', () => forceConnectivityCheck());
  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') forceConnectivityCheck();
    });
  }

  // Запускаем цикл монитора: heartbeat в онлайне ловит пропажу сервера,
  // probe в офлайне — его возвращение.
  scheduleNextTick();
}
