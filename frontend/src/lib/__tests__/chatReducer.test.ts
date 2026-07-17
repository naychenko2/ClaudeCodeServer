import { describe, it, expect } from 'vitest';
import type { ChatItem, ServerMessage } from '../../types';
import { applyServerMessage, normalizeHistory, serverHistoryNewer, initialChatState, type ChatState } from '../chatReducer';

// --- Хелперы ---

const SID = 's1';

// Сообщение сервера с проставленным sessionId
const msg = (m: Omit<ServerMessage, 'sessionId'>): ServerMessage =>
  ({ sessionId: SID, ...m } as ServerMessage);

// Состояние с переопределениями
const state = (over: Partial<ChatState> = {}): ChatState =>
  ({ ...initialChatState(), ...over });

const toolUse = (id: string, extra: Partial<Extract<ChatItem, { kind: 'tool_use' }>> = {}): ChatItem =>
  ({ kind: 'tool_use', id, name: 'Bash', input: { command: 'ls' }, ...extra });

// Прогоняет цепочку сообщений через редьюсер
const run = (msgs: Array<Omit<ServerMessage, 'sessionId'>>, initial = state()): ChatState =>
  msgs.reduce((s, m) => applyServerMessage(s, msg(m)), initial);

// --- session_started ---

describe('applyServerMessage: session_started', () => {
  it('новая сессия → элемент session_started с параметрами', () => {
    const next = run([{ type: 'session_started', claudeSessionId: 'c1', isResume: false, model: 'claude-opus-4-8', mode: 'default', cwd: '/w', toolCount: 5 }]);
    expect(next.items).toEqual([
      { kind: 'session_started', model: 'claude-opus-4-8', mode: 'default', cwd: '/w', toolCount: 5, mcpServers: undefined },
    ]);
  });

  it('resume → элемент resumed, повторный resume не дублируется (та же ссылка)', () => {
    const first = run([{ type: 'session_started', claudeSessionId: 'c1', isResume: true, model: 'm', mode: 'default' }]);
    expect(first.items).toEqual([{ kind: 'resumed' }]);
    const second = applyServerMessage(first, msg({ type: 'session_started', claudeSessionId: 'c1', isResume: true, model: 'm', mode: 'default' }));
    expect(second).toBe(first);
  });
});

// --- text_delta / thinking_delta ---

describe('applyServerMessage: дельты текста', () => {
  it('text_delta накапливается в последний text-элемент', () => {
    const next = run([
      { type: 'text_delta', text: 'При' },
      { type: 'text_delta', text: 'вет' },
    ]);
    expect(next.items).toEqual([{ kind: 'text', text: 'Привет' }]);
  });

  it('text_delta после элемента другого вида открывает новый text', () => {
    const next = run(
      [{ type: 'text_delta', text: 'после' }],
      state({ items: [{ kind: 'text', text: 'до' }, toolUse('t1')] }),
    );
    expect(next.items).toHaveLength(3);
    expect(next.items[2]).toEqual({ kind: 'text', text: 'после' });
  });

  it('thinking_delta накапливается и сохраняет expanded', () => {
    const initial = state({ items: [{ kind: 'thinking', text: 'a', expanded: true }] });
    const next = run([{ type: 'thinking_delta', text: 'b' }], initial);
    expect(next.items).toEqual([{ kind: 'thinking', text: 'ab', expanded: true }]);
  });

  it('thinking_delta с нуля создаёт свёрнутый thinking', () => {
    const next = run([{ type: 'thinking_delta', text: 'думаю' }]);
    expect(next.items).toEqual([{ kind: 'thinking', text: 'думаю', expanded: false }]);
  });

  it('text_delta не приклеивается к тексту сабагента — открывает новый элемент', () => {
    const next = run(
      [{ type: 'text_delta', text: 'основной' }],
      state({ items: [{ kind: 'text', text: 'сабагент', parentToolUseId: 'task1' }] }),
    );
    expect(next.items).toHaveLength(2);
    expect(next.items[1]).toEqual({ kind: 'text', text: 'основной' });
  });

  it('thinking_delta не приклеивается к thinking сабагента', () => {
    const next = run(
      [{ type: 'thinking_delta', text: 'основной' }],
      state({ items: [{ kind: 'thinking', text: 'сабагент', expanded: false, parentToolUseId: 'task1' }] }),
    );
    expect(next.items).toHaveLength(2);
    expect(next.items[1]).toEqual({ kind: 'thinking', text: 'основной', expanded: false });
  });
});

