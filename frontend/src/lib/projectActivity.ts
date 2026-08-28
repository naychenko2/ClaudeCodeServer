import { useSyncExternalStore } from 'react';
import type { HomeSessionInfo } from '../types';
import { api } from './api';
import { C } from './design';
import { hasUnread, subscribeReadState } from './chatReadState';
import { loadChatFilters, matchChatFilter } from './chatFilters';
import { onMessage, onReconnected } from './signalr';
import { bgWorkPresenceSnapshot, subscribeAgentsPresence } from './agentsPresence';

// Стор активности проектов для переключателя в сайдбаре (флаг sidebar-project-switcher):
// агрегат «в каком проекте агент работает / ждет ответа» по live-сессиям всех проектов.
// Источник — GET /api/home/summary (active[]). Статусы ПРОЕКТНЫХ сессий не приходят
// в user-группу SignalR (только в группы session/project — см. useHomeSummary), поэтому
// добираем страховочным поллингом; он живет только пока есть подписчики (плашка видна
// только в открытом воркспейсе).

export interface ProjectActivity {
  // Приоритет: waiting > working > unread. Точка одна, поэтому показывает
  // самое важное: сначала «от меня ждут ответа», потом «идёт работа», и лишь
  // если ничего не происходит — «сюда не заходили»
  status: 'waiting' | 'working' | 'unread';
  // id первой сессии проекта со статусом waiting — для диплинка сразу в ждущий чат
  waitingChatId?: string;
}

export type ActivityStatus = ProjectActivity['status'];

// Вид точки статуса — ЕДИНЫЙ для всех рельс (док проектов, док стены): цвет и
// мерцание значат одно и то же, где бы точка ни стояла.
//
// Цвет несёт смысл: waiting — медовый warning («брось дело, нужен ответ»),
// working — контрастный accent (прямо сейчас идёт работа), unread — нейтрально-
// серый (сюда не заходили, не событие). Медовый и accent на 8px-точке почти
// неотличимы, поэтому waiting дополнительно выделен формой — полое колечко.
export const STATUS_COLOR: Record<ActivityStatus, string> = {
  waiting: C.warning,
  working: C.accent,
  unread: C.textMuted,
};

// Мерцание точки: waiting дышит медленно (тревожить, но не дёргать), working —
// живее (там прямо сейчас идёт работа), unread не мерцает вовсе. cc-dot нужен
// всем: он рисует цветную заливку (::after), ободок и непрозрачную подложку.
// waiting вдобавок полый (cc-dot--ring) — по цвету он сливается с working
export const STATUS_PULSE: Record<ActivityStatus, string> = {
  waiting: ' cc-dot cc-dot--ring cc-dot-pulse cc-dot-pulse--slow',
  working: ' cc-dot cc-dot-pulse',
  unread: ' cc-dot',
};

const POLL_MS = 15_000;

let _agg = new Map<string, ProjectActivity>();
// Отпечаток агрегата: не эмитим (и не пересоздаем Map) когда данные не изменились,
// иначе плашка ререндерилась бы каждый тик поллинга
let _fingerprint = '';
// Второй срез тех же данных — статус per-ЧАТ (для номерков дока стены). Свой
// отпечаток: чатный агрегат меняется чаще проектного (проектный схлопывает по
// рангу), и пересоздавать Map друг из-за друга — лишние ререндеры подписчиков
let _chatAgg = new Map<string, ActivityStatus>();
let _chatFingerprint = '';
const _listeners = new Set<() => void>();

let _timer: ReturnType<typeof setInterval> | null = null;
let _offMessage: (() => void) | null = null;
let _offReconnected: (() => void) | null = null;
let _offReadState: (() => void) | null = null;
let _offPresence: (() => void) | null = null;

// Кэш предикатов фильтра по projectId: чтение localStorage — по разу на проект
// за пересчёт (clear в начале aggregate, иначе смена фильтра не подхватится)
const _filterPreds = new Map<string, (s: HomeSessionInfo) => boolean>();

// Виден ли чат сводки по фильтру его проекта. Предикат берём из того же matchChatFilter,
// что красит список чатов, чтобы точка и список не разъезжались. Фильтр per-scope
// (свой у каждого проекта). HomeSessionInfo несёт все поля, которые проверяет
// matchChatFilter — приводим к Session
function visibleInProjectFilter(s: HomeSessionInfo): boolean {
  if (!s.projectId) return true;
  let pred = _filterPreds.get(s.projectId);
  if (!pred) {
    pred = matchChatFilter(loadChatFilters(s.projectId)) as (x: HomeSessionInfo) => boolean;
    _filterPreds.set(s.projectId, pred);
  }
  return pred(s);
}

