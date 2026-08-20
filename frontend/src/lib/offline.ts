// Офлайн-слой: обёртка над fetch с network-first → IndexedDB-fallback
// и блокировка мутаций офлайн.
//
// GET:    онлайн → сеть + запись в кэш; офлайн/сетевая ошибка → отдаём из кэша.
// Мутации (POST/PUT/DELETE): офлайн → ошибка; онлайн → как обычно.
//
// Состояние связи — бинарное (online | offline), без промежуточных ступеней.
//
// ВХОДЫ в offline (три факта, эвристики нет):
//   1. window 'offline' — ОС потеряла сеть.
//   2. Сетевая ошибка fetch в catch-ветке request(), ПОДТВЕРЖДЁННАЯ пингом
//      /api/health (см. pingSharedFlight) — бэкенд недостижим. Одиночный провал
//      fetch сам по себе входом не является.
//   3. SignalR: окончательный обрыв (onclose) — сразу, ИЛИ ветки reject ожидания
//      подключения в ensureConnected() (таймаут 8с / переход в Disconnected) —
//      через то же подтверждение confirmOffline().
//      Ветки reject нужны, потому что текущий делегат ретраев nextRetryDelayInMilliseconds
//      всегда возвращает число → onclose практически недостижим, и единственный
//      реальный сигнал «хаб не поднялся» приходит именно отсюда.
//
// ВЫХОДЫ в online:
//   1. window 'online' — ОС сообщает о возврате.
//   2. SignalR onreconnected и успешный start() из Disconnected.
//   3. Успешный ответ сервера в request() (включая 401/404/4xx/5xx — сервер живой).
//   4. Односторонний зонд: GET /api/health раз в PROBE_INTERVAL_MS. Живёт
//      ТОЛЬКО пока мы offline; в online не тикает. Провал зонда состояние
//      НЕ меняет — это не «возврат эвристики».

import { idbGet, idbSet } from './idb';

const BASE = '/api';

// --- Состояние связи ---

let _online = typeof navigator !== 'undefined' ? navigator.onLine : true;
const _listeners = new Set<() => void>();

export function isOnline(): boolean {
  return _online;
}

