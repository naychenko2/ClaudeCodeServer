// Офлайн-слой: обёртка над fetch с network-first → IndexedDB-fallback,
// глобальное состояние online/offline и блокировка мутаций офлайн.
//
// GET:    онлайн → сеть + запись в кэш; офлайн/сетевая ошибка → отдаём из кэша.
// Мутации (POST/PUT/DELETE): офлайн → ошибка; онлайн → как обычно.

import { idbGet, idbSet } from './idb';

const BASE = '/api';

// --- Состояние связи ---

// Тройное состояние: online — стабильно; degraded — подозрение на нестабильность
// (зависший запрос, первый промах пинга, SignalR reconnecting), данные тянутся,
// мутации НЕ блокируются; offline — устойчивая пропажа, мутации → OfflineError.
export type ConnectionState = 'online' | 'degraded' | 'offline';

let _online = typeof navigator !== 'undefined' ? navigator.onLine : true;
let _connectionState: ConnectionState = _online ? 'online' : 'offline';
const _listeners = new Set<() => void>();

export function isOnline(): boolean {
  return _online;
}

export function getConnectionState(): ConnectionState {
  return _connectionState;
}

// Оба сабскрайба делят один набор слушателей: любой переход дёргает всех, а
// useSyncExternalStore сам отсеивает неизменившийся снимок (isOnline/getConnectionState).
export function subscribeOnline(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

export function subscribeConnectionState(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

export function setConnectionState(value: ConnectionState) {
  // Успех (обычный запрос ответил, зонд достучался) сбрасывает счётчик промахов —
  // иначе накопленные фейлы могли бы тут же снова увести в офлайн.
  if (value === 'online') {
    _consecutiveFailures = 0;
    _firstFailureAt = null;
  }
  if (_connectionState === value) {
    return;
  }
  _connectionState = value;
  // Бинарный флаг — производный: degraded считается «ещё онлайн» (запросы и мутации идут).
  _online = value !== 'offline';
  // Состояние сменилось — пересчитаем каденс монитора немедленно
  // (offline → частый зонд возврата; degraded → быстрый добор; online → спокойный heartbeat).
  rescheduleMonitor();
  _listeners.forEach(fn => fn());
}

// Старые вызовы (ОС-события, тесты) маппятся на крайние состояния
function setOnline(value: boolean) {
  setConnectionState(value ? 'online' : 'offline');
}

// Подозрение на нестабильность. Переход только из online: из offline в degraded
// не выходим — возврат требует явного успеха (ответ сервера / пинг).
export function setDegraded(_reason: string) {
  if (_connectionState !== 'online') return;
  setConnectionState('degraded');
}

// --- Монитор связи (health-ping) ---
// Активно проверяем достижимость сервера лёгким пингом. Один цикл, два режима:
//   online  — heartbeat раз в HEARTBEAT_INTERVAL; устойчивый провал дольше
//             OFFLINE_DWELL_MS уводит в offline. Это ловит «зависшую» сеть (мобильный
//             интернет то есть, то нет): сокет цел, navigator.onLine=true, обычных
//             запросов нет — раньше UI узнавал о пропаже только через server-timeout
//             SignalR (~60с) или упёршись в 30-сек таймаут ручного действия.
//   offline — probe раз в PROBE_INTERVAL; первый же ответ возвращает в онлайн.
// Пинг гейтится видимостью вкладки: в фоне сеть/батарею не жжём, при возврате —
// немедленная проверка (см. initConnectivity).
let _monitorTimer: ReturnType<typeof setTimeout> | null = null;
let _consecutiveFailures = 0;
export let _firstFailureAt: number | null = null;
export let _pingInFlight: Promise<boolean> | null = null;
let _lastForcedCheckAt = 0;
const HEARTBEAT_INTERVAL_MS = 15_000;
const FAST_RETRY_MS = 5_000;
const PROBE_INTERVAL_MS = 4_000;
const PING_TIMEOUT_MS = 4_000;
// Сколько должен длиться непрерывный провал, чтобы уйти в offline.
// На мобильной сети короткие блипы не считаем — нужно устойчивое падение.
const OFFLINE_DWELL_MS = 8_000;
// Минимальный интервал между форс-проверками (focus, visibilitychange, ОС-событие online).
const MIN_FORCED_CHECK_INTERVAL_MS = 2_000;
// Порог «зависания запроса» — для мобилы 5с адекватнее, чем 2с.
const DEGRADED_THRESHOLD_MS = 5_000;
const FETCH_TIMEOUT_MS = 30_000;

// Один пинг сервера. true = сервер достижим (200/401/404 и т.п.),
// false = сеть недоступна, таймаут ИЛИ gateway-ошибка реверс-прокси (502/503/504).
export async function pingServer(): Promise<boolean> {
  const token = typeof localStorage !== 'undefined'
    ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
    : null;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), PING_TIMEOUT_MS);
  try {
    // Лёгкий health-эндпоинт. Не через request() — чтобы не триггерить IDB-fallback/логаут.
    // На старом сервере без /health вернётся 404 — это тоже «достижим». SW не кэширует /api.
    const res = await fetch(BASE + '/health', {
      method: 'GET',
      cache: 'no-store',
      signal: controller.signal,
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });
    // За реверс-прокси (боевой :80/:8080) убитый бэкенд отдаёт 502/503/504 при живом
    // прокси. Раньше это считалось «доступен» → приложение не уходило в офлайн, а
    // запросы к API падали. Трактуем gateway-ошибки как недоступность бэкенда.
    if (res.status === 502 || res.status === 503 || res.status === 504) return false;
    return true;
  } catch {
    return false; // reject (сетевой сбой) или abort по таймауту
  } finally {
    clearTimeout(timer);
  }
}