// --- agent_text / agent_thinking (поток сабагента) ---

describe('applyServerMessage: поток сабагента', () => {
  it('agent_text добавляет text с parentToolUseId отдельным элементом', () => {
    const next = run([
      { type: 'text_delta', text: 'основной' },
      { type: 'agent_text', parentToolUseId: 'task1', text: 'реплика сабагента' },
    ]);
    expect(next.items).toEqual([
      { kind: 'text', text: 'основной' },
      { kind: 'text', text: 'реплика сабагента', parentToolUseId: 'task1' },
    ]);
  });

  it('два agent_text подряд не склеиваются (хронология между инструментами)', () => {
    const next = run([
      { type: 'agent_text', parentToolUseId: 'task1', text: 'первый ход' },
      { type: 'agent_text', parentToolUseId: 'task1', text: 'второй ход' },
    ]);
    expect(next.items).toHaveLength(2);
  });

  it('agent_text дедуплицируется по совпадению с ПОСЛЕДНИМ текстом того же родителя — та же ссылка', () => {
    const first = run([{ type: 'agent_text', parentToolUseId: 'task1', text: 'реплика' }]);
    const second = applyServerMessage(first, msg({ type: 'agent_text', parentToolUseId: 'task1', text: 'реплика' }));
    expect(second).toBe(first);
    // Тот же текст у другого родителя — не дубль
    const third = applyServerMessage(second, msg({ type: 'agent_text', parentToolUseId: 'task2', text: 'реплика' }));
    expect(third.items).toHaveLength(2);
  });

  it('легитимный повтор agent_text через другой элемент НЕ схлопывается', () => {
    // Агент дважды сказал «готово» в разные моменты (между ними другая реплика) — оба остаются
    const next = run([
      { type: 'agent_text', parentToolUseId: 'task1', text: 'готово' },
      { type: 'agent_text', parentToolUseId: 'task1', text: 'проверяю ещё раз' },
      { type: 'agent_text', parentToolUseId: 'task1', text: 'готово' },
    ]);
    expect(next.items).toHaveLength(3);
    // А эхо (тот же текст сразу после последнего) — дубль
    const echoed = applyServerMessage(next, msg({ type: 'agent_text', parentToolUseId: 'task1', text: 'готово' }));
    expect(echoed).toBe(next);
  });

  it('agent_thinking добавляет свёрнутый thinking с parentToolUseId и дедуплицируется по последнему', () => {
    const first = run([{ type: 'agent_thinking', parentToolUseId: 'task1', text: 'мысль' }]);
    expect(first.items).toEqual([{ kind: 'thinking', text: 'мысль', expanded: false, parentToolUseId: 'task1' }]);
    const second = applyServerMessage(first, msg({ type: 'agent_thinking', parentToolUseId: 'task1', text: 'мысль' }));
    expect(second).toBe(first);
    // Повтор той же мысли через другую — легитимен
    const third = run([
      { type: 'agent_thinking', parentToolUseId: 'task1', text: 'другая' },
      { type: 'agent_thinking', parentToolUseId: 'task1', text: 'мысль' },
    ], second);
    expect(third.items).toHaveLength(3);
  });
});

// --- tool_use / tool_input_delta / tool_result ---

