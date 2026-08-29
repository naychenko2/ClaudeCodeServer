import { useSyncExternalStore } from 'react';
import { api } from './api';
import { onMessage, onReconnected } from './signalr';

// Чаты, в которых прямо сейчас идёт ФОНОВАЯ работа — двух разных видов:
//   • агенты (Agent run_in_background / Workflow) — карточка светится и показывает робота;
//   • фоновая команда (Bash с run_in_background: дев-сервер, watch) — своя пометка значком
//     терминала. Она живёт часами и о завершении не сообщает, так что «агенты работают» на
//     ней врало бы, а молчание скрывало бы причину, по которой чат держит живой процесс CLI.
//
// Виды РАЗЛИЧАЮТСЯ только значком: подсветка (перелив плитки чата, точки рельсы проектов и
// стены, счётчики свёрнутых веток) у обоих одна — с точки зрения человека это один вопрос
// «идёт ли в чате работа прямо сейчас», и отвечает на него bgWork, объединение обоих видов.
//
// Зачем отдельный стор, а не поле статуса сессии: пока фон работает, ход чата уже завершён
// и статус сессии — Active, у которого нет ни свечения, ни движения. Снаружи такой чат
// выглядел остывшим, хотя внутри процесса CLI идёт работа. Признак живёт ровно столько,
// сколько живёт процесс, поэтому в sessions.json ему делать нечего.
//
// Источник — событие bg_agents_presence (приходит ТОЛЬКО на смене состояния) плюс снимок
// GET /api/chats/agents-presence: открывший список уже после старта агентов иначе не узнал
// бы о них до самого конца работы. Снимок снимается при первом подписчике и на переподключении.

let _ids: ReadonlySet<string> = new Set();
let _commandIds: ReadonlySet<string> = new Set();
// Объединение обоих видов — «в чате идёт фоновая работа». Держим отдельным полем, а не
// считаем на каждый вызов: снимок useSyncExternalStore сравнивается ПО ССЫЛКЕ, и свежий
// Set с тем же составом ререндерил бы весь список чатов на каждом чужом событии
let _bgWork: ReadonlySet<string> = new Set();
const _listeners = new Set<() => void>();

let _offMessage: (() => void) | null = null;
let _offReconnected: (() => void) | null = null;

function emit() {
  for (const fn of _listeners) fn();
}

// Set пересоздаём только при реальном изменении: useSyncExternalStore сравнивает
// снимок по ссылке, и новый Set с тем же составом дал бы ререндер всего списка чатов
function withId(set: ReadonlySet<string>, sessionId: string, present: boolean): ReadonlySet<string> {
  if (set.has(sessionId) === present) return set;
  const next = new Set(set);
  if (present) next.add(sessionId);
  else next.delete(sessionId);
  return next;
}

// Пересобрать объединение после смены любого из двух множеств. Состав не изменился —
// ссылку сохраняем (см. комментарий к _bgWork)
function syncBgWork() {
  const next = new Set(_ids);
  for (const id of _commandIds) next.add(id);
  // Сравнение по составу, а не по размерам исходных множеств: один чат может держать
  // и агента, и фоновую команду разом — в объединении это ОДИН элемент
  if (next.size === _bgWork.size && [...next].every(id => _bgWork.has(id))) return;
  _bgWork = next;
}

// Оба вида приходят одним событием и меняются независимо: агент закончил, а дев-сервер
// в том же чате работает дальше. Один emit на событие — иначе подписчики дёргаются дважды
function setPresence(sessionId: string, active: boolean, command: boolean) {
  const nextIds = withId(_ids, sessionId, active);
  const nextCommands = withId(_commandIds, sessionId, command);
  if (nextIds === _ids && nextCommands === _commandIds) return;
  _ids = nextIds;
  _commandIds = nextCommands;
  syncBgWork();
  emit();
}

const sameIds = (ids: string[], set: ReadonlySet<string>) =>
  ids.length === set.size && ids.every(id => set.has(id));

