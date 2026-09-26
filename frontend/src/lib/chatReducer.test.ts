import { describe, it, expect } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  serverHistoryNewer, PERSISTED_KINDS, applyServerMessage, normalizeHistory, initialChatState,
  resolveCardsFromHistory, type ChatState,
} from './chatReducer';
import type { ChatItem, ServerMessage } from '../types';

// Сверка «история сервера новее живой ленты?» — единственный гейт, через который
// перезагруженная история попадает в ленту. Ошибочное «не новее» означает, что
// оборванный посреди хода ответ залипает: ровно так терялись ответы, когда
// пользователь уходил в другой чат, не дождавшись конца хода.

const SID = 's1';

// Живая лента: прогоняем события через тот же редьюсер, что и в рантайме
const feed = (state: ChatState, ...msgs: Array<Partial<ServerMessage> & { type: string }>): ChatState =>
  msgs.reduce((s, m) => applyServerMessage(s, { sessionId: SID, ...m } as ServerMessage), state);

// Снимок истории с сервера: stored-сообщения, как их отдаёт GET /history
const snapshot = (...stored: unknown[]) => normalizeHistory(stored);

// Один завершённый ход в обоих представлениях — стартовая точка «лента = история»
const TURN1_STORED = [
  { kind: 'user_message', text: 'первый вопрос' },
  { kind: 'text', text: 'первый ответ' },
  { kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1 },
];
const afterTurn1 = (): ChatState => feed(initialChatState(),
  { type: 'user_message', text: 'первый вопрос' },
  { type: 'text_delta', text: 'первый ответ' },
  { type: 'result', subtype: 'success', durationMs: 1, numTurns: 1 },
);

describe('возврат в чат посреди идущего хода', () => {
  // Пользователь ушёл, когда ассистент написал только начало ответа. Пока он был
  // в другом чате, события шли мимо (вне SignalR-группы). Вернулся — ход ещё идёт,
  // снимок истории обязан подтянуть накопленный текст, иначе в ответе будет дыра.
  const startSecondTurn = (state: ChatState) => feed(state,
    { type: 'user_message', text: 'второй вопрос' },
    { type: 'session_started', model: 'opus', mode: 'default' },
    { type: 'text_delta', text: 'Начало отве' },
  );

  const serverMidTurn = () => snapshot(
    ...TURN1_STORED,
    { kind: 'user_message', text: 'второй вопрос' },
    { kind: 'session_started', model: 'opus', mode: 'default' },
    { kind: 'text', text: 'Начало ответа, который дописался пока вкладка была в другом чате' },
  );

  it('обычный чат: снимок сервера новее — дописанный хвост подтянется', () => {
    const client = startSecondTurn(afterTurn1());
    expect(serverHistoryNewer(serverMidTurn(), client.items)).toBe(true);
  });

  // Регрессия. Эти события существуют только в ленте вкладки — бэкенд их в history.json
  // не пишет. Пока они учитывались в сверке длин, лента навсегда оказывалась «длиннее»
  // серверной, снимок отвергался, и текст ответа обрывался на полуслове.
  const liveOnlyEvents: Array<[string, Partial<ServerMessage> & { type: string }]> = [
    ['provider_limit', { type: 'provider_limit', providers: [] }],
    ['provider_switched', { type: 'provider_switched', label: 'GLM', auto: false }],
    ['git_turn_commit', { type: 'git_turn_commit', projectId: 'p1', sha: 'abc123', subject: 'фикс' }],
    ['permission_request', { type: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: {} }],
    ['session_ended', { type: 'exited', code: 0 }],
    ['truncated', { type: 'truncated' }],
    ['redacted_thinking', { type: 'redacted_thinking' }],
    ['team_planning_done', {
      type: 'team_planning', start: false, success: true,
      subtaskCount: 2, waveCount: 1, elapsedMs: 1000, route: null, failure: null,
      promptChars: 0, responseChars: 0,
    }],
  ];

  it.each(liveOnlyEvents)('%s в ленте не блокирует снимок', (_name, event) => {
    const client = startSecondTurn(feed(afterTurn1(), event));
    expect(serverHistoryNewer(serverMidTurn(), client.items)).toBe(true);
  });

  it('лента ушла вперёд дельтами — снимок не применяем, чтобы не откатить свежее', () => {
    const client = feed(startSecondTurn(afterTurn1()),
      { type: 'text_delta', text: 'та + хвост, которого в снимке ещё нет' });
    const stale = snapshot(
      ...TURN1_STORED,
      { kind: 'user_message', text: 'второй вопрос' },
      { kind: 'session_started', model: 'opus', mode: 'default' },
      { kind: 'text', text: 'Начало отве' },
    );
    expect(serverHistoryNewer(stale, client.items)).toBe(false);
  });

  it('та же история — не новее (иначе цикл перезагрузок)', () => {
    const items = snapshot(...TURN1_STORED);
    expect(serverHistoryNewer(items, items)).toBe(false);
  });
});