describe('applyServerMessage: инструменты', () => {
  it('tool_use добавляет карточку', () => {
    const next = run([{ type: 'tool_use', id: 't1', name: 'Read', input: { path: 'a.ts' } }]);
    expect(next.items).toEqual([{ kind: 'tool_use', id: 't1', name: 'Read', input: { path: 'a.ts' }, parentToolUseId: undefined }]);
  });

  it('повторный tool_use с тем же id обновляет карточку: input, сброс streamingArg, parentToolUseId не теряется', () => {
    const initial = state({ items: [toolUse('t1', { streamingArg: '{"par', parentToolUseId: 'p1' })] });
    const next = run([{ type: 'tool_use', id: 't1', name: 'Bash', input: { command: 'ls -la' } }], initial);
    expect(next.items).toHaveLength(1);
    expect(next.items[0]).toMatchObject({ kind: 'tool_use', id: 't1', input: { command: 'ls -la' }, streamingArg: undefined, parentToolUseId: 'p1' });
  });

  it('tool_input_delta пишет streamingArg только в свою карточку', () => {
    const initial = state({ items: [toolUse('t1'), toolUse('t2')] });
    const next = run([{ type: 'tool_input_delta', toolUseId: 't2', partialJson: '{"x":' }], initial);
    expect(next.items[0]).not.toHaveProperty('streamingArg', '{"x":');
    expect(next.items[1]).toMatchObject({ id: 't2', streamingArg: '{"x":' });
  });

  it('tool_result привязывается к карточке по toolUseId', () => {
    const initial = state({ items: [toolUse('t1'), toolUse('t2')] });
    const next = run([{ type: 'tool_result', toolUseId: 't1', content: 'ok', isError: false }], initial);
    expect(next.items[0]).toMatchObject({ id: 't1', result: 'ok', isError: false });
    expect(next.items[1]).not.toHaveProperty('result', 'ok');
  });

  it('workflow_progress обновляет агентов своей карточки', () => {
    const initial = state({ items: [toolUse('t1')] });
    const agents = [{ id: 'a1', prompt: 'сделай' }];
    const next = run([{ type: 'workflow_progress', toolUseId: 't1', agents, isDone: true }], initial);
    expect(next.items[0]).toMatchObject({ workflowAgents: agents, workflowDone: true });
  });
});

// --- bg_agent_done (завершение фоновых агентов) ---

describe('applyServerMessage: bg_agent_done', () => {
  it('помечает bgDone у всех перечисленных tool_use', () => {
    const initial = state({ items: [toolUse('t1'), toolUse('t2'), toolUse('t3')] });
    const next = run([{ type: 'bg_agent_done', toolUseIds: ['t1', 't3'] }], initial);
    expect(next.items[0]).toMatchObject({ bgDone: true });
    expect(next.items[1]).not.toHaveProperty('bgDone', true);
    expect(next.items[2]).toMatchObject({ bgDone: true });
    expect(next.items[0]).not.toHaveProperty('bgAborted', true);
  });

  it('aborted=true дополнительно ставит bgAborted', () => {
    const initial = state({ items: [toolUse('t1')] });
    const next = run([{ type: 'bg_agent_done', toolUseIds: ['t1'], aborted: true }], initial);
    expect(next.items[0]).toMatchObject({ bgDone: true, bgAborted: true });
  });

  it('без совпавших карточек состояние не меняется (та же ссылка)', () => {
    const initial = state({ items: [toolUse('t1')] });
    const next = applyServerMessage(initial, msg({ type: 'bg_agent_done', toolUseIds: ['нет-такого'] }));
    expect(next).toBe(initial);
  });

  it('повторное событие по уже помеченной карточке — no-op (та же ссылка)', () => {
    const first = run([{ type: 'bg_agent_done', toolUseIds: ['t1'] }], state({ items: [toolUse('t1')] }));
    const second = applyServerMessage(first, msg({ type: 'bg_agent_done', toolUseIds: ['t1'] }));
    expect(second).toBe(first);
  });
});

