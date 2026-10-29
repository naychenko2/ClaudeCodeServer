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

import { idbCountRange, idbDelete, idbGet, idbKeys, idbRangeStamps, idbSet } from './idb';

const BASE = '/api';

// === Безопасное чтение токена из storage ===
//
// cc_token из localStorage/sessionStorage читают ВСЕ места, где формируется
// Authorization (REST-запросы, прямые fetch для FormData, SignalR для WebSocket).
// Раньше каждый зов читал cc_token из двух хранилищ вручную
// и потом тернарник `token ? { Authorization: \`Bearer ${token}\` } : {}` —
// а если в storage лежит строка `"null"`/`"undefined"` или пустая строка
// (например, из-за миграции/внешнего вмешательства), она truthy, и запрос
// уходит с заголовком `Bearer null`. Сервер отвечает 401, а дальше идёт
// цикл cc-unauthorized → чистка storage → снова пустой токен → 401.
//
// Единый хелпер фильтрует мусор: вернёт только валидную непустую строку.
// Невалидное значение приравнивается к отсутствию токена — запрос уходит
// без Authorization, бэк отвечает 401 нормальным потоком, фронт уходит
// на логин без «Bearer null» в логах. Источник мусора в storage не
// разыскиваем (по QA-гипотезе — окно между cc-unauthorized и приходом
// токена после /auth/me; реальный путь может быть и в миграции данных) —
// защищаем чтение на выходе.
//
// Порядок чтения — «сначала своё»: sessionStorage (вход этой вкладки без «запомнить
// меня») раньше localStorage (общий на профиль). Иначе вход другого человека в соседней
// вкладке с «запомнить меня» подменил бы владельца запросов в этой вкладке
// («одна вкладка — один владелец», docs/architecture/plugins.md).
// Каждое хранилище читается отдельно: отсутствие или отказ одного (SecurityError,
// окружение без sessionStorage) не прячет значение из другого.
function readFrom(name: 'sessionStorage' | 'localStorage', key: string): string | null {
  try {
    if (typeof globalThis[name] === 'undefined') return null;
    return globalThis[name].getItem(key) || null;
  } catch {
    // Хранилище отключено (приватный режим, dom.storage.enabled=false)
    return null;
  }
}

function cleanStored(raw: string | null): string | null {
  if (!raw) return null;
  // Строки "null"/"undefined" — частый мусор: где-то в цепочке
  // `setItem(key, null|undefined)` молча сохранил их как строки.
  // Пустая строка сюда не попадёт (отсечена выше), но на всяч — trim().
  const trimmed = raw.trim();
  if (!trimmed || trimmed === 'null' || trimmed === 'undefined') return null;
  return trimmed;
}

// Пара «токен + владелец» читается из ОДНОГО хранилища — того, откуда взят токен
// (sessionStorage первым). Инвариант входа: токен в хранилище ⇒ id в том же хранилище;
// id из другого хранилища чужой и не подхватывается (иначе сверка владельца обманута).
function readStoredPair(): { token: string | null; userId: string | null } {
  for (const name of ['sessionStorage', 'localStorage'] as const) {
    const token = cleanStored(readFrom(name, 'cc_token'));
    if (token) return { token, userId: cleanStored(readFrom(name, 'cc_user_id')) };
  }
  return { token: null, userId: null };
}

export function readStoredToken(): string | null {
  return readStoredPair().token;
}

// Запись токена туда, откуда его читает readStoredToken (sessionStorage первым): режим входа
// вкладки не меняется, а токен чужого владельца в общий localStorage не попадает.
export function writeStoredToken(token: string): void {
  const own = readFrom('sessionStorage', 'cc_token') !== null;
  const shared = readFrom('localStorage', 'cc_token') !== null;
  if (!own && shared) localStorage.setItem('cc_token', token);
  else sessionStorage.setItem('cc_token', token);
  invalidateTokenCache();
}

// Владелец вкладки по хранилищу; пара к readStoredToken, тот же порядок чтения.
export function readStoredUserId(): string | null {
  return readStoredPair().userId;
}

// --- Кэшированный токен (для частых вызовов, напр. avatarUrl) ---
// readStoredToken читает localStorage/sessionStorage на каждый вызов; при тысячах
// аватаров в панели «Задачи» это сотни чтений storage на перерисовку. Кэшируем
// значение в памяти модуля и сбрасываем при смене токена (storage / cc-unauthorized /
// явный вызов invalidateTokenCache при логине/логауте/смене пароля).
let _cachedToken: string | null | undefined = undefined; // undefined = ещё не читали
let _tokenCacheWired = false;

function wireTokenCache(): void {
  if (_tokenCacheWired || typeof window === 'undefined') return;
  _tokenCacheWired = true;
  // Другая вкладка сменила токен (logout/login в соседней вкладке)
  window.addEventListener('storage', e => {
    if (e.key === 'cc_token' || e.key === 'cc_user_id' || e.key === null) _cachedToken = undefined;
  });
  // Токен отозван сервером (401) — сбрасываем кэш
  window.addEventListener('cc-unauthorized', () => { _cachedToken = undefined; });
}

export function cachedToken(): string | null {
  if (_cachedToken === undefined) {
    wireTokenCache();
    _cachedToken = readStoredToken();
  }
  return _cachedToken;
}

// Явный сброс кэша: вызывать после setItem/removeItem('cc_token', …) в этой вкладке
// (LoginPage, App logout, смена пароля) — storage-событие в той же вкладке не срабатывает.
export function invalidateTokenCache(): void {
  _cachedToken = undefined;
}

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

// === Display-состояние (с гистерезисом) ===
//
// Бинарный флаг _online меняется мгновенно по любому признаку (ОС-событие,
// SignalR, успешный ответ), но UI реагирует на «не-онлайн» с задержкой:
// иначе короткий блип (разморозка вкладки на планшете, кратковременный
// провал WS) раскрашивает маркер как аварию, и старый тост «Связь
// восстановлена» вылетал на каждый блип. Маркер у аватарки — пассивный,
// блипы НЕ должны менять интерфейс.
//
// Переходы:
//   online → offline:   3с устойчивой потери → 'unstable' (пунктир warning)
//   unstable → offline: ещё 7с (10с от потери) → 'offline' (grayscale+черта)
//   * → online:         мгновенно, без промежуточных (возврат тихий)
//
// Гистерезис НЕ маскирует реальный разрыв: confirmOffline() уже подтвердил
// провал пингом /api/health, бинарный isOnline() ушёл в false, и зонд
// возврата + signalr onreconnected поднимут его при первой возможности. 3с —
// только пауза перед тем, как начать показывать не-онлайн-вид.
export type ConnectionDisplayState = 'online' | 'unstable' | 'offline';

// Сколько выдерживаем устойчивую потерю связи, прежде чем показывать 'unstable'.
// Короткий блип (разморозка вкладки, кратковременный провал WS) укладывается в
// это окно — и НЕ красит интерфейс.
const OFFLINE_DISPLAY_DELAY_MS = 3_000;
// Дополнительная выдержка в 'unstable' перед переходом в 'offline'. В сумме
// 10с от потери связи. Если в это окно уложился возврат — переход в 'offline'
// отменяется, и пользователь видит только 'unstable' (пунктир) без эскалации.
const UNSTABLE_TO_OFFLINE_DELAY_MS = 7_000;

let _display: ConnectionDisplayState = 'online';
let _unstableTimer: ReturnType<typeof setTimeout> | null = null;
let _offlineTimer: ReturnType<typeof setTimeout> | null = null;
const _displayListeners = new Set<() => void>();

export function getConnectionDisplayState(): ConnectionDisplayState {
  return _display;
}

export function subscribeConnectionDisplayState(fn: () => void): () => void {
  _displayListeners.add(fn);
  return () => { _displayListeners.delete(fn); };
}

function clearUnstableTimer() {
  if (_unstableTimer !== null) { clearTimeout(_unstableTimer); _unstableTimer = null; }
}
function clearOfflineTimer() {
  if (_offlineTimer !== null) { clearTimeout(_offlineTimer); _offlineTimer = null; }
}

