// Стор «Стены»: набор чатов (порядок = порядок монет рельсы), фокус, live-статусы.
// Модульное состояние + подписки + useSyncExternalStore — паттерн useSession/featureFlags.
//
// Источник правды состава — бэкенд (User.WallChatIds, /api/me/wall): состав переживает
// перезагрузку и переезд между машинами, а мёртвые id сервер фильтрует лениво.
// Мутации применяются локально сразу (отзывчивость) и с дебаунсом уходят PUT'ом;
// ответ сервера (после его чистки) применяется обратно.
//
// Live-статусы: проектные сессии шлют status_changed в группу project_{id},
// внепроектные — в user_{ownerId} (BroadcastStatusChangeAsync). Поэтому стена
// вступает в JoinProject по КАЖДОМУ проекту набора + JoinUser. Групп не покидаем
// при смене состава (v1): лишние отвалятся вместе с соединением, а leave мог бы
// оборвать группу, которую держит другой экран. После реконнекта SignalR группы
// теряются — re-join + перезапрос снимка (паттерн WorkspacePage).
import { useSyncExternalStore } from 'react';
import type { Project, Session, ServerMessage } from '../../types';
import { api } from '../../lib/api';
import { joinProject, joinUser, onMessage, onReconnected } from '../../lib/signalr';

// Потолок видимых колонок: дальше это видеостена, а не работа; ширина всё равно
// раньше упрётся в MIN_COL. Сервер держит свой потолок набора (24) — это про монеты.
export const MAX_SLOTS = 5;
// Потолок набора: столько же, сколько колонок влезает на самый широкий экран.
// Больше — это уже не работа, а видеостена: каждый чат может вести свой ход.
export const MAX_CHATS = 5;
// Минимальная ширина колонки: уже — лента чата перестаёт читаться
export const MIN_COL = 420;

export interface WallState {
  loaded: boolean;
  // Чаты набора в порядке монет (полные Session из GET /api/me/wall)
  chats: Session[];
  // Проекты владельца: колонке нужны rootPath (панели сессии) и сам Project
  // (история проектного чата, ProjectIcon); Session несёт только projectId
  projects: Map<string, Project>;
  // Фокусная колонка (id чата); не персистится
  focusId: string | null;
  // Live-статусы поверх снимка: status_changed всегда СИЛЬНЕЕ Session.status из
  // GET/PUT-ответов (ответ дебаунс-PUT мог приехать позже более свежего события)
  statuses: Map<string, string>;
}

let _state: WallState = { loaded: false, chats: [], projects: new Map(), focusId: null, statuses: new Map() };
const _listeners = new Set<() => void>();

function setState(patch: Partial<WallState>) {
  _state = { ..._state, ...patch };
  _listeners.forEach(fn => fn());
}

export function getWallState(): WallState { return _state; }

