import { useEffect, useState, useCallback } from 'react';
import type { ChatItem, ServerMessage, RateLimitInfo, WorkLoopState } from '../types';
import { joinSession, joinProject, leaveSession, onMessage, onReconnected, sendMessage, respondPermission, interruptSession, compactSession, answerQuestion as sendAnswer, respondPlan as sendPlanDecision, setMode as sendSetMode } from '../lib/signalr';
import { setRecallManifest } from '../lib/recallManifest';
import { api } from '../lib/api';
import { applyServerMessage, normalizeHistory, serverHistoryNewer, initialChatState, type ChatState } from '../lib/chatReducer';

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

// История чата: у проектной сессии — через /projects/{id}/sessions, у чата вне проекта — через /chats
const loadHistory = (sid: string, projectId?: string) =>
  projectId ? api.sessions.getHistory(projectId, sid) : api.chats.getHistory(sid);

// Нормализация истории с опциями текущей сессии (derive разделителей в групповом чате)
const normalizeFor = (sid: string, raw: unknown[]) =>
  normalizeHistory(raw, { deriveSpeakers: getState(sid).isGroup });

function getState(sid: string): SessionState {
  if (!_store.has(sid)) _store.set(sid, { ...initialChatState(), isJoined: false, isHistoryLoading: true });
  return _store.get(sid)!;
}

function setState(sid: string, updater: (prev: SessionState) => SessionState) {
  _store.set(sid, updater(getState(sid)));
  _listeners.get(sid)?.forEach(fn => fn());
}