// Один сабскрайб на смену состояния; useSyncExternalStore сам отсеивает
// неизменившийся снимок (isOnline), лишних нотификаций нет.
export function subscribeOnline(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Отметка последнего возврата вкладки в видимость — «тихое окно» для тоста
// «Связь восстановлена». Фоновый разрыв сокета (планшет заморозил/затроттлил вкладку
// при переключении приложений) пользователь не наблюдал, а формальный переход
// offline → online при заморозке вообще случается уже ПОСЛЕ разморозки — поэтому
// привязываемся не к видимости в момент обрыва, а ко времени с момента возврата.
// 0 = вкладка не уходила в фон с загрузки, окно не активно.
let _lastBecameVisibleAt = 0;
const VISIBILITY_QUIET_WINDOW_MS = 5_000;

// true — вкладка стала видимой только что (меньше VISIBILITY_QUIET_WINDOW_MS назад).
// Читает App.tsx: восстановление связи в этом окне не озвучивается тостом.
export function becameVisibleRecently(): boolean {
  return _lastBecameVisibleAt !== 0
    && Date.now() - _lastBecameVisibleAt < VISIBILITY_QUIET_WINDOW_MS;
}

export function setOnline(value: boolean) {
  if (_online === value) return;
  _online = value;
  // Односторонний зонд: тикает ТОЛЬКО в offline, гаснет в online.
  // Провал зонда НЕ меняет состояние — поэтому это не возврат к вырезанной
  // эвристике: раньше мигание вело «промах → подозрение → таймер выдержки →
  // офлайн», здесь провал вообще ничего не делает, а в online зонд не
  // запускается вовсе.
  if (value) stopProbe(); else scheduleNextProbe();
  _listeners.forEach(fn => fn());
}

// signalr.ts зовёт setConnectionState('online' | 'offline') — единый биндинг
// слоя SignalR на бинарный флаг isOnline.
export function setConnectionState(value: 'online' | 'offline') {
  setOnline(value === 'online');
}

// --- Односторонний зонд возврата (offline → online) ---
// Живёт ТОЛЬКО пока мы offline. Любой успешный ответ сервера (включая 401/404)
// поднимает флаг; 502/503/504 от реверс-прокси = бэкенд недостижим (живой прокси
// при мёртвом сервере), таймаут и сетевой сбой — тоже провал. ПРОВАЛ НИЧЕГО
// НЕ ДЕЛАЕТ с состоянием: зонд не может увести из online в offline.
// Отдельный fetch (не через request()) — чтобы не триггерить IDB-фallback
// и логаут по 401.
const PROBE_INTERVAL_MS = 4_000;
// Таймаут пинга строго меньше PROBE_INTERVAL_MS — иначе на «зависшей» сети
// (fetch не реджектит, а висит) цикл получается ~8с вместо целевых 4с.
const PING_TIMEOUT_MS = 3_000;
// Минимальный интервал между форс-тиками зонда. На мобиле visibilitychange → visible
// прилетает пачками (клавиатура, шторка, уведомления), fetch к мёртвому серверу
// реджектит мгновенно, а мьютекс _probeInFlight про параллельность, не про частоту.
const MIN_FORCED_PROBE_INTERVAL_MS = 2_000;
let _lastForcedProbeAt = 0;
let _probeTimer: ReturnType<typeof setTimeout> | null = null;
let _probeInFlight: Promise<boolean> | null = null;

function scheduleNextProbe() {
  if (_probeTimer !== null || typeof window === 'undefined') return;
  _probeTimer = setTimeout(() => { void runProbeTick(); }, PROBE_INTERVAL_MS);
}

function stopProbe() {
  if (_probeTimer !== null) { clearTimeout(_probeTimer); _probeTimer = null; }
}

// Форсированный немедленный тик при возврате вкладки в видимость: сеть/батарею
// в фоне не жжём, но вернулись — пробуем сразу, не ждём PROBE_INTERVAL_MS.
function fireProbeNow() {
  if (_online || typeof window === 'undefined') return;
  // Троттл: visibilitychange на мобиле летит пачками — без него падает 5 пингов
  // подряд. Сбрасываем отметку ДО проверки _probeInFlight, чтобы повторный тик
  // внутри «тихого окна» отсчитывал новое 2с от своего фактического срабатывания.
  const now = Date.now();
  if (now - _lastForcedProbeAt < MIN_FORCED_PROBE_INTERVAL_MS) return;
  _lastForcedProbeAt = now;
  if (_probeTimer !== null) { clearTimeout(_probeTimer); _probeTimer = null; }
  if (_probeInFlight !== null) return;
  void runProbeTick();
}

// Возвращает вердикт «сервер ответил». При успехе флаг online поднимается прямо тут —
// обоим потребителям нужно именно это. А вот ЧТО делать с провалом, решает потребитель:
// для зонда возврата провал не значит ничего, для подтверждающего пинга из request() —
// это санкция уводить в офлайн.
async function probeHealth(): Promise<boolean> {
  const token = typeof localStorage !== 'undefined'
    ? (localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token'))
    : null;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), PING_TIMEOUT_MS);
  try {
    const res = await fetch(BASE + '/health', {
      method: 'GET',
      cache: 'no-store',
      signal: controller.signal,
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    });
    // 502/503/504 от реверс-прокси при мёртвом бэкенде — НЕ «сервер достижим»
    if (res.status === 502 || res.status === 503 || res.status === 504) return false;
    // Любой другой HTTP-ответ = сервер живой → поднимаем флаг.
    setOnline(true);
    return true;
  } catch {
    // Сетевой сбой или таймаут — сам по себе состояние не меняет
    return false;
  } finally {
    clearTimeout(timer);
  }
}

