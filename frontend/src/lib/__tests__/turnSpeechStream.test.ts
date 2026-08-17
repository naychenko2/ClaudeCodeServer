import { describe, it, expect } from 'vitest';
import { turnText, turnStreamChunks, turnStreamTail, TURN_STREAM_INIT } from '../turnSpeechStream';
import type { ChatItem } from '../../types';

// Ход в ленте: user_message → текст (возможно несколько элементов после tool_use) →
// result. Собираем минимальные массивы ChatItem для функций стриминга.
const user = (text: string): ChatItem => ({ kind: 'user_message', text });
const text = (t: string, parentToolUseId?: string): ChatItem =>
  ({ kind: 'text', text: t, ...(parentToolUseId ? { parentToolUseId } : {}) });
const result: ChatItem = { kind: 'result', totalCostUsd: 0, usage: { inputTokens: 0, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0 }, numTurns: 1, durationMs: 1, contextTokens: 0 };

describe('turnText', () => {
  it('конкатенирует text-элементы последнего хода, сабагентов пропускает', () => {
    const items: ChatItem[] = [
      user('вопрос'),
      text('Первый абзац.'),
      { kind: 'tool_use', id: 't1', name: 'Read', input: {} } as ChatItem,
      text('подумал сабагент', 't1'),
      text('Второй абзац.'),
      result,
    ];
    expect(turnText(items)).toBe('Первый абзац.\nВторой абзац.');
  });

  it('не лезет в прошлые ходы', () => {
    const items: ChatItem[] = [
      user('в1'), text('Старый ответ.'), result,
      user('в2'), text('Новый ответ.'), result,
    ];
    expect(turnText(items)).toBe('Новый ответ.');
  });

  // Регрессия: живой ВТОРОЙ ход — формулы «после последнего result» здесь
  // возвращала текст первого хода, и стриминг озвучивал старый ответ первой
  // фразой ещё до дельт нового (баг «модель без раздумья говорит первой фразой»)
  it('живой второй ход — не видит текст первого', () => {
    const secondTurn: ChatItem[] = [user('в2')];
    expect(turnText([user('в1'), text('Старый ответ.'), result, ...secondTurn])).toBe('');
    // Первые дельты второго хода — только они, старых кусков нет
    expect(turnText([user('в1'), text('Старый ответ.'), result, user('в2'), text('Нов')])).toBe('Нов');
  });

  it('живый ход без реплики пользователя (снимок истории) — пусто', () => {
    expect(turnText([text('осиротевший текст')])).toBe('');
  });
});

describe('turnStreamChunks', () => {
  it('второй ход не доозвучивает остатки первого', () => {
    // Регрессия бага «первой фразой без раздумья»: очередь редьюсера держит оба
    // хода; на старте второго стриминг обязан получить ПУСТУЮ нарезку
    const items: ChatItem[] = [
      user('в1'), text('Старый ответ. Вторая фраза.'), result,
      user('в2'),
    ];
    const r = turnStreamChunks(TURN_STREAM_INIT, items);
    expect(r.chunks).toEqual([]);
    expect(r.cursor).toBe(0);
  });

  it('два куска до result: первый ход отдаёт куски по ходу дельт', () => {
    // Дельта 1: предложение не дописано — куска нет
    let st = TURN_STREAM_INIT;
    let r = turnStreamChunks(st, [user('в'), text('Первое предлож')]);
    expect(r.chunks).toEqual([]);
    expect(r.cursor).toBe(0);
    // Дельта 2: первое предложение закрылось
    r = turnStreamChunks({ ...st, cursor: r.cursor }, [user('в'), text('Первое предложение. Втор')]);
    expect(r.chunks).toEqual(['Первое предложение.']);
    st = { ...st, cursor: r.cursor };
    // Дельта 3: второе закрылось
    r = turnStreamChunks(st, [user('в'), text('Первое предложение. Второе предложение.')]);
    expect(r.chunks).toEqual(['Второе предложение.']);
  });

  it('ход закрыт маркером конца (result/error) — кусков не отдаёт', () => {
    const r = turnStreamChunks(TURN_STREAM_INIT, [user('в'), text('Готовый ответ.'), result]);
    expect(r.ended).toBe(true);
    expect(r.chunks).toEqual([]);
  });

  it('interrupted/error тоже закрывают ход', () => {
    const interrupted: ChatItem = { kind: 'interrupted' };
    const r = turnStreamChunks(TURN_STREAM_INIT, [user('в'), text('обрывок'), interrupted]);
    expect(r.ended).toBe(true);
  });

  it('hitMarkup выключает стриминг хода: код-блок впереди', () => {
    const r = turnStreamChunks(TURN_STREAM_INIT, [user('в'), text('Смотри код:\n```ts')]);
    expect(r.off).toBe(true);
    // Текст до разметки не отдаётся этим вызовом
    expect(r.chunks).toEqual([]);
  });

  it('после off резка больше не идёт (гейт эффекта)', () => {
    const st = { cursor: 0, off: true };
    const r = turnStreamChunks(st, [user('в'), text('Всё равно не читаю. Дальше.')]);
    expect(r.off).toBe(true);
    expect(r.chunks).toEqual([]);
  });
});

describe('turnStreamTail', () => {
  it('хвост на result — всегда, даже если cursor не двигался', () => {
    // Короткий ответ без точек: ни один кусок не ушёл из дельт, весь текст — хвост
    const items: ChatItem[] = [user('в'), text('коротко без знаков'), result];
    expect(turnStreamTail(TURN_STREAM_INIT, items)).toBe('коротко без знаков');
  });

  it('хвост — только неозвученный остаток', () => {
    const items: ChatItem[] = [user('в'), text('Первое. Второе.'), result];
    const afterFirst = turnStreamChunks(TURN_STREAM_INIT, [user('в'), text('Первое. Втор')]);
    const st = { cursor: afterFirst.cursor, off: false };
    expect(turnStreamTail(st, items)).toBe('Второе.');
  });

  it('санитайзер чистит хвост с разметкой (ветка hitMarkup)', () => {
    const items: ChatItem[] = [user('в'), text('Сначала скажу.\n```ts\nconst a = 1;\n```\nПотом.'), result];
    // Дельты отдали текст до разметки и выключились
    const cut = turnStreamChunks(TURN_STREAM_INIT, [user('в'), text('Сначала скажу.\n```ts')]);
    expect(cut.off).toBe(true);
    const tail = turnStreamTail({ cursor: cut.cursor, off: true }, items);
    expect(tail).not.toContain('const');
    expect(tail).toContain('Потом.');
  });

  it('пустой хвост — пустая строка', () => {
    const items: ChatItem[] = [user('в'), text('Всё озвучено.'), result];
    const r = turnStreamChunks(TURN_STREAM_INIT, [user('в'), text('Всё озвучено.')]);
    expect(turnStreamTail({ cursor: r.cursor, off: false }, items)).toBe('');
  });
});
