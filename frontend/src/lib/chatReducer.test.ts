import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  serverHistoryNewer, PERSISTED_KINDS, applyServerMessage, normalizeHistory, initialChatState,
  type ChatState,
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
// бэкенд реально пишет в history.json. Разъехались — сверка длин врёт.
describe('PERSISTED_KINDS ↔ StoredMessage.cs', () => {
  it('совпадает с дискриминаторами StoredMessage', () => {
    const here = path.dirname(fileURLToPath(import.meta.url));
    const storedPath = path.resolve(here, '..', '..', '..', 'backend',
      'ClaudeHomeServer', 'Protocol', 'StoredMessage.cs');
    const src = readFileSync(storedPath, 'utf8');
    const discriminators = [...src.matchAll(/JsonDerivedType\(typeof\([^)]+\),\s*"([a-z_]+)"\)/g)]
      .map(m => m[1]);

    expect(discriminators.length).toBeGreaterThan(10); // регулярка жива, файл найден

    // workflow_progress персистится, но собственным элементом ленты не становится:
    // normalizeHistory вливает его в карточку родительского tool_use
    const expected = discriminators.filter(k => k !== 'workflow_progress');

    expect([...PERSISTED_KINDS].sort()).toEqual([...expected].sort());
  });
});