// --- Запросы, требующие ответа пользователя ---

describe('applyServerMessage: ожидание ответа пользователя', () => {
  it('permission_request ставит isWaiting и добавляет нерешённый запрос', () => {
    const next = run([{ type: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: { command: 'rm' } }]);
    expect(next.isWaiting).toBe(true);
    expect(next.items).toEqual([{ kind: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: { command: 'rm' }, resolved: false }]);
  });

  it('ask_question ставит isWaiting и добавляет вопрос', () => {
    const next = run([{ type: 'ask_question', toolUseId: 't1', input: { q: '?' } }]);
    expect(next.isWaiting).toBe(true);
    expect(next.items).toEqual([{ kind: 'ask_question', toolUseId: 't1', input: { q: '?' }, resolved: false }]);
  });

  it('plan_review ставит isWaiting и добавляет план', () => {
    const next = run([{ type: 'plan_review', requestId: 'r1', plan: '# План' }]);
    expect(next.isWaiting).toBe(true);
    expect(next.items).toEqual([{ kind: 'plan_review', requestId: 'r1', plan: '# План', resolved: false }]);
  });

  // Сервер реплеит ожидающую карточку при JoinSession (реконнект) — обработка идемпотентна
  it('повторный permission_request с тем же requestId не дублируется, isWaiting остаётся', () => {
    const first = run([{ type: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: {} }]);
    const second = applyServerMessage(first, msg({ type: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: {} }));
    expect(second).toBe(first);
    // Реплей после сброса isWaiting (реконнект) — карточка не дублируется, ожидание восстанавливается
    const reset = { ...first, isWaiting: false };
    const replayed = applyServerMessage(reset, msg({ type: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: {} }));
    expect(replayed.items).toHaveLength(1);
    expect(replayed.isWaiting).toBe(true);
  });

  it('повторный ask_question по toolUseId и plan_review по requestId не дублируются', () => {
    const q = run([{ type: 'ask_question', toolUseId: 't1', input: { q: '?' } }]);
    expect(applyServerMessage(q, msg({ type: 'ask_question', toolUseId: 't1', input: { q: '?' } }))).toBe(q);
    const p = run([{ type: 'plan_review', requestId: 'r1', plan: '# П' }]);
    expect(applyServerMessage(p, msg({ type: 'plan_review', requestId: 'r1', plan: '# П' }))).toBe(p);
  });
});

// --- result / error / exited / file_changed ---

describe('applyServerMessage: завершение хода', () => {
  it('result снимает isWaiting и добавляет итог с usage', () => {
    const usage = { inputTokens: 10, outputTokens: 5, cacheReadTokens: 1, cacheCreationTokens: 2 };
    const next = run(
      [{ type: 'result', subtype: 'success', durationMs: 100, numTurns: 2, usage, totalCostUsd: 0.01 }],
      state({ isWaiting: true }),
    );
    expect(next.isWaiting).toBe(false);
    expect(next.items[0]).toMatchObject({ kind: 'result', subtype: 'success', usage, totalCostUsd: 0.01 });
  });

  it('error снимает isWaiting, ошибка с возможностью повтора', () => {
    const next = run([{ type: 'error', text: 'упало' }], state({ isWaiting: true }));
    expect(next.isWaiting).toBe(false);
    expect(next.items).toEqual([{ kind: 'error', text: 'упало', canRetry: true }]);
  });

  it('file_changed добавляется в ленту', () => {
    const next = run([{ type: 'file_changed', path: 'src/a.ts', added: 3, removed: 1 }]);
    expect(next.items).toEqual([{ kind: 'file_changed', path: 'src/a.ts', added: 3, removed: 1 }]);
  });

  it('exited при isWaiting без завершающего элемента → аварийный session_ended', () => {
    const next = run([{ type: 'exited' }], state({ isWaiting: true, items: [{ kind: 'text', text: 'полответа' }] }));
    expect(next.isWaiting).toBe(false);
    expect(next.items[next.items.length - 1]).toEqual({ kind: 'session_ended' });
  });

  it('exited после interrupted — не аварийный, session_ended не добавляется', () => {
    const next = run([{ type: 'exited' }], state({ isWaiting: true, items: [{ kind: 'interrupted' }] }));
    expect(next.isWaiting).toBe(false);
    expect(next.items).toEqual([{ kind: 'interrupted' }]);
  });

  it('exited без ожидания — просто снимает isWaiting', () => {
    const next = run([{ type: 'exited' }], state({ items: [{ kind: 'text', text: 'готово' }] }));
    expect(next.items).toEqual([{ kind: 'text', text: 'готово' }]);
  });
});

