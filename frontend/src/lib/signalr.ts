import * as signalR from '@microsoft/signalr';
import type { ServerMessage, TeamPlanDecision } from '../types';
import { confirmOffline, setConnectionState } from './offline';

let connection: signalR.HubConnection | null = null;

// Набор подписчиков на событие reconnected: поддерживает отписку (в отличие от
// прямых conn.onreconnected(), которые не имеют публичного off())
const _reconnectedCallbacks = new Set<() => void>();

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
    // onreconnecting — промежуточные попытки авто-реконнекта; UI не дёргаем:
    // возврат поднимется через onreconnected, окончательный обрыв — через onclose.
    // Кратковременные разрывы сети/WS теперь не мигают индикатором.
    connection.onreconnecting(() => { /* noop */ });
    // onclose — теоретический вход в offline (реконнекты исчерпаны или явный stop).
    // Подтверждения тут сознательно НЕТ: это терминальное событие (авто-реконнект
    // сдался либо мы сами позвали stop), ложного срабатывания класса «блип» здесь не
    // бывает, а откладывать флаг на асинхронный пинг у заведомо мёртвого хаба незачем.
    // При текущем делегате ретраев (nextRetryDelayInMilliseconds всегда возвращает
    // число) onclose практически недостижим: реальный сигнал «хаб не поднялся»
    // приходит через ветки reject в ensureConnected ниже — таймаут ожидания
    // (8с) и переход в Disconnected, — где и поднимается offline.
    connection.onclose(() => setConnectionState('offline'));
    // onreconnected — онлайн + диспатч подписчикам
    connection.onreconnected(() => {
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
      _startPromise = conn.start()
        .then(() => {
          // Старт из Disconnected НЕ триггерит onreconnected (это не reconnect).
          // Без явного подъёма флаг останется offline при заведомо живом хабе —
          // на экране логина это и был дефект «Повторить врёт».
          setConnectionState('online');
        })
        .finally(() => { _startPromise = null; });
    }
    await _startPromise;
  } else if (conn.state === signalR.HubConnectionState.Connecting ||
             conn.state === signalR.HubConnectionState.Reconnecting) {
    // Ждём пока не подключится; таймаут — чтобы офлайн (вечный Reconnecting) не висел бесконечно.
    // В ветках reject просим подтверждение офлайна: зонд возврата в онлайн тикает только
    // пока мы offline, и при неподнявшемся хабе UI должен видеть честный статус. Сценарий,
    // ради которого ветки заведены: Wi-Fi без интернета / captive portal / упавший бэкенд
    // за живым реверс-прокси — navigator.onLine === true, REST-запросов нет, сокет уходит
    // в бесконечный Reconnecting, индикатор врёт «Онлайн», а sendMessage повисает на 8с.
    // Он выживает: там /api/health либо не отвечает вовсе, либо отдаёт 502/503/504 от
    // прокси — оба случая для confirmOffline() провал, флаг падает как раньше.
    // Отсекается ложное срабатывание при ЖИВОМ сервере: 8с здесь легко накрывают паузу
    // экспоненциального отката авто-реконнекта (после четвёртой попытки она сама больше
    // 8с), и отправка, попавшая в эту паузу, красила интерфейс в офлайн на ровном месте.
    // confirmOffline не ждём намеренно: промис ensureConnected обязан реджектнуться сразу
    // и с тем же текстом — живой REST не делает неподнявшийся хаб успехом.
    await new Promise<void>((resolve, reject) => {
      let waited = 0;
      const timer = setInterval(() => {
        if (conn.state === signalR.HubConnectionState.Connected) {
          clearInterval(timer);
          resolve();
        } else if (conn.state === signalR.HubConnectionState.Disconnected) {
          clearInterval(timer);
          void confirmOffline();
          reject(new Error('SignalR disconnected while waiting'));
        } else if ((waited += 50) >= 8000) {
          clearInterval(timer);
          void confirmOffline();
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
// событием user_message); 'queued-preempted' — то же, но идущий ход ради этого сообщения
// пришлось прервать (ждал ответа человека либо шёл цикл «до готово»): убитый ход пришлёт
// голый exited, и лента обязана отметить прерывание, иначе нарисует ложную аварию.
// Контракт «честной очереди».
export type SendOutcome = 'started' | 'queued' | 'queued-preempted';

export async function sendMessage(sessionId: string, text: string, attachedPaths: string[] = [], mode?: string, auto = false): Promise<SendOutcome> {
  const conn = await ensureConnected();
  return conn.invoke<SendOutcome>('SendMessage', sessionId, text, attachedPaths, mode ?? null, auto);
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
// executorPersonaId (карточка остаётся открытой), edit требует feedback (сервер сам
// пересобирает план), run/cancel закрывают карточку.
export async function respondTeamPlan(sessionId: string, planId: string, decision: TeamPlanDecision,
  subtaskId?: string, executorPersonaId?: string, feedback?: string): Promise<void> {
  const conn = await ensureConnected();
  await conn.invoke('RespondTeamPlan', sessionId, planId, decision, subtaskId ?? null, executorPersonaId ?? null, feedback ?? null);
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
