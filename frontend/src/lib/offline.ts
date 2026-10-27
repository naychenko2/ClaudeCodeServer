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
const PASSIVE_RANGE_END = '\uffff';
// Потолок записей на владельца и запас снизу: после ужима до 180 следующая запись прохода
// не вызывает (ужим до самого потолка запускал бы проход на каждой записи).
export const PASSIVE_CACHE_MAX = 200;
export const PASSIVE_CACHE_TRIM_TO = 180;
// Троттлинг прохода: не чаще раза в минуту либо после 100 записей с прошлого прохода.
const EVICT_MIN_INTERVAL_MS = 60_000;
const EVICT_AFTER_WRITES = 100;
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

function ownerPrefix(owner: string): string {
  // encodeURIComponent: «:» в id не должен сливать диапазоны двух владельцев
  return `${PASSIVE_PREFIX}${encodeURIComponent(owner)}:`;
}

// Ключ записи; null — читать/писать нельзя (пассивный запрос без известного владельца)
function passiveKey(url: string, owner: string | null): string | null {
  return owner ? ownerPrefix(owner) + url : null;
}

// Префикс диапазона пассивного кэша текущего владельца; null — владелец неизвестен.
// Для потребителей, которые пишут и читают кэш напрямую (sync.ts, SessionList):
// контракт ключа у них общий с request() — тот же префикс с владельцем.
export function passiveCachePrefix(): string | null {
  const owner = readStoredUserId();
  return owner ? ownerPrefix(owner) : null;
}

export function passiveCacheKey(url: string): string | null {
  const prefix = passiveCachePrefix();
  return prefix === null ? null : prefix + url;
}

// Префикс ключей содержимого файлов проекта: тот же контракт, что у request().
// Читает и чистит их sync.ts (докачка, снятие синхронизации) — по голому URL они
// больше не адресуемы: запись живёт под префиксом владельца.
export function fileContentCachePrefix(projectId: string): string | null {
  const prefix = passiveCachePrefix();
  return prefix === null ? null : `${prefix}/projects/${projectId}/files/content?path=`;
}

// Разбор ключа записи содержимого файла: <владелец>:/projects/<pid>/files/content?path=<путь>[&…].
// Формат ключа живёт здесь — обе стороны (sync.ts читает и чистит эти записи) смотрят
// на один контракт, а не повторяют разбор по-своему.
export function parseFileContentCacheKey(key: string): { projectId: string; path: string } | null {
  const i = key.indexOf('/projects/');
  if (i < 0) return null;
  const rest = key.slice(i + '/projects/'.length);
  const sep = '/files/content?path=';
  const j = rest.indexOf(sep);
  if (j < 0) return null;
  const projectId = rest.slice(0, j);
  if (!projectId) return null;
  const tail = rest.slice(j + sep.length);
  const amp = tail.indexOf('&');
  const raw = amp >= 0 ? tail.slice(0, amp) : tail;
  try { return { projectId, path: decodeURIComponent(raw) }; } catch { return null; }
}

const _usedTouched = new Map<string, number>();
let _lastEvictAt = Date.now();
let _writesSinceEvict = 0;
let _evicting = false;
let _maintainedOwner: string | null = null;

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

async function passiveRead<T>(url: string): Promise<T | undefined> {
  const key = passiveKey(url, readStoredUserId());
  if (key === null) return undefined;
  const cached = await idbGet<T>(key);
  if (!cached) return undefined;
  const now = Date.now();
  if (now - (_usedTouched.get(key) ?? 0) >= USED_AT_REFRESH_MS) {
    if (_usedTouched.size > 1000) _usedTouched.clear();
    _usedTouched.set(key, now);
    // savedAt остаётся прежним: это «данные по состоянию на», а не время обращения
    idbSet(key, { ...cached, usedAt: now }).catch(() => { /* не критично */ });
  }
  return cached.data;
}