// --- rate_limit / fal_cost / прочие ---

describe('applyServerMessage: телеметрия', () => {
  it('rate_limit хранит последнее значение по каждому окну', () => {
    const next = run([
      { type: 'rate_limit', limitType: 'five_hour', utilization: 0.3, status: 'allowed' },
      { type: 'rate_limit', limitType: 'seven_day', utilization: 0.1 },
      { type: 'rate_limit', limitType: 'five_hour', utilization: 0.5, status: 'allowed_warning' },
    ]);
    expect(Object.keys(next.rateLimits).sort()).toEqual(['five_hour', 'seven_day']);
    expect(next.rateLimits.five_hour).toMatchObject({ utilization: 0.5, status: 'allowed_warning' });
    expect(next.items).toEqual([]); // в ленту не пишется
  });

  it('fal_cost добавляется один раз, дубль по requestId игнорируется (та же ссылка)', () => {
    const first = run([{ type: 'fal_cost', requestId: 'rq1', costUsd: 0.05 }]);
    expect(first.items).toHaveLength(1);
    const second = applyServerMessage(first, msg({ type: 'fal_cost', requestId: 'rq1', costUsd: 0.05 }));
    expect(second).toBe(first);
  });

  it('truncated и redacted_thinking добавляются в ленту', () => {
    const next = run([{ type: 'truncated' }, { type: 'redacted_thinking' }]);
    expect(next.items).toEqual([{ kind: 'truncated' }, { kind: 'redacted_thinking' }]);
  });
});

// --- Компакция контекста ---

describe('applyServerMessage: компакция', () => {
  it('compact_status compacting → isCompacting', () => {
    const next = run([{ type: 'compact_status', status: 'compacting' }]);
    expect(next.isCompacting).toBe(true);
  });

  it('failed с «not enough messages» → мягкий note без ошибки в ленте', () => {
    const next = run(
      [{ type: 'compact_status', compactResult: 'failed', compactError: 'Not enough messages to compact' }],
      state({ isCompacting: true }),
    );
    expect(next.isCompacting).toBe(false);
    expect(next.compactNote).toBe('Пока нечего сжимать — слишком мало сообщений.');
    expect(next.items).toEqual([]);
  });

  it('failed с другой ошибкой → красная плашка error без повтора', () => {
    const next = run(
      [{ type: 'compact_status', compactResult: 'failed', compactError: 'boom' }],
      state({ isCompacting: true }),
    );
    expect(next.compactNote).toBeUndefined();
    expect(next.items).toEqual([{ kind: 'error', text: 'Не удалось сжать контекст: boom', canRetry: false }]);
  });

  it('compact_boundary добавляет маркер и сбрасывает isCompacting/note', () => {
    const next = run(
      [{ type: 'compact_boundary', trigger: 'manual', preTokens: 100_000, postTokens: 10_000 }],
      state({ isCompacting: true, compactNote: 'старый note' }),
    );
    expect(next.isCompacting).toBe(false);
    expect(next.compactNote).toBeUndefined();
    expect(next.items).toEqual([{ kind: 'compact_boundary', trigger: 'manual', preTokens: 100_000, postTokens: 10_000 }]);
  });
});