function applySnapshot(agents: string[], commands: string[]) {
  if (sameIds(agents, _ids) && sameIds(commands, _commandIds)) return;
  _ids = new Set(agents);
  _commandIds = new Set(commands);
  syncBgWork();
  emit();
}

async function fetchSnapshot() {
  try {
    const snapshot = await api.chats.agentsPresence();
    applySnapshot(snapshot.agents ?? [], snapshot.commands ?? []);
  } catch {
    // Снимок — уточнение поверх realtime: не вышло, живём на событиях до следующей попытки
  }
}

function start() {
  void fetchSnapshot();
  _offMessage = onMessage(msg => {
    if (msg.type === 'bg_agents_presence' && msg.sessionId)
      setPresence(msg.sessionId, msg.active, msg.command);
    // Чат удалили — его присутствие больше не про что
    else if (msg.type === 'chat_deleted' && msg.sessionId) setPresence(msg.sessionId, false, false);
  });
  // За время обрыва смены состояния прошли мимо — состав пересобираем снимком
  _offReconnected = onReconnected(() => void fetchSnapshot());
}

function stop() {
  _offMessage?.(); _offMessage = null;
  _offReconnected?.(); _offReconnected = null;
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  if (_listeners.size === 1) start();
  return () => {
    _listeners.delete(fn);
    if (_listeners.size === 0) stop();
  };
}

const snapshot = () => _ids;
const commandsSnapshot = () => _commandIds;
const bgWorkSnapshot = () => _bgWork;

/** Чаты с живыми фоновыми агентами. Пустое множество — агентов нет нигде. */
export function useAgentsPresence(): ReadonlySet<string> {
  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

/** Работают ли в этом чате фоновые агенты прямо сейчас. */
export function useAgentsRunning(sessionId: string): boolean {
  return useAgentsPresence().has(sessionId);
}

/** Чаты с живой фоновой КОМАНДОЙ (Bash в фоне) — отличаются от агентов только значком. */
export function useBgCommandsPresence(): ReadonlySet<string> {
  return useSyncExternalStore(subscribe, commandsSnapshot, commandsSnapshot);
}

/** Работает ли в этом чате фоновая команда прямо сейчас. */
export function useBgCommandRunning(sessionId: string): boolean {
  return useBgCommandsPresence().has(sessionId);
}

/**
 * Чаты с ЛЮБОЙ живой фоновой работой (агенты ∪ фоновая команда) — источник подсветки:
 * перелив плитки чата, точки рельсы проектов и стены, счётчики свёрнутых веток. Всё, что
 * отвечает человеку на вопрос «идёт ли тут работа», обязано смотреть сюда, а не в один
 * из двух видов: чат с дев-сервером живой ровно так же, как чат с агентом.
 */
export function useBgWorkPresence(): ReadonlySet<string> {
  return useSyncExternalStore(subscribe, bgWorkSnapshot, bgWorkSnapshot);
}

/** Идёт ли в этом чате фоновая работа любого вида прямо сейчас. */
export function useBgWorkRunning(sessionId: string): boolean {
  return useBgWorkPresence().has(sessionId);
}

/**
 * Подписка для потребителей ВНЕ React (сторы projectActivity, wallStore): те держат
 * собственные агрегаты и обязаны пересчитываться, когда фон появился или закончился.
 * Возвращает функцию отписки; первый подписчик поднимает снимок и realtime.
 */
export const subscribeAgentsPresence = subscribe;

/** Текущее множество чатов с живыми агентами — для тех же не-React потребителей. */
export const agentsPresenceSnapshot = snapshot;

/** Текущее множество чатов с живой фоновой командой — для не-React потребителей. */
export const bgCommandsPresenceSnapshot = commandsSnapshot;

/** Текущее множество чатов с фоновой работой любого вида — для не-React потребителей. */
export const bgWorkPresenceSnapshot = bgWorkSnapshot;

// Только для тестов: сбросить состояние между кейсами
export function __resetAgentsPresence() {
  _ids = new Set();
  _commandIds = new Set();
  _bgWork = new Set();
  _listeners.clear();
  stop();
}