// Один полёт пинга на всех потребителей. Зонд возврата и подтверждение из request()
// живут в разных фазах (offline / online) и наперегонки не бегают, но внутри одной
// фазы поводов много: разморозка вкладки реджектит ВСЕ in-flight запросы разом, и
// каждый из них приходит в catch за подтверждением. Пачка провалов обязана дать
// ОДИН /api/health, поэтому опоздавшие ждут уже летящий пинг и берут его вердикт.
function pingSharedFlight(): Promise<boolean> {
  if (_probeInFlight === null) {
    _probeInFlight = probeHealth().finally(() => { _probeInFlight = null; });
  }
  return _probeInFlight;
}

// Подтверждение разрыва для тех, кто судит о связи по СВОЕМУ каналу: сейчас это
// ветки reject ensureConnected() в signalr.ts. Судья один и тот же, что у request(), —
// молчание /api/health, и решение принимается только о флаге: вызывающий волен
// реджектить свою операцию независимо от вердикта (хаб не поднялся — это его беда,
// даже если REST жив).
export async function confirmOffline(): Promise<void> {
  // Из офлайна подтверждать нечего — мы уже там, а лишний пинг только шумит.
  if (!_online) return;
  if (!await pingSharedFlight()) setOnline(false);
}

async function runProbeTick() {
  _probeTimer = null;
  if (typeof document !== 'undefined' && document.visibilityState === 'hidden') {
    scheduleNextProbe();
    return;
  }
  // Инвариант односторонности: в онлайне зонд не работает.
  if (_online) return;
  if (_probeInFlight !== null) {
    scheduleNextProbe();
    return;
  }
  await pingSharedFlight();
  // Если за время полёта подняли флаг — больше не тикаем
  if (_online) return;
  scheduleNextProbe();
}

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

const FETCH_TIMEOUT_MS = 30_000;

// fetch при сетевом сбое реджектит с TypeError; HTTP-ошибки (4xx/5xx) — это res.ok=false (сервер доступен)
function isNetworkError(e: unknown): boolean {
  return e instanceof TypeError;
}


// --- Запрос ---

