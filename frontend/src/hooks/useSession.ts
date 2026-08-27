import { useEffect, useState, useCallback } from 'react';
import type { ChatItem, ServerMessage, RateLimitInfo, WorkLoopState, TeamImplementState, TeamPlanDecision, TeamWavePulse } from '../types';
import { joinSession, joinProject, leaveSession, onMessage, onReconnected, sendMessage, respondPermission, interruptSession, compactSession, answerQuestion as sendAnswer, respondPlan as sendPlanDecision, respondTeamPlan as sendTeamPlanDecision, respondTeamEscalation as sendTeamEscalationDecision, setMode as sendSetMode } from '../lib/signalr';
import { setRecallManifest } from '../lib/recallManifest';
import { requestWakeLock, releaseWakeLock } from '../lib/wakeLock';
import { api } from '../lib/api';
import { applyServerMessage, normalizeHistory, serverHistoryNewer, initialChatState, consumeComposerRestore, teamImplementSnapshot, turnAlreadyEnded, type ChatState, type PendingChatMessage, type ComposerRestore } from '../lib/chatReducer';

// --- Модульный персистентный стор ---
// Состояние живёт на уровне модуля и переживает переключение между сессиями.
// Компоненты подписываются на обновления своей сессии, но при анмаунте
// не отписываются от SignalR — сессия продолжает получать сообщения в фоне.

// ChatState (items/isWaiting/rateLimits/компакция) — в lib/chatReducer.ts,
// здесь добавляются только поля жизненного цикла подключения
interface SessionState extends ChatState {
  isJoined: boolean;
  projectId?: string;
  isHistoryLoading: boolean;
  // Групповой чат (participants > 1): в normalizeHistory включается derive
  // разделителей «Теперь отвечает…» по смене personaId между репликами
  isGroup?: boolean;
}

const _store = new Map<string, SessionState>();
const _listeners = new Map<string, Set<() => void>>();
const _joining = new Map<string, Promise<void>>();
const _lastSeen = new Map<string, number>(); // LRU: sid → ts последней мутации состояния

// Потолок LRU-кэша холодных сессий (см. evictColdSessions). Открытые/активные не считаются.
const MAX_CACHED_SESSIONS = 20;

// История чата: у проектной сессии — через /projects/{id}/sessions, у чата вне проекта — через /chats
const loadHistory = (sid: string, projectId?: string) =>
  projectId ? api.sessions.getHistory(projectId, sid) : api.chats.getHistory(sid);

// Нормализация истории с опциями текущей сессии (derive разделителей в групповом чате)
const normalizeFor = (sid: string, raw: unknown[]) =>
  normalizeHistory(raw, { deriveSpeakers: getState(sid).isGroup });

// Ход завершён, если последний содержательный элемент истории — result или error.
// fal_cost дописывается асинхронно уже после конца хода, на признак не влияет.
// Пока ход идёт, хвостом истории всегда что-то из хода (user_message пишется в
// аккумулятор сразу при отправке) — ложного «завершён» это не даёт.
function historyTurnFinished(items: ChatItem[]): boolean {
  for (let i = items.length - 1; i >= 0; i--) {
    const kind = items[i].kind;
    if (kind === 'fal_cost') continue;
    return kind === 'result' || kind === 'error';
  }
  return false;
}

// Перезагрузка истории с сервера с применением, если она новее живой ленты
// (сервер — источник истины, сверка — serverHistoryNewer). Заодно лечит залипший
// isWaiting: у завершённого хвоста ход точно кончился, даже если событие result
// прошло мимо клиента, пока он был вне SignalR-группы.
async function reloadHistory(sid: string, projectId?: string) {
  try {
    const raw = await loadHistory(sid, projectId);
    const items = normalizeFor(sid, raw);
    setState(sid, prev => {
      if (!serverHistoryNewer(items, prev.items)) return prev;
      return { ...prev, items, isWaiting: historyTurnFinished(items) ? false : prev.isWaiting };
    });
  } catch { /* история недоступна — не блокируем */ }
}

// Авторитетное состояние режима «Командная реализация» с сервера (M10/M12).
// JoinSession реплеит статус, но НЕ снапшот team_implement, а live-объект в сторе
// «прилипает» после первого события навсегда: всё, что случилось за разрыв (смена
// стадии, лок селектора, выключение режима), врало бы до F5. Перечитываем сессию
// и подменяем live-снимок — включая выключенный режим (неактивный снимок, не undefined).
async function refreshTeamImplement(sid: string) {
  try {
    const s = await api.chats.get(sid);
    setState(sid, prev => ({ ...prev, teamImplement: teamImplementSnapshot(s.teamImplement ?? null) }));
  } catch { /* сеть — живём на живых событиях, следующее освежение поправит */ }
}