// active — живые сессии (starting/working/waiting), recent — завершённые и
// простаивающие. Непрочитанность ищем в обоих: чат чаще всего дочитывают уже
// после того, как агент закончил, то есть непрочитанный лежит именно в recent.
function aggregate(active: HomeSessionInfo[], recent: HomeSessionInfo[]): Map<string, ProjectActivity> {
  // Сбрасываем кэш предикатов фильтра: фильтр мог измениться с прошлого пересчёта
  _filterPreds.clear();
  const next = new Map<string, ProjectActivity>();
  // Ранг статуса: чем больше, тем важнее. Позволяет не городить лестницу if'ов
  // при сравнении с уже записанным значением
  const rank = { unread: 1, working: 2, waiting: 3 } as const;
  const put = (projectId: string, a: ProjectActivity) => {
    const cur = next.get(projectId);
    if (!cur || rank[a.status] > rank[cur.status]) next.set(projectId, a);
  };

  for (const s of active) {
    if (!s.projectId) continue; // чаты вне проектов в плашке проектов не участвуют
    if (s.status === 'waiting') {
      // Первый ждущий чат запоминаем для диплинка
      const cur = next.get(s.projectId);
      if (cur?.status !== 'waiting') put(s.projectId, { status: 'waiting', waitingChatId: s.id });
    } else {
      // starting/working → «работает»
      put(s.projectId, { status: 'working' });
    }
  }

  // Чат с живой ФОНОВОЙ работой (агенты или команда в фоне — дев-сервер, watch): ход в нём
  // завершён, статус Active, сервер отдаёт такой чат в recent, то есть «живым» он не
  // считается нигде. Работа при этом идёт, и точка проекта обязана гореть, иначе рельса
  // разойдётся со списком чатов, где у той же сессии переливается плитка. Фильтр видимости
  // здесь не применяем — он про непрочитанность (ниже), а работа идёт независимо от того,
  // что скрыто в списке
  const withBgWork = bgWorkPresenceSnapshot();
  if (withBgWork.size > 0)
    for (const s of [...active, ...recent])
      if (s.projectId && withBgWork.has(s.id)) put(s.projectId, { status: 'working' });

  for (const s of [...active, ...recent]) {
    if (!s.projectId) continue;
    // Непрочитанность считаем только по чатам, видимым в фильтре ЭТОГО проекта:
    // иначе скрытая выполненная задача светилась бы в рельсе, хотя в списке её нет
    if (!visibleInProjectFilter(s)) continue;
    if (hasUnread(s.updatedAt, s.id, s.lastReadAt)) put(s.projectId, { status: 'unread' });
  }
  return next;
}

// Статус per-чат из того же снимка: живая сессия несёт свой статус (waiting/working),
// у остальных смотрим непрочитанность. Фильтр списка чатов тут НЕ применяется:
// потребитель (док стены) показывает явно выбранные чаты, и прятать их точку по
// фильтру чужого списка было бы враньём.
function aggregateChats(active: HomeSessionInfo[], recent: HomeSessionInfo[]): Map<string, ActivityStatus> {
  const next = new Map<string, ActivityStatus>();
  for (const s of active) next.set(s.id, s.status === 'waiting' ? 'waiting' : 'working');
  // Живой фон — та же работа: без этого номерок чата в доке стены остался бы немым
  // (или серым «сюда не заходили»), пока агенты пашут или крутится фоновая команда
  const withBgWork = bgWorkPresenceSnapshot();
  for (const s of recent) if (withBgWork.has(s.id)) next.set(s.id, 'working');
  for (const s of [...active, ...recent]) {
    if (next.has(s.id)) continue; // живой статус важнее непрочитанности
    if (hasUnread(s.updatedAt, s.id, s.lastReadAt)) next.set(s.id, 'unread');
  }
  return next;
}

// Последний ответ сервера. Нужен, чтобы пересобрать агрегат без запроса, когда
// изменилась только прочитанность (гибрид: локальная отметка в localStorage
// меняется мгновенно, серверная lastReadAt приедет со следующим поллингом)
let _lastSummary: { active: HomeSessionInfo[]; recent: HomeSessionInfo[] } | null = null;

