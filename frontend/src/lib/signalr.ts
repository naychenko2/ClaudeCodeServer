import * as signalR from '@microsoft/signalr';
import type { ServerMessage, TeamPlanDecision } from '../types';
import { getConnectionState, setConnectionState, setDegraded } from './offline';

let connection: signalR.HubConnection | null = null;

// Набор подписчиков на событие reconnected: поддерживает отписку (в отличие от
// прямых conn.onreconnected(), которые не имеют публичного off())
const _reconnectedCallbacks = new Set<() => void>();

// Дебаунс перехода в офлайн. Кратковременные разрывы WS (VPN/прокси/сеть рвут
// канал, авто-реконнект поднимает за доли секунды) НЕ должны мигать индикатором.
// Показываем «Офлайн» только если реконнект не удался за OFFLINE_DEBOUNCE_MS.
let _offlineDebounce: ReturnType<typeof setTimeout> | null = null;
const OFFLINE_DEBOUNCE_MS = 2_500;

function scheduleOffline() {
  // Начало реконнекта — сразу degraded: индикатор объясняет, что происходит, а данные
  // продолжают тянуться. В offline уходим только если реконнект не удался за дебаунс.
  if (getConnectionState() === 'online') setDegraded('SignalR reconnecting');
  if (_offlineDebounce !== null) return; // уже запланировано
  _offlineDebounce = setTimeout(() => { _offlineDebounce = null; setConnectionState('offline'); }, OFFLINE_DEBOUNCE_MS);
}

function cancelOffline() {
  if (_offlineDebounce !== null) { clearTimeout(_offlineDebounce); _offlineDebounce = null; }
}

export function getConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/session', {
        // JWT для WebSocket уходит как ?access_token= (заголовок задать нельзя)
        accessTokenFactory: () => localStorage.getItem('cc_token') || sessionStorage.getItem('cc_token') || '',
      })
      // Смягчаем разрывы на дрожащем канале: клиент считает сервер живым 60 с
      // (дефолт 30 с рвал соединение при коротком отсутствии сообщений), пингует каждые 15 с.
      // Согласовано с серверными ClientTimeoutInterval=60 / KeepAliveInterval=15.
      .withServerTimeout(60_000)
      .withKeepAliveInterval(15_000)
      .withAutomaticReconnect({
        // Первая попытка — мгновенно (0мс): кратковременный блип чинится сразу,
        // UI не успевает мигнуть. Дальше — экспоненциальный откат, макс 30 сек.
        nextRetryDelayInMilliseconds: ctx =>
          ctx.previousRetryCount === 0
            ? 0
            : Math.min(1000 * Math.pow(2, ctx.previousRetryCount - 1), 30_000),
      })
      .build();
    // Состояние соединения двигает глобальный online/offline флаг.
    // В офлайн уходим с дебаунсом — кратковременный разрыв+реконнект не мигает.
    connection.onreconnecting(() => scheduleOffline());
    // onclose — соединение закрыто окончательно (реконнекты исчерпаны или явный
    // stop); тут офлайн без дебаунса.
    connection.onclose(() => { cancelOffline(); setConnectionState('offline'); });
    // Единственный onreconnected-обработчик: отменяем отложенный офлайн + online + диспатч
    connection.onreconnected(() => {
      cancelOffline();
      setConnectionState('online');
      _reconnectedCallbacks.forEach(cb => { try { cb(); } catch { /* не даём одному упавшему обработчику блокировать остальных */ } });
    });
  }
  return connection;
}

let _startPromise: Promise<void> | null = null;

export async function ensureConnected(): Promise<signalR.HubConnection> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    if (!_startPromise) {
      _startPromise = conn.start().finally(() => { _startPromise = null; });
    }
    await _startPromise;
  } else if (conn.state === signalR.HubConnectionState.Connecting ||
             conn.state === signalR.HubConnectionState.Reconnecting) {
    // ждём пока не подключится; таймаут — чтобы офлайн (вечный Reconnecting) не висел бесконечно
    await new Promise<void>((resolve, reject) => {
      let waited = 0;
      const timer = setInterval(() => {
        if (conn.state === signalR.HubConnectionState.Connected) {
          clearInterval(timer);
          resolve();
        } else if (conn.state === signalR.HubConnectionState.Disconnected) {
          clearInterval(timer);
          reject(new Error('SignalR disconnected while waiting'));
        } else if ((waited += 50) >= 8000) {
          clearInterval(timer);
          reject(new Error('SignalR connect timeout'));
        }
      }, 50);
    });
  }
  return conn;
}

export async function joinSession(sessionId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('JoinSession', sessionId);
}

export async function leaveSession(sessionId: string): Promise<void> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Connected) {
    await conn.invoke('LeaveSession', sessionId);
  }
}