function getState(sid: string): SessionState {
  if (!_store.has(sid)) _store.set(sid, { ...initialChatState(), isJoined: false, isHistoryLoading: true });
  return _store.get(sid)!;
}

// Единственная точка мутации _store: любая запись освежает LRU-метку. getState
// при lazy-init метку НЕ ставит намеренно — LRU считаем по мутациям, не по чтениям
// (иначе фоновый onMessage от project-групп держал бы чужие сессии вечно горячими).
function touch(sid: string, next: SessionState) {
  _store.set(sid, next);
  _lastSeen.set(sid, Date.now());
  // Poll реконсиляции живёт ровно пока есть занятый открытый чат (см. syncWaitPoll)
  syncWaitPoll();
}

function setState(sid: string, updater: (prev: SessionState) => SessionState) {
  touch(sid, updater(getState(sid)));
  _listeners.get(sid)?.forEach(fn => fn());
}

// LRU-потолок: держим не более MAX_CACHED_SESSIONS холодных сессий. Холодная =
// никто не смотрит (нет подписчиков), ход не идёт (isWaiting) и мы вышли из её
// SignalR-группы (isJoined). Открытые/активные не считаем и не выселяем. Ленты
// выселенных освобождаются; история восстановится через loadHistory при повторном
// открытии. Зовётся из cleanup при закрытии последнего зрителя сессии.
function evictColdSessions() {
  const cold: Array<[string, number]> = [];
  for (const [sid, s] of _store) {
    const watched = (_listeners.get(sid)?.size ?? 0) > 0;
    if (watched || s.isWaiting || s.isJoined) continue;
    cold.push([sid, _lastSeen.get(sid) ?? 0]);
  }
  if (cold.length <= MAX_CACHED_SESSIONS) return;
  cold.sort((a, b) => a[1] - b[1]); // старые первыми
  for (const [sid] of cold.slice(0, cold.length - MAX_CACHED_SESSIONS)) {
    _store.delete(sid);
    _lastSeen.delete(sid);
  }
}

function updateItems(sid: string, fn: (items: ChatItem[]) => ChatItem[]) {
  setState(sid, prev => ({ ...prev, items: fn(prev.items) }));
}

// --- Реконсиляция isWaiting с серверным статусом ---
// Клиентский флаг чисто событийный: любое пропущенное событие (разрыв сети, вкладка вне
// группы, гонка) оставляет его врущим до следующего события. Лечение — повторный
// joinSession: хаб идемпотентен и на КАЖДЫЙ вызов шлёт вызвавшему авторитетный
// status_changed, который редьюсер применяет к isWaiting. Бэкенд менять не нужно.

// Потолок ожидания входа в группу перед снятием истории (см. joinAndLoadHistory).
// На живом соединении join укладывается в десятки миллисекунд, поэтому окно короткое:
// когда хаб висит в Reconnecting, история должна читаться практически сразу, а дыру
// в этом случае закрывает повторный снимок после входа.
const JOIN_WAIT_MS = 400;

const RECONCILE_POLL_MS = 25_000;   // как часто переспрашиваем статус занятого открытого чата
const RECONCILE_WINDOW_MS = 10_000; // сколько ждём replay, чтобы засчитать его как ответ на запрос

// Сессии, ждущие replay-статуса: sid → ts запроса
const _reconciling = new Map<string, number>();

// Счётчик расхождений клиентского флага с серверным статусом — чтобы понять, какой класс
// рассинхрона доминирует. Из консоли доступен как window.__ccWaitDiag.
export const waitDiag = {
  mismatches: 0,
  last: null as { sid: string; client: boolean; server: string; at: string } | null,
};
if (typeof window !== 'undefined')
  (window as unknown as Record<string, unknown>).__ccWaitDiag = waitDiag;

const isWatched = (sid: string) => (_listeners.get(sid)?.size ?? 0) > 0;

// Статусы, означающие «ход не идёт» (редьюсер снимает по ним isWaiting)
const isIdleStatus = (status: string) =>
  status === 'active' || status === 'error' || status === 'finished' || status === 'orphaned';

// Единственная точка входа в SignalR-группу: любой успешный join отмечает членство.
// Раньше isJoined выставлял только joinAndLoadHistory, поэтому чат с упавшим первым
// join навсегда оставался «не в группе» — onReconnected его пропускал, live-события
// не доходили, и лента оживала лишь после переключения чатов.
function joinTracked(sid: string): Promise<void> {
  return joinSession(sid).then(() => {
    if (!getState(sid).isJoined) setState(sid, prev => ({ ...prev, isJoined: true }));
  });
}

// Запрос авторитетного статуса. Ошибку глотаем: офлайн вылечит reconnect-обработчик.
function reconcile(sid: string) {
  _reconciling.set(sid, Date.now());
  joinTracked(sid).catch(() => { _reconciling.delete(sid); });
}