function updateItems(sid: string, fn: (items: ChatItem[]) => ChatItem[]) {
  setState(sid, prev => ({ ...prev, items: fn(prev.items) }));
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
      try { await joinProject(pid); } catch { }
    }

    for (const [sid, s] of _store) {
      if (!s.isJoined) continue;
      try {
        await joinSession(sid);
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
        }
      } catch { /* пропускаем — не блокируем остальные */ }
    }
  });

  onMessage((msg: ServerMessage) => {
    const sid = msg.sessionId;
    if (!sid) return;

    // Чистая часть — в редьюсере (lib/chatReducer.ts). Если состояние не изменилось
    // (вернулась та же ссылка) — подписчиков не будим.
    const prev = getState(sid);
    const next = applyServerMessage(prev, msg);
    if (next !== prev) {
      _store.set(sid, next);
      _listeners.get(sid)?.forEach(fn => fn());
    }

    // Манифест recall (F3): что персона подтянула в ход — в отдельный стор для вкладки контекста
    if (msg.type === 'recall_manifest') setRecallManifest(sid, msg.items);

    // Побочный эффект вне редьюсера: при переходе в active перезагружаем историю —
    // клиент мог пропустить text_delta/tool_use пока был оффлайн или не в группе
    if (msg.type === 'status_changed' && msg.status === 'active') {
      const projectId = getState(sid).projectId;
      loadHistory(sid, projectId).then(raw => {
        const serverItems = normalizeFor(sid, raw);
        setState(sid, p => {
          if (!serverHistoryNewer(serverItems, p.items)) return p;
          return { ...p, items: serverItems };
        });
      }).catch(() => {});
    }
  });
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

  // История — приоритет и грузится НЕЗАВИСИМО от SignalR. Офлайн соединение
  // может «зависнуть» в Reconnecting, поэтому join не должен блокировать историю.
  try {
    const raw = await loadHistory(sid, projectId);
    const items = normalizeFor(sid, raw);
    setState(sid, prev => {
      // Сервер — источник истины: используем его данные, если история новее
      // (сверка без live-only элементов — см. serverHistoryNewer). Иначе оставляем
      // живые сообщения от стриминга (race condition при активном ходе).
      if (!serverHistoryNewer(items, prev.items)) return { ...prev, isHistoryLoading: false };
      return { ...prev, items, isHistoryLoading: false };
    });
  } catch {
    // История недоступна — не блокируем работу
    setState(sid, prev => ({ ...prev, isHistoryLoading: false }));
  }

  // Присоединение к группе — фоном, не блокирует чтение истории.
  // Офлайн промис не зарезолвится — это нормально, при reconnect перезайдём.
  joinSession(sid)
    .then(() => setState(sid, prev => ({ ...prev, isJoined: true })))
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
    // Нужно чтобы после завершённых ходов (пока был открыт другой чат) данные были актуальны.
    const st = getState(sessionId);
    if (st.isJoined && !st.isWaiting) {
      loadHistory(sessionId, projectId).then(raw => {
        const serverItems = normalizeFor(sessionId, raw);
        setState(sessionId, prev => {
          if (!serverHistoryNewer(serverItems, prev.items)) return prev;
          return { ...prev, items: serverItems };
        });
      }).catch(() => {});
    }

    return () => {
      listeners.delete(notify);
      // Покидаем группу SignalR только когда сессию не смотрит больше ни один компонент:
      // ChatPanel и ArtifactsPanel (useSessionArtifacts) делят одну сессию, и уход одного
      // не должен рвать realtime другого. Последний ушедший снимает счётчик зрителей
      // (сервер сможет слать push/тост) и сбрасывает isJoined для перезахода при возврате.
      if ((_listeners.get(sessionId)?.size ?? 0) === 0) {
        leaveSession(sessionId);
        setState(sessionId, prev => ({ ...prev, isJoined: false }));
      }
    };
  }, [sessionId, projectId, isGroup]);

  const state = sessionId ? getState(sessionId) : { items: [] as ChatItem[], isWaiting: false, isJoined: false, isHistoryLoading: false, rateLimits: {} as Record<string, RateLimitInfo>, isCompacting: false, compactNote: undefined as string | undefined, workLoop: undefined as WorkLoopState | undefined, promptSuggestion: null as string | null };

  const send = useCallback(async (text: string, attachedPaths: string[] = [], mode?: string, opts?: { auto?: boolean }) => {
    if (!sessionId) return;
    const auto = opts?.auto ?? false;
    // Авто-ходы (командные механики, «продолжить обсуждение») НЕ добавляем оптимистично:
    // сервер рассылает их событием user_message в session-группу — иначе дубль в ленте.
    // promptSuggestion сбрасываем здесь: обычный ход идёт в обход редьюсера, а stale-подсказка
    // не должна всплывать, если новый ход её не породил (холодный кэш)
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: true,
      promptSuggestion: null,
      items: auto ? prev.items : [...prev.items, { kind: 'user_message', text, attachedPaths }],
    }));
    try {
      // Всегда подтверждаем членство в группе перед отправкой:
      // защита от потери группы при переподключении или переключении проекта
      await joinSession(sessionId);
      setState(sessionId, prev => ({ ...prev, isJoined: true }));
      await sendMessage(sessionId, text, attachedPaths, mode, auto);
    } catch (err) {
      setState(sessionId, prev => ({
        ...prev,
        isWaiting: false,
        items: [...prev.items, {
          kind: 'error' as const,
          text: `Ошибка отправки: ${err instanceof Error ? err.message : String(err)}`,
          canRetry: true,
        }],
      }));
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
    await joinSession(sessionId); // гарантируем группу перед ответом
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
    await joinSession(sessionId); // гарантируем группу перед ответом
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
    await joinSession(sessionId); // гарантируем группу перед ответом
    await respondPermission(sessionId, requestId, 'allow_always');
  }, [sessionId]);

  // Ручное сворачивание контекста (/compact). Оптимистично ставим ожидание —
  // фактический статус придёт через status_changed и compact_status
  const compact = useCallback(async () => {
    if (!sessionId) return;
    setState(sessionId, prev => ({ ...prev, isWaiting: true, isCompacting: true, compactNote: undefined }));
    try {
      await joinSession(sessionId); // гарантируем группу перед отправкой
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
    await joinSession(sessionId); // гарантируем группу перед ответом
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
    await joinSession(sessionId); // гарантируем группу перед ответом
    await sendPlanDecision(sessionId, requestId, approve, feedback);
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

  return { items: state.items, isWaiting: state.isWaiting, isJoined: state.isJoined, isHistoryLoading: state.isHistoryLoading, rateLimits: state.rateLimits, isCompacting: state.isCompacting, compactNote: state.compactNote, workLoop: state.workLoop, promptSuggestion: state.promptSuggestion, send, allowPermission, denyPermission, allowAlways, answerQuestion, respondPlan, interrupt, compact, toggleThinking, noteCompanionSwitch, changeMode };
}
