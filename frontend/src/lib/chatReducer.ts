// Чистый редьюсер ленты чата: применяет входящее ServerMessage к состоянию сессии.
// Извлечён из useSession.ts, чтобы логику обработки сообщений можно было
// тестировать без рендера React. Побочные эффекты (загрузка истории, SignalR)
// остаются в хуке; редьюсер только считает следующее состояние.

import type { ChatItem, ServerMessage, RateLimitInfo } from '../types';

// Часть состояния сессии, которой управляет редьюсер
export interface ChatState {
  items: ChatItem[];
  isWaiting: boolean;
  // Лимиты подписки по окнам (five_hour/seven_day/…) — последнее значение, обновляется каждый ход
  rateLimits: Record<string, RateLimitInfo>;
  // Идёт сворачивание контекста (system/status: compacting → compact_result)
  isCompacting: boolean;
  // Мягкое уведомление о последнем компакте (например «нечего сжимать») — не ошибка
  compactNote?: string;
}

export function initialChatState(): ChatState {
  return { items: [], isWaiting: false, rateLimits: {}, isCompacting: false };
}

// Сообщение истории с сервера: сериализованный ChatItem без клиентских UI-полей
// (expanded/canRetry живут только в памяти вкладки и в историю не пишутся)
interface StoredHistoryMessage {
  kind: ChatItem['kind'];
  [field: string]: unknown;
}

// Приводит сырую историю к ChatItem[]: проставляет UI-поля дефолтами —
// thinking свёрнут, error без кнопки повтора (повторять исторические ошибки нельзя)
export function normalizeHistory(raw: unknown[]): ChatItem[] {
  return raw.map((msg): ChatItem => {
    const m = msg as StoredHistoryMessage;
    if (m.kind === 'thinking') return { ...m, expanded: false } as unknown as ChatItem;
    if (m.kind === 'error') return { ...m, canRetry: false } as unknown as ChatItem;
    return m as unknown as ChatItem;
  });
}