// Окно отправки: sid здесь с начала send() до ответа хаба. Реплей статуса на JoinSession
// внутри этого окна описывает состояние ДО нашего хода, и понижающий статус (ход ещё не
// стартовал) не должен гасить оптимистичный isWaiting и дёргать reloadHistory.
const _sending = new Set<string>();

// Ход прерван сервером ради очереди (кнопка «Прервать и отправить» либо исход
// 'queued-preempted'). Факт держим здесь, а НЕ только отметкой в ленте: лента переживает
// замену историей с сервера (маркер 'interrupted' live-only, в history его нет), и после
// реконнекта посреди преемпта `exited` убитого хода снова читался бы как авария. Флаг живёт
// до ближайшего конца хода — дальше он неактуален и следующую настоящую аварию не заглушит.
const _preempted = new Set<string>();

let _pollTimer: ReturnType<typeof setInterval> | null = null;

// Тик poll: переспрашиваем статус у занятых открытых сессий
function pollWaiting() {
  for (const [sid, s] of _store) if (s.isWaiting && isWatched(sid)) reconcile(sid);
  syncWaitPoll();
}

// Ленивый poll: таймер нужен, пока есть хоть одна занятая (isWaiting) открытая (есть
// подписчики) сессия, и гаснет сразу, как такая пропала — снят флаг или ушли зрители.
// Таймер один на модуль, поэтому повторные рендеры его не плодят.
function syncWaitPoll() {
  let busy = false;
  for (const [sid, s] of _store) {
    if (s.isWaiting && isWatched(sid)) { busy = true; break; }
  }
  if (busy && _pollTimer === null) _pollTimer = setInterval(pollWaiting, RECONCILE_POLL_MS);
  else if (!busy && _pollTimer !== null) { clearInterval(_pollTimer); _pollTimer = null; }
  // Тот же признак держит экран: планшет с открытым чатом засыпал посреди ответа, и
  // человек возвращался к погасшему экрану вместо ленты. Условие ровно то же, что у
  // poll — ход идёт И чат кто-то смотрит, поэтому фоновые чаты экран не жгут. Владелец
  // отдельный: петля разговора держит блокировку своим (см. lib/wakeLock)
  if (busy) requestWakeLock('turn'); else releaseWakeLock('turn');
}

// Диагностика расхождения: сверяем клиентский флаг с replay-статусом, пришедшим в ответ
// на нашу реконсиляцию (обычная смена статуса по ходу рассинхроном не считается).
function noteReconcileResult(sid: string, clientWaiting: boolean, status: string) {
  const requestedAt = _reconciling.get(sid);
  if (requestedAt === undefined) return;
  _reconciling.delete(sid);
  if (Date.now() - requestedAt > RECONCILE_WINDOW_MS) return; // ответ уже не наш
  const serverWaiting = status === 'working' || status === 'waiting';
  const known = serverWaiting || isIdleStatus(status);
  if (!known || clientWaiting === serverWaiting) return;
  waitDiag.mismatches++;
  waitDiag.last = { sid, client: clientWaiting, server: status, at: new Date().toISOString() };
  console.warn(`[wait-sync] расхождение индикатора: ${sid} — клиент isWaiting=${clientWaiting}, сервер «${status}»`);
}