export function subscribeWall(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

export function useWallState(): WallState {
  return useSyncExternalStore(subscribeWall, getWallState, getWallState);
}

// Эффективный статус чата: live-событие сильнее снимка
export function chatStatus(s: Session): string {
  return _state.statuses.get(s.id) ?? s.status;
}

// Сколько колонок влезает: ширина окна минус обе рельсы и зазоры, по MIN_COL на колонку.
// Формула намеренно грубая (рельсы ~ по 60px с зазорами) — точность тут не важна,
// важна монотонность и границы 1..MAX_SLOTS.
export function slotCount(windowWidth: number): number {
  const usable = windowWidth - 2 * 60;
  return Math.max(1, Math.min(MAX_SLOTS, Math.floor(usable / MIN_COL)));
}

// --- Синхронизация с бэком ---

let _putTimer: ReturnType<typeof setTimeout> | null = null;
// PUT, ушедший на сервер и ещё не ответивший: вместе с таймером образует понятие
// «незавершённая мутация» — пока она есть, refresh не смеет перетирать состав
let _putInFlight = false;
// Монотонный номер мутации состава: ответ PUT применяется, только если с момента его
// старта не было НИ новой мутации, ни свежего refresh — иначе два PUT в полёте
// (латентность > дебаунса) перетирали бы друг друга не в порядке отправки.
let _seq = 0;

// Есть ли незавершённая локальная мутация (дебаунс ждёт ИЛИ PUT в полёте)
function mutationPending(): boolean {
  return _putTimer !== null || _putInFlight;
}

// Дебаунс-запись состава. Ответ сервера (итог после дедупа/отброса чужих/потолка)
// применяем ТОЛЬКО к составу — статусы живут своей жизнью (live сильнее).
function schedulePut() {
  _seq++;
  if (_putTimer !== null) clearTimeout(_putTimer);
  _putTimer = setTimeout(() => {
    _putTimer = null;
    const mySeq = _seq;
    const ids = _state.chats.map(c => c.id);
    _putInFlight = true;
    api.wall.put(ids)
      .then(({ chats }) => {
        if (_seq === mySeq) setState({ chats });
      })
      .catch(() => { /* офлайн/гонка — состав доедет со следующим PUT или GET */ })
      .finally(() => { _putInFlight = false; });
  }, 500);
}

// Вступление в группы статусов. БЕЗ кеша «уже вступили»: join идемпотентен и дёшев,
// а кеш ломался о чужой leaveProject — воркспейс при размонтировании покидает группу
// своего проекта, и стена после zoom-возврата оставалась глухой до реконнекта.
async function joinGroups(ownerId: string | undefined) {
  const pids = new Set(_state.chats.map(c => c.projectId).filter((p): p is string => !!p));
  for (const pid of pids) {
    try { await joinProject(pid); } catch { /* офлайн — реконнект добъёт */ }
  }
  if (ownerId) {
    try { await joinUser(ownerId); } catch { /* офлайн — реконнект добъёт */ }
  }
}

let _wired = false;
let _ownerId: string | undefined;
let _unsubReconnect: (() => void) | null = null;

// Одноразовая проводка событий. Живёт на модуле: стена — единственный потребитель,
// а события статусов дешёвые (фильтр по набору).
function ensureWired() {
  if (_wired) return;
  _wired = true;

  onMessage((msg: ServerMessage) => {
    if (msg.type !== 'status_changed' || !msg.sessionId) return;
    if (!_state.chats.some(c => c.id === msg.sessionId)) return;
    const statuses = new Map(_state.statuses);
    statuses.set(msg.sessionId, msg.status);
    setState({ statuses });
  });

  // SignalR-группы живут на сервере и теряются при разрыве: re-join всех групп +
  // перезапрос снимка (заодно доезжают статусы, сменившиеся за время разрыва —
  // сервер не шлёт status_changed при рестарте)
  _unsubReconnect = onReconnected(() => { void refresh(); });
}

// Загрузка/перезагрузка снимка стены + проектов + вступление в группы
export async function refresh(): Promise<void> {
  try {
    const [{ chats: server }, projects] = await Promise.all([api.wall.get(), api.projects.list()]);
    const map = new Map(projects.map(p => [p.id, p]));
    // Незавершённая локальная мутация (дроп на док → мгновенный вход на стену:
    // дебаунс-PUT ещё не улетел ИЛИ уже в полёте) СИЛЬНЕЕ серверного снимка —
    // иначе refresh откатывал бы только что добавленный чат.
    const dirty = mutationPending();
    const chats = dirty ? _state.chats : server;
    if (!dirty) _seq++; // свежий применённый снимок отменяет ответы PUT'ов в полёте
    setState({
      loaded: true,
      chats,
      projects: map,
      // Фокус чинится, если его чат ушёл из набора
      focusId: _state.focusId && chats.some(c => c.id === _state.focusId) ? _state.focusId : (chats[0]?.id ?? null),
    });
    void joinGroups(_ownerId);
  } catch {
    // Офлайн/ошибка: уже показанный состав НЕ стираем — пустой экран читался бы как
    // «стену снесли». loaded ставим только чтобы уйти с вечного «ничего» при старте.
    if (!_state.loaded) setState({ loaded: true });
  }
}

// Вход на экран: провести события, запомнить владельца (для JoinUser), загрузить снимок.
// undefined НЕ затирает уже известного владельца — док воркспейса зовёт initWall без
// него, и обнуление лишало бы JoinUser (статусы внепроектных чатов) до захода на стену.
export function initWall(ownerId: string | undefined): void {
  _ownerId = ownerId ?? _ownerId;
  ensureWired();
  void refresh();
}

// --- Мутации состава (локально сразу + дебаунс-PUT) ---

// Исход добавления: чат встал на стену / он там уже был / набор полон.
// Три состояния, а не boolean: «уже на стене» и «мест нет» — разные новости,
// и одинаковый тост про переполнение на дубле откровенно врал.
export type AddChatResult = 'added' | 'duplicate' | 'full';

export function addChat(s: Session): AddChatResult {
  if (_state.chats.some(c => c.id === s.id)) return 'duplicate';
  if (_state.chats.length >= MAX_CHATS) return 'full';
  setState({ chats: [..._state.chats, s], focusId: s.id });
  void joinGroups(_ownerId);
  schedulePut();
  return 'added';
}

// Добавление ИЗВНЕ стены (док воркспейса, пункт меню чата): состав мог быть ещё не
// загружен, а PUT шлёт ПОЛНЫЙ список — без снимка дроп затёр бы существующие монеты.
export async function addChatSafe(s: Session): Promise<AddChatResult> {
  if (!_state.loaded) await refresh();
  return addChat(s);
}

export function removeChat(id: string): void {
  const chats = _state.chats.filter(c => c.id !== id);
  setState({
    chats,
    focusId: _state.focusId === id ? (chats[0]?.id ?? null) : _state.focusId,
  });
  schedulePut();
}

// --- Перетаскивание для смены порядка (общее для монет рельсы и колонок) ---
// Один протокол на оба места: тянуть можно и монету, и саму колонку, бросать —
// на монету или на колонку. В dataTransfer кладём индекс позиции в наборе.
export const WALL_ORDER_TYPE = 'cc-wall-order';

// Что делает дроп: 'move' — перестановка (колонку тащат за ярлык), 'swap' — обмен
// местами (кнопку чата, не влезшего на экран, роняют на конкретную колонку, чтобы
// выбрать, какую именно он заменит).
export type OrderDragMode = 'move' | 'swap';

export function startOrderDrag(e: React.DragEvent, index: number, mode: OrderDragMode = 'move'): void {
  e.dataTransfer.setData(WALL_ORDER_TYPE, JSON.stringify({ index, mode }));
  e.dataTransfer.effectAllowed = 'move';
}

// Тащат именно перестановку (а не чат из списка чатов на док стены)
export function isOrderDrag(e: React.DragEvent): boolean {
  return e.dataTransfer.types.includes(WALL_ORDER_TYPE);
}

// Дроп на позицию index: переставляет набор («move») либо меняет местами («swap»).
// Возвращает true, если что-то сделал
export function dropOrder(e: React.DragEvent, index: number): boolean {
  const raw = e.dataTransfer.getData(WALL_ORDER_TYPE);
  if (!raw) return false;
  e.preventDefault();
  let from: number, mode: OrderDragMode;
  try {
    const d = JSON.parse(raw) as { index: number; mode: OrderDragMode };
    from = d.index; mode = d.mode;
  } catch { return false; }
  if (!Number.isInteger(from) || from === index) return false;
  if (mode === 'swap') swapChats(from, index); else reorderChat(from, index);
  return true;
}

// Обмен местами двух позиций набора: так чат, не влезший на экран, встаёт вместо
// выбранной колонки (а та уезжает на его место — за пределы видимой части)
export function swapChats(a: number, b: number): void {
  const n = _state.chats.length;
  if (a === b || a < 0 || b < 0 || a >= n || b >= n) return;
  const chats = [..._state.chats];
  [chats[a], chats[b]] = [chats[b], chats[a]];
  setState({ chats, focusId: chats[b].id });
  schedulePut();
}

// Перестановка (drag-sort): from/to — индексы в наборе
export function reorderChat(from: number, to: number): void {
  if (from === to || from < 0 || to < 0 || from >= _state.chats.length || to >= _state.chats.length) return;
  const chats = [..._state.chats];
  const [moved] = chats.splice(from, 1);
  chats.splice(to, 0, moved);
  setState({ chats });
  schedulePut();
}

// Клик по приглушённой монете (чат вне экрана): обмен с ПОСЛЕДНЕЙ видимой колонкой
// + фокус. Обмен, а не сдвиг: остальные колонки не должны перескакивать.
export function moveToVisible(id: string, slots: number): void {
  const idx = _state.chats.findIndex(c => c.id === id);
  if (idx < 0) return;
  if (idx < slots) { setState({ focusId: id }); return; } // уже на экране — просто фокус
  const chats = [..._state.chats];
  const lastVisible = slots - 1;
  [chats[lastVisible], chats[idx]] = [chats[idx], chats[lastVisible]];
  setState({ chats, focusId: id });
  schedulePut();
}

export function focusChat(id: string): void {
  if (_state.focusId !== id && _state.chats.some(c => c.id === id)) setState({ focusId: id });
}

// Обновление Session после серверной мутации из колонки (смена модели/режима/цикла…):
// ChatPanel зовёт onSessionUpdated, и без применения сюда снимок стора остаётся старым —
// композер после ре-рендера показывал бы прежние настройки. Состав не меняется, PUT не нужен.
// Обновление проекта в сторе (правки из диалога настроек): имя, иконка,
// toolsEnabled фокусной колонки должны примениться без перезагрузки экрана
export function updateProject(p: Project): void {
  if (!_state.projects.has(p.id)) return;
  const projects = new Map(_state.projects);
  projects.set(p.id, p);
  setState({ projects });
}

export function updateChat(s: Session): void {
  if (!_state.chats.some(c => c.id === s.id)) return;
  setState({ chats: _state.chats.map(c => (c.id === s.id ? s : c)) });
}

// Для тестов: полный сброс модульного состояния
export function __resetWallForTests(): void {
  if (_putTimer !== null) { clearTimeout(_putTimer); _putTimer = null; }
  _putInFlight = false;
  _seq = 0;
  _state = { loaded: false, chats: [], projects: new Map(), focusId: null, statuses: new Map() };
  _ownerId = undefined;
  _unsubReconnect?.();
  _unsubReconnect = null;
  _wired = false;
}