describe('serverHistoryNewer: базовые случаи', () => {
  const userMsg = (text: string): ChatItem => ({ kind: 'user_message', text });
  const text = (t: string): ChatItem => ({ kind: 'text', text: t });
  const result = (): ChatItem => ({ kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1 });

  it('история длиннее живой ленты — новее', () => {
    expect(serverHistoryNewer(
      [userMsg('в'), text('ответ целиком'), result()],
      [userMsg('в'), text('отв')],
    )).toBe(true);
  });

  it('при равной длине новее тот, у кого последний текст длиннее', () => {
    expect(serverHistoryNewer([userMsg('в'), text('ответ целиком')], [userMsg('в'), text('отв')])).toBe(true);
  });

  it('ответа нет ни там, ни там — не новее', () => {
    expect(serverHistoryNewer([userMsg('в')], [userMsg('в')])).toBe(false);
  });
});

// Сторож: белый список персистируемых видов на фронте обязан совпадать с тем, что
// бэкенд реально пишет в history.json. Разъехались — сверка длин врёт. Источник правды
// один — дискриминаторы StoredMessage.cs, тест читает сам файл, а не копию списка.

// Путь к StoredMessage.cs ищем подъёмом вверх от файла теста до корня репозитория.
// Не `../../..`: так сторож переживает переезд самого теста внутри frontend/, а корень
// опознаётся по искомому файлу, а не по `.git` (в worktree это файл, а не папка).
// Разделители пути собирает path.join — тест одинаково работает на Windows и на Linux в CI.
const STORED_MESSAGE_REL = path.join('backend', 'ClaudeHomeServer.Core', 'Protocol', 'StoredMessage.cs');

function storedMessagePath(): string {
  const start = path.dirname(fileURLToPath(import.meta.url));
  for (let dir = start; ;) {
    const candidate = path.join(dir, STORED_MESSAGE_REL);
    if (existsSync(candidate)) return candidate;
    const parent = path.dirname(dir);
    // Дошли до корня файловой системы — падаем с диагнозом, а не проходим тихо
    if (parent === dir) throw new Error(
      `Сторож PERSISTED_KINDS: от ${start} вверх до корня не найден ${STORED_MESSAGE_REL}. ` +
      'Файл переехал или переименован — почини путь, а не выключай сторож.');
    dir = parent;
  }
}

// Исключение №1: бэкенд пишет этот вид в history.json, но СОБСТВЕННЫМ элементом ленты он
// не становится. Критерий — в normalizeHistory такой kind до items не доходит. Считать его
// в serverHistoryNewer нельзя: на клиенте его нет ни в живой ленте, ни в нормализованной
// истории, и он завысил бы длину серверного списка.
const STORED_BUT_NOT_LIVE_ITEM: Record<string, string> = {
  workflow_progress: 'снапшот прогресса Workflow: normalizeHistory вливает его в карточку ' +
    'родительского tool_use (ветка m.kind === "workflow_progress"), отдельной строки ленты нет',
};

// Исключение №2: вид ленты участвует в сверке длин, но своего C#-типа в StoredMessage.cs
// не имеет. Критерий — элемент переживает перезагрузку, но приезжает с сервера не отдельной
// записью истории (например, собирается фронтом из других записей). Сейчас таких нет, и это
// не «на всякий случай»: чисто клиентские виды (provider_limit, provider_switched,
// git_turn_commit, companion_switched…) в PERSISTED_KINDS не входят вовсе и исключения не
// требуют. Появится такой вид — вписать сюда с объяснением, иначе сторож покраснеет.
const PERSISTED_WITHOUT_STORED_TYPE: Record<string, string> = {};