// Единственный глобальный обработчик — регистрируется один раз при загрузке модуля
let _handlerReady = false;
function ensureHandler() {
  if (_handlerReady) return;
  _handlerReady = true;

  // После переподключения: сбрасываем зависшие isWaiting, перезаходим в группы,
  // подтягиваем историю чтобы показать сообщения пропущенные во время разрыва
  onReconnected(async () => {
    // Запоминаем какие сессии ждали ДО сброса, затем немедленно снимаем ожидание
    const wasWaiting = new Set<string>();
    for (const [sid, s] of _store) {
      if (s.isWaiting) {
        wasWaiting.add(sid);
        setState(sid, prev => ({ ...prev, isWaiting: false }));
      }
    }
    // Переподключаем project-группы (для real-time статусов)
    const projectIds = new Set<string>();
    for (const [, s] of _store) {
      if (s.projectId) projectIds.add(s.projectId);
    }
    for (const pid of projectIds) {
      // Осознанно: неудача переподключения project-группы не критична — догонится следующим join
      try { await joinProject(pid); } catch { /* ignore */ }
    }

    for (const [sid, s] of _store) {
      // Перезаходим и в сессии, которые считают себя вне группы, но их кто-то смотрит:
      // ровно так выглядит чат с упавшим первым join — по isJoined он пропускался навсегда
      if (!s.isJoined && !isWatched(sid)) continue;
      try {
        await joinTracked(sid);
        // Историю подтягиваем для сессий с живой активностью на момент разрыва
        // (working/waiting → isWaiting) И для открытых сейчас (есть подписчики-компоненты) —
        // открыта одна-две, дёшево; иначе завершённый в офлайне ход не доехал бы до ленты
        const isOpen = (_listeners.get(sid)?.size ?? 0) > 0;
        if (wasWaiting.has(sid) || isOpen) {
          try {
            const raw = await loadHistory(sid, s.projectId);
            const items = normalizeFor(sid, raw);
            if (items.length > 0) {
              setState(sid, prev => ({
                ...prev,
                items: serverHistoryNewer(items, prev.items) ? items : prev.items,
              }));
            }
          } catch { /* история недоступна — не блокируем */ }
          // Состояние режима «Командная реализация» не реплеится — освежаем REST'ом
          await refreshTeamImplement(sid);
        }
      } catch { /* пропускаем — не блокируем остальные */ }
    }
  });

  onMessage((msg: ServerMessage) => {
    const sid = msg.sessionId;
    if (!sid) return;

    // Понижающий реплей статуса в окне send относится к состоянию ДО отправки —
    // гасим его целиком, иначе он снимет ожидание и запустит лишнюю перезагрузку
    // истории наперегонки со стримом только что стартовавшего хода.
    if (msg.type === 'status_changed' && _sending.has(sid) && isIdleStatus(msg.status)) {
      _reconciling.delete(sid);
      return;
    }

    // Ход убит сервером ради нашего же сообщения (preempt при Waiting/цикле), но exited
    // убитого прогона обогнал ответ хаба с исходом 'queued-preempted' — они идут разными
    // каналами, порядок не гарантирован. Без отметки редьюсер счёл бы этот exited аварией и
    // нарисовал «AI завершился неожиданно». Ставим её здесь, пока окно отправки открыто.
    // Размен осознанный: настоящая смерть процесса, попавшая ровно в это окно (сотни мс),
    // покажется как «Прервано» — обмануть мягко лучше, чем пугать регулярной ложной аварией.
    if (msg.type === 'exited' && (_sending.has(sid) || _preempted.has(sid))) {
      const s = getState(sid);
      if (s.isWaiting && !turnAlreadyEnded(s.items))
        touch(sid, { ...s, items: [...s.items, { kind: 'interrupted' }] });
    }
    // Ход, который прерывали, закончился — флаг отработал. Гасим и на result: если перебой
    // не состоялся (409, гонка), ход дошёл до конца сам, и держать флаг дальше нельзя —
    // он заглушил бы аварию следующего хода.
    if (msg.type === 'exited' || msg.type === 'result') _preempted.delete(sid);

    // Чистая часть — в редьюсере (lib/chatReducer.ts). Если состояние не изменилось
    // (вернулась та же ссылка) — подписчиков не будим.
    const prev = getState(sid);
    const next = applyServerMessage(prev, msg);
    if (next !== prev) {
      touch(sid, next);
      _listeners.get(sid)?.forEach(fn => fn());
    }

    // Манифест recall (F3): что персона подтянула в ход — в отдельный стор для вкладки контекста
    if (msg.type === 'recall_manifest') setRecallManifest(sid, msg.items);

    // Побочный эффект вне редьюсера: при переходе в active перезагружаем историю —
    // клиент мог пропустить text_delta/tool_use пока был оффлайн или не в группе
    if (msg.type === 'status_changed') {
      // Флаг сверяем ДО применения редьюсера — prev держит клиентское состояние
      noteReconcileResult(sid, prev.isWaiting, msg.status);
      if (msg.status === 'active') reloadHistory(sid, getState(sid).projectId);
    }
  });

  // Возврат внимания на вкладку: пока она была скрыта, события могли пройти мимо
  // (разрыв WS, троттлинг таймеров) — переспрашиваем статус у открытых чатов
  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState !== 'visible') return;
      for (const sid of _listeners.keys()) if (isWatched(sid)) reconcile(sid);
    });
  }
}


// Присоединяемся к сессии один раз и остаёмся — даже при переключении между сессиями
function ensureJoined(sid: string, projectId?: string) {
  if (getState(sid).isJoined || _joining.has(sid)) return;
  const p = joinAndLoadHistory(sid, projectId)
    .finally(() => _joining.delete(sid));
  _joining.set(sid, p);
}