function setDisplay(next: ConnectionDisplayState) {
  if (_display === next) return;
  _display = next;
  _displayListeners.forEach(fn => fn());
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

  if (value) {
    // Мгновенный возврат в online: отменяем оба таймера эскалации, маркер
    // снимается сразу. Никаких промежуточных состояний на возврате — это и
    // есть «тихий возврат», который заменяет тост.
    clearUnstableTimer();
    clearOfflineTimer();
    setDisplay('online');
  } else {
    // Уход в offline: держим визуально 'online' ещё OFFLINE_DISPLAY_DELAY_MS,
    // чтобы блип не успел покрасить интерфейс. По истечении — 'unstable', и
    // запускается второй таймер на эскалацию до 'offline'.
    clearUnstableTimer();
    _unstableTimer = setTimeout(() => {
      _unstableTimer = null;
      // Между setOnline(false) и срабатыванием таймера могли вернуться в online:
      // тогда setOnline(true) сбросил _unstableTimer, и мы сюда не попадём.
      // Дополнительная проверка — защита от гонки с явным setDisplay('online').
      if (_online) return;
      setDisplay('unstable');
      clearOfflineTimer();
      _offlineTimer = setTimeout(() => {
        _offlineTimer = null;
        if (_online) return;
        setDisplay('offline');
      }, UNSTABLE_TO_OFFLINE_DELAY_MS);
    }, OFFLINE_DISPLAY_DELAY_MS);
  }
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
  const token = readStoredToken();
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

// Отмена — по имени ошибки, а не по типу: потребитель (featurePage.ts:105) судит по
// e.name === 'AbortError', и внутренняя проверка не должна расходиться с этим контрактом:
// instanceof-гейт отсекал бы Error-подобную отмену (name='AbortError' без типа Error),
// и она прошла бы мимо обеих проверок. Поэтому судим только по имени у объекта.
function isAbortError(e: unknown): boolean {
  return typeof e === 'object' && e !== null && (e as { name?: unknown }).name === 'AbortError';
}


// --- Пассивный GET-кэш IndexedDB ---
// Ключ записи — `offline:<ownerId>:<url>`: владелец — часть структуры ключа, а не дисциплина
// вызова. Читаем строго по своему владельцу и БЕЗ отката на ключ без префикса: запись без
// владельца могла бы отдаться другому аккаунту. Нет владельца (не вошли) — промах.
// Всё вытеснение и чистка работают только по диапазону `offline:`: outbox, tasks:all, индекс
// заметок и продолжения чатов сюда не попадают. Ключи sync-класса (листинги дерева файлов и
// их содержимое, индекс фич, список чатов проекта) тоже живут под префиксом владельца —
// иначе класс содержимого файлов рос бы без потолка до логаута.
const PASSIVE_PREFIX = 'offline:';
// Всё семейство пассивных ключей — общий класс и по-чатовой. Диапазон шире общего
// ('offline:' → 'offlinechat:'), поэтому чужие владельцы сносятся одним курсором.
// Класс содержимого файлов ('offlinefile:') в это семейство НЕ входит: у него свой
// диапазон, свой бюджет по весу и своё вытеснение (см. «Класс содержимого файлов»).
const PASSIVE_FAMILY_LOW = 'offline';
const PASSIVE_RANGE_END = '\uffff';
// Потолок записей на владельца и запас снизу: после ужима до 180 следующая запись прохода
// не вызывает (ужим до самого потолка запускал бы проход на каждой записи).
export const PASSIVE_CACHE_MAX = 200;
export const PASSIVE_CACHE_TRIM_TO = 180;
// Троттлинг прохода: не чаще раза в минуту либо после 100 записей с прошлого прохода.
const EVICT_MIN_INTERVAL_MS = 60_000;
const EVICT_AFTER_WRITES = 100;
// --- Класс ответов заметок ---
//
// Ответы заметок (список, папки, граф и детали каждой заметки) живут в ОТДЕЛЬНОМ
// классе со своим бюджетом: `offlinenotes:<ownerId>:<url>`. Причина не «ещё один
// префикс», а ВЫТЕСНЕНИЕ: прогрев заметок пишет сотни ответов и заканчивается
// ПОСЛЕДНИМ шагом снапшота. Пока он делил бюджет с листингами дерева, в офлайне
// оставалось либо одно, либо другое (замер QA4: 183 заметки и 0 листингов из 33
// проектов) — и решала это гонка записи, а не полезность. Свой класс делает
// выбор предсказуемым: листинги держат общий потолок, ответы заметок — свой.
const NOTES_CLASS_PREFIX = 'offlinenotes:';
// Потолок класса ответов заметок — те же числа, что у общего класса: ответов
// заметок в прогреве столько же, сколько листингов в снапшоте.
export const NOTES_CLASS_MAX_RECORDS = 200;
export const NOTES_CLASS_TRIM_RECORDS = 180;
// Мета-ответы (список заметок, папки, граф, источники): без них офлайн не
// покажет ни списка заметок, ни дерева папок. Они защищены от вытеснения —
// детали заметок уходят первыми: их содержимое и так лежит в локальном слое
// noteContent, который наполняет тот же прогрев.
//
// Защищается ФОРМА, а не точный URL: мета-ответ списка приходит с параметрами
// ('/notes?source=…', '/notes?q=…'), граф — с флагом ('/notes/graph?annotations=true').
// Защита по точному списку оставила бы варианты с параметрами без защиты,
// и офлайн терял бы список заметок раньше деталей.
//
// Детали заметок ('/notes/<id>') под защиту НЕ попадают: их база не совпадает
// ни с одной базой мета-ответов — они уходят первыми, как и задумано.
const NOTES_META_URL_BASES = new Set(['/notes', '/notes/folders', '/notes/graph', '/notes/sources']);

// Базовый путь URL (без query-строки).
function urlBase(url: string): string {
  const i = url.indexOf('?');
  return i < 0 ? url : url.slice(0, i);
}

// Мета-ответ заметок по форме пути: '/notes', '/notes/folders', '/notes/graph',
// '/notes/sources' — с любыми параметрами ('/notes?q=…', '/notes/graph?annotations=true').
function isNotesMetaUrl(url: string): boolean {
  return NOTES_META_URL_BASES.has(urlBase(url));
}
// usedAt обновляем при чтении не чаще раза в 5 минут на ключ (карта живёт в памяти вкладки
// и правдой не является: лишнее обновление безвредно).
const USED_AT_REFRESH_MS = 5 * 60_000;

// Старые пассивные записи (до префикса владельца): ключ — голый URL запроса.
// Теперь ЛЮБОЙ такой ключ осиротел: пассивный кэш живёт только под offline:<owner>:<url>,
// а ключи, которые кто-то читал напрямую (sync.ts — листинги дерева и содержимое файлов,
// SessionList — список чатов, индекс фич), переехали под префикс с владельцем.
// Перечень потребителей конечным быть не может (им писал кэш каждый GET-запрос), поэтому
// правило — по форме ключа, а не по списку: голый URL осиротел. Служебные ключи
// (tasks:*, notes:*, synced-*, chat:continue:*) не начинаются с «/» — остаются нетронутыми.
// Единственные места перечня: разовая чистка tasks.ts и постоянный проход по диапазону.
export function isLegacyPassiveKey(k: string): boolean {
  return k.startsWith('/');
}

// Содержимое файлов прежнего контракта: лежало в общем классе под префиксом владельца
// ('offline:<owner>:/projects/<pid>/files/content?path=…'). После переезда содержимого в
// отдельный класс ('offlinefile:') такие записи осиротели — их больше не читают, и они
// сносятся проходом обслуживания, чтобы не занимать место в общем LRU.
export function isLegacyFileContentKey(k: string): boolean {
  return k.startsWith(PASSIVE_PREFIX) && k.includes('/files/content?path=');
}

function ownerPrefix(owner: string): string {
  // encodeURIComponent: «:» в id не должен сливать диапазоны двух владельцев
  return `${PASSIVE_PREFIX}${encodeURIComponent(owner)}:`;
}

function notesPrefix(owner: string): string {
  return `${NOTES_CLASS_PREFIX}${encodeURIComponent(owner)}:`;
}

// URL заметок — по форме пути: '/notes', '/notes?…', '/notes/…'. Других разделов
// API с таким префиксом нет, поэтому правило однозначно.
function isNotesUrl(url: string): boolean {
  return url === '/notes' || url.startsWith('/notes?') || url.startsWith('/notes/');
}

// --- По-чатовой класс кэша ---
// Ответы, привязанные к конкретному чату (метаданные чата, его история, бейдж расхода,
// статистика локальной модели, git-статус в дереве чата), живут в ОТДЕЛЬНОМ диапазоне:
// `offlinechat:<ownerId>:<chatId>:<url>`. Причина не «ещё один префикс», а два свойства:
//   1) СВОЙ бюджет (потолок записей и веса). По-чатовые ответы не должны вытеснять sync-класс
//      (листинги файлов, содержимое, индекс фич, список чатов) общим правилом вытеснения —
//      иначе по-чатовой вектор растёт вместе с числом открытых чатов и съедает чужое.
//   2) Вытеснение ПО ЧАТУ целиком: уходит самый давно тронутый чат (все его записи), а не
//      отдельная запись — иначе у чата остаётся половина данных, и офлайн он читается
//      неправильно (метаданные от свежего запроса, история от старого).
// Чат — часть структуры ключа, поэтому диапазон не смешивается с общим (`offline:`),
// а вытеснение по-чатовых записей не трогает общий класс и наоборот.
const CHAT_CLASS_PREFIX = 'offlinechat:';
// Потолок по-чатового класса: сколько записей и сколько веса он держит. История чата —
// до 2,5 МБ, поэтому одного счёта записей мало: без потолка по весу два-три тяжёлых чата
// съедали бы весь бюджет офлайна.
export const CHAT_CLASS_MAX_RECORDS = 60;
export const CHAT_CLASS_TRIM_RECORDS = 48;
// Тот же порог — и потолок ОДНОЙ записи (F3-S4b): запись тяжелее не пишется вовсе —
// она одна исчерпала бы весь бюджет класса, а её чтение при загрузке снапшота
// раздувает вкладку. Меняется одной строкой.
export const CHAT_CLASS_MAX_BYTES = 4 * 1024 * 1024;
// Запас до запуска прохода: без него проход шёл бы на каждую запись (оценка после
// ужима равна потолку, значит следующая запись снова его перешагивала бы) и класс
// успевал бы выйти за потолок — записи идут быстрее, чем проходы за ними.
const CHAT_CLASS_TRIGGER_MARGIN = 1.25;

function chatClassPrefix(owner: string): string {
  return `${CHAT_CLASS_PREFIX}${encodeURIComponent(owner)}:`;
}

function chatClassKey(url: string, owner: string, chatId: string): string | null {
  if (!chatId) return null;
  return `${chatClassPrefix(owner)}${encodeURIComponent(chatId)}:${url}`;
}

// Из ключа по-чатового класса — id чата: `offlinechat:<owner>:<chatId>:<url>`.
// Владелец и чат закодированы, поэтому двоеточие внутри них невозможно.
function chatIdOfKey(key: string): string | null {
  if (!key.startsWith(CHAT_CLASS_PREFIX)) return null;
  const rest = key.slice(CHAT_CLASS_PREFIX.length);
  const i = rest.indexOf(':');
  if (i < 0) return null;
  const j = rest.indexOf(':', i + 1);
  if (j < 0) return null;
  try { return decodeURIComponent(rest.slice(i + 1, j)); } catch { return null; }
}

// Ключ записи; null — читать/писать нельзя (пассивный запрос без известного владельца)
// chatScope — id чата: ответ уходит в по-чатовой класс, без него — в класс по URL.
function passiveKey(url: string, owner: string | null, chatScope?: string): string | null {
  if (!owner) return null;
  if (chatScope) return chatClassKey(url, owner, chatScope);
  return rangeOfUrl(url).prefixOf(owner) + url;
}

// Префикс диапазона пассивного кэша текущего владельца; null — владелец неизвестен.
// Для потребителей, которые пишут и читают кэш напрямую (sync.ts, SessionList):
// контракт ключа у них общий с request() — тот же префикс с владельцем.
export function passiveCachePrefix(): string | null {
  const owner = readStoredUserId();
  return owner ? ownerPrefix(owner) : null;
}

export function passiveCacheKey(url: string): string | null {
  const owner = readStoredUserId();
  if (owner === null) return null;
  // Ключ берётся из ТОГО ЖЕ класса, куда пишет request(): иначе потребитель,
  // читающий кэш напрямую, искал бы запись не там, где она лежит.
  return rangeOfUrl(url).prefixOf(owner) + url;
}

// --- Классы пассивного кэша: диапазон, бюджет, вытеснение ---
//
// Межвкладочные счётчики классов публикуются в localStorage под своими
// префиксами: счётчик одного класса — не протухание оценки другого.
const RANGE_EPOCH_PREFIX = 'cc-offline-range:';
//
// Класс — это диапазон ключей со своим потолком и своим проходом вытеснения.
// Классов два с общим бюджетом записей: общий ('offline:') и ответов заметок
// ('offlinenotes:'). Пока класс был один, прогрев заметок и листинги дерева
// делили один потолок, и кто записал последним, тот и вытеснил чужое (замер
// QA4: 183 заметки и 0 листингов из 33 проектов). Разведение по классам
// делает выбор предсказуемым: листинги держат общий потолок, ответы заметок —
// свой. Остальные классы (по-чатовой, содержимого файлов) — как были.
interface PassiveRange {
  id: string;
  prefix: string;            // префикс диапазона класса
  prefixOf: (owner: string) => string;
  max: number;               // потолок записей
  trimTo: number;            // до скольких ужимает проход
  epochPrefix: string;       // префикс межвкладочных счётчиков класса
  protectedUrl?: (url: string) => boolean;   // не вытесняются, пока есть другие
}

const GENERAL_RANGE: PassiveRange = {
  id: 'general',
  prefix: PASSIVE_PREFIX,
  prefixOf: owner => ownerPrefix(owner),
  max: PASSIVE_CACHE_MAX,
  trimTo: PASSIVE_CACHE_TRIM_TO,
  epochPrefix: RANGE_EPOCH_PREFIX,
};

const NOTES_RANGE: PassiveRange = {
  id: 'notes',
  prefix: NOTES_CLASS_PREFIX,
  prefixOf: owner => notesPrefix(owner),
  max: NOTES_CLASS_MAX_RECORDS,
  trimTo: NOTES_CLASS_TRIM_RECORDS,
  epochPrefix: RANGE_EPOCH_PREFIX + 'notes:',
  // Мета-ответы (список, папки, граф, источники) — основа офлайна заметок:
  // без них офлайн не покажет ни списка, ни дерева папок. Детали уходят первыми.
  protectedUrl: url => isNotesMetaUrl(url),
};

// Класс по ключу записи: общий или ответов заметок.
function rangeOfKey(key: string): PassiveRange | null {
  if (key.startsWith(NOTES_CLASS_PREFIX)) return NOTES_RANGE;
  if (key.startsWith(PASSIVE_PREFIX)) return GENERAL_RANGE;
  return null;
}

// Класс по URL — единственная точка правды «кому принадлежит ответ GET».
// Ответы заметок — свой класс, всё остальное — общий.
function rangeOfUrl(url: string): PassiveRange {
  return isNotesUrl(url) ? NOTES_RANGE : GENERAL_RANGE;
}

// Владелец из ключа класса: `offline:<owner>:<url>`. Владелец закодирован,
// поэтому двоеточие внутри него невозможно.
function ownerOfRangeKey(range: PassiveRange, key: string): string | null {
  if (!key.startsWith(range.prefix)) return null;
  const rest = key.slice(range.prefix.length);
  const i = rest.indexOf(':');
  if (i < 0) return null;
  try { return decodeURIComponent(rest.slice(0, i)); } catch { return null; }
}

// URL из ключа класса: хвост после префикса и владельца.
function urlOfRangeKey(range: PassiveRange, key: string): string {
  if (!key.startsWith(range.prefix)) return '';
  const rest = key.slice(range.prefix.length);
  const i = rest.indexOf(':');
  return i < 0 ? '' : rest.slice(i + 1);
}

// Истина по размеру диапазона — стор (счёт по диапазону ключей).
// Сбой стора не должен ронять запись: оценка останется нулевой, кэш просто
// перестанет считать (потолок проверяется на живом сторе).
async function countRange(range: PassiveRange, owner: string): Promise<number> {
  const lo = range.prefixOf(owner);
  try { return await idbCountRange(lo, lo + PASSIVE_RANGE_END); } catch { return 0; }
}

// --- Межвкладочная синхронизация оценки ---
//
// Оценка числа записей диапазона живёт в памяти вкладки, а сам диапазон в сторе
// ОБЩИЙ: записи второй вкладки в оценку первой не попадают, и потолок держался
// бы только внутри одной вкладки (две вкладки — до 2× потолка).
//
// Поэтому каждая вкладка публикует СВОЙ счётчик записанных записей в localStorage
// (хранилище общее для origin и видно СИНХРОННО), а перед записью сверяет чужие
// счётчики: изменились — оценка протухла, и она пересчитывается по стору (истина).
//
// Почему localStorage, а не BroadcastChannel: доставка BroadcastChannel асинхронна,
// между записью соседней вкладки и её уведомлением остаётся окно, в котором
// вторая вкладка пишет по устаревшей оценке. localStorage виден сразу — окна нет.
// Срок жизни чужого счётчика: вкладка, которая молчит дольше, считается закрытой
// (её ключ вычищается при следующей публикации). Живая вкладка публикует счётчик
// на каждой записи, поэтому молчащей она бывает только между сессиями.
const RANGE_EPOCH_TTL_MS = 6 * 60 * 60 * 1000;
// Идентификатор вкладки: на каждый экземпляр модуля свой (две вкладки — два ключа).
const _tabId = Math.random().toString(36).slice(2, 10);
// Свой счётчик записей (публикуется в localStorage) и последняя виденная подпись
// чужих счётчиков.
let _ownEpoch = 0;

function rangeEpochPrefix(range: PassiveRange, owner: string): string {
  return `${range.epochPrefix}${encodeURIComponent(owner)}:`;
}

function epochValue(): string {
  return `${Date.now()}:${++_ownEpoch}`;
}

// Подпись чужих счётчиков: изменилась — оценка диапазона протухла.
function foreignRangeEpoch(range: PassiveRange, owner: string): string {
  try {
    const ls = typeof globalThis.localStorage !== 'undefined' ? globalThis.localStorage : null;
    if (!ls) return '';
    const prefix = rangeEpochPrefix(range, owner);
    const ownKey = prefix + _tabId;
    const parts: string[] = [];
    for (let i = 0; i < ls.length; i++) {
      const k = ls.key(i);
      if (!k || !k.startsWith(prefix) || k === ownKey) continue;
      parts.push(`${k}=${ls.getItem(k)}`);
    }
    return parts.sort().join('|');
  } catch { return ''; }
}

// Опубликовать свой счётчик: соседние вкладки должны узнать, что диапазон изменился.
// Заодно вычищаются ключи закрытых вкладок (иначе их счётчики копятся вечно).
function publishRangeEpoch(range: PassiveRange, owner: string): void {
  try {
    const ls = typeof globalThis.localStorage !== 'undefined' ? globalThis.localStorage : null;
    if (!ls) return;
    const prefix = rangeEpochPrefix(range, owner);
    const now = Date.now();
    for (let i = ls.length - 1; i >= 0; i--) {
      const k = ls.key(i);
      if (!k || !k.startsWith(prefix) || k === prefix + _tabId) continue;
      const stamp = Number(String(ls.getItem(k) ?? '').split(':')[0]);
      if (Number.isFinite(stamp) && now - stamp > RANGE_EPOCH_TTL_MS) ls.removeItem(k);
    }
    ls.setItem(prefix + _tabId, epochValue());
  } catch { /* localStorage недоступен — межвкладочной защиты не будет */ }
}

// --- Оценка размера диапазона (в памяти вкладки) ---
//
// Потолок ограничивал УСТАНОВИВШЕЕСЯ состояние, а не пик: обслуживание тикало от
// записей, прошедших через request(), а листинги дерева (снапшот) писались в IDB
// напрямую и счётчик их не видел — на пике диапазон владельца доходил до 957
// записей при потолке 200.
//
// Теперь счётчиком служит не «число записей, прошедших через request()», а
// ОЦЕНКА ЧИСЛА ЗАПИСЕЙ В ДИАПАЗОНЕ КЛАССА: любая запись в класс — и из
// request(), и из снапшота — попадает в неё. Вытеснение срабатывает ДО
// превышения потолка: место под запись освобождается перед её записью.
//
// Оценка живёт в памяти и обновляется на каждой записи и на каждом проходе
// вытеснения (проход считает диапазон курсором — это истина). Она может
// ПРЕУВЕЛИЧИВАТЬ (перезапись того же ключа тоже её считает) — это безопасно:
// преуменьшать нельзя, тогда диапазон перешагнул бы потолок.
//
// Состояние — на класс и владельца: у каждого класса своя оценка и свой счётчик
// зарезервированных мест.
interface RangeState {
  records: number;    // оценка числа записей диапазона
  countKnown: boolean; // оценка взята из стора (true) или ещё неизвестна (false)
  pending: number;    // зарезервированные, но ещё не записанные места
  noted: number;      // накопительный счётчик записанных записей
}
const _rangeStates = new Map<string, RangeState>();

function rangeState(range: PassiveRange, owner: string): RangeState {
  const key = `${range.id}:${owner}`;
  let st = _rangeStates.get(key);
  if (!st) {
    st = { records: 0, countKnown: false, pending: 0, noted: 0 };
    _rangeStates.set(key, st);
  }
  return st;
}

// Последняя виденная подпись чужих счётчиков — на класс и владельца.
const _foreignEpochs = new Map<string, string>();

// Освободить место под запись: пока в диапазоне класса хотя бы max записей,
// вытеснение приводит его к trimTo. Место освобождается ДО записи — иначе на
// пике диапазон перешагнул бы потолок (между проходами лежало в пять раз
// больше, чем потолок разрешает).
async function ensureRangeBudget(range: PassiveRange, owner: string): Promise<void> {
  const st = rangeState(range, owner);
  // Чужие вкладки пишут в тот же диапазон: их записи в нашу оценку не попадают.
  // Сверяем их счётчики — изменились, значит оценка протухла.
  const foreign = foreignRangeEpoch(range, owner);
  const fKey = `${range.id}:${owner}`;
  if (foreign !== (_foreignEpochs.get(fKey) ?? '')) {
    _foreignEpochs.set(fKey, foreign);
    st.countKnown = false;
  }
  if (!st.countKnown) {
    // Оценки ещё нет — берём истину из стора (плюс зарезервированные места).
    st.records = await countRange(range, owner) + st.pending;
    st.countKnown = true;
  }
  // Пока в диапазоне хотя бы max записей, место освобождается:
  // иначе на пике диапазон перешагнул бы потолок.
  for (let i = 0; i < 3 && st.records >= range.max; i++) {
    await scheduleEvictRange(range, owner, true);
  }
}

// Учёт записи в диапазоне класса. Перезапись того же ключа тоже считается —
// оценка от этого только преувеличивает (безопасно), зато не требует чтения стора.
// Место резервируется ДО записи в стор и снимается после неё.
function noteRangeWrite(range: PassiveRange, owner: string): void {
  const st = rangeState(range, owner);
  st.records++;
  st.pending++;
  st.noted++;
}

function finishRangeWrite(range: PassiveRange, owner: string): void {
  const st = rangeState(range, owner);
  if (st.pending > 0) st.pending--;
}

// Запись в общий класс пассивного кэша мимо request(): листинги дерева снапшота.
// Индекс фич и список чатов сюда НЕ ходят — они пишутся через request().
// Ключ берётся из passiveCacheKey() — он общий с request(). Потолок диапазона
// владельца держится и для этих записей: они идут через тот же учёт, что и
// записи request().
export async function writePassiveRangeEntry(key: string, data: unknown, bytes?: number): Promise<void> {
  const range = rangeOfKey(key);
  if (range === null) return;
  const owner = ownerOfRangeKey(range, key);
  if (owner === null) return;
  // Запись прежнего владельца в диапазон нового не кладём
  if (readStoredUserId() !== owner) return;
  // Место резервируем ДО проверки бюджета: две параллельные записи не должны обе
  // пройти проверку и положиться в диапазон сверх потолка.
  noteRangeWrite(range, owner);
  await ensureRangeBudget(range, owner);
  const now = Date.now();
  await idbSet(key, { data, savedAt: now, usedAt: now, bytes }).catch(() => { /* кэш недоступен — не критично */ });
  finishRangeWrite(range, owner);
  // Соседние вкладки должны узнать, что диапазон изменился (см. publishRangeEpoch).
  publishRangeEpoch(range, owner);
}

// --- Класс содержимого файлов ---
//
// Содержимое файлов проекта живёт ОТДЕЛЬНО от общего GET-кэша: диапазон
// `offlinefile:<ownerId>:<projectId>:<k|f>:<extra>:<path>`. Причины две, и обе
// про вытеснение, а не про «ещё один префикс»:
//   1) СВОЙ бюджет ПО ВЕСУ. Содержимое тяжелее обычного GET в разы, а общий LRU на
//      200 записей не различает «листинг папки» и «файл на 300 КБ»: папка в ~180
//      файлов офлайн не помещалась никогда, и каждый снапшот перекачивал вытесненное,
//      вытесняя соседей.
//   2) Помеченное содержимое не вытесняется содержимым непомеченных файлов. Пометка
//      «k» (файл помечен к офлайн-синхронизации) и «f» (файл просмотрен в обход
//      синхронизации) разведены по бюджетам: у «f» свой небольшой потолок, и
//      вытесняется он первым; «k» вытесняется, только когда помеченного больше, чем
//      в его потолок помещается (иначе класс растёт без предела — а это ровно тот
//      вектор, который фича «OOM на странице» и закрывает).
//
// Формат ключа: `offlinefile:<owner>:<projectId>:<k|f>:<extra>:<path>`. Владелец,
// проект, «extra» и путь закодированы, поэтому двоеточие внутри них невозможно.
// «extra» — хвост запроса после `path=` (например `featureId=…&sessionId=…`): файл,
// прочитанный в дереве чата, — это НЕ тот же файл, что файл проекта, и путать их
// в одном ключе нельзя.
const FILE_CLASS_PREFIX = 'offlinefile:';
// Потолок по весу помеченного содержимого. Число: типичный исходник — 20–50 КБ,
// рабочая папка из 300 файлов — около 10 МБ; 64 МБ — с запасом на несколько папок и
// тяжёлые файлы (картинки, архивы), и при этом существенно меньше квоты IndexedDB
// вкладки (сотни МБ). Меняется одной правкой — см. setFileClassBudget и ADR-036 (И10).
export const FILE_CLASS_MAX_BYTES = 64 * 1024 * 1024;
// Потолок по весу НЕпомеченного содержимого: свой небольшой класс, чтобы просмотренные
// в обход синхронизации файлы не занимали место помеченных. 8 МБ — несколько десятков
// просмотренных файлов; этого хватает, чтобы открытый недавно файл дожил до конца
// рабочей сессии, и мало, чтобы что-то вытеснить.
export const FILE_CLASS_FREE_MAX_BYTES = 8 * 1024 * 1024;
// Потолок по числу записей — страховка от множества мелких файлов (вес тогда мал,
// а записей тысячи, и каждая тянет за собой запись в сторе).
export const FILE_CLASS_MAX_RECORDS = 5000;
// Проход по классу запускается не чаще, чем раз в 100 записей, — иначе курсор по
// диапазону шёл бы на каждую запись (записи идут быстрее, чем проходы за ними).
const FILE_EVICT_AFTER_WRITES = 100;
// Запас до запуска прохода по весу: без него оценка после ужима равна потолку,
// и следующая запись снова его перешагивала бы.
const FILE_CLASS_TRIGGER_MARGIN = 1.25;

let _fileMaxBytes = FILE_CLASS_MAX_BYTES;
let _fileFreeMaxBytes = FILE_CLASS_FREE_MAX_BYTES;
let _fileMaxRecords = FILE_CLASS_MAX_RECORDS;

// Смена потолков класса содержимого файлов. По умолчанию — константы выше; вызывается
// человеком (или тестом), когда нужен другой бюджет.
export function setFileClassBudget(opts: { maxBytes?: number; freeMaxBytes?: number; maxRecords?: number }): void {
  if (opts.maxBytes !== undefined && opts.maxBytes > 0) _fileMaxBytes = opts.maxBytes;
  if (opts.freeMaxBytes !== undefined && opts.freeMaxBytes > 0) _fileFreeMaxBytes = opts.freeMaxBytes;
  if (opts.maxRecords !== undefined && opts.maxRecords > 0) _fileMaxRecords = opts.maxRecords;
}

function fileClassPrefix(owner: string): string {
  return `${FILE_CLASS_PREFIX}${encodeURIComponent(owner)}:`;
}

// Ключ записи содержимого: владелец, проект, пометка, «extra», путь.
function fileContentKeyFor(owner: string, projectId: string, path: string, keep: boolean, extra: string): string {
  return `${fileClassPrefix(owner)}${encodeURIComponent(projectId)}:${keep ? 'k' : 'f'}:${encodeURIComponent(extra)}:${encodeURIComponent(path)}`;
}

// Префикс диапазона содержимого файлов проекта. Читает и чистит их sync.ts
// (докачка, снятие синхронизации) — по голому URL они больше не адресуемы.
export function fileContentCachePrefix(projectId: string): string | null {
  const owner = readStoredUserId();
  return owner === null ? null : `${fileClassPrefix(owner)}${encodeURIComponent(projectId)}:`;
}

// Разбор ключа записи содержимого файла. Формат ключа живёт здесь — обе стороны
// (sync.ts читает и чистит эти записи) смотрят на один контракт, а не повторяют
// разбор по-своему. Старый контракт (содержимое в общем классе 'offline:') тоже
// разбирается: его записи осиротели, но слушатель вытеснения должен сбросить mtime
// у путей, чьё содержимое снесено.
export function parseFileContentCacheKey(key: string): { projectId: string; path: string; keep: boolean; extra: string } | null {
  if (key.startsWith(FILE_CLASS_PREFIX)) {
    const rest = key.slice(FILE_CLASS_PREFIX.length);
    const i1 = rest.indexOf(':');
    const i2 = i1 < 0 ? -1 : rest.indexOf(':', i1 + 1);
    const i3 = i2 < 0 ? -1 : rest.indexOf(':', i2 + 1);
    const i4 = i3 < 0 ? -1 : rest.indexOf(':', i3 + 1);
    if (i4 < 0) return null;
    const kind = rest.slice(i2 + 1, i3);
    if (kind !== 'k' && kind !== 'f') return null;
    try {
      return {
        projectId: decodeURIComponent(rest.slice(i1 + 1, i2)),
        path: decodeURIComponent(rest.slice(i4 + 1)),
        keep: kind === 'k',
        extra: decodeURIComponent(rest.slice(i3 + 1, i4)),
      };
    } catch { return null; }
  }
  const sep = '/files/content?path=';
  const j = key.indexOf(sep);
  if (j < 0) return null;
  const head = key.slice(0, j);
  const pidStart = head.lastIndexOf('/projects/');
  if (pidStart < 0) return null;
  const projectId = head.slice(pidStart + '/projects/'.length);
  if (!projectId) return null;
  const tail = key.slice(j + sep.length);
  const amp = tail.indexOf('&');
  const raw = amp >= 0 ? tail.slice(0, amp) : tail;
  try { return { projectId, path: decodeURIComponent(raw), keep: false, extra: amp >= 0 ? tail.slice(amp + 1) : '' }; } catch { return null; }
}

// Разбор URL содержимого файла: /projects/<pid>/files/content?path=<путь>[&extra…].
// Возвращает проект, путь и хвост запроса (extra).
function parseFileContentUrl(url: string): { projectId: string; path: string; extra: string } | null {
  const m = /^\/projects\/([^/?]+)\/files\/content\?path=([^&]*)(&.*)?$/.exec(url);
  if (!m) return null;
  try {
    return {
      projectId: decodeURIComponent(m[1]),
      path: decodeURIComponent(m[2]),
      extra: m[3] ? m[3].slice(1) : '',
    };
  } catch { return null; }
}

// --- Помеченные к офлайн-синхронизации файлы ---
// Пометка — свойство ФАЙЛА, а не запроса: помеченный файл остаётся помеченным, когда
// его открывают в просмотрщике (тот же URL). Поэтому список помеченных путей живёт
// здесь, а владелец политики (sync.ts) сообщает о нём при каждом изменении.
// Содержимое помеченного файла не вытесняется содержимым непомеченных.
//
// Храним помеченные ФАЙЛЫ и помеченные КАТАЛОГИ отдельно: помеченная папка помечает
// всё вложенное, поэтому «помечен ли файл» решается и по его родителю.
interface KeptPaths {
  files: Set<string>;
  dirs: Set<string>;
}
const _keptFiles = new Map<string, KeptPaths>();
// Проекты, чей список помеченных пришёл от владельца политики (sync.ts): он авторитетен,
// восстановление из стора их не трогает.
const _authoritativeProjects = new Set<string>();
// Пометки восстановлены из стора: после перезагрузки страницы реестр в памяти пуст,
// а помеченные файлы уже лежат в сторе под «k» — без этого открытие такого файла
// переписало бы его как непомеченное и выбило из-под защиты.
let _keptPathsHydrated = false;

function keptOf(projectId: string): KeptPaths {
  return _keptFiles.get(projectId) ?? { files: new Set(), dirs: new Set() };
}

// Заменить список помеченных путей проекта (новые метки синхронизации).
export function setKeptFilePaths(projectId: string, files: Iterable<string>, dirs: Iterable<string> = []): void {
  _keptFiles.set(projectId, { files: new Set(files), dirs: new Set(dirs) });
  _authoritativeProjects.add(projectId);
}

// Пометить путь (человек включил синхронизацию пути/папки).
export function keepFilePath(projectId: string, path: string, isDirectory = false): void {
  const kept = keptOf(projectId);
  if (isDirectory) kept.dirs.add(path);
  else kept.files.add(path);
  _keptFiles.set(projectId, kept);
  _authoritativeProjects.add(projectId);
}

// Снять пометку с пути и всего вложенного (человек выключил синхронизацию).
export function unkeepFilePath(projectId: string, path: string): void {
  const kept = _keptFiles.get(projectId);
  if (!kept) return;
  kept.files.delete(path);
  kept.dirs.delete(path);
  for (const fp of [...kept.files]) {
    if (fp.startsWith(path + '/')) kept.files.delete(fp);
  }
  for (const dp of [...kept.dirs]) {
    if (dp.startsWith(path + '/')) kept.dirs.delete(dp);
  }
  _authoritativeProjects.add(projectId);
}

// Помечено ли содержимое этого файла к офлайн-синхронизации: сам файл помечен или
// помечен каталог, в котором он лежит.
export function isKeptFilePath(projectId: string, path: string): boolean {
  const kept = _keptFiles.get(projectId);
  if (!kept) return false;
  const p = path.replace(/\\/g, '/');
  if (kept.files.has(p)) return true;
  // Родительские каталоги: помеченная папка помечает всё вложенное
  let i = p.lastIndexOf('/');
  while (i > 0) {
    if (kept.dirs.has(p.slice(0, i))) return true;
    i = p.lastIndexOf('/', i - 1);
  }
  return kept.dirs.has('');
}

// Восстановить пометки из стора (однократно): помеченные файлы лежат под «k».
// Проекты, чей список уже пришёл от sync.ts, не трогаем — их данные авторитетны.
async function hydrateKeptPaths(owner: string): Promise<void> {
  if (_keptPathsHydrated) return;
  _keptPathsHydrated = true;
  try {
    const lo = fileClassPrefix(owner);
    const stamps = await idbRangeStamps(lo, lo + PASSIVE_RANGE_END);
    for (const s of stamps) {
      if (!isKeepFileKey(s.key)) continue;
      const parsed = parseFileContentCacheKey(s.key);
      if (!parsed || parsed.extra) continue;
      if (_authoritativeProjects.has(parsed.projectId)) continue;
      const kept = keptOf(parsed.projectId);
      kept.files.add(parsed.path);
      _keptFiles.set(parsed.projectId, kept);
    }
  } catch { /* кэш недоступен — не критично */ }
}

const _usedTouched = new Map<string, number>();
let _lastEvictAt = Date.now();
let _writesSinceEvict = 0;
let _maintainedOwner: string | null = null;

// Оценка числа записей диапазона ОБЩЕГО класса — наружу, чтобы инвариант
// «оценка никогда не меньше истины» можно было проверить (тест гонки) и
// посмотреть в диагностике.
export function rangeRecordsEstimate(): number {
  const owner = readStoredUserId();
  if (!owner) return 0;
  return rangeState(GENERAL_RANGE, owner).records;
}
// Проход по по-чатовому классу идёт по достижении потолка: курсор берёт только
// метки диапазона (десятки записей), а сам проход не на каждую запись.
let _chatEvicting = false;
let _chatScanAgain = false;
// Проход по классу содержимого файлов — тоже не чаще одного за раз.
let _fileScanAgain = false;
// Оценка веса класса в памяти — повод для прохода (истина считается курсором в проходе)
let _chatEstBytes = 0;
let _chatEstRecords = 0;

// Слушатели вытеснения пассивного кэша. Ключи содержимого файлов снимаются вытеснением,
// а их mtime-записи лежат вне диапазона вытеснения — потребитель (sync.ts) по снятым
// ключам сбрасывает mtime вытесненных путей, иначе авто-докачка сочтёт их свежими
// и содержимое пропадёт из офлайна навсегда.
type EvictListener = (keys: string[]) => void | Promise<void>;
const _evictListeners = new Set<EvictListener>();

export function onPassiveEvict(fn: EvictListener): () => void {
  _evictListeners.add(fn);
  return () => { _evictListeners.delete(fn); };
}

async function notifyEvicted(keys: string[]): Promise<void> {
  if (!keys.length) return;
  for (const fn of [..._evictListeners]) {
    try { await fn(keys); } catch { /* сбой слушателя не ломает вытеснение */ }
  }
}

// Обновление usedAt при чтении (для LRU). Вынесено: читают и общий класс, и класс
// содержимого файлов, а правило одно.
function touchUsedAt<T>(key: string, cached: { data: T; savedAt: number; usedAt?: number; bytes?: number }): void {
  const now = Date.now();
  if (now - (_usedTouched.get(key) ?? 0) >= USED_AT_REFRESH_MS) {
    if (_usedTouched.size > 1000) _usedTouched.clear();
    _usedTouched.set(key, now);
    // savedAt остаётся прежним: это «данные по состоянию на», а не время обращения
    idbSet(key, { ...cached, usedAt: now }).catch(() => { /* не критично */ });
  }
}

// Чтение из пассивного кэша. Отдаёт и вес записи (bytes) вместе с данными —
// офлайн-ветка request() сообщает его колбэку onBytes: мера та же, что при записи.
// У записей, написанных до появления поля bytes, веса нет (undefined).
async function passiveRead<T>(url: string, chatScope?: string): Promise<{ data: T; bytes?: number } | undefined> {
  const owner = readStoredUserId();
  // Содержимое файла — в своём классе: сначала помеченная запись («k»), потом
  // непомеченная («f»). Помеченная защищена от вытеснения содержимым непомеченных.
  const fileRef = parseFileContentUrl(url);
  if (fileRef && owner !== null) {
    for (const keep of [true, false]) {
      const key = fileContentKeyFor(owner, fileRef.projectId, fileRef.path, keep, fileRef.extra);
      const cached = await idbGet<T>(key);
      if (!cached) continue;
      touchUsedAt(key, cached);
      return { data: cached.data, bytes: cached.bytes };
    }
    return undefined;
  }
  const key = passiveKey(url, owner, chatScope);
  if (key === null) return undefined;
  const cached = await idbGet<T>(key);
  if (!cached) return undefined;
  touchUsedAt(key, cached);
  return { data: cached.data, bytes: cached.bytes };
}

// owner — владелец на момент СТАРТА запроса: если к записи вкладка вышла или сменила аккаунт,
// ответ чужой сессии в кэш не ложится.
// chatScope — id чата: ответ уходит в по-чатовой класс (свой бюджет и своё вытеснение).
// bytes — оценка веса ответа (по умолчанию неизвестна — считаем нулём).
async function passiveWrite(url: string, data: unknown, owner: string | null, chatScope?: string, bytes?: number): Promise<void> {
  if (!owner) return;
  if (readStoredUserId() !== owner) return;
  const now = Date.now();
  const key = passiveKey(url, owner, chatScope);
  if (key === null) return;
  if (chatScope) {
    // По-чатовой класс: свой бюджет и своё вытеснение (F2c-e) — путь прежний.
    // Запись тяжелее потолка одной записи (F3-S4b) молча пропускается: такая история
    // одна исчерпала бы бюджет класса, а её чтение раздувает вкладку. Вместе с пропуском
    // удаляется прежняя запись под тем же ключом: чат вырос за потолок — старая версия
    // отвечала бы офлайн-чтению на уже устаревший запрос; офлайн-чтение такого чата —
    // miss. Вес неизвестен (bytes нет) — пишем как раньше: отсутствие меры не является
    // поводом пропустить запись.
    if (bytes !== undefined && bytes > CHAT_CLASS_MAX_BYTES) {
      void idbDelete(key).catch(() => {});
      return;
    }
    idbSet(key, { data, savedAt: now, usedAt: now, bytes }).catch(() => { /* кэш недоступен — не критично */ });
    if (_maintainedOwner !== owner) { void runPassiveMaintenance(); return; }
    // Проход по по-чатовому классу запускаем не на каждую запись, а когда накопленный
    // вес класса доходит до потолка: проход — это курсор по диапазону плюс удаления,
    // и на каждой записи он шёл бы позади (коммит записи занимает миллисекунды, а
    // записи идут быстрее) — класс успевал бы выйти за потолка. Счётчик веса в памяти —
    // только повод для прохода; истина (что в сторе) считается курсором в проходе.
    _chatEstBytes += bytes ?? 0;
    _chatEstRecords++;
    if (_chatEstBytes > CHAT_CLASS_MAX_BYTES * CHAT_CLASS_TRIGGER_MARGIN
      || _chatEstRecords > CHAT_CLASS_MAX_RECORDS * CHAT_CLASS_TRIGGER_MARGIN) {
      void evictChatClass(owner);
    }
    return;
  }
  // Общий класс и класс ответов заметок: запись уходит в фоновую очередь —
  // ответ сети не ждёт ни прохода обслуживания, ни записи в стор.
  // Класс берётся из rangeOfUrl() — той же точки правды, что и ключ записи:
  // иначе запись и учёт бюджета разошлись бы по разным ответам предиката.
  const range = rangeOfUrl(url);
  enqueueGeneralWrite(() => writeRangeEntry(range, url, data, owner, bytes, now));
}

// Фоновая очередь записей общего класса.
//
// Зачем очередь, а не просто «void»: записи идут одна за другой, и каждая
// проверяет бюджет диапазона перед записью. Параллельные записи прошли бы проверку
// одновременно (потолок превышался на пике), а очередь даёт две вещи сразу:
// ответ сети не ждёт записи, и потолок держится на пике.
// Порядок ответов сохраняется: более старый ответ не перезапишет более новый.
let _generalWriteQueue: Promise<void> = Promise.resolve();

function enqueueGeneralWrite(run: () => Promise<void>): void {
  _generalWriteQueue = _generalWriteQueue.then(run, run).catch(() => { /* сбой записи не ломает очередь */ });
}

// Дождаться завершения фоновых записей общего класса. Нужен там, где решение
// принимается по содержимому стора (тесты, синхронизация).
export async function flushPassiveWrites(): Promise<void> {
  await _generalWriteQueue.catch(() => { /* кэш недоступен — не критично */ });
}

// Запись в класс пассивного кэша (путь request()).
async function writeRangeEntry(range: PassiveRange, url: string, data: unknown, owner: string, bytes: number | undefined, now: number): Promise<void> {
  // Первый ход после старта или смены владельца: проход обслуживания сносит чужие
  // записи и даёт истину по размеру диапазона (оценка бюджета берётся из неё).
  // Ждём его: иначе первая же запись легла бы поверх того, что он сносит.
  if (_maintainedOwner !== owner) { await runPassiveMaintenance(); }
  if (readStoredUserId() !== owner) return;
  const key = passiveKey(url, owner);
  if (key === null) return;
  // Место под запись: потолок диапазона держится и на пике (между проходами).
  // Резерв делается ДО проверки: параллельные записи не должны обе пройти её.
  noteRangeWrite(range, owner);
  await ensureRangeBudget(range, owner);
  await idbSet(key, { data, savedAt: now, usedAt: now, bytes }).catch(() => { /* кэш недоступен — не критично */ });
  finishRangeWrite(range, owner);
  // Соседние вкладки должны узнать, что диапазон изменился: иначе их оценка
  // устареет и потолок перестанет держаться на общем диапазоне.
  publishRangeEpoch(range, owner);
  _writesSinceEvict++;
  if (_writesSinceEvict >= EVICT_AFTER_WRITES || now - _lastEvictAt >= EVICT_MIN_INTERVAL_MS) {
    void scheduleEvictRange(range, owner, false);
  }
}

// --- Запись и вытеснение класса содержимого файлов ---

// Оценки класса в памяти — повод для прохода; истина (что в сторе) считается курсором.
let _fileEstKeepBytes = 0;
let _fileEstFreeBytes = 0;
let _fileEstRecords = 0;
let _fileWrites = 0;
let _fileEviction: Promise<void> | null = null;

async function writeFileContent(owner: string, ref: { projectId: string; path: string; extra: string }, data: unknown, bytes: number): Promise<void> {
  // Пометка — свойство файла, а не запроса: помеченный файл остаётся помеченным и
  // когда его открывают в просмотрщике. После перезагрузки страницы реестр в памяти
  // пуст — сперва восстанавливаем пометки из стора (помеченное лежит под «k»).
  await hydrateKeptPaths(owner);
  // Пометку НЕЛЬЗЯ понижать: если в сторе уже есть защищённая запись «k», файл
  // помечен (реестр мог ещё не знать о пометке — например, просмотрщик открыл файл
  // раньше, чем пришло дерево проекта). Запись «k» переносим в «f» — файл потерял
  // защиту от вытеснения, хотя человек его пометил.
  //
  // extra ≠ пусто — это файл, прочитанный в дереве ЧАТА (sessionId): другой файл
  // с тем же путём, к офлайн-синхронизации проекта он отношения не имеет.
  const keepKey = fileContentKeyFor(owner, ref.projectId, ref.path, true, ref.extra);
  const keep = ref.extra === ''
    && (isKeptFilePath(ref.projectId, ref.path) || (await idbGet(keepKey)) !== undefined);
  const key = keep ? keepKey : fileContentKeyFor(owner, ref.projectId, ref.path, false, ref.extra);
  const now = Date.now();
  await idbSet(key, { data, savedAt: now, usedAt: now, bytes }).catch(() => { /* кэш недоступен — не критично */ });
  // Запись с противоположной пометкой того же файла устарела: две записи одного файла
  // место занимают впустую, а «f»-версия вытесняла бы «k»-версию из-под защиты.
  await idbDelete(fileContentKeyFor(owner, ref.projectId, ref.path, !keep, ref.extra)).catch(() => {});
  if (keep) _fileEstKeepBytes += bytes; else _fileEstFreeBytes += bytes;
  _fileEstRecords++;
  _fileWrites++;
  if (_fileWrites >= FILE_EVICT_AFTER_WRITES
    || _fileEstKeepBytes > _fileMaxBytes * FILE_CLASS_TRIGGER_MARGIN
    || _fileEstFreeBytes > _fileFreeMaxBytes * FILE_CLASS_TRIGGER_MARGIN
    || _fileEstRecords > _fileMaxRecords * FILE_CLASS_TRIGGER_MARGIN) {
    _fileWrites = 0;
    void scheduleFileEviction(owner);
  }
}

// Пометка «k» в ключе записи содержимого.
function isKeepFileKey(key: string): boolean {
  if (!key.startsWith(FILE_CLASS_PREFIX)) return false;
  const rest = key.slice(FILE_CLASS_PREFIX.length);
  const i1 = rest.indexOf(':');
  const i2 = i1 < 0 ? -1 : rest.indexOf(':', i1 + 1);
  const i3 = i2 < 0 ? -1 : rest.indexOf(':', i2 + 1);
  return i3 >= 0 && rest.slice(i2 + 1, i3) === 'k';
}

// Проход по классу содержимого: не чаще одного за раз (запись, попавшая в класс во
// время прохода, помечает «надо пройти ещё раз»).
function scheduleFileEviction(owner: string): Promise<void> {
  if (_fileEviction) { _fileScanAgain = true; return _fileEviction; }
  _fileEviction = (async () => {
    try {
      do {
        _fileScanAgain = false;
        await evictFileClassOnce(owner);
      } while (_fileScanAgain);
    } finally {
      _fileEviction = null;
      // Сигнал «надо пройти ещё раз» мог прийти в последний момент (между проверкой
      // цикла и завершением) — иначе он потерялся бы, и класс остался бы выше потолка.
      if (_fileScanAgain) void scheduleFileEviction(owner);
    }
  })();
  return _fileEviction;
}

// Дождаться завершения вытеснения класса содержимого. Нужен потребителю, который
// решает по содержимому стора (sync.ts — «лежит ли файл в офлайне»): без ожидания
// решение принималось бы по устаревшему снимку, и mtime встал бы у вытесненного файла.
export async function flushFileEviction(): Promise<void> {
  // Ждём не текущего прогона, а ПОКОЯ: проход, начавшийся после нашего ожидания,
  // тоже должен завершиться, иначе решение снова принято по устаревшему снимку.
  for (let i = 0; i < 10; i++) {
    const p = _fileEviction;
    if (!p) return;
    await p.catch(() => { /* кэш недоступен — не критично */ });
  }
}

// Вытеснение класса содержимого файлов. Порядок: сначала непомеченное («f») — у него
// свой небольшой бюджет, и оно само по себе не вытесняет помеченное; затем помеченное
// («k») — но только когда помеченного больше, чем в его потолок помещается.
//
// Потолки по числу записей считаются у каждой части ОТДЕЛЬНО: иначе множество
// непомеченных записей вытеснило бы помеченное (а это ровно то, чего класс содержимого
// и добивается: помеченное человек держит, непомеченное — гость).
async function evictFileClassOnce(owner: string): Promise<void> {
  try {
    const lo = fileClassPrefix(owner);
    const hi = lo + PASSIVE_RANGE_END;
    const stamps = await idbRangeStamps(lo, hi);
    if (!stamps.length) {
      _fileEstKeepBytes = 0; _fileEstFreeBytes = 0; _fileEstRecords = 0;
      return;
    }
    const stamp = (e: { savedAt: number; usedAt?: number }) => e.usedAt ?? e.savedAt;
    const keep = stamps.filter(s => isKeepFileKey(s.key)).sort((a, b) => stamp(a) - stamp(b));
    const free = stamps.filter(s => !isKeepFileKey(s.key)).sort((a, b) => stamp(a) - stamp(b));
    let keepBytes = 0; for (const s of keep) keepBytes += s.bytes ?? 0;
    let freeBytes = 0; for (const s of free) freeBytes += s.bytes ?? 0;
    let keepRecords = keep.length;
    let freeRecords = free.length;
    const drop: string[] = [];
    for (const s of free) {
      if (freeBytes <= _fileFreeMaxBytes && freeRecords <= _fileMaxRecords) break;
      drop.push(s.key); freeBytes -= s.bytes ?? 0; freeRecords--;
    }
    for (const s of keep) {
      if (keepBytes <= _fileMaxBytes && keepRecords <= _fileMaxRecords) break;
      drop.push(s.key); keepBytes -= s.bytes ?? 0; keepRecords--;
    }
    if (drop.length) {
      await Promise.all(drop.map(k => idbDelete(k).catch(() => {})));
      // Слушатель (sync.ts) по снятым ключам сбросит mtime вытесненных путей.
      await notifyEvicted(drop);
    }
    _fileEstKeepBytes = keepBytes; _fileEstFreeBytes = freeBytes; _fileEstRecords = keepRecords + freeRecords;
  } catch { /* кэш недоступен — не критично */ }
}

// Вытеснение по наименьшему usedAt. Источник правды — сам стор, счётчика в памяти нет.
// force — ужимать, даже когда записей ровно потолок класса: вызывающий так
// освобождает место под следующую запись (потолок не превышается даже на пике).
// Обычный проход (обслуживание, троттлинг) ужимает только при превышении.
async function evictRange(
  range: PassiveRange,
  owner: string,
  force: boolean,
  known?: { key: string; savedAt: number; usedAt?: number; bytes?: number }[],
): Promise<void> {
  _lastEvictAt = Date.now();
  _writesSinceEvict = 0;
  try {
    const st = rangeState(range, owner);
    const lo = range.prefixOf(owner);
    const hi = lo + PASSIVE_RANGE_END;
    // Захват зарезервированных мест на НАЧАЛЕ прохода: к концу прохода он устареет
    // (места займут записи, начатые во время ужима). Поэтому запас под ужим и
    // оценка после прохода берутся из ЖИВОГО счётчика, а не из этого захвата.
    const pending = st.pending;
    // Записи, начатые во время прохода: их вклад в оценку нельзя терять.
    const notedAtStart = st.noted;
    // Истина по размеру диапазона — либо уже снятые метки (known), либо счёт по
    // диапазону: метки дают и размер, и порядок вытеснения сразу.
    const total = known ? known.length : await idbCountRange(lo, hi);
    const during = Math.max(0, st.noted - notedAtStart);
    // Истина по размеру диапазона — оценка бюджета приводится к ней. Зарезервированные
    // (ещё не записанные) места тоже в счёт: они лягут в диапазон сразу после прохода.
    st.records = total + during + pending;
    st.countKnown = true;
    const over = force
      ? st.records >= range.max
      : st.records > range.max;
    if (!over) {
      // Проход ничего не сносил: оценка = истина + места, занятые во время прохода.
      // Записи, начатые в нём, могли уже лечь в стор — без этого слага оценка ушла бы
      // ниже истины, и потолок держался бы хуже, чем должен.
      st.records = total + during + st.pending;
      return;
    }
    const stamps = known ?? await idbRangeStamps(lo, hi);
    // Порядок вытеснения — по времени ДОСТУПА; у старых записей без usedAt это savedAt
    const stamp = (e: { savedAt: number; usedAt?: number }) => e.usedAt ?? e.savedAt;
    stamps.sort((a, b) => stamp(a) - stamp(b));
    // Ужим с запасом на зарезервированные места: иначе они легли бы сверх потолка.
    // Запас — ЖИВОЙ: места могли занять прямо во время прохода.
    const trimTo = Math.max(0, range.trimTo - st.pending);
    const drop = pickEvictable(range, stamps, Math.max(0, stamps.length - trimTo));
    await Promise.all(drop.map(s => idbDelete(s.key).catch(() => {})));
    // Оценка после прохода: записи в сторе минус снесённые плюс места, занятые
    // ПРЯМО СЕЙЧАС. Захват pending в начале прохода устарел бы: запись, начатая
    // во время ужима, выпала бы из оценки, и диапазон оказался бы выше потолка
    // до следующего прохода.
    st.records = Math.max(0, stamps.length - drop.length) + st.pending;
    await notifyEvicted(drop.map(s => s.key));
  } catch { /* кэш недоступен — не критично */ }
}

// Какие записи снести: по порядку доступа (stamps уже отсортированы), но
// защищённые записи класса (мета-ответы заметок: список, папки, граф) уходят
// последними — пока есть что вытеснить обычное, они не трогаются. Иначе офлайн
// потерял бы список заметок, а детали ушли бы первыми.
function pickEvictable(
  range: PassiveRange,
  stamps: { key: string; savedAt: number; usedAt?: number; bytes?: number }[],
  mustDrop: number,
): typeof stamps {
  const drop: typeof stamps = [];
  if (mustDrop <= 0) return drop;
  const isProtected = (s: typeof stamps[number]) =>
    !!range.protectedUrl && range.protectedUrl(urlOfRangeKey(range, s.key));
  for (const s of stamps) {
    if (drop.length >= mustDrop) break;
    if (isProtected(s)) continue;
    drop.push(s);
  }
  // Защищённых не хватило — сносим и их (лучше потерять мета, чем превысить потолок).
  if (drop.length < mustDrop) {
    for (const s of stamps) {
      if (drop.length >= mustDrop) break;
      if (drop.includes(s)) continue;
      drop.push(s);
    }
  }
  return drop;
}

// Проход вытеснения класса — не чаще одного за раз: второй зов ждёт первый.
// Принудительная просьба (освободить место под запись), пришедшая во время
// прохода, не теряется: она встаёт в очередь и проходит следующим проходом.
// Мьютекс — на класс: проходы разных классов не должны ждать друг друга.
const _evictPromises = new Map<string, Promise<void>>();
const _evictForcePending = new Map<string, boolean>();

function scheduleEvictRange(range: PassiveRange, owner: string, force: boolean): Promise<void> {
  if (force) _evictForcePending.set(range.id, true);
  const running = _evictPromises.get(range.id);
  if (running) return running;
  const p = (async () => {
    try {
      const f = _evictForcePending.get(range.id) ?? false;
      _evictForcePending.set(range.id, false);
      await evictRange(range, owner, f);
    } finally {
      _evictPromises.delete(range.id);
      // Принудительная просьба, пришедшая во время прохода, — следующим проходом
      if (_evictForcePending.get(range.id)) void scheduleEvictRange(range, owner, true);
    }
  })();
  _evictPromises.set(range.id, p);
  return p;
}

// Проход обслуживания: идемпотентен, на каждом старте и при смене владельца, не под флагом
// (вкладка со старым бандлом продолжает писать ключи без префикса — их надо сносить снова).
export async function runPassiveMaintenance(): Promise<void> {
  const owner = readStoredUserId();
  try {
    const keys = await idbKeys();
    const legacy = keys.filter(isLegacyPassiveKey);
    await Promise.all(legacy.map(k => idbDelete(k).catch(() => {})));
    // Содержимое файлов прежнего контракта (лежало в общем классе 'offline:') осиротело:
    // его больше не читают, а в общем LRU оно занимало место наравне с листингами.
    const legacyContent = keys.filter(isLegacyFileContentKey);
    await Promise.all(legacyContent.map(k => idbDelete(k).catch(() => {})));
    if (owner) {
      _maintainedOwner = owner;
      const own = ownerPrefix(owner);
      const ownChat = chatClassPrefix(owner);
      const ownNotes = notesPrefix(owner);
      const ownFile = fileClassPrefix(owner);
      // Один курсор на всё семейство пассивных ключей: и общий класс ('offline:'),
      // и по-чатовой ('offlinechat:'), и ответов заметок ('offlinenotes:'). Свои — по
      // своему префиксу, чужие (прежний аккаунт) — сносятся: иначе записи чужого
      // владельца остались бы в сторе.
      const all = await idbRangeStamps(PASSIVE_FAMILY_LOW, PASSIVE_FAMILY_LOW + PASSIVE_RANGE_END);
      const foreign = all.filter(s => !s.key.startsWith(own) && !s.key.startsWith(ownChat)
        && !s.key.startsWith(ownNotes) && !s.key.startsWith(FILE_CLASS_PREFIX));
      await Promise.all(foreign.map(s => idbDelete(s.key).catch(() => {})));
      // Ответы заметок переехали в свой класс ('offlinenotes:') — записи, оставшиеся
      // в общем классе, осиротели: их больше не читают, и в общем бюджете они занимали
      // место наравне с листингами дерева.
      const movedNotes = all.filter(s => s.key.startsWith(own) && isNotesUrl(urlOfRangeKey(GENERAL_RANGE, s.key)));
      await Promise.all(movedNotes.map(s => idbDelete(s.key).catch(() => {})));
      // Класс содержимого файлов — свой диапазон: чужие владельцы сносятся отдельно
      // (общий курсор их не видит — диапазон 'offlinefile:' не входит в семейство
      // пассивных ключей по владельцу).
      const allFiles = await idbRangeStamps(FILE_CLASS_PREFIX, FILE_CLASS_PREFIX + PASSIVE_RANGE_END);
      const foreignFiles = allFiles.filter(s => !s.key.startsWith(ownFile));
      await Promise.all(foreignFiles.map(s => idbDelete(s.key).catch(() => {})));
      // Снятое содержимое (смена владельца, осиротевшие ключи) — его mtime-записи
      // тоже сбрасываются, иначе докачка не вернёт эти файлы.
      await notifyEvicted([...legacy, ...legacyContent, ...foreign.map(s => s.key), ...foreignFiles.map(s => s.key)]);
      await scheduleEvictRange(GENERAL_RANGE, owner, false);
      // Класс ответов заметок: метки берём из того же курсора по семейству
      // пассивных ключей — второй курсор здесь не нужен.
      await evictRange(NOTES_RANGE, owner, false, all.filter(s => s.key.startsWith(ownNotes)));
      await evictFileClassOnce(owner);
      // Метки по-чатового класса берём из того же курсора — второй курсор здесь не нужен.
      await evictChatClass(owner, all.filter(s => s.key.startsWith(ownChat)));
    }
  } catch { /* кэш недоступен — не критично */ }
}

// Вытеснение по-чатового класса: уходит ЦЕЛЫМ чат — самый давно тронутый (по времени
// доступа к его записям). Записи чата связаны между собой (метаданные, история, бейдж),
// поэтому выкидывать их по одной нельзя: у чата осталась бы половина данных.
// Потолки — и по числу записей, и по весу (история чата — до 2,5 МБ).
//
// Проход идёт не чаще одного за раз: запись, попавшая в класс во время прохода,
// помечает «надо пройти ещё раз» — иначе последняя запись осталась бы вне потолка.
async function evictChatClass(owner: string, known?: { key: string; savedAt: number; usedAt?: number; bytes?: number }[]): Promise<void> {
  if (_chatEvicting) { _chatScanAgain = true; return; }
  _chatEvicting = true;
  try {
    do {
      _chatScanAgain = false;
      await evictChatClassOnce(owner, known);
      known = undefined; // после первого прохода метки устарели — берём свежие
    } while (_chatScanAgain);
  } finally {
    _chatEvicting = false;
  }
}

async function evictChatClassOnce(owner: string, known?: { key: string; savedAt: number; usedAt?: number; bytes?: number }[]): Promise<void> {
  try {
    const lo = chatClassPrefix(owner);
    const hi = lo + PASSIVE_RANGE_END;
    const stamps = known ?? await idbRangeStamps(lo, hi);
    if (!stamps.length) { _chatEstBytes = 0; _chatEstRecords = 0; return; }
    let totalBytes = 0;
    for (const s of stamps) totalBytes += s.bytes ?? 0;
    const overRecords = stamps.length > CHAT_CLASS_MAX_RECORDS;
    const overBytes = totalBytes > CHAT_CLASS_MAX_BYTES;
    if (!overRecords && !overBytes) {
      // Потолок не превышен — оценка в памяти пересчитывается по истине (стору)
      _chatEstBytes = totalBytes;
      _chatEstRecords = stamps.length;
      return;
    }

    // Группируем записи по чату; время чата — самое свежее из его записей
    const byChat = new Map<string, { keys: string[]; bytes: number; lastUsed: number }>();
    for (const s of stamps) {
      const chatId = chatIdOfKey(s.key);
      if (chatId === null) continue;
      const used = s.usedAt ?? s.savedAt;
      const g = byChat.get(chatId) ?? { keys: [], bytes: 0, lastUsed: used };
      g.keys.push(s.key);
      g.bytes += s.bytes ?? 0;
      if (used > g.lastUsed) g.lastUsed = used;
      byChat.set(chatId, g);
    }
    const chats = [...byChat.values()].sort((a, b) => a.lastUsed - b.lastUsed);
    const drop: string[] = [];
    let records = stamps.length;
    for (let i = 0; i < chats.length; i++) {
      if (records <= CHAT_CLASS_TRIM_RECORDS && totalBytes <= CHAT_CLASS_MAX_BYTES) break;
      // Последний чат не вытесняем: только что открытый чат должен остаться офлайн
      if (i === chats.length - 1) break;
      const c = chats[i];
      drop.push(...c.keys);
      records -= c.keys.length;
      totalBytes -= c.bytes;
    }
    if (!drop.length) {
      _chatEstBytes = totalBytes;
      _chatEstRecords = records;
      return;
    }
    await Promise.all(drop.map(k => idbDelete(k).catch(() => {})));
    await notifyEvicted(drop);
    // Оценка в памяти — по истине после вытеснения
    _chatEstBytes = totalBytes;
    _chatEstRecords = records;
  } catch { /* кэш недоступен — не критично */ }
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
//
// onBytes — необязательный колбэк веса ответа. Мера одна на обеих ветках: онлайн —
// длина текста ответа (то же число, что пишется в bytes пассивной записи), офлайн-фолбэк —
// bytes сохранённой записи (запись без этого поля — без веса, колбэк не зовётся).
// Второй меры не заводим: оценка вызывающего и учёт кэша обязаны сходиться.
// Для бинарного ответа ('blob') колбэк не зовётся: меры текста у него нет.
export async function request<T>(url: string, options?: RequestInit & { timeoutMs?: number; parse?: 'json'; live?: boolean; chatScope?: string; onBytes?: (n: number) => void }): Promise<T>;
export async function request(url: string, options: RequestInit & { timeoutMs?: number; parse: 'blob'; live?: boolean; chatScope?: string; onBytes?: (n: number) => void }): Promise<Blob>;
export async function request<T>(url: string, options?: RequestInit & { timeoutMs?: number; parse?: 'json' | 'blob'; live?: boolean; chatScope?: string; onBytes?: (n: number) => void }): Promise<T | Blob> {
  const method = (options?.method ?? 'GET').toUpperCase();
  const isGet = method === 'GET';

  // Мутации офлайн запрещены
  if (!isGet && !_online) {
    throw new OfflineError();
  }

  const token = readStoredToken();
  const ownerAtStart = readStoredUserId();

  // AbortController для таймаута: если сеть «зависла» (пакеты идут, но ответа нет),
  // мы не ждём браузерного TCP-таймаута (может быть минуты).
  // timeoutMs — оверрайд для заведомо долгих запросов (AI-генерация и т.п.)
  const { timeoutMs, parse, live, chatScope, onBytes, signal: callerSignal, ...fetchOptions } = options ?? {};
  // Кэшируем и достаём из кэша только те GET, которым это на пользу (см. комментарий к live)
  const useOfflineCache = isGet && !live;
  const controller = new AbortController();
  const effectiveTimeout = timeoutMs ?? FETCH_TIMEOUT_MS;
  // Признак «прервали мы сами по таймауту»: AbortError от нашего таймера неотличим от
  // прочих abort'ов, а причину надо назвать точно (см. RequestTimeoutError)
  let timedOut = false;
  const timeoutId = setTimeout(() => { timedOut = true; controller.abort(); }, effectiveTimeout);
  // Signal вызывающего («Остановить» в шапке резюме, смена fid) не перетирается внутренним
  // контроллером, а объединяется с ним: любой из двух abort'ов обрывает fetch.
  // Отметка «отменил вызывающий» нужна, чтобы его отмена не трактовалась как пропажа
  // связи — это не сетевой сбой, а намеренная отмена (её узнают featurePage и FeatureAnalysisLine).
  let abortedByCaller = false;
  const onCallerAbort = () => { abortedByCaller = true; controller.abort(); };
  if (callerSignal) {
    if (callerSignal.aborted) {
      abortedByCaller = true;
      controller.abort();
    } else {
      callerSignal.addEventListener('abort', onCallerAbort);
    }
  }

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
    // Вес ответа наружу — той же мерой, что идёт в кэш (text.length): второй меры нет,
    // иначе оценка вызывающего разошлась бы с учётом кэша.
    if (onBytes) onBytes(text.length);
    if (useOfflineCache) {
      // Вес ответа — по длине текста: оценка снизу для кириллицы (2 байта на символ),
      // потолок берём с запасом. Точный размер нужен только чтобы понять, что класс
      // по-чатовых записей пора ужимать.
      //
      // Содержимое файла — отдельный класс (свой бюджет по весу): его запись ЖДЁМ:
      // от ответа зависит, что человек увидит офлайн, и пометка «помечен/не помечен»
      // выбирается по восстановленному из стора реестру.
      const fileRef = parseFileContentUrl(url);
      if (fileRef && ownerAtStart && readStoredUserId() === ownerAtStart) {
        await writeFileContent(ownerAtStart, fileRef, data, text.length);
      } else {
        // Общий класс: запись в кэш уходит в фон — ответ сети не ждёт ни прохода
        // обслуживания, ни записи в стор. Порядок «первая запись после смены
        // владельца не ложится поверх сносимого» держит сама очередь записи.
        void passiveWrite(url, data, ownerAtStart, chatScope, text.length);
      }
    }
    return data;
  } catch (e) {
    clearTimeout(timeoutId);
    // Отмена вызывающего — не пропажа связи и не наш таймаут: AbortError отдаём как есть,
    // кэш не подставляем (устаревший ответ под новой фичей — хуже, чем пустота).
    if (isAbortError(e) && abortedByCaller) {
      throw e;
    }
    // AbortError от нашего таймаута трактуем как сетевую проблему
    if (isNetworkError(e) || isAbortError(e)) {
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
      // Кэш выручает GET при сетевом обрыве или нашем таймауте — сначала пробуем его.
      // Отмена вызывающим возвращена выше как есть — IDB-фолбэка для неё нет.
      // Кроме live-запросов: там подстановка прошлого ответа выдала бы лежащий сервер за
      // работающий, а вызывающему нужен именно факт обрыва.
      if (useOfflineCache) {
        const hit = await passiveRead<T>(url, chatScope).catch(() => undefined);
        if (hit !== undefined) {
          // Вес из кэша — тот же text.length, что был записан в запись; у записи
          // без bytes (написанной до этого поля) веса нет, колбэк не зовётся.
          if (hit.bytes !== undefined) onBytes?.(hit.bytes);
          return hit.data;
        }
      }
      if (ourTimeout) throw new RequestTimeoutError(effectiveTimeout);
      throw new OfflineError(isGet ? 'Нет сохранённых данных для офлайн-доступа' : undefined);
    }
    throw e; // HTTP-ошибка или прочее — пробрасываем как есть
  } finally {
    // Сигнал вызывающего может жить дольше запроса (тот же signal передают в следующий
    // вызов): снимаем слушателя, чтобы они не накапливались на нём.
    if (callerSignal) callerSignal.removeEventListener('abort', onCallerAbort);
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
        // Возврат на вкладку при офлайне — форсированный тик зонда, не ждём PROBE_INTERVAL_MS
        fireProbeNow();
      }
    });
  }
  // При загрузке уже офлайн (navigator.onLine === false, сервер не доступен) — запускаем зонд
  if (!_online) scheduleNextProbe();
  void runPassiveMaintenance();
}