function recompute() {
  if (!_lastSummary) return;
  let changed = false;

  const next = aggregate(_lastSummary.active, _lastSummary.recent);
  const fp = [...next.entries()]
    .map(([pid, a]) => `${pid}:${a.status}:${a.waitingChatId ?? ''}`)
    .sort()
    .join('|');
  if (fp !== _fingerprint) {
    _fingerprint = fp;
    _agg = next;
    changed = true;
  }

  const chatNext = aggregateChats(_lastSummary.active, _lastSummary.recent);
  const chatFp = [...chatNext.entries()].map(([id, st]) => `${id}:${st}`).sort().join('|');
  if (chatFp !== _chatFingerprint) {
    _chatFingerprint = chatFp;
    _chatAgg = chatNext;
    changed = true;
  }

  if (changed) _listeners.forEach(fn => fn());
}

function fetchNow() {
  api.home.summary(20)
    .then(d => {
      _lastSummary = { active: d.active, recent: d.recent };
      recompute();
    })
    .catch(() => { /* сервер недоступен — оставляем прошлый агрегат */ });
}

// Поллинг и realtime-подписки живут только при активных подписчиках
function start() {
  if (_timer) return;
  fetchNow();
  _timer = setInterval(fetchNow, POLL_MS);
  _offMessage = onMessage(msg => {
    if (msg.type === 'status_changed' || msg.type === 'chat_deleted') fetchNow();
  });
  _offReconnected = onReconnected(fetchNow);
  // Чат отметили прочитанным — точка обязана погаснуть сразу, без ожидания
  // поллинга. Данные с сервера при этом не меняются, поэтому пересобираем
  // агрегат из последнего ответа
  _offReadState = subscribeReadState(recompute);
  // Фон появился/закончился — данные сводки те же, пересобираем агрегат из последнего
  // ответа (как и с прочитанностью). Заодно подписка поднимает сам стор присутствия
  _offPresence = subscribeAgentsPresence(recompute);
}

function stop() {
  if (_timer) { clearInterval(_timer); _timer = null; }
  _offMessage?.(); _offMessage = null;
  _offReconnected?.(); _offReconnected = null;
  _offReadState?.(); _offReadState = null;
  _offPresence?.(); _offPresence = null;
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  if (_listeners.size === 1) start();
  return () => {
    _listeners.delete(fn);
    if (_listeners.size === 0) stop();
  };
}

// Только для тестов: агрегаты без React и сброс состояния между кейсами
export const __subscribeActivity = subscribe;
export const __projectAggSnapshot = () => _agg;
export const __chatAggSnapshot = () => _chatAgg;
export function __resetProjectActivity() {
  _agg = new Map();
  _chatAgg = new Map();
  _fingerprint = '';
  _chatFingerprint = '';
  _lastSummary = null;
  _listeners.clear();
  stop();
}

// Активность проектов: Map<projectId, ProjectActivity>. Проекты без live-сессий в Map отсутствуют.
export function useProjectActivity(): Map<string, ProjectActivity> {
  return useSyncExternalStore(subscribe, () => _agg, () => _agg);
}

// Активность чатов: Map<sessionId, status>. Тихие прочитанные чаты в Map отсутствуют.
// Ограничение источника: summary отдаёт recent с лимитом, совсем старый чат без
// живой сессии в карту не попадёт — точки у него просто не будет (как и в доке проектов).
export function useChatActivity(): Map<string, ActivityStatus> {
  return useSyncExternalStore(subscribe, () => _chatAgg, () => _chatAgg);
}

// Публичный refresh для тех, кто видит проектные события, не доходящие до агрегата:
// статусы и новые сообщения ПРОЕКТНЫХ чатов приходят в session/project-группы
// SignalR, а агрегат слушает user-группу (там их нет). Воркспейс, будучи в
// project-группе, зовёт этот метод при статусе/сообщении активного проекта —
// точка обновляется сразу, а не ждёт поллинга.
// Троттлинг ~1.5с: событий может прийти пачкой (поток сообщений), запрос summary
// на каждое — лишняя нагрузка, а точке достаточно догнать раз в полторы секунды
let _refreshPending = false;
export function refreshProjectActivity(): void {
  if (_refreshPending) return;
  _refreshPending = true;
  setTimeout(() => { _refreshPending = false; fetchNow(); }, 1500);
}