// --- status_changed / необрабатываемые типы ---

describe('applyServerMessage: статусы и no-op', () => {
  it.each(['working', 'waiting'] as const)('status_changed %s → isWaiting=true', (status) => {
    expect(run([{ type: 'status_changed', status }]).isWaiting).toBe(true);
  });

  it.each(['active', 'error', 'finished', 'orphaned'] as const)('status_changed %s → isWaiting=false', (status) => {
    expect(run([{ type: 'status_changed', status }], state({ isWaiting: true })).isWaiting).toBe(false);
  });

  it('status_changed starting не трогает состояние', () => {
    const initial = state({ isWaiting: true });
    expect(applyServerMessage(initial, msg({ type: 'status_changed', status: 'starting' }))).toBe(initial);
  });

  it('notification не меняет состояние (та же ссылка)', () => {
    const initial = state({ items: [{ kind: 'text', text: 'x' }] });
    const next = applyServerMessage(initial, msg({ type: 'notification', title: 't', body: 'b', kind: 'info' }));
    expect(next).toBe(initial);
  });
});

// --- normalizeHistory ---

describe('normalizeHistory', () => {
  it('thinking из истории свёрнут, error без повтора, остальное как есть', () => {
    const raw = [
      { kind: 'thinking', text: 'мысль', expanded: true },
      { kind: 'error', text: 'ошибка', canRetry: true },
      { kind: 'text', text: 'обычный' },
    ];
    expect(normalizeHistory(raw)).toEqual([
      { kind: 'thinking', text: 'мысль', expanded: false },
      { kind: 'error', text: 'ошибка', canRetry: false },
      { kind: 'text', text: 'обычный' },
    ]);
  });

  it('пустая история → пустой список', () => {
    expect(normalizeHistory([])).toEqual([]);
  });

  it('bgDone из stored tool_use переносится в ChatItem', () => {
    const raw = [
      { kind: 'tool_use', id: 't1', name: 'Agent', input: { run_in_background: true }, result: 'Async agent launched successfully', bgDone: true },
    ];
    expect(normalizeHistory(raw)[0]).toMatchObject({ kind: 'tool_use', id: 't1', bgDone: true });
  });

  it('workflow_progress вмерживается в свой tool_use и не рендерится отдельным элементом', () => {
    const agents = [{ id: 'a1', prompt: 'сделай', isDone: true }];
    const raw = [
      { kind: 'tool_use', id: 'wf1', name: 'Workflow', input: {}, result: 'Transcript dir: /x' },
      { kind: 'text', text: 'после' },
      { kind: 'workflow_progress', toolUseId: 'wf1', isDone: false, agents },
    ];
    const items = normalizeHistory(raw);
    expect(items).toHaveLength(2);
    expect(items[0]).toMatchObject({ kind: 'tool_use', id: 'wf1', workflowAgents: agents, workflowDone: false });
    expect(items[0]).not.toHaveProperty('workflowAborted');
  });

  it('workflow_progress с aborted помечает tool_use как workflowAborted', () => {
    const raw = [
      { kind: 'tool_use', id: 'wf1', name: 'Workflow', input: {} },
      { kind: 'workflow_progress', toolUseId: 'wf1', isDone: false, aborted: true, agents: [] },
    ];
    const items = normalizeHistory(raw);
    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({ workflowDone: false, workflowAborted: true });
  });

  it('workflow_progress без парного tool_use молча пропускается', () => {
    const raw = [
      { kind: 'text', text: 'один' },
      { kind: 'workflow_progress', toolUseId: 'нет-такого', isDone: true, agents: [] },
    ];
    expect(normalizeHistory(raw)).toEqual([{ kind: 'text', text: 'один' }]);
  });
});

// --- Групповой чат: speaker_changed + derive разделителей из истории ---