async function joinAndLoadHistory(sid: string, projectId?: string) {
  setState(sid, prev => ({ ...prev, projectId }));

  // Вход в группу СНАЧАЛА, но с коротким потолком ожидания: снятая после входа история
  // уже покрывает всё до него, поэтому повторный снимок (у активных чатов это мегабайты
  // JSON на каждое открытие) не нужен вовсе. На живом соединении join занимает десятки
  // миллисекунд и первый показ ленты не задерживает.
  let joinDone = false;
  const joined = joinTracked(sid).then(() => { joinDone = true; });
  // История — приоритет и грузится НЕЗАВИСИМО от SignalR. Офлайн соединение
  // может «зависнуть» в Reconnecting, поэтому join не должен блокировать историю.
  await Promise.race([
    joined.catch(() => { /* не вошли — читаем историю и лечимся снимком после join */ }),
    new Promise(r => setTimeout(r, JOIN_WAIT_MS)),
  ]);
  // Снимок на момент старта запроса истории: позже join мог доехать сам, но история
  // к тому времени уже снята — и дыру закрывать всё равно придётся снимком.
  const joinedBeforeHistory = joinDone;

  try {
    const raw = await loadHistory(sid, projectId);
    const items = normalizeFor(sid, raw);
    setState(sid, prev => {
      // Сервер — источник истины: используем его данные, если история новее
      // (сверка без live-only элементов — см. serverHistoryNewer). Иначе оставляем
      // живые сообщения от стриминга (race condition при активном ходе).
      if (!serverHistoryNewer(items, prev.items)) return { ...prev, isHistoryLoading: false };
      return {
        ...prev, items, isHistoryLoading: false,
        isWaiting: historyTurnFinished(items) ? false : prev.isWaiting,
      };
    });
  } catch {
    // История недоступна — не блокируем работу
    setState(sid, prev => ({ ...prev, isHistoryLoading: false }));
  }

  // Офлайн промис join не зарезолвится — это нормально, при reconnect перезайдём.
  joined
    .then(() => {
      // Снимок ПОСЛЕ входа в группу закрывает дыру «история снята — событий ещё не шлют».
      // Нужен только когда в группу мы вошли уже ПОСЛЕ снятия истории (join не уложился
      // в JOIN_WAIT_MS): иначе история и так снята из-под живой подписки.
      if (!joinedBeforeHistory) return reloadHistory(sid, projectId);
    })
    .then(() => refreshTeamImplement(sid))
    .catch(() => { /* офлайн — остаёмся без группы, читаем из кэша */ });
}

// --- React-хук ---

