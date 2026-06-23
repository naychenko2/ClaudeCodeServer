import { useEffect, useState, useCallback } from 'react';
import type { ChatItem, ServerMessage } from '../types';
import { joinSession, joinProject, onMessage, onReconnected, sendMessage, respondPermission, interruptSession, answerQuestion as sendAnswer, respondPlan as sendPlanDecision } from '../lib/signalr';
import { api } from '../lib/api';

// --- Модульный персистентный стор ---
// Состояние живёт на уровне модуля и переживает переключение между сессиями.
// Компоненты подписываются на обновления своей сессии, но при анмаунте
// не отписываются от SignalR — сессия продолжает получать сообщения в фоне.

interface SessionState {
  items: ChatItem[];
  isWaiting: boolean;
  isJoined: boolean;
  projectId?: string;
}

const _store = new Map<string, SessionState>();
const _listeners = new Map<string, Set<() => void>>();
const _joining = new Map<string, Promise<void>>();

function getState(sid: string): SessionState {
  if (!_store.has(sid)) _store.set(sid, { items: [], isWaiting: false, isJoined: false });
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
        // Для сессий что ждали — подтягиваем историю (пропущенные сообщения)
        if (wasWaiting.has(sid) && s.projectId) {
          try {
            const raw = await api.sessions.getHistory(s.projectId, sid);
            const items = (raw as any[]).map((msg: any): ChatItem => {
              if (msg.kind === 'thinking') return { ...msg, expanded: false };
              if (msg.kind === 'error') return { ...msg, canRetry: false };
              return msg as ChatItem;
            });
            if (items.length > 0) {
              setState(sid, prev => ({
                ...prev,
                items: prev.items.length > items.length ? prev.items : items,
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
    switch (msg.type) {
      case 'session_started':
        if (msg.isResume)
          updateItems(sid, items => items.some(i => i.kind === 'resumed') ? items : [...items, { kind: 'resumed' }]);
        else
          updateItems(sid, items => [...items, { kind: 'session_started', model: msg.model, mode: msg.mode, cwd: msg.cwd, toolCount: msg.toolCount, mcpServers: msg.mcpServers }]);
        break;
      case 'text_delta':
        updateItems(sid, items => {
          const last = items[items.length - 1];
          if (last?.kind === 'text') return [...items.slice(0, -1), { kind: 'text', text: last.text + msg.text }];
          return [...items, { kind: 'text', text: msg.text }];
        });
        break;
      case 'thinking_delta':
        updateItems(sid, items => {
          const last = items[items.length - 1];
          if (last?.kind === 'thinking') return [...items.slice(0, -1), { ...last, text: last.text + msg.text }];
          return [...items, { kind: 'thinking', text: msg.text, expanded: false }];
        });
        break;
      case 'tool_use':
        // Дедуп по id: ранняя карточка из стрима + финальный assistant с тем же id → обновляем
        updateItems(sid, items => {
          const idx = items.findIndex(it => it.kind === 'tool_use' && it.id === msg.id);
          if (idx >= 0) {
            const next = [...items];
            const ex = next[idx] as Extract<ChatItem, { kind: 'tool_use' }>;
            next[idx] = { ...ex, name: msg.name, input: msg.input, streamingArg: undefined };
            return next;
          }
          return [...items, { kind: 'tool_use', id: msg.id, name: msg.name, input: msg.input, parentToolUseId: msg.parentToolUseId }];
        });
        break;
      case 'tool_input_delta':
        updateItems(sid, items => items.map(it =>
          it.kind === 'tool_use' && it.id === msg.toolUseId ? { ...it, streamingArg: msg.partialJson } : it
        ));
        break;
      case 'tool_result':
        updateItems(sid, items => items.map(item =>
          item.kind === 'tool_use' && item.id === msg.toolUseId
            ? { ...item, result: msg.content, isError: msg.isError }
            : item
        ));
        break;
      case 'permission_request':
        setState(sid, prev => ({
          ...prev,
          isWaiting: true,
          items: [...prev.items, {
            kind: 'permission_request',
            requestId: msg.requestId,
            toolName: msg.toolName,
            toolInput: msg.toolInput,
            resolved: false,
          }],
        }));
        break;
      case 'ask_question':
        setState(sid, prev => ({
          ...prev,
          isWaiting: true,
          items: [...prev.items, {
            kind: 'ask_question',
            toolUseId: msg.toolUseId,
            input: msg.input,
            resolved: false,
          }],
        }));
        break;
      case 'plan_review':
        setState(sid, prev => ({
          ...prev,
          isWaiting: true,
          items: [...prev.items, {
            kind: 'plan_review',
            requestId: msg.requestId,
            plan: msg.plan,
            resolved: false,
          }],
        }));
        break;
      case 'file_changed':
        updateItems(sid, items => [...items, { kind: 'file_changed', path: msg.path, added: msg.added, removed: msg.removed }]);
        break;
      case 'result':
        setState(sid, prev => ({
          ...prev,
          isWaiting: false,
          items: [...prev.items, { kind: 'result', subtype: msg.subtype, durationMs: msg.durationMs, numTurns: msg.numTurns, usage: msg.usage, totalCostUsd: msg.totalCostUsd, apiErrorStatus: msg.apiErrorStatus, permissionDenials: msg.permissionDenials }],
        }));
        break;
      case 'truncated':
        updateItems(sid, items => [...items, { kind: 'truncated' }]);
        break;
      case 'redacted_thinking':
        updateItems(sid, items => [...items, { kind: 'redacted_thinking' }]);
        break;
      case 'rate_limit':
        // Реальный лимит/предупреждение во время хода (status != allowed). isWaiting не трогаем.
        // Дедуп: если последний элемент — такой же баннер лимита, не дублируем.
        updateItems(sid, items => {
          const last = items[items.length - 1];
          if (last?.kind === 'rate_limit' && last.limitType === msg.limitType && last.status === msg.status) return items;
          return [...items, { kind: 'rate_limit', limitType: msg.limitType, resetsAt: msg.resetsAt, status: msg.status }];
        });
        break;
      case 'compact_boundary':
        updateItems(sid, items => [...items, { kind: 'compact_boundary', trigger: msg.trigger, preTokens: msg.preTokens }]);
        break;
      case 'error':
        setState(sid, prev => ({
          ...prev,
          isWaiting: false,
          items: [...prev.items, { kind: 'error', text: msg.text, canRetry: true }],
        }));
        break;
      case 'exited':
        // Процесс claude завершился. Если ждали ответ и не было result/прерывания/ошибки — это аварийный выход.
        setState(sid, prev => {
          const last = prev.items[prev.items.length - 1];
          const abnormal = prev.isWaiting && !(last && (last.kind === 'interrupted' || last.kind === 'error' || last.kind === 'session_ended'));
          return { ...prev, isWaiting: false, items: abnormal ? [...prev.items, { kind: 'session_ended' }] : prev.items };
        });
        break;
      case 'status_changed': {
        // Синхронизируем isWaiting по статусу — работает для всех открытых вкладок/браузеров
        if (msg.status === 'working' || msg.status === 'waiting') {
          setState(sid, prev => ({ ...prev, isWaiting: true }));
        } else if (msg.status === 'active' || msg.status === 'error' || msg.status === 'finished') {
          setState(sid, prev => ({ ...prev, isWaiting: false }));
        }
        // При переходе в active — перезагружаем историю:
        // клиент мог пропустить text_delta/tool_use пока был оффлайн или не в группе
        if (msg.status === 'active') {
          const projectId = getState(sid).projectId;
          if (projectId) {
            api.sessions.getHistory(projectId, sid).then(raw => {
              const serverItems = (raw as any[]).map((m: any): ChatItem => {
                if (m.kind === 'thinking') return { ...m, expanded: false };
                if (m.kind === 'error') return { ...m, canRetry: false };
                return m as ChatItem;
              });
              setState(sid, prev => {
                if (serverItems.length <= prev.items.length) return prev;
                return { ...prev, items: serverItems };
              });
            }).catch(() => {});
          }
        }
        break;
      }
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
  if (projectId) {
    try {
      const raw = await api.sessions.getHistory(projectId, sid);
      const items = (raw as any[]).map((msg: any): ChatItem => {
        if (msg.kind === 'thinking') return { ...msg, expanded: false };
        if (msg.kind === 'error') return { ...msg, canRetry: false };
        return msg as ChatItem;
      });
      setState(sid, prev => {
        // Сервер — источник истины: используем его данные если их больше.
        // Иначе оставляем живые сообщения от стриминга (race condition при активном ходе).
        if (items.length <= prev.items.length) return prev;
        return { ...prev, items };
      });
    } catch {
      // История недоступна — не блокируем работу
    }
  }

  // Присоединение к группе — фоном, не блокирует чтение истории.
  // Офлайн промис не зарезолвится — это нормально, при reconnect перезайдём.
  joinSession(sid)
    .then(() => setState(sid, prev => ({ ...prev, isJoined: true })))
    .catch(() => { /* офлайн — остаёмся без группы, читаем из кэша */ });
}

// --- React-хук ---

export function useSession(sessionId: string | null, projectId?: string) {
  ensureHandler();
  const [, setTick] = useState(0);

  useEffect(() => {
    if (!sessionId) return;

    const listeners = _listeners.get(sessionId) ?? new Set<() => void>();
    _listeners.set(sessionId, listeners);
    const notify = () => setTick(n => n + 1);
    listeners.add(notify);

    ensureJoined(sessionId, projectId);

    // При переключении на уже-присоединённую сессию подтягиваем историю с сервера.
    // Нужно чтобы после завершённых ходов (пока был открыт другой чат) данные были актуальны.
    const st = getState(sessionId);
    if (st.isJoined && projectId && !st.isWaiting) {
      api.sessions.getHistory(projectId, sessionId).then(raw => {
        const serverItems = (raw as any[]).map((m: any): ChatItem => {
          if (m.kind === 'thinking') return { ...m, expanded: false };
          if (m.kind === 'error') return { ...m, canRetry: false };
          return m as ChatItem;
        });
        setState(sessionId, prev => {
          if (serverItems.length <= prev.items.length) return prev;
          return { ...prev, items: serverItems };
        });
      }).catch(() => {});
    }

    return () => {
      listeners.delete(notify);
      // leaveSession не вызываем — сессия продолжает работать в фоне
    };
  }, [sessionId, projectId]);

  const state = sessionId ? getState(sessionId) : { items: [] as ChatItem[], isWaiting: false, isJoined: false };

  const send = useCallback(async (text: string, attachedPaths: string[] = [], mode?: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: true,
      items: [...prev.items, { kind: 'user_message', text, attachedPaths }],
    }));
    try {
      // Всегда подтверждаем членство в группе перед отправкой:
      // защита от потери группы при переподключении или переключении проекта
      await joinSession(sessionId);
      setState(sessionId, prev => ({ ...prev, isJoined: true }));
      await sendMessage(sessionId, text, attachedPaths, mode);
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

  const allowPermission = useCallback(async (requestId: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: false,
      items: prev.items.map(item =>
        item.kind === 'permission_request' && item.requestId === requestId
          ? { ...item, resolved: true } : item
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
          ? { ...item, resolved: true } : item
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
          ? { ...item, resolved: true } : item
      ),
    }));
    await joinSession(sessionId); // гарантируем группу перед ответом
    await respondPermission(sessionId, requestId, 'allow_always');
  }, [sessionId]);

  const interrupt = useCallback(() => {
    if (!sessionId) return;
    interruptSession(sessionId);
    // Оптимистично помечаем ход как остановленный пользователем
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: false,
      items: prev.items[prev.items.length - 1]?.kind === 'interrupted'
        ? prev.items
        : [...prev.items, { kind: 'interrupted' }],
    }));
  }, [sessionId]);

  // Ответ на уточняющий вопрос Claude (AskUserQuestion)
  const answerQuestion = useCallback(async (toolUseId: string, answerText: string) => {
    if (!sessionId) return;
    setState(sessionId, prev => ({
      ...prev,
      isWaiting: true,
      items: prev.items.map(item =>
        item.kind === 'ask_question' && item.toolUseId === toolUseId
          ? { ...item, resolved: true } : item
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

  return { items: state.items, isWaiting: state.isWaiting, isJoined: state.isJoined, send, allowPermission, denyPermission, allowAlways, answerQuestion, respondPlan, interrupt, toggleThinking };
}
