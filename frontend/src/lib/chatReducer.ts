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
  // Подсказка следующего сообщения — чип в композере.
  // Эфемерная: в историю не пишется, сбрасывается при отправке хода (в хуке).
  promptSuggestion: string | null;
}

export function initialChatState(): ChatState {
  return { items: [], isWaiting: false, rateLimits: {}, isCompacting: false, promptSuggestion: null };
}

// Сообщение истории с сервера: сериализованный ChatItem без клиентских UI-полей
// (expanded/canRetry живут только в памяти вкладки и в историю не пишутся).
// kind — string: история несёт и не-ChatItem записи (workflow_progress, legacy, будущие)
interface StoredHistoryMessage {
  kind: string;
  [field: string]: unknown;
}

// Записи снесённых механик в истории старых чатов: рендера для них больше нет —
// молча пропускаем, чтобы старые чаты открывались без ошибок и мусорных элементов
const LEGACY_KINDS = new Set(['meeting', 'meeting_phase', 'pipeline', 'pipeline_phase']);

// Приводит сырую историю к ChatItem[]: проставляет UI-поля дефолтами —
// thinking свёрнут, error без кнопки повтора (повторять исторические ошибки нельзя).
// deriveSpeakers (групповой чат): между соседними text-сообщениями с разным personaId
// вставляется разделитель «Теперь отвечает: …» (label резолвится по personaId при рендере).
// Запись workflow_progress не рендерится отдельным элементом — вмерживается в свой
// tool_use (по toolUseId), ровно как live-событие workflow_progress в редьюсере.
// Неизвестные kind проходят насквозь — ChatItemView игнорирует их в default-ветке.
export function normalizeHistory(raw: unknown[], opts?: { deriveSpeakers?: boolean }): ChatItem[] {
  const items: ChatItem[] = [];
  // Автор последней виденной text-реплики (undefined — реплик ещё не было)
  let lastPersonaId: string | undefined;
  let sawText = false;

  for (const msg of raw) {
    const m = msg as StoredHistoryMessage;

    if (LEGACY_KINDS.has(m.kind as string)) continue;

    if (m.kind === 'workflow_progress') {
      // Снапшот прогресса workflow из истории → в карточку tool_use (bgDone у tool_use
      // приходит собственным полем и переносится насквозь спредом ниже)
      const wp = m as unknown as { toolUseId?: string; isDone?: boolean; aborted?: boolean; agents?: unknown };
      const idx = items.findIndex(it => it.kind === 'tool_use' && it.id === wp.toolUseId);
      if (idx >= 0) {
        const ex = items[idx] as Extract<ChatItem, { kind: 'tool_use' }>;
        items[idx] = {
          ...ex,
          workflowAgents: (wp.agents ?? []) as Extract<ChatItem, { kind: 'tool_use' }>['workflowAgents'],
          workflowDone: wp.isDone === true,
          ...(wp.aborted === true ? { workflowAborted: true } : {}),
        };
      }
      continue; // карточки без пары молча пропускаем (tool_use потерян — рендерить нечего)
    }

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

// Элементы, живущие только в живой ленте вкладки — в history.json не персистятся.
// При сверке «история сервера новее?» их надо исключать из длины клиента, иначе
// live-only элементы завышают её и дозаписанная история никогда не подтягивается.
const LIVE_ONLY_KINDS = new Set<ChatItem['kind']>([
  'permission_request', 'interrupted', 'resumed', 'session_ended',
  'companion_switched', 'truncated', 'redacted_thinking',
]);

// Стоит ли заменить живую ленту историей с сервера: сравнение длин БЕЗ live-only
// элементов (фильтруются с обеих сторон: derive-разделители группового чата попадают
// и в нормализованную историю). При равной длине сервер новее, если последний
// text-элемент у него длиннее (дозаписанный хвост после флаша буфера).
// После замены items той же историей проверка даёт false — цикла перезагрузок нет.
export function serverHistoryNewer(serverItems: ChatItem[], prevItems: ChatItem[]): boolean {
  const countable = (items: ChatItem[]) => items.filter(i => !LIVE_ONLY_KINDS.has(i.kind));
  const server = countable(serverItems);
  const prev = countable(prevItems);
  if (server.length !== prev.length) return server.length > prev.length;

  const lastText = (items: ChatItem[]): string | null => {
    for (let i = items.length - 1; i >= 0; i--) {
      const it = items[i];
      if (it.kind === 'text') return it.text;
    }
    return null;
  };
  const s = lastText(server);
  if (s === null) return false;
  const p = lastText(prev);
  return p === null ? s.length > 0 : s.length > p.length;
}

// Последний блок сабагента данного вида у данного родителя — для дедупа эха
// «история + live» при reconnect (см. case agent_text/agent_thinking)
function lastAgentBlock(items: ChatItem[], kind: 'text' | 'thinking', parentToolUseId: string) {
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.kind === kind && it.parentToolUseId === parentToolUseId) return it;
  }
  return null;
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
    // (рендерится внутри карточки родительского tool_use). Мягкий дедуп закрывает гонку
    // «история уже загружена + live-событие догнало» при reconnect: дублем считается
    // только совпадение с ПОСЛЕДНИМ блоком того же вида у того же родителя — более
    // ранние совпадения легитимны (агент повторил ту же реплику позже).
    case 'agent_text':
      return lastAgentBlock(prev.items, 'text', msg.parentToolUseId)?.text === msg.text
        ? prev
        : withItems([...prev.items, { kind: 'text', text: msg.text, parentToolUseId: msg.parentToolUseId }]);

    case 'agent_thinking':
      return lastAgentBlock(prev.items, 'thinking', msg.parentToolUseId)?.text === msg.text
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

    // Ожидающие карточки сервер РЕПЛЕИТ при JoinSession (реконнект, пока CLI ждёт ответа) —
    // обработка идемпотентна: дубль по requestId/toolUseId не добавляется повторно.
    case 'permission_request':
      if (prev.items.some(it => it.kind === 'permission_request' && it.requestId === msg.requestId))
        return prev.isWaiting ? prev : { ...prev, isWaiting: true };
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
      if (prev.items.some(it => it.kind === 'ask_question' && it.toolUseId === msg.toolUseId))
        return prev.isWaiting ? prev : { ...prev, isWaiting: true };
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
      if (prev.items.some(it => it.kind === 'plan_review' && it.requestId === msg.requestId))
        return prev.isWaiting ? prev : { ...prev, isWaiting: true };
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

    case 'file_changed': {
      // Дедуп за ход: повторная правка того же файла обновляет существующую строку
      // (дельты суммируем), а не плодит новую. Граница хода — последний 'result'
      // (симметрично серверному TurnAccumulator.OnFileChanged).
      for (let i = prev.items.length - 1; i >= 0; i--) {
        const it = prev.items[i];
        if (it.kind === 'result') break;
        if (it.kind === 'file_changed' && it.path === msg.path) {
          const items = prev.items.slice();
          items[i] = { ...it, added: it.added + msg.added, removed: it.removed + msg.removed };
          return withItems(items);
        }
      }
      return withItems([...prev.items, { kind: 'file_changed', path: msg.path, added: msg.added, removed: msg.removed }]);
    }

    case 'result':
      return {
        ...prev,
        isWaiting: false,
        items: [...prev.items, { kind: 'result', subtype: msg.subtype, durationMs: msg.durationMs, numTurns: msg.numTurns, usage: msg.usage, totalCostUsd: msg.totalCostUsd, apiErrorStatus: msg.apiErrorStatus, permissionDenials: msg.permissionDenials, contextTokens: msg.contextTokens }],
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

    case 'bg_agent_done': {
      // Фоновые агенты завершились: помечаем их карточки — единственный достоверный
      // признак «ответ готов» для tool_use с квитанцией фонового запуска
      const ids = new Set(msg.toolUseIds);
      let changed = false;
      const next = prev.items.map(item => {
        if (item.kind !== 'tool_use' || !ids.has(item.id) || item.bgDone === true) return item;
        changed = true;
        return { ...item, bgDone: true, ...(msg.aborted ? { bgAborted: true } : {}) };
      });
      return changed ? withItems(next) : prev;
    }

    case 'speaker_changed':
      // Групповой чат: сервер переключил активного спикера по @упоминанию —
      // локальный разделитель тем же рендером, что и смена собеседника вручную
      return withItems([...prev.items, { kind: 'companion_switched', label: msg.label, personaId: msg.personaId }]);

    case 'provider_switched': {
      // Смена аккаунта/провайдера: висящие карточки-предложения гасим (миграция состоялась
      // или чат тихо переехал внутри пула); явная миграция добавляет разделитель
      const items = prev.items.map(it =>
        it.kind === 'provider_limit' && !it.resolved ? { ...it, resolved: true } : it);
      return withItems(msg.auto || !msg.label
        ? items
        : [...items, { kind: 'provider_switched', label: msg.label }]);
    }

    case 'provider_limit':
      // Лимит исчерпан, пул не помог — карточка «Продолжить на …».
      // Дедуп: пока висит неиспользованная карточка, вторую не плодим
      return prev.items.some(it => it.kind === 'provider_limit' && !it.resolved)
        ? prev
        : withItems([...prev.items, { kind: 'provider_limit', resetsAt: msg.resetsAt, providers: msg.providers }]);

    case 'git_turn_commit':
      // Документный режим: ход зафиксирован авто-коммитом — плашка со ссылкой на просмотр
      return withItems([...prev.items, { kind: 'git_turn_commit', projectId: msg.projectId, sha: msg.sha, subject: msg.subject }]);

    case 'work_loop':
      // Цикл «до готово»: приходит при каждом изменении состояния (вкл/итерация/верификация/стоп)
      return {
        ...prev,
        workLoop: { active: msg.active, iteration: msg.iteration, maxIterations: msg.maxIterations, phase: msg.phase },
      };

    case 'prompt_suggestion':
      // Подсказка следующего сообщения — приходит после result хода; в ленту не попадает
      return { ...prev, promptSuggestion: msg.text };

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