export function useSession(sessionId: string | null, projectId?: string, isGroup?: boolean) {
  ensureHandler();
  const [, setTick] = useState(0);

  useEffect(() => {
    if (!sessionId) return;

    const listeners = _listeners.get(sessionId) ?? new Set<() => void>();
    _listeners.set(sessionId, listeners);
    const notify = () => setTick(n => n + 1);
    listeners.add(notify);

    // Признак группового чата — до загрузки истории (влияет на normalizeHistory).
    // Только от потребителя, который его знает: useSessionArtifacts зовёт хук без
    // isGroup и не должен перетирать флаг, выставленный ChatPanel.
    if (isGroup !== undefined && getState(sessionId).isGroup !== isGroup)
      setState(sessionId, prev => ({ ...prev, isGroup }));

    ensureJoined(sessionId, projectId);

    // При переключении на уже-присоединённую сессию подтягиваем историю с сервера.
    // Нужно чтобы после завершённых ходов (пока был открыт другой чат) данные были актуальны,
    // и в занятом чате тоже — сервер мог дописать историю (например карточку отчёта делегированной
    // задачи), пока по нему не пришло ни одного live-события.
    const st = getState(sessionId);
    if (st.isJoined) {
      // Повторный вход дёшев и идемпотентен, зато чинит членство в группе, если она
      // потерялась незаметно (переподключение хаба, рестарт сервера), и приносит
      // авторитетный статус реплеем — открытие чата всегда лечит рассинхрон.
      reconcile(sessionId);
      reloadHistory(sessionId, projectId);
      // Live-состояние режима могло устареть, пока чат был закрыт (события шли мимо
      // либо их реплея нет вовсе) — заход в чат подтягивает авторитетный снимок
      void refreshTeamImplement(sessionId);
    }
    // Появился зритель — у занятого чата poll без мутации состояния не завёлся бы
    syncWaitPoll();

    return () => {
      listeners.delete(notify);
      // Отпускаем сессию только когда её не смотрит больше ни один компонент: ChatPanel и
      // ArtifactsPanel (useSessionArtifacts) делят одну сессию, и уход одного не должен рвать
      // realtime другого. Последний ушедший снимает счётчик зрителей (сервер сможет слать
      // push/тост) и сбрасывает isJoined для перезахода при возврате. Из группы SignalR при
      // этом выводит СЕРВЕР и только если ход не идёт (SessionHub.LeaveSession): события
      // незавершённого хода продолжают капать сюда в фоне, иначе лента останется с обрывком.
      if ((_listeners.get(sessionId)?.size ?? 0) === 0) {
        leaveSession(sessionId);
        setState(sessionId, prev => ({ ...prev, isJoined: false }));
        evictColdSessions();
      }
    };
  }, [sessionId, projectId, isGroup]);

  const state = sessionId ? getState(sessionId) : { items: [] as ChatItem[], isWaiting: false, isJoined: false, isHistoryLoading: false, rateLimits: {} as Record<string, RateLimitInfo>, isCompacting: false, compactNote: undefined as string | undefined, workLoop: undefined as WorkLoopState | undefined, teamImplement: undefined as TeamImplementState | undefined, teamPlanning: undefined as { startedAt: number; personaId?: string | null } | null | undefined, teamWavePulse: undefined as TeamWavePulse | undefined, promptSuggestion: null as string | null, pending: [] as PendingChatMessage[], composerRestore: null as ComposerRestore | null };

  // Снять сообщение из очереди (крестик на карточке-призраке). Ответ сервера придёт
  // событием pending_messages — локально состояние не правим, чтобы не разъехалось.
  const cancelPending = useCallback(async (messageId: string) => {
    if (!sessionId) return;
    try { await api.sessions.cancelPending(sessionId, messageId); }
    catch { /* уже доставлено или сеть — снимок от сервера всё поправит */ }
  }, [sessionId]);

  // Прервать идущий ход и доставить ждущее сейчас (кнопка на карточке очереди). Элемент
  // 'interrupted' ставим оптимистично, как по «Стоп»: убитый ход пришлёт голый exited, и без
  // этой отметки редьюсер счёл бы его аварией и нарисовал «AI завершился неожиданно».
  // isWaiting не гасим — ждущее сообщение сейчас уйдёт в работу, ход продолжается.
  const preemptForPending = useCallback(async () => {
    if (!sessionId) return;
    let added = false;
    _preempted.add(sessionId);
    setState(sessionId, prev => {
      if (turnAlreadyEnded(prev.items)) return prev;
      added = true;
      return { ...prev, items: [...prev.items, { kind: 'interrupted' }] };
    });
    try { await api.sessions.preemptForPending(sessionId); }
    catch (e) {
      // Запрос не удался. 409 — сервер точно НИЧЕГО не прерывал (ход кончился сам либо
      // очередь пуста): снимаем и отметку, и флаг. Держать флаг тут нельзя — живого хода
      // нет, погаснуть ему не на чем до конца СЛЕДУЮЩЕГО хода, и его голый exited (реальная
      // авария) нарисовался бы как «Прервано». Прочие ошибки (сеть, потерянный ответ при
      // состоявшемся перебое) неотличимы от живого прерывания — там флаг оставляем, иначе
      // exited убитого хода дал бы ложную аварию; он погаснет на ближайшем конце хода.
      // Отметку в ленте снимаем в обоих случаях и только если хвост всё ещё наш — за время
      // запроса лента могла уехать вперёд.
      if ((e as { status?: number } | null)?.status === 409) _preempted.delete(sessionId);
      if (added) setState(sessionId, prev =>
        prev.items[prev.items.length - 1]?.kind === 'interrupted'
          ? { ...prev, items: prev.items.slice(0, -1) }
          : prev);
    }
  }, [sessionId]);

  // Команда composer_restore отработана (текст и вложения поставил Composer, режим — ChatPanel):
  // гасим её, чтобы возврат в чат не подставил в поле ввода уже отправленное сообщение.
  const consumeRestore = useCallback(() => {
    if (!sessionId) return;
    if (getState(sessionId).composerRestore === null) return; // гасить нечего — не будим подписчиков
    setState(sessionId, consumeComposerRestore);
  }, [sessionId]);

  const send = useCallback(async (text: string, attachedPaths: string[] = [], mode?: string, opts?: { auto?: boolean }) => {
    if (!sessionId) return;
    const auto = opts?.auto ?? false;
    // Блокируем композер сразу (пошёл обмен с хабом), но баллон НЕ добавляем до исхода —
    // «честная очередь»: если чат занят, сервер вернёт 'queued', сообщение встанет в видимую
    // очередь (карточку даст снимок pending_messages), а доставленное вернётся user_message.
    // Опережающий баллон при queued дал бы зависший дубль — гонка «отправил в момент
    // завершения хода». promptSuggestion сбрасываем: обычный ход идёт в обход редьюсера.
    _sending.add(sessionId);
    setState(sessionId, prev => ({ ...prev, isWaiting: true, promptSuggestion: null }));
    try {
      // Всегда подтверждаем членство в группе перед отправкой:
      // защита от потери группы при переподключении или переключении проекта
      await joinTracked(sessionId);
      const outcome = await sendMessage(sessionId, text, attachedPaths, mode, auto);
      // 'started' — ход запущен: рисуем оптимистичный баллон (как раньше). Авто-ходы сервер
      // рассылает user_message в session-группу, поэтому их не дублируем.
      // 'queued' — баллон не нужен: карточку даст pending_messages, isWaiting удержит ход.
      if (outcome === 'started' && !auto) {
        setState(sessionId, prev => ({ ...prev, items: [...prev.items, { kind: 'user_message', text, attachedPaths, ts: Date.now() }] }));
      }
      // 'queued-preempted' — ради этого сообщения сервер прервал идущий ход (тот ждал ответа
      // человека либо шёл цикл «до готово»). Отмечаем прерывание, как по «Стоп»: убитый ход
      // пришлёт голый exited, и без отметки редьюсер счёл бы его аварией «AI завершился
      // неожиданно». isWaiting держим — ход продолжится доставленным сообщением.
      if (outcome === 'queued-preempted') {
        _preempted.add(sessionId);
        setState(sessionId, prev => turnAlreadyEnded(prev.items)
          ? prev
          : { ...prev, items: [...prev.items, { kind: 'interrupted' }] });
      }
    } catch (err) {
      // JoinSession мог упасть потому, что чат удалён на сервере или у нас нет к нему
      // доступа. Сырой текст HubException («Доступ запрещён» / «Чат не найден») в ленте
      // не помогает — переводим в понятное сообщение и снимаем canRetry, потому что повтор
      // той же команды к удалённому/чужому чату не вылечит. Текст пользователя при этом
      // остаётся в композере (sendMessage ещё не вызывался) — можно переключиться на
      // другой чат и попробовать там.
      const raw = err instanceof Error ? err.message : String(err);
      const isGhost = raw.includes('Чат не найден');
      const isForbidden = raw.includes('Доступ запрещён');
      const text = isGhost
        ? 'Чат не найден. Возможно, он был удалён — откройте другой чат.'
        : isForbidden
        ? 'Нет доступа к чату.'
        : `Ошибка отправки: ${raw}`;
      setState(sessionId, prev => ({
        ...prev,
        isWaiting: false,
        items: [...prev.items, {
          kind: 'error' as const,
          text,
          canRetry: !isGhost && !isForbidden,
        }],
      }));
    } finally {
      _sending.delete(sessionId);
    }
  }, [sessionId]);

  // Локальный разделитель «Теперь отвечает: …» при смене собеседника по ходу разговора.
  // Client-side сахар: не с сервера, не переживает перезагрузку (после неё авторство
  // реплик приходит из истории — personaId в text-сообщениях). prevPersonaId «замораживает»
  // прежнего автора у live-реплик без personaId, чтобы их аватар не сменился на нового.
  const noteCompanionSwitch = useCallback((label: string, prevPersonaId?: string | null) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      items: [
        ...prev.items.map(it =>
          prevPersonaId && it.kind === 'text' && !it.personaId ? { ...it, personaId: prevPersonaId } : it),
        { kind: 'companion_switched', label },
      ],
    }));
  }, [sessionId]);

  const allowPermission = useCallback(async (requestId: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: false,
      items: prev.items.map(item =>
        item.kind === 'permission_request' && item.requestId === requestId
          ? { ...item, resolved: true, decision: 'allowed' as const } : item
      ),
    }));
    await joinTracked(sessionId); // гарантируем группу перед ответом
    await respondPermission(sessionId, requestId, 'allow');
  }, [sessionId]);

  const denyPermission = useCallback(async (requestId: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: false,
      items: prev.items.map(item =>
        item.kind === 'permission_request' && item.requestId === requestId
          ? { ...item, resolved: true, decision: 'denied' as const } : item
      ),
    }));
    await joinTracked(sessionId); // гарантируем группу перед ответом
    await respondPermission(sessionId, requestId, 'deny');
  }, [sessionId]);

  // Разрешить и больше не спрашивать про этот инструмент в текущей сессии
  const allowAlways = useCallback(async (requestId: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: false,
      items: prev.items.map(item =>
        item.kind === 'permission_request' && item.requestId === requestId
          ? { ...item, resolved: true, decision: 'always' as const } : item
      ),
    }));
    await joinTracked(sessionId); // гарантируем группу перед ответом
    await respondPermission(sessionId, requestId, 'allow_always');
  }, [sessionId]);

  // Ручное сворачивание контекста (/compact). Оптимистично ставим ожидание —
  // фактический статус придёт через status_changed и compact_status
  const compact = useCallback(async () => {
    if (!sessionId) return;
    setState(sessionId, prev => ({ ...prev, isWaiting: true, isCompacting: true, compactNote: undefined }));
    try {
      await joinTracked(sessionId); // гарантируем группу перед отправкой
      await compactSession(sessionId);
    } catch (err) {
      setState(sessionId, prev => ({
        ...prev,
        isWaiting: false,
        isCompacting: false,
        items: [...prev.items, {
          kind: 'error' as const,
          text: `Не удалось сжать контекст: ${err instanceof Error ? err.message : String(err)}`,
          canRetry: false,
        }],
      }));
    }
  }, [sessionId]);

  const interrupt = useCallback(() => {
    if (!sessionId) return;
    interruptSession(sessionId);
    // Оптимистично помечаем ход как остановленный пользователем; подсказка прерванного хода неактуальна
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: false,
      promptSuggestion: null,
      items: prev.items[prev.items.length - 1]?.kind === 'interrupted'
        ? prev.items
        : [...prev.items, { kind: 'interrupted' }],
    }));
  }, [sessionId]);

  // Ответ на уточняющий вопрос Claude (AskUserQuestion)
  const answerQuestion = useCallback(async (toolUseId: string, answerText: string) => {
    if (!sessionId) return;
    // Сохраняем выбранные ответы в элемент, чтобы показать сводку даже после перезагрузки
    let answers: Record<string, string | string[]> | undefined;
    try { answers = JSON.parse(answerText)?.answers; } catch { /* оставим undefined */ }
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: true,
      items: prev.items.map(item =>
        item.kind === 'ask_question' && item.toolUseId === toolUseId
          ? { ...item, resolved: true, answers } : item
      ),
    }));
    await joinTracked(sessionId); // гарантируем группу перед ответом
    await sendAnswer(sessionId, toolUseId, answerText);
  }, [sessionId]);

  const respondPlan = useCallback(async (requestId: string, approve: boolean, feedback?: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: true,
      items: prev.items.map(item =>
        item.kind === 'plan_review' && item.requestId === requestId
          ? { ...item, resolved: true, approved: approve, feedback } : item
      ),
    }));
    await joinTracked(sessionId); // гарантируем группу перед ответом
    await sendPlanDecision(sessionId, requestId, approve, feedback);
  }, [sessionId]);

  // Решение по карточке плана командной реализации. Оптимистично применяем видимую
  // часть (смена исполнителя / решённое состояние) — сервер переиздаст карточку
  // событием team_plan с тем же planId и приведёт её к своему состоянию (у edit —
  // резолвит с supersededBy, точный номер версии оптимистично не угадываем).
  const respondTeamPlan = useCallback(async (planId: string, decision: TeamPlanDecision,
    subtaskId?: string, executorPersonaId?: string, feedback?: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      items: prev.items.map(item => {
        if (item.kind !== 'team_plan' || item.planId !== planId) return item;
        if (decision === 'reassign') {
          if (!subtaskId || !executorPersonaId) return item;
          return {
            ...item,
            plan: {
              ...item.plan,
              subtasks: item.plan.subtasks.map(s =>
                s.id === subtaskId ? { ...s, executorPersonaId } : s),
            },
          };
        }
        return { ...item, resolved: true, approved: decision === 'run' };
      }),
    }));
    await joinTracked(sessionId); // гарантируем группу перед ответом
    await sendTeamPlanDecision(sessionId, planId, decision, subtaskId, executorPersonaId, feedback);
  }, [sessionId]);

  // Решение по карточке остановки. Гасим карточку оптимистично — сервер переиздаст её
  // событием team_escalation с тем же escalationId и приведёт к своему состоянию
  const respondTeamEscalation = useCallback(async (escalationId: string,
    actionId?: string, comment?: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      items: prev.items.map(item =>
        item.kind === 'team_escalation' && item.escalationId === escalationId
          ? { ...item, escalation: { ...item.escalation, resolved: true, chosenActionId: actionId ?? null } }
          : item
      ),
    }));
    await joinTracked(sessionId); // гарантируем группу перед ответом
    await sendTeamEscalationDecision(sessionId, escalationId, actionId, comment);
  }, [sessionId]);

  const toggleThinking = useCallback((index: number) => {
    if (!sessionId) return;
    updateItems(sessionId, items => items.map((item, i) =>
      i === index && item.kind === 'thinking' ? { ...item, expanded: !item.expanded } : item
    ));
  }, [sessionId]);

  // Смена режима прав на лету (переключатель композера) — применяется к идущему ходу,
  // не дожидаясь отправки следующего сообщения
  const changeMode = useCallback((mode: string) => {
    if (!sessionId) return;
    sendSetMode(sessionId, mode).catch(() => {});
  }, [sessionId]);

  return { items: state.items, isWaiting: state.isWaiting, isJoined: state.isJoined, isHistoryLoading: state.isHistoryLoading, rateLimits: state.rateLimits, isCompacting: state.isCompacting, compactNote: state.compactNote, workLoop: state.workLoop, teamImplement: state.teamImplement, teamPlanning: state.teamPlanning, teamWavePulse: state.teamWavePulse, promptSuggestion: state.promptSuggestion, pending: state.pending, composerRestore: state.composerRestore, consumeRestore, send, allowPermission, denyPermission, allowAlways, answerQuestion, respondPlan, respondTeamPlan, respondTeamEscalation, interrupt, compact, toggleThinking, noteCompanionSwitch, changeMode, cancelPending, preemptForPending };
}