describe('групповой чат', () => {
  it('speaker_changed добавляет разделитель companion_switched с label и personaId', () => {
    const next = run([{ type: 'speaker_changed', personaId: 'p2', label: 'Дизайнер (Света)' }]);
    expect(next.items).toEqual([
      { kind: 'companion_switched', label: 'Дизайнер (Света)', personaId: 'p2' },
    ]);
  });

  it('normalizeHistory с deriveSpeakers вставляет разделитель на смене personaId', () => {
    const raw = [
      { kind: 'user_message', text: 'привет' },
      { kind: 'text', text: 'ответ первой', personaId: 'p1' },
      { kind: 'user_message', text: '@vtoraya, а ты что думаешь?' },
      { kind: 'text', text: 'ответ второй', personaId: 'p2' },
      { kind: 'text', text: 'продолжение второй', personaId: 'p2' },
    ];
    const items = normalizeHistory(raw, { deriveSpeakers: true });
    expect(items).toEqual([
      { kind: 'user_message', text: 'привет' },
      { kind: 'text', text: 'ответ первой', personaId: 'p1' },
      { kind: 'user_message', text: '@vtoraya, а ты что думаешь?' },
      { kind: 'companion_switched', label: '', personaId: 'p2' },
      { kind: 'text', text: 'ответ второй', personaId: 'p2' },
      { kind: 'text', text: 'продолжение второй', personaId: 'p2' },
    ]);
  });

  it('без deriveSpeakers разделители не вставляются', () => {
    const raw = [
      { kind: 'text', text: 'a', personaId: 'p1' },
      { kind: 'text', text: 'b', personaId: 'p2' },
    ];
    expect(normalizeHistory(raw)).toHaveLength(2);
  });

  it('текст сабагента (parentToolUseId) не рождает разделитель и проходит насквозь', () => {
    const raw = [
      { kind: 'text', text: 'ответ первой', personaId: 'p1' },
      { kind: 'text', text: 'реплика сабагента', parentToolUseId: 'task1' },
      { kind: 'thinking', text: 'мысль сабагента', parentToolUseId: 'task1' },
      { kind: 'text', text: 'продолжение первой', personaId: 'p1' },
    ];
    const items = normalizeHistory(raw, { deriveSpeakers: true });
    expect(items).toEqual([
      { kind: 'text', text: 'ответ первой', personaId: 'p1' },
      { kind: 'text', text: 'реплика сабагента', parentToolUseId: 'task1' },
      { kind: 'thinking', text: 'мысль сабагента', parentToolUseId: 'task1', expanded: false },
      { kind: 'text', text: 'продолжение первой', personaId: 'p1' },
    ]);
  });
});

// --- Неизвестные/устаревшие записи истории и события ---

describe('неизвестные типы записей', () => {
  it('устаревшие записи снесённых механик (meeting/pipeline) молча выкидываются из истории', () => {
    const raw = [
      { kind: 'user_message', text: 'старт' },
      { kind: 'meeting_phase', meetingId: 'm1', phase: 'independent', question: 'в?', entries: [] },
      { kind: 'meeting', meetingId: 'm1', question: 'в?', phases: {} },
      { kind: 'pipeline_phase', pipelineId: 'pl1', phase: 'plan', task: 'т', personaId: 'p1', text: 'план' },
      { kind: 'pipeline', pipelineId: 'pl1', task: 'т', phases: [] },
      { kind: 'text', text: 'ответ' },
    ];
    const items = normalizeHistory(raw);
    expect(items).toEqual([
      { kind: 'user_message', text: 'старт' },
      { kind: 'text', text: 'ответ' },
    ]);
  });

  it('неизвестный тип stored-записи в истории игнорируется без ошибки (проходит насквозь)', () => {
    const raw = [
      { kind: 'text', text: 'до' },
      { kind: 'something_from_the_future', payload: { x: 1 } },
      { kind: 'text', text: 'после' },
    ];
    expect(() => normalizeHistory(raw)).not.toThrow();
    const items = normalizeHistory(raw);
    // Запись не теряется и не ломает соседей; рендер игнорирует её в default-ветке
    expect(items).toHaveLength(3);
    expect(items[0]).toEqual({ kind: 'text', text: 'до' });
    expect(items[2]).toEqual({ kind: 'text', text: 'после' });
  });

  it('неизвестный тип live-события не меняет состояние (та же ссылка)', () => {
    const initial = state({ items: [{ kind: 'text', text: 'x' }] });
    const next = applyServerMessage(initial, msg({ type: 'meeting_progress', meetingId: 'm1', phase: 'independent' } as unknown as Omit<ServerMessage, 'sessionId'>));
    expect(next).toBe(initial);
  });
});