// Возвращает исход постановки пользовательского сообщения: 'started' — ход запущен
// (оптимистичный баллон уместен); 'queued' — чат занят, сообщение встало в серверную
// очередь (баллон НЕ рисуем: карточку даст снимок pending_messages, доставленное —
// событием user_message). Контракт «честной очереди».
export async function sendMessage(sessionId: string, text: string, attachedPaths: string[] = [], mode?: string, auto = false): Promise<'started' | 'queued'> {
  const conn = await ensureConnected();
  return conn.invoke<'started' | 'queued'>('SendMessage', sessionId, text, attachedPaths, mode ?? null, auto);
}

export async function respondPermission(
  sessionId: string,
  requestId: string,
  behavior: 'allow' | 'deny' | 'allow_always',
): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('RespondPermission', sessionId, requestId, behavior);
}

export async function interruptSession(sessionId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('Interrupt', sessionId);
}

// Смена режима прав на лету (переключатель композера) — применяется к идущему ходу
export async function setMode(sessionId: string, mode: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('SetMode', sessionId, mode);
}

// Ручное сворачивание контекста сессии (/compact)
export async function compactSession(sessionId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('CompactSession', sessionId);
}

export async function answerQuestion(sessionId: string, toolUseId: string, answerText: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('AnswerQuestion', sessionId, toolUseId, answerText);
}

export async function respondPlan(sessionId: string, requestId: string, approve: boolean, feedback?: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('RespondPlan', sessionId, requestId, approve, feedback ?? null);
}

// Решение по карточке плана командной реализации. reassign требует subtaskId +
// executorPersonaId (карточка остаётся открытой), run/cancel закрывают карточку.
export async function respondTeamPlan(sessionId: string, planId: string, decision: TeamPlanDecision,
  subtaskId?: string, executorPersonaId?: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('RespondTeamPlan', sessionId, planId, decision, subtaskId ?? null, executorPersonaId ?? null);
}

// Решение по карточке остановки: кнопка (actionId) и/или комментарий человека.
// Карточка гаснет, решение уходит координатору отдельным ходом
export async function respondTeamEscalation(sessionId: string, escalationId: string,
  actionId?: string, comment?: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('RespondTeamEscalation', sessionId, escalationId, actionId ?? null, comment ?? null);
}

export function onMessage(handler: (msg: ServerMessage) => void): () => void {
  const conn = getConnection();
  conn.on('message', handler);
  return () => conn.off('message', handler);
}

// Watcher: сервер сообщает об изменении файлов проекта (создание/правка/удаление)
export function onFilesChanged(handler: (data: { projectId: string; paths: string[] }) => void): () => void {
  const conn = getConnection();
  conn.on('filesChanged', handler);
  return () => conn.off('filesChanged', handler);
}

// Git-статус проекта изменился (commit/stage/checkout/…) — приходит событием
// message с type=git_status_changed в группу user_*; клиент перезапрашивает статус
export function onGitStatusChanged(handler: (data: { projectId: string }) => void): () => void {
  const conn = getConnection();
  const h = (msg: ServerMessage) => {
    if (msg.type === 'git_status_changed') handler({ projectId: msg.projectId });
  };
  conn.on('message', h);
  return () => conn.off('message', h);
}

export async function joinProject(projectId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('JoinProject', projectId);
}

export async function leaveProject(projectId: string): Promise<void> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Connected)
    await conn.invoke('LeaveProject', projectId);
}

// Подписка на вывод дев-сервера (вкладка «Логи» панели «Сервисы»). Накопленный буфер
// приходит ОТВЕТОМ на этот вызов (а не сообщением — иначе при пере-монтировании вьюера
// его ловят оба инстанса и лог задваивается), дальше вывод идёт событиями preview_log.
export async function joinPreviewLog(projectId: string, serviceId: string): Promise<string | null> {
  const conn = await ensureConnected();
  return conn.invoke<string | null>('JoinPreviewLog', projectId, serviceId);
}

export async function leavePreviewLog(projectId: string, serviceId: string): Promise<void> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Connected)
    await conn.invoke('LeavePreviewLog', projectId, serviceId);
}

// Группа для realtime-обновления списка чатов вне проекта (статусы)
export async function joinUser(userId: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('JoinUser', userId);
}

export async function leaveUser(userId: string): Promise<void> {
  const conn = getConnection();
  if (conn.state === signalR.HubConnectionState.Connected)
    await conn.invoke('LeaveUser', userId);
}

// Подписка на reconnected. Возвращает функцию отписки — обязательно вызывать при unmount.
export function onReconnected(callback: () => void): () => void {
  _reconnectedCallbacks.add(callback);
  return () => _reconnectedCallbacks.delete(callback);
}