// Применяет сообщение сервера к состоянию. Возвращает prev той же ссылкой,
// если сообщение состояние не меняет (подписчиков можно не будить).
// Generic: работает и с ChatState, и с расширяющим его SessionState хука.
export function applyServerMessage<S extends ChatState>(prev: S, msg: ServerMessage): S {
  const withItems = (items: ChatItem[]): S => ({ ...prev, items });

  switch (msg.type) {
    case 'session_started':
      if (msg.isResume)
        return prev.items.some(i => i.kind === 'resumed')
          ? prev
          : withItems([...prev.items, { kind: 'resumed' }]);
      return withItems([...prev.items, { kind: 'session_started', model: msg.model, mode: msg.mode, cwd: msg.cwd, toolCount: msg.toolCount, mcpServers: msg.mcpServers }]);

    case 'text_delta': {
      const last = prev.items[prev.items.length - 1];
      if (last?.kind === 'text') return withItems([...prev.items.slice(0, -1), { kind: 'text', text: last.text + msg.text }]);
      return withItems([...prev.items, { kind: 'text', text: msg.text }]);
    }

    case 'thinking_delta': {
      const last = prev.items[prev.items.length - 1];
      if (last?.kind === 'thinking') return withItems([...prev.items.slice(0, -1), { ...last, text: last.text + msg.text }]);
      return withItems([...prev.items, { kind: 'thinking', text: msg.text, expanded: false }]);
    }

    case 'tool_use': {
      // Дедуп по id: ранняя карточка из стрима + финальный assistant с тем же id → обновляем
      const idx = prev.items.findIndex(it => it.kind === 'tool_use' && it.id === msg.id);
      if (idx >= 0) {
        const next = [...prev.items];
        const ex = next[idx] as Extract<ChatItem, { kind: 'tool_use' }>;
        next[idx] = { ...ex, name: msg.name, input: msg.input, streamingArg: undefined, parentToolUseId: msg.parentToolUseId ?? ex.parentToolUseId };
        return withItems(next);
      }
      return withItems([...prev.items, { kind: 'tool_use', id: msg.id, name: msg.name, input: msg.input, parentToolUseId: msg.parentToolUseId }]);
    }

    case 'tool_input_delta':
      return withItems(prev.items.map(it =>
        it.kind === 'tool_use' && it.id === msg.toolUseId ? { ...it, streamingArg: msg.partialJson } : it
      ));

    case 'tool_result':
      return withItems(prev.items.map(item =>
        item.kind === 'tool_use' && item.id === msg.toolUseId
          ? { ...item, result: msg.content, isError: msg.isError }
          : item
      ));

    case 'permission_request':
      return {
        ...prev,
        isWaiting: true,
        items: [...prev.items, {
          kind: 'permission_request',
          requestId: msg.requestId,
          toolName: msg.toolName,
          toolInput: msg.toolInput,
          resolved: false,
        }],
      };

    case 'ask_question':
      return {
        ...prev,
        isWaiting: true,
        items: [...prev.items, {
          kind: 'ask_question',
          toolUseId: msg.toolUseId,
          input: msg.input,
          resolved: false,
        }],
      };

    case 'plan_review':
      return {
        ...prev,
        isWaiting: true,
        items: [...prev.items, {
          kind: 'plan_review',
          requestId: msg.requestId,
          plan: msg.plan,
          resolved: false,
        }],
      };

    case 'file_changed':
      return withItems([...prev.items, { kind: 'file_changed', path: msg.path, added: msg.added, removed: msg.removed }]);

    case 'result':
      return {
        ...prev,
        isWaiting: false,
        items: [...prev.items, { kind: 'result', subtype: msg.subtype, durationMs: msg.durationMs, numTurns: msg.numTurns, usage: msg.usage, totalCostUsd: msg.totalCostUsd, apiErrorStatus: msg.apiErrorStatus, permissionDenials: msg.permissionDenials }],
      };

    case 'fal_cost':
      // Стоимость генерации fal.ai приходит асинхронно. Дедуп по requestId
      // (run_model + get_job_result несут один id; возможен повтор из истории).
      return prev.items.some(it => it.kind === 'fal_cost' && it.requestId === msg.requestId)
        ? prev
        : withItems([...prev.items, { kind: 'fal_cost', requestId: msg.requestId, endpointId: msg.endpointId, costUsd: msg.costUsd, outputUnits: msg.outputUnits, unitPrice: msg.unitPrice }]);

    case 'truncated':
      return withItems([...prev.items, { kind: 'truncated' }]);

    case 'redacted_thinking':
      return withItems([...prev.items, { kind: 'redacted_thinking' }]);

    case 'rate_limit':
      // Телеметрия использования подписки (приходит каждый ход). Храним последнее значение
      // по каждому окну; индикатор/строку рисует ChatPanel из этого состояния (не в ленте).
      return {
        ...prev,
        rateLimits: {
          ...prev.rateLimits,
          [msg.limitType]: {
            limitType: msg.limitType,
            utilization: msg.utilization,
            resetsAt: msg.resetsAt,
            status: msg.status,
            isUsingOverage: msg.isUsingOverage,
            overageStatus: msg.overageStatus,
            overageResetsAt: msg.overageResetsAt,
          },
        },
      };

    case 'compact_boundary':
      // Успешное сжатие — сбрасываем note о «нечего сжимать»
      return {
        ...prev,
        isCompacting: false,
        compactNote: undefined,
        items: [...prev.items, { kind: 'compact_boundary', trigger: msg.trigger, preTokens: msg.preTokens, postTokens: msg.postTokens }],
      };

    case 'compact_status':
      // Ход компакции: compacting → началась; compact_result — завершилась.
      // «Not enough messages» — не ошибка, а «сжимать пока нечего»: показываем мягко (note), без красной плашки.
      if (msg.status === 'compacting') {
        return { ...prev, isCompacting: true };
      } else if (msg.compactResult) {
        const soft = msg.compactResult === 'failed' && /not enough/i.test(msg.compactError ?? '');
        return {
          ...prev,
          isCompacting: false,
          compactNote: soft ? 'Пока нечего сжимать — слишком мало сообщений.' : undefined,
          items: (msg.compactResult === 'failed' && !soft)
            ? [...prev.items, { kind: 'error', text: `Не удалось сжать контекст: ${msg.compactError ?? 'неизвестная ошибка'}`, canRetry: false }]
            : prev.items,
        };
      }
      return prev;

    case 'error':
      return {
        ...prev,
        isWaiting: false,
        items: [...prev.items, { kind: 'error', text: msg.text, canRetry: true }],
      };

    case 'exited': {
      // Процесс claude завершился. Если ждали ответ и не было result/прерывания/ошибки — это аварийный выход.
      const last = prev.items[prev.items.length - 1];
      const abnormal = prev.isWaiting && !(last && (last.kind === 'interrupted' || last.kind === 'error' || last.kind === 'session_ended'));
      return { ...prev, isWaiting: false, items: abnormal ? [...prev.items, { kind: 'session_ended' }] : prev.items };
    }

    case 'workflow_progress':
      return withItems(prev.items.map(item =>
        item.kind === 'tool_use' && item.id === msg.toolUseId
          ? { ...item, workflowAgents: msg.agents, workflowDone: msg.isDone }
          : item
      ));

    case 'status_changed':
      // Синхронизируем isWaiting по статусу — работает для всех открытых вкладок/браузеров.
      // Перезагрузка истории при переходе в active — побочный эффект, остаётся в хуке.
      if (msg.status === 'working' || msg.status === 'waiting') {
        return { ...prev, isWaiting: true };
      } else if (msg.status === 'active' || msg.status === 'error' || msg.status === 'finished' || msg.status === 'orphaned') {
        return { ...prev, isWaiting: false };
      }
      return prev;

    default:
      // task_changed, notification и прочие — на состояние чата не влияют
      return prev;
  }
}