// Задержка до следующего тика — по текущему состоянию связи и серии промахов.
function nextMonitorDelay(): number {
  if (_connectionState === 'offline') return PROBE_INTERVAL_MS;
  // degraded — быстрая проверка стабилизации (подтвердить онлайн или добрать до офлайна)
  if (_connectionState === 'degraded') return FAST_RETRY_MS;
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

export async function runMonitorTick() {
  _monitorTimer = null;
  if (typeof document !== 'undefined' && document.visibilityState === 'hidden') {
    scheduleNextTick();
    return;
  }
  // Мьютекс: один пинг в полёте. Если уже идёт — просто дождёмся следующего тика.
  if (_pingInFlight !== null) {
    scheduleNextTick();
    return;
  }
  _pingInFlight = pingServer().finally(() => { _pingInFlight = null; });
  const reachable = await _pingInFlight;
  if (reachable) {
    _consecutiveFailures = 0;
    _firstFailureAt = null;
    if (_connectionState !== 'online') setConnectionState('online');
  } else {
    _consecutiveFailures++;
    if (_firstFailureAt === null) _firstFailureAt = Date.now();
    // Устойчивый провал дольше OFFLINE_DWELL_MS — уходим в offline.
    if (_connectionState === 'online') {
      setDegraded('промах health-ping');
    } else if (_connectionState === 'degraded'
        && Date.now() - (_firstFailureAt ?? Date.now()) >= OFFLINE_DWELL_MS) {
      setConnectionState('offline');
    }
  }
  scheduleNextTick();
}

// Немедленная внеплановая проверка (возврат на вкладку, ОС-событие online, фокус окна).
export function forceConnectivityCheck() {
  if (typeof window === 'undefined') return;
  // На мобиле focus/visibilitychange летят пачками (клавиатура, шторка, уведомления).
  // Троттлим, иначе каждое событие запускает свой пинг и ломает гистерезис.
  const now = Date.now();
  if (now - _lastForcedCheckAt < MIN_FORCED_CHECK_INTERVAL_MS) {
    return;
  }
  _lastForcedCheckAt = now;
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

// Сервер не ответил за отведённое время. Отдельно от OfflineError: связь может быть
// цела, а запрос просто оказался дольше своего лимита (ход модели, git-операция на
// большой репе). Раньше такой обрыв показывался как «Действие недоступно офлайн» —
// человек чинил сеть вместо того, чтобы дать серверу время.
// Бросается ТОЛЬКО при явном timeoutMs: у запросов с дефолтным лимитом зависание —
// действительно признак проблем со связью, и там прежнее поведение сохранено
// (офлайн-очереди задач и заметок ловят OfflineError по instanceof).
export class RequestTimeoutError extends Error {
  // Поле объявлено отдельно, а не parameter property: в проекте включён erasableSyntaxOnly
  readonly timeoutMs: number;
  constructor(timeoutMs: number) {
    super(`Сервер не ответил за ${Math.round(timeoutMs / 1000)} с`);
    this.name = 'RequestTimeoutError';
    this.timeoutMs = timeoutMs;
  }
}

// fetch при сетевом сбое реджектит с TypeError; HTTP-ошибки (4xx/5xx) — это res.ok=false (сервер доступен)
function isNetworkError(e: unknown): boolean {
  return e instanceof TypeError;
}


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
  const effectiveTimeout = timeoutMs ?? FETCH_TIMEOUT_MS;
  // Признак «прервали мы сами по таймауту»: AbortError от нашего таймера неотличим от
  // прочих abort'ов, а причину надо назвать точно (см. RequestTimeoutError)
  let timedOut = false;
  const timeoutId = setTimeout(() => { timedOut = true; controller.abort(); }, effectiveTimeout);
  // Ранний таймер degraded: запрос «завис» >2с — сразу показываем нестабильность и
  // форсим проверку связи, не дожидаясь 30-секундного таймаута. На заведомо долгие
  // запросы (явный timeoutMs) не вешается — их длительность ожидаема.
  const degradedId = timeoutMs === undefined
    ? setTimeout(() => {
        // Degraded — только индикатор; форс-пинг отсюда не нужен, иначе добавим
        // нагрузку в узкий канал и спровоцируем лавину провалов на мобиле.
        if (getConnectionState() === 'online') setDegraded('зависший запрос');
      }, DEGRADED_THRESHOLD_MS)
    : null;
  const clearReqTimers = () => {
    clearTimeout(timeoutId);
    if (degradedId !== null) clearTimeout(degradedId);
  };

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
    clearReqTimers();
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
      // (перманентно) от 5xx/сетевых (стоит повторить). Заголовки тоже отдаём наружу:
      // на них живут служебные маркеры вроде X-CodeGraph-Building («граф строится»).
      // Тело отдаём целиком: у ошибки бывают поля сверх error — по ним потребитель
      // предлагает лекарство (git-публикация по diverged зовёт «Подтянуть и опубликовать»).
      const httpErr = new Error(err.error ?? res.statusText) as Error & {
        status?: number; responseHeaders?: Headers; body?: unknown;
      };
      httpErr.status = res.status;
      httpErr.responseHeaders = res.headers;
      httpErr.body = err;
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
    clearReqTimers();
    // AbortError от нашего таймаута трактуем как сетевую проблему
    if (isNetworkError(e) || (e instanceof DOMException && e.name === 'AbortError')) {
      // Свой таймаут на заведомо долгом запросе (явный timeoutMs) — не улика против
      // связи: сервер мог просто думать дольше отведённого. Состояние не трогаем,
      // иначе успешная, но медленная операция уводила бы приложение в degraded/offline.
      const ourTimeout = timedOut && timeoutMs !== undefined;
      if (!ourTimeout) {
        // Уступчивый переход вместо резкого офлайна: первая ошибка — degraded,
        // повтор из degraded — offline
        if (_connectionState === 'online') setDegraded('сетевая ошибка запроса');
        else if (_connectionState === 'degraded') setConnectionState('offline');
      }
      // Кэш выручает GET независимо от причины обрыва — сначала пробуем его
      if (isGet) {
        const cached = await idbGet<T>(url).catch(() => undefined);
        if (cached) return cached.data;
      }
      if (ourTimeout) throw new RequestTimeoutError(effectiveTimeout);
      throw new OfflineError(isGet ? 'Нет сохранённых данных для офлайн-доступа' : undefined);
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
  // а тут же проверяем пингом (за ≤4с подтвердит или оставит офлайн).
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