describe('PERSISTED_KINDS ↔ StoredMessage.cs', () => {
  it('совпадает с дискриминаторами StoredMessage', () => {
    const src = readFileSync(storedMessagePath(), 'utf8');
    const discriminators = [...src.matchAll(/JsonDerivedType\(typeof\([^)]+\),\s*"([a-z_]+)"\)/g)]
      .map(m => m[1]);

    // Регулярка жива: файл нашёлся, но разбор дал пустоту — значит изменился синтаксис
    // атрибутов, и сравнение ниже было бы сравнением с пустым списком
    expect(discriminators.length,
      `в ${STORED_MESSAGE_REL} не разобрано ни одного [JsonDerivedType] — проверь регулярку`)
      .toBeGreaterThan(10);

    const csharp = new Set(discriminators);
    const front = new Set<string>(PERSISTED_KINDS);

    // Протухшие исключения — такая же красная лампа, как расхождение списков: молчаливого
    // «пропустим лишнее» быть не должно
    expect(Object.keys(STORED_BUT_NOT_LIVE_ITEM).filter(k => !csharp.has(k)),
      `исключение STORED_BUT_NOT_LIVE_ITEM протухло: этих видов в ${STORED_MESSAGE_REL} больше нет — убери записи`)
      .toEqual([]);
    expect(Object.keys(PERSISTED_WITHOUT_STORED_TYPE).filter(k => !front.has(k) || csharp.has(k)),
      'исключение PERSISTED_WITHOUT_STORED_TYPE протухло: вид либо исчез из PERSISTED_KINDS, либо обзавёлся C#-типом')
      .toEqual([]);

    const missingOnFront = discriminators.filter(k => !front.has(k) && !(k in STORED_BUT_NOT_LIVE_ITEM));
    expect(missingOnFront,
      'бэкенд пишет в history.json виды, которых нет в PERSISTED_KINDS (frontend/src/lib/chatReducer.ts): ' +
      `${missingOnFront.join(', ')}. Добавить их в белый список либо (если элементом ленты они не становятся) ` +
      'в STORED_BUT_NOT_LIVE_ITEM с объяснением')
      .toEqual([]);

    const missingInCSharp = [...front].filter(k => !csharp.has(k) && !(k in PERSISTED_WITHOUT_STORED_TYPE));
    expect(missingInCSharp,
      `в PERSISTED_KINDS есть виды без дискриминатора в ${STORED_MESSAGE_REL}: ${missingInCSharp.join(', ')}. ` +
      'В историю они не пишутся — убрать из белого списка либо занести в PERSISTED_WITHOUT_STORED_TYPE с объяснением')
      .toEqual([]);
  });
});