// --- serverHistoryNewer (сверка «история сервера новее живой ленты?») ---

describe('serverHistoryNewer', () => {
  const u = (text: string): ChatItem => ({ kind: 'user_message', text });
  const t = (text: string): ChatItem => ({ kind: 'text', text });

  it('сервер длиннее → true', () => {
    expect(serverHistoryNewer([u('в'), t('ответ')], [u('в')])).toBe(true);
  });

  it('одинаковая история → false (после reload повторная проверка не зацикливает)', () => {
    const server = [u('в'), t('ответ')];
    expect(serverHistoryNewer(server, [...server])).toBe(false);
  });

  it('live-only элементы клиента не завышают длину: сервер с дозаписью всё равно новее', () => {
    // Клиент: вопрос + interrupted (live-only) = 2 элемента; сервер: вопрос + ответ = 2.
    // Наивное сравнение длин сочло бы их равными — хелпер видит, что stored-часть выросла.
    const prev: ChatItem[] = [u('в'), { kind: 'interrupted' }];
    const server = [u('в'), t('ответ')];
    expect(serverHistoryNewer(server, prev)).toBe(true);
  });

  it.each([
    ['permission_request', { kind: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: {}, resolved: true }],
    ['resumed', { kind: 'resumed' }],
    ['session_ended', { kind: 'session_ended' }],
    ['companion_switched', { kind: 'companion_switched', label: 'Роль (Имя)' }],
    ['truncated', { kind: 'truncated' }],
    ['redacted_thinking', { kind: 'redacted_thinking' }],
  ] as const)('live-only %s не учитывается с обеих сторон', (_kind, liveOnly) => {
    const prev = [u('в'), liveOnly as ChatItem];
    expect(serverHistoryNewer([u('в')], prev)).toBe(false);
  });

  it('равная длина, последний text сервера длиннее (дозаписанный хвост) → true', () => {
    expect(serverHistoryNewer([u('в'), t('ответ целиком')], [u('в'), t('ответ')])).toBe(true);
  });

  it('равная длина, последний text сервера короче или равен → false', () => {
    expect(serverHistoryNewer([u('в'), t('ответ')], [u('в'), t('ответ целиком')])).toBe(false);
    expect(serverHistoryNewer([u('в'), t('ответ')], [u('в'), t('ответ')])).toBe(false);
  });

  it('derive-разделители группового чата в истории сервера не считаются новизной', () => {
    // normalizeHistory(deriveSpeakers) вставляет companion_switched — фильтруется с обеих сторон
    const server: ChatItem[] = [t('a'), { kind: 'companion_switched', label: '', personaId: 'p2' }, t('b')];
    const prev: ChatItem[] = [t('a'), { kind: 'companion_switched', label: 'Р (И)', personaId: 'p2' }, t('b')];
    expect(serverHistoryNewer(server, prev)).toBe(false);
  });

  it('клиент реально длиннее (оптимистичный ввод) → false', () => {
    expect(serverHistoryNewer([u('в')], [u('в'), u('ещё')])).toBe(false);
  });
});
