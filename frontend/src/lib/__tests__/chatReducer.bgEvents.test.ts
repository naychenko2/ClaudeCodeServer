// Тесты fail-safe закрытия bg-карточек в case 'exited' chatReducer: если процесс
// claude умер до прихода финального bg_agent_done, висящие карточки фоновых агентов
// должны закрыться «прервано» (bgDone + bgAborted), иначе их спиннер висит навсегда.
// Уже полученные терминальные статусы (успех/ошибка) fail-safe не перетирает.

import { describe, it, expect } from 'vitest';
import type { ChatItem, ServerMessage } from '../../types';
import { applyServerMessage, initialChatState, type ChatState } from '../chatReducer';

const SID = 's1';

const msg = (m: Omit<ServerMessage, 'sessionId'>): ServerMessage =>
  ({ sessionId: SID, ...m } as ServerMessage);

const state = (over: Partial<ChatState> = {}): ChatState =>
  ({ ...initialChatState(), ...over });

// Квитанция фонового запуска CLI — по ней isBgLaunchResult узнаёт bg-карточку
const BG_ACK = 'Async agent launched successfully.\nagentId: a123 (use SendMessage to continue)\noutput_file: /tmp/a123.out';

// Карточка фонового агента: result — квитанция запуска, ответ ещё не доехал
const bgToolUse = (id: string, extra: Partial<Extract<ChatItem, { kind: 'tool_use' }>> = {}): ChatItem =>
  ({ kind: 'tool_use', id, name: 'Agent', input: { prompt: 'исследуй', run_in_background: true }, result: BG_ACK, ...extra });

describe("applyServerMessage: exited — fail-safe bg-карточек", () => {
  it("закрывает висящую bg-карточку «прервано» (bgDone + bgAborted)", () => {
    const initial = state({ items: [bgToolUse('t1')] });
    const next = applyServerMessage(initial, msg({ type: 'exited' }));
    expect(next.items[0]).toMatchObject({ kind: 'tool_use', id: 't1', bgDone: true, bgAborted: true });
  });

  it('не перетирает уже полученный терминальный статус (успех/ошибка сохраняются)', () => {
    const initial = state({
      items: [
        bgToolUse('ok', { bgDone: true }),                    // успех — завершился до exited
        bgToolUse('fail', { bgDone: true, bgAborted: true }), // уже помечен прерванным
      ],
    });
    const next = applyServerMessage(initial, msg({ type: 'exited' }));
    // Успех не превращается в «прервано»: bgAborted не появляется
    expect(next.items[0]).toMatchObject({ bgDone: true });
    expect(next.items[0]).not.toHaveProperty('bgAborted', true);
    // Оба элемента возвращены той же ссылкой — карточки не пересобирались
    expect(next.items[0]).toBe(initial.items[0]);
    expect(next.items[1]).toBe(initial.items[1]);
    expect(next.items[1]).toMatchObject({ bgDone: true, bgAborted: true });
  });

  it('без открытых bg-карточек не мутирует ленту (items той же ссылкой, идемпотентно)', () => {
    const plainTool: ChatItem = { kind: 'tool_use', id: 't9', name: 'Bash', input: { command: 'ls' }, result: 'ok', isError: false };
    const initial = state({ items: [{ kind: 'text', text: 'готово' }, plainTool] });
    const next = applyServerMessage(initial, msg({ type: 'exited' }));
    // Нет ни пересборки массива, ни новых элементов (session_ended не добавляется — isWaiting false)
    expect(next.items).toBe(initial.items);
    const again = applyServerMessage(next, msg({ type: 'exited' }));
    expect(again.items).toBe(next.items);
  });
});