// owner — владелец на момент СТАРТА запроса: если к записи вкладка вышла или сменила аккаунт,
// ответ чужой сессии в кэш не ложится.
function passiveWrite(url: string, data: unknown, owner: string | null): void {
  if (!owner) return;
  if (readStoredUserId() !== owner) return;
  const key = passiveKey(url, owner);
  if (key === null) return;
  const now = Date.now();
  idbSet(key, { data, savedAt: now, usedAt: now }).catch(() => { /* кэш недоступен — не критично */ });
  if (_maintainedOwner !== owner) { void runPassiveMaintenance(); return; }
  _writesSinceEvict++;
  if (_writesSinceEvict >= EVICT_AFTER_WRITES || now - _lastEvictAt >= EVICT_MIN_INTERVAL_MS) {
    void evictPassive(owner);
  }
}

// Вытеснение по наименьшему usedAt. Источник правды — сам стор, счётчика в памяти нет.
async function evictPassive(owner: string): Promise<void> {
  if (_evicting) return;
  _evicting = true;
  _lastEvictAt = Date.now();
  _writesSinceEvict = 0;
  try {
    const lo = ownerPrefix(owner);
    const hi = lo + PASSIVE_RANGE_END;
    if (await idbCountRange(lo, hi) <= PASSIVE_CACHE_MAX) return;
    const stamps = await idbRangeStamps(lo, hi);
    // Порядок вытеснения — по времени ДОСТУПА; у старых записей без usedAt это savedAt
    const stamp = (e: { savedAt: number; usedAt?: number }) => e.usedAt ?? e.savedAt;
    stamps.sort((a, b) => stamp(a) - stamp(b));
    const drop = stamps.slice(0, Math.max(0, stamps.length - PASSIVE_CACHE_TRIM_TO));
    await Promise.all(drop.map(s => idbDelete(s.key).catch(() => {})));
    await notifyEvicted(drop.map(s => s.key));
  } catch { /* кэш недоступен — не критично */ } finally {
    _evicting = false;
  }
}

// Проход обслуживания: идемпотентен, на каждом старте и при смене владельца, не под флагом
// (вкладка со старым бандлом продолжает писать ключи без префикса — их надо сносить снова).
export async function runPassiveMaintenance(): Promise<void> {
  const owner = readStoredUserId();
  try {
    const keys = await idbKeys();
    const legacy = keys.filter(isLegacyPassiveKey);
    await Promise.all(legacy.map(k => idbDelete(k).catch(() => {})));
    if (owner) {
      _maintainedOwner = owner;
      const own = ownerPrefix(owner);
      const all = await idbRangeStamps(PASSIVE_PREFIX, PASSIVE_PREFIX + PASSIVE_RANGE_END);
      const foreign = all.filter(s => !s.key.startsWith(own));
      await Promise.all(foreign.map(s => idbDelete(s.key).catch(() => {})));
      // Снятое содержимое (смена владельца, осиротевшие ключи) — его mtime-записи
      // тоже сбрасываются, иначе докачка не вернёт эти файлы.
      await notifyEvicted([...legacy, ...foreign.map(s => s.key)]);
      await evictPassive(owner);
    }
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
export async function request<T>(url: string, options?: RequestInit & { timeoutMs?: number; parse?: 'json'; live?: boolean }): Promise<T>;
export async function request(url: string, options: RequestInit & { timeoutMs?: number; parse: 'blob'; live?: boolean }): Promise<Blob>;
export async function request<T>(url: string, options?: RequestInit & { timeoutMs?: number; parse?: 'json' | 'blob'; live?: boolean }): Promise<T | Blob> {
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
  const { timeoutMs, parse, live, signal: callerSignal, ...fetchOptions } = options ?? {};
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
    if (useOfflineCache) {
      passiveWrite(url, data, ownerAtStart);
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
        const cached = await passiveRead<T>(url).catch(() => undefined);
        if (cached !== undefined) return cached;
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