// parse: 'blob' — бинарный ответ (mp3 озвучки) отдаётся как Blob, минуя JSON.parse и кэш
// IndexedDB. Ошибочные ветки (401, !res.ok, офлайн, таймаут) общие с JSON-путём: тело ошибки
// сервер шлёт JSON'ом и при бинарном запросе — на полях status/body держится фолбэк озвучки.
//
// live: true — запрос, которому офлайн-кэш ВРЕДЕН: ответ не сохраняется в IndexedDB и не
// достаётся оттуда при обрыве. Нужно там, где значение имеет не только содержимое ответа, но и
// сам факт «сервер сейчас отвечает». Пример — статус выкатки: продукт на время публикации
// гаснет, и подставленный из кэша прошлый ответ превращает «сервер лежит, значит выкатка идёт»
// в «сервер отвечает, ничего не происходит». Окно выкатки на этом различии и построено.
export async function request<T>(url: string, options?: RequestInit & { timeoutMs?: number; parse?: 'json'; live?: boolean }): Promise<T>;
export async function request(url: string, options: RequestInit & { timeoutMs?: number; parse: 'blob'; live?: boolean }): Promise<Blob>;
export async function request<T>(url: string, options?: RequestInit & { timeoutMs?: number; parse?: 'json' | 'blob'; live?: boolean }): Promise<T | Blob> {
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
  const { timeoutMs, parse, live, ...fetchOptions } = options ?? {};
  // Кэшируем и достаём из кэша только те GET, которым это на пользу (см. комментарий к live)
  const useOfflineCache = isGet && !live;
  const controller = new AbortController();
  const effectiveTimeout = timeoutMs ?? FETCH_TIMEOUT_MS;
  // Признак «прервали мы сами по таймауту»: AbortError от нашего таймера неотличим от
  // прочих abort'ов, а причину надо назвать точно (см. RequestTimeoutError)
  let timedOut = false;
  const timeoutId = setTimeout(() => { timedOut = true; controller.abort(); }, effectiveTimeout);

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
      // status прикрепляем, как в ветке !res.ok ниже, — потребители (refreshMe)
      // отличают протухшую сессию от сетевого сбоя: 401 глотать нельзя
      const authErr = new Error(err.error ?? 'Неверный API-ключ') as Error & { status?: number };
      authErr.status = 401;
      throw authErr;
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

    // Бинарный ответ (mp3 озвучки): без JSON.parse и без кэша IndexedDB
    if (parse === 'blob') return await res.blob();

    // Тело может быть пустым (Ok() без контента у мутаций) — не парсим пустую строку как JSON
    const text = res.status === 204 ? '' : await res.text();
    const data = (text ? JSON.parse(text) : undefined) as T;
    if (useOfflineCache) {
      idbSet(url, { data, savedAt: Date.now() }).catch(() => { /* кэш недоступен — не критично */ });
    }
    return data;
  } catch (e) {
    clearTimeout(timeoutId);
    // AbortError от нашего таймаута трактуем как сетевую проблему
    if (isNetworkError(e) || (e instanceof DOMException && e.name === 'AbortError')) {
      // Свой таймаут на заведомо долгом запросе (явный timeoutMs) — не улика против
      // связи: сервер мог просто думать дольше отведённого. Состояние не трогаем,
      // иначе успешная, но медленная операция уводила бы приложение в офлайн.
      const ourTimeout = timedOut && timeoutMs !== undefined;
      if (!ourTimeout) {
        // Одиночный провал fetch — ещё не доказательство разрыва. Планшет размораживает
        // вкладку и реджектит все висевшие в полёте запросы разом при живом сервере и
        // нерванном сокете: прежний немедленный setOnline(false) давал ровно то мигание
        // «Офлайн → через пару секунд снова онлайн», которым заканчивал зонд возврата.
        // Асимметрия была в том, что провал ЗОНДА состояние не менял, а провал одного
        // fetch менял его единолично. Теперь оба канала судят по одному факту — молчанию
        // /api/health, — и REST становится настолько же терпелив, насколько WS
        // (withServerTimeout 60с, onreconnecting — сознательный noop).
        // Мутации по-прежнему блокируются, а IDB-fallback для GET ниже ловят по
        // instanceof OfflineError (taskOutbox/notesOffline) — контракт не меняется.
        // Гард «только из online» и сам пинг — внутри confirmOffline().
        await confirmOffline();
      }
      // Кэш выручает GET независимо от причины обрыва — сначала пробуем его.
      // Кроме live-запросов: там подстановка прошлого ответа выдала бы лежащий сервер за
      // работающий, а вызывающему нужен именно факт обрыва.
      if (useOfflineCache) {
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

  // ОС однозначно потеряла сеть — сразу офлайн
  window.addEventListener('offline', () => setOnline(false));
  // ОС сообщает о возврате сети — сразу онлайн. Проверять пингом не нужно:
  // первый же реальный запрос через request() вернёт либо 200 (мы и так онлайн),
  // либо сетевую ошибку (мы и так уйдём в офлайн). Без пинга возврат из
  // авиарежима сразу поднимет флаг — иначе приложение застряло бы в офлайне
  // до первого успешного запроса.
  window.addEventListener('online', () => setOnline(true));
  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') {
        _lastBecameVisibleAt = Date.now();
        // Возврат на вкладку при офлайне — форсированный тик зонда, не ждём PROBE_INTERVAL_MS
        fireProbeNow();
      }
    });
  }
  // При загрузке уже офлайн (navigator.onLine === false, сервер не доступен) — запускаем зонд
  if (!_online) scheduleNextProbe();
}
