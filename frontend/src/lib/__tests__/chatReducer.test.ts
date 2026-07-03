import { describe, it, expect } from 'vitest';
import type { ChatItem, ServerMessage } from '../../types';
import { applyServerMessage, normalizeHistory, initialChatState, type ChatState } from '../chatReducer';

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
});