// Регрессия: BroadcastSessionMessageAsync рассылает «доклад» о делегированной задаче
// И в session-группу, И в project_/user_-группу SignalR — клиент открытого чата состоит
// в обеих и получает одно и то же событие дважды (карточка дублируется до перезагрузки
// истории). Сервер ставит один и тот же timestamp в оба прохода — applyServerMessage
// дедупит по хвосту ленты (timestamp + текст, + personaId у guest_text).
describe('applyServerMessage: дедуп эха «доклада» о делегированной задаче', () => {
  it('повторный guest_text с тем же timestamp+текстом не добавляет второй элемент', () => {
    const once = feed(initialChatState(),
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000 });
    expect(once.items).toHaveLength(1);

    const twice = applyServerMessage(once, { sessionId: SID, type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000 } as ServerMessage);
    expect(twice.items).toHaveLength(1);
    expect(twice).toBe(once); // та же ссылка — подписчики не будятся
  });

  it('повторный user_message с viaAgent (senderChatName) не добавляет второй элемент', () => {
    const once = feed(initialChatState(),
      { type: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача: починить билд', auto: true, timestamp: 2000 });
    expect(once.items).toHaveLength(1);

    const twice = applyServerMessage(once, { sessionId: SID, type: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача: починить билд', auto: true, timestamp: 2000 } as ServerMessage);
    expect(twice.items).toHaveLength(1);
    expect(twice).toBe(once);
  });

  it('два guest_text с одинаковым текстом, но разными timestamp — добавляются оба', () => {
    const state = feed(initialChatState(),
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000 },
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1001 },
    );
    expect(state.items).toHaveLength(2);
  });

  it('обычные user_message пользователя (без timestamp/senderChatName) не дедупятся — два подряд легитимны', () => {
    const state = feed(initialChatState(),
      { type: 'user_message', text: 'привет' },
      { type: 'user_message', text: 'привет' },
    );
    expect(state.items).toHaveLength(2);
  });
});

// Связь доклада с задачей — структурное поле сообщения, а не id, выковыренный из текста
// маркера. Ломается в двух местах: живое событие (редьюсер) и снимок истории
// (normalizeHistory). Старые записи поля не несут — карточка обязана деградировать.
describe('доклад о задаче: delegationTaskId доезжает до ленты', () => {
  it('guest_text несёт delegationTaskId в text-элемент', () => {
    const state = feed(initialChatState(),
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000, delegationTaskId: 't-42' });
    expect(state.items[0]).toMatchObject({ kind: 'text', delegationTaskId: 't-42' });
  });

  it('user_message несёт delegationTaskId (доклад из чата без персоны)', () => {
    const state = feed(initialChatState(),
      { type: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача: починить билд', auto: true, timestamp: 2000, delegationTaskId: 't-43' });
    expect(state.items[0]).toMatchObject({ kind: 'user_message', delegationTaskId: 't-43' });
  });

  it('история: поле переживает нормализацию для text и user_message', () => {
    const items = snapshot(
      { kind: 'text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000, delegationTaskId: 't-42' },
      { kind: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача', timestamp: 2000, delegationTaskId: 't-43' },
    );
    expect(items[0]).toMatchObject({ kind: 'text', ts: 1000, delegationTaskId: 't-42' });
    expect(items[1]).toMatchObject({ kind: 'user_message', ts: 2000, delegationTaskId: 't-43' });
  });

  it('совместимость: у старых записей поля нет — undefined, а не мусор', () => {
    const items = snapshot({ kind: 'text', text: 'старая реплика', personaId: 'p1' });
    expect((items[0] as { delegationTaskId?: string }).delegationTaskId).toBeUndefined();

    const live = feed(initialChatState(), { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1' });
    expect((live.items[0] as { delegationTaskId?: string }).delegationTaskId).toBeUndefined();
  });
});

// Э2 КР-наблюдаемости: событие team_wave_pulse эфемерное — пишется в ChatState.
// teamWavePulse, а НЕ в items. Регрессия: счётчик длины ленты (serverHistoryNewer)
// считает ТОЛЬКО persisted kinds, и если бы pulse стал persisted, перезагрузка истории
// навсегда признавалась бы «не новее» живой ленты, и ответ оборванного хода залипал бы
describe('team_wave_pulse: эфемерность', () => {
  const waveState = (over: Partial<Extract<ServerMessage, { type: 'team_implement' }>> = {}) =>
    ({
      type: 'team_implement', active: true, stage: 'wave', waveNumber: 1, plannedWaves: 2,
      autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-plan',
      executorPersonaIds: ['p-1'], budget: null, planCardId: null, modeLocked: false,
      ...over,
    }) as unknown as ServerMessage;

  const wavePulse = (over: Partial<Extract<ServerMessage, { type: 'team_wave_pulse' }>> = {}) =>
    ({
      type: 'team_wave_pulse', stage: 'wave', waveNumber: 1, plannedWaves: 2,
      tasksActive: 2, tasksTotal: 5,
      lastActivityAt: '2026-08-23T11:00:00Z', quietSeconds: 240, liveness: 'alive',
      ...over,
    }) as unknown as ServerMessage;

  it('не добавляет элементов в items', () => {
    const before: ChatState = applyServerMessage(initialChatState(), waveState());
    const after: ChatState = applyServerMessage(before, wavePulse());
    expect(after.items).toEqual(before.items);
  });

  it('обновляет ChatState.teamWavePulse (не путать с persisted)', () => {
    let s: ChatState = applyServerMessage(initialChatState(), waveState());
    expect(s.teamWavePulse).toBeUndefined();
    s = applyServerMessage(s, wavePulse({ liveness: 'stalled', quietSeconds: 2400 }));
    expect(s.teamWavePulse?.liveness).toBe('stalled');
    expect(s.teamWavePulse?.quietSeconds).toBe(2400);
    expect(s.teamWavePulse?.waveNumber).toBe(1);
  });

  it('live-only: сверка «сервер новее?» не считает team_wave_pulse', () => {
    // Без этой проверки вышло бы: пульс удлиняет items, serverHistoryNewer возвращает
    // false (live длиннее), и перезагрузка истории не подтягивает оборванный хвост хода
    const client = feed(initialChatState(), waveState(), wavePulse(), wavePulse());
    const server = snapshot({ kind: 'text', text: 'ответ, дописанный пока вкладка была в другом чате' });
    expect(serverHistoryNewer(server, client.items)).toBe(true);
  });
});

// Ответ на карточку с ДРУГОГО устройства: своя копия формы обязана погаснуть.
// Без события interaction_resolved форма висела активной до перезагрузки страницы —
// перезагрузка истории тут не спасает, потому что ответ не добавляет элемент в ленту,
// а дописывает решение в существующий, и сверка по длине даёт «сервер не новее».
describe('interaction_resolved: ответ с другого устройства', () => {
  const askItem = (s: ChatState) =>
    s.items.find(i => i.kind === 'ask_question') as Extract<ChatItem, { kind: 'ask_question' }>;

  it('гасит карточку вопроса и сохраняет выбранные ответы', () => {
    const before = feed(initialChatState(),
      { type: 'ask_question', toolUseId: 't-1', input: { questions: [] } });
    expect(askItem(before).resolved).toBe(false);

    const after = feed(before, {
      type: 'interaction_resolved', kind: 'question', id: 't-1',
      answers: { 'Подход': 'Первый' },
    });
    expect(askItem(after).resolved).toBe(true);
    expect(askItem(after).answers).toEqual({ 'Подход': 'Первый' });
  });

  it('гасит карточку разрешения с вердиктом', () => {
    const after = feed(initialChatState(),
      { type: 'permission_request', requestId: 'r-1', toolName: 'Bash', toolInput: {} },
      { type: 'interaction_resolved', kind: 'permission', id: 'r-1', decision: 'always' });
    const item = after.items.find(i => i.kind === 'permission_request') as
      Extract<ChatItem, { kind: 'permission_request' }>;
    expect(item.resolved).toBe(true);
    expect(item.decision).toBe('always');
  });

  it('гасит карточку плана с решением и комментарием', () => {
    const after = feed(initialChatState(),
      { type: 'plan_review', requestId: 'r-2', plan: 'план' },
      { type: 'interaction_resolved', kind: 'plan', id: 'r-2', approved: false, feedback: 'доработать' });
    const item = after.items.find(i => i.kind === 'plan_review') as
      Extract<ChatItem, { kind: 'plan_review' }>;
    expect(item.resolved).toBe(true);
    expect(item.approved).toBe(false);
    expect(item.feedback).toBe('доработать');
  });

  it('чужой id и уже погашенную карточку не трогает (возвращает прежнее состояние)', () => {
    const before = feed(initialChatState(),
      { type: 'ask_question', toolUseId: 't-1', input: { questions: [] } },
      { type: 'interaction_resolved', kind: 'question', id: 't-1', answers: {} });
    // Повторное событие и событие о чужой карточке — холостые
    expect(feed(before, { type: 'interaction_resolved', kind: 'question', id: 't-1' })).toBe(before);
    expect(feed(before, { type: 'interaction_resolved', kind: 'question', id: 'другой' })).toBe(before);
  });

  it('live-only: сверка «сервер новее?» не считает interaction_resolved', () => {
    const client = feed(initialChatState(),
      { type: 'ask_question', toolUseId: 't-1', input: { questions: [] } },
      { type: 'interaction_resolved', kind: 'question', id: 't-1', answers: {} });
    const server = snapshot({ kind: 'ask_question', toolUseId: 't-1', input: { questions: [] } });
    expect(serverHistoryNewer(server, client.items)).toBe(false);
  });
});


// Разрыв связи ровно в момент ответа с другого устройства: событие interaction_resolved
// прошло мимо, остаётся перезагрузка истории. Гасим точечно, не подменяя ленту —
// подмена унесла бы live-only карточку разрешения (permission_request в history не пишется).
describe('resolveCardsFromHistory: догоняем пропущенный ответ', () => {
  const openAsk = (id = 't-1') => ({ type: 'ask_question', toolUseId: id, input: { questions: [] } });

  it('гасит вопрос и переносит выбранные ответы из истории', () => {
    const client = feed(initialChatState(), openAsk());
    const server = snapshot({
      kind: 'ask_question', toolUseId: 't-1', input: { questions: [] },
      resolved: true, answers: { 'Подход': 'Первый' },
    });
    const patched = resolveCardsFromHistory(server, client.items)!;
    const card = patched.find(i => i.kind === 'ask_question') as Extract<ChatItem, { kind: 'ask_question' }>;
    expect(card.resolved).toBe(true);
    expect(card.answers).toEqual({ 'Подход': 'Первый' });
  });

  it('гасит карточку плана с решением и комментарием', () => {
    const client = feed(initialChatState(), { type: 'plan_review', requestId: 'r-1', plan: 'план' });
    const server = snapshot({
      kind: 'plan_review', requestId: 'r-1', plan: 'план', resolved: true, approved: false, feedback: 'доработать',
    });
    const card = resolveCardsFromHistory(server, client.items)!
      .find(i => i.kind === 'plan_review') as Extract<ChatItem, { kind: 'plan_review' }>;
    expect(card.resolved).toBe(true);
    expect(card.approved).toBe(false);
    expect(card.feedback).toBe('доработать');
  });

  it('работает и когда живая лента ДЛИННЕЕ истории (снимок сервера ещё не догнал ход)', () => {
    // Сверка длин тут молчит: prev длиннее. Без точечного гашения форма висела бы до конца хода
    const client = feed(initialChatState(), openAsk(), { type: 'text_delta', text: 'продолжаю работу' });
    const server = snapshot({
      kind: 'ask_question', toolUseId: 't-1', input: { questions: [] }, resolved: true, answers: {},
    });
    expect(serverHistoryNewer(server, client.items)).toBe(false);
    const patched = resolveCardsFromHistory(server, client.items)!;
    expect((patched.find(i => i.kind === 'ask_question') as { resolved: boolean }).resolved).toBe(true);
    // Хвост живой ленты на месте — историю мы не подменяли
    expect(patched.some(i => i.kind === 'text' && i.text === 'продолжаю работу')).toBe(true);
  });

  it('живая карточка разрешения остаётся в ленте: её в истории нет вовсе', () => {
    // Регрессия, ради которой гасим точечно: подмена ленты историей унесла бы кнопки
    // permission с экрана, а отвечать было бы нечем — CLI ждёт до таймаута
    const client = feed(initialChatState(), openAsk(),
      { type: 'permission_request', requestId: 'r-1', toolName: 'Bash', toolInput: {} });
    const server = snapshot({
      kind: 'ask_question', toolUseId: 't-1', input: { questions: [] }, resolved: true, answers: {},
    });
    const patched = resolveCardsFromHistory(server, client.items)!;
    const perm = patched.find(i => i.kind === 'permission_request') as
      Extract<ChatItem, { kind: 'permission_request' }>;
    expect(perm.resolved).toBe(false);
  });

  it('гасить нечего — возвращает null (лента остаётся прежней)', () => {
    const client = feed(initialChatState(), openAsk('t-2'));
    // Ни одной погашенной карточки в истории
    expect(resolveCardsFromHistory(snapshot({ kind: 'text', text: 'ответ' }), client.items)).toBeNull();
    // Погашена ЧУЖАЯ карточка
    const foreign = snapshot({
      kind: 'ask_question', toolUseId: 't-1', input: { questions: [] }, resolved: true, answers: {},
    });
    expect(resolveCardsFromHistory(foreign, client.items)).toBeNull();
    // Наша карточка уже погашена — повтор холостой
    const resolvedClient = feed(client, { type: 'interaction_resolved', kind: 'question', id: 't-2' });
    const server = snapshot({
      kind: 'ask_question', toolUseId: 't-2', input: { questions: [] }, resolved: true, answers: {},
    });
    expect(resolveCardsFromHistory(server, resolvedClient.items)).toBeNull();
  });
});

describe('тихие строки чата картинки (ADR-018 §1, §2)', () => {
  const launch = { type: 'image_launch', by: 'human', prompt: 'вечер', provider: 'fal', model: 'auto', count: 2, jobId: 'j1', timestamp: 5 };

  it('живая строка «Вы запустили» — одна на задачу, повторная доставка не удваивает', () => {
    const s = feed(initialChatState(), launch, launch);
    expect(s.items.filter(i => i.kind === 'image_launch')).toHaveLength(1);
  });

  it('живая лента и история сверяются по длине: строки переживают перезагрузку', () => {
    const live = feed(initialChatState(), launch, { type: 'image_file_moved', from: 'a.png', to: 'a.v2.png', timestamp: 6 });
    const stored = snapshot(
      { kind: 'image_launch', by: 'human', prompt: 'вечер', provider: 'fal', model: 'auto', count: 2, jobId: 'j1', timestamp: 5 },
      { kind: 'image_file_moved', from: 'a.png', to: 'a.v2.png', timestamp: 6 },
    );
    expect(stored.map(i => i.kind)).toEqual(['image_launch', 'image_file_moved']);
    expect(serverHistoryNewer(stored, live.items)).toBe(false);
  });
});
