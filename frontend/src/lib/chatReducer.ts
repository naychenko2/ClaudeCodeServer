// Чистый редьюсер ленты чата: применяет входящее ServerMessage к состоянию сессии.
// Извлечён из useSession.ts, чтобы логику обработки сообщений можно было
// тестировать без рендера React. Побочные эффекты (загрузка истории, SignalR)
// остаются в хуке; редьюсер только считает следующее состояние.

import type { ChatItem, ServerMessage, RateLimitInfo, WorkLoopState } from '../types';

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
  // Live-состояние цикла «до готово» (событие work_loop, флаг work-loop).
  // undefined — событий ещё не было (UI берёт значение из Session.workLoop)
  workLoop?: WorkLoopState;
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

// Записи снесённых механик в истории старых чатов: рендера для них больше нет —
// молча пропускаем, чтобы старые чаты открывались без ошибок и мусорных элементов
const LEGACY_KINDS = new Set(['meeting', 'meeting_phase', 'pipeline', 'pipeline_phase']);

// Приводит сырую историю к ChatItem[]: проставляет UI-поля дефолтами —
// thinking свёрнут, error без кнопки повтора (повторять исторические ошибки нельзя).
// deriveSpeakers (групповой чат): между соседними text-сообщениями с разным personaId
// вставляется разделитель «Теперь отвечает: …» (label резолвится по personaId при рендере).
// Неизвестные kind проходят насквозь — ChatItemView игнорирует их в default-ветке.
export function normalizeHistory(raw: unknown[], opts?: { deriveSpeakers?: boolean }): ChatItem[] {
  const items: ChatItem[] = [];
  // Автор последней виденной text-реплики (undefined — реплик ещё не было)
  let lastPersonaId: string | undefined;
  let sawText = false;

  for (const msg of raw) {
    const m = msg as StoredHistoryMessage;

    if (LEGACY_KINDS.has(m.kind as string)) continue;

    if (opts?.deriveSpeakers && m.kind === 'text' && !(m as { parentToolUseId?: string }).parentToolUseId) {
      const pid = (m as { personaId?: string }).personaId;
      if (sawText && pid && pid !== lastPersonaId)
        items.push({ kind: 'companion_switched', label: '', personaId: pid });
      if (pid !== undefined) lastPersonaId = pid;
      sawText = true;
    }

    if (m.kind === 'thinking') items.push({ ...m, expanded: false } as unknown as ChatItem);
    else if (m.kind === 'error') items.push({ ...m, canRetry: false } as unknown as ChatItem);
    else items.push(m as unknown as ChatItem);
  }
  return items;
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
      // spread сохраняет прочие поля реплики (personaId — авторство).
      // К тексту сабагента (parentToolUseId) дельту основного агента не приклеиваем —
      // начинаем новый элемент (тот же «разрез», что делает FlushBuffers в истории).
      if (last?.kind === 'text' && !last.parentToolUseId)
        return withItems([...prev.items.slice(0, -1), { ...last, text: last.text + msg.text }]);
      return withItems([...prev.items, { kind: 'text', text: msg.text }]);
    }

    case 'user_message':
      // Сервер-инициированная отправка (автоматизация/задача) — клиент не добавлял её
      // оптимистично, поэтому сообщение приходит живьём. Дублей нет: ввод пользователя
      // (auto=false) этим событием не рассылается.
      return withItems([...prev.items, {
        kind: 'user_message', text: msg.text,
        ...(msg.attachedPaths ? { attachedPaths: msg.attachedPaths } : {}),
        ...(msg.senderPersonaId ? { senderPersonaId: msg.senderPersonaId } : {}),
        ...(msg.auto ? { auto: true } : {}),
      }]);

    case 'thinking_delta': {
      const last = prev.items[prev.items.length - 1];
      if (last?.kind === 'thinking' && !last.parentToolUseId)
        return withItems([...prev.items.slice(0, -1), { ...last, text: last.text + msg.text }]);
      return withItems([...prev.items, { kind: 'thinking', text: msg.text, expanded: false }]);
    }

    // Текст/thinking сабагента: приходят целыми блоками, каждый — отдельным элементом ленты
    // (рендерится внутри карточки родительского tool_use). Мягкий дедуп по (parent, text)
    // закрывает гонку «история уже загружена + live-событие догнало» при reconnect.
    case 'agent_text':
      return prev.items.some(it => it.kind === 'text' && it.parentToolUseId === msg.parentToolUseId && it.text === msg.text)
        ? prev
        : withItems([...prev.items, { kind: 'text', text: msg.text, parentToolUseId: msg.parentToolUseId }]);

    case 'agent_thinking':
      return prev.items.some(it => it.kind === 'thinking' && it.parentToolUseId === msg.parentToolUseId && it.text === msg.text)
        ? prev
        : withItems([...prev.items, { kind: 'thinking', text: msg.text, expanded: false, parentToolUseId: msg.parentToolUseId }]);

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

    case 'speaker_changed':
      // Групповой чат: сервер переключил активного спикера по @упоминанию —
      // локальный разделитель тем же рендером, что и смена собеседника вручную
      return withItems([...prev.items, { kind: 'companion_switched', label: msg.label, personaId: msg.personaId }]);

    case 'work_loop':
      // Цикл «до готово»: приходит при каждом изменении состояния (вкл/итерация/верификация/стоп)
      return {
        ...prev,
        workLoop: { active: msg.active, iteration: msg.iteration, maxIterations: msg.maxIterations, phase: msg.phase },
      };

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
      // task_changed, notification, неизвестные/устаревшие типы — на состояние чата не влияют
      return prev;
  }
}
