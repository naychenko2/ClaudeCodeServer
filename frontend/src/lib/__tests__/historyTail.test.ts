import { describe, it, expect } from 'vitest';
import { applyHistoryReply, cursorQuery } from '../historyTail';

// Фейковый сервер: тот же алгоритм, что ChatHistoryTail на бэке (образец — serverTail
// в hooks/__tests__/useSession.test.ts). Вес ответа — длина его JSON: так его мерит
// вызывающий, передавая в applyHistoryReply
type Rec = { kind: string; [k: string]: unknown };

function serverTail(history: Rec[], q: { after: number; fp?: string }) {
  let anchor = -1;
  history.forEach((r, i) => { if (r.kind === 'result' || r.kind === 'error' || r.kind === 'interrupted') anchor = i; });
  const fpOf = (i: number) => (i < 0 ? null : JSON.stringify(history.slice(0, i + 1)));
  const anchorFp = fpOf(anchor);
  if (q.after < 0 || q.after >= history.length || fpOf(q.after) !== q.fp)
    return { reset: true, messages: history, anchorIndex: anchor, anchorFp };
  return { reset: false, messages: history.slice(q.after + 1), anchorIndex: anchor, anchorFp };
}

const weight = (messages: unknown[]) => JSON.stringify(messages).length;

// Завершённый первый ход и завершённый второй (то, что приклеится хвостом)
const turn1 = (): Rec[] => [
  { kind: 'user_message', text: 'первый' },
  { kind: 'tool_use', id: 't1', name: 'Bash', input: {}, result: 'ok' },
  { kind: 'text', text: 'ответ 1' },
  { kind: 'result', subtype: 'success' },
];
const turn2 = (): Rec[] => [
  { kind: 'user_message', text: 'второй' },
  { kind: 'result', subtype: 'success' },
];

describe('applyHistoryReply: вес курсора (F3-S1b)', () => {
  it('полный цикл (полная загрузка + хвост): вес = сумма весов ответов', () => {
    const full1 = turn1();
    // 1. Первая загрузка after=-1: сервер отдаёт всё — вес = вес ответа
    const q1 = { after: -1 };
    const r1 = serverTail(full1, q1);
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));
    expect(a1.bytes).toBe(weight(full1));
    expect(a1.cursor?.bytes).toBe(weight(full1));

    // 2. Вторая загрузка: курсор на якоре первого хода, сервер отдаёт только хвост.
    // Итоговый вес = прошлый + хвост и равен весу всей склеенной ленты
    const q2 = cursorQuery(a1.cursor);
    const server2 = [...full1, ...turn2()];
    const r2 = serverTail(server2, q2);
    expect(r2.reset).toBe(false); // страховка: сценарий именно склейка
    const a2 = applyHistoryReply(a1.cursor, q2, r2, weight(r2.messages));
    expect(a2.raw).toEqual(server2);
    expect(a2.bytes).toBe(weight(full1) + weight(turn2()));
    expect(a2.cursor?.bytes).toBe(weight(full1) + weight(turn2()));
  });

  it('reset от сервера (отпечаток не совпал): вес только нового ответа, прошлый не прибавляется', () => {
    const full1 = turn1();
    const q1 = { after: -1 };
    const r1 = serverTail(full1, q1);
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));

    // Что-то до курсора переписано: сервер отвечает reset и полной лентой
    const rewritten = [{ kind: 'user_message', text: 'первый (переписан)' }, ...full1.slice(1)];
    const q2 = cursorQuery(a1.cursor);
    const r2 = serverTail(rewritten, q2);
    expect(r2.reset).toBe(true); // страховка: сценарий именно reset
    const a2 = applyHistoryReply(a1.cursor, q2, r2, weight(r2.messages));
    expect(a2.bytes).toBe(weight(rewritten));
    expect(a2.cursor?.bytes).toBe(weight(rewritten));
  });

  it('полная выдача после after=-1 при курсоре без якоря: вес только ответа', () => {
    // Курсор без якоря: у чата был только идущий ход — следующая загрузка снова полная
    const running = [{ kind: 'user_message', text: 'идёт ход' }];
    const q1 = { after: -1 };
    const r1 = serverTail(running, q1);
    expect(r1.anchorIndex).toBe(-1); // страховка: якоря правда нет
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));
    expect(a1.bytes).toBe(weight(running));

    // Ход завершился: запрос снова after=-1, сервер отдаёт полную ленту —
    // вес считается заново, прошлый к нему не прибавляется
    const done = [...running, { kind: 'result', subtype: 'success' }];
    const q2 = cursorQuery(a1.cursor);
    expect(q2).toEqual({ after: -1 }); // страховка: запрос действительно полный
    const r2 = serverTail(done, q2);
    const a2 = applyHistoryReply(a1.cursor, q2, r2, weight(r2.messages));
    expect(a2.bytes).toBe(weight(done));
    expect(a2.cursor?.bytes).toBe(weight(done));
  });

  it('плоский массив (старый сервер): вес возвращён, курсора нет', () => {
    const full1 = turn1();
    const q1 = { after: -1 };
    const r1 = serverTail(full1, q1);
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));

    // Старый сервер отвечает плоским массивом: следующая загрузка снова полная,
    // курсор сбрасывается — накопленный вес с ним уходит
    const flat = [...full1, ...turn2()];
    const a2 = applyHistoryReply(a1.cursor, cursorQuery(a1.cursor), flat, weight(flat));
    expect(a2.raw).toEqual(flat);
    expect(a2.bytes).toBe(weight(flat));
    expect(a2.cursor).toBeUndefined();
  });

  it('вызов без веса (как в useSession.ts:62): вес 0, старый вызов компилируется', () => {
    const full1 = turn1();
    const q1 = { after: -1 };
    const r1 = serverTail(full1, q1);
    const a1 = applyHistoryReply(undefined, q1, r1); // 4-го аргумента нет — по умолчанию 0
    expect(a1.bytes).toBe(0);
    expect(a1.cursor?.bytes).toBe(0);

    const a2 = applyHistoryReply(a1.cursor, cursorQuery(a1.cursor), [...full1]); // плоский, без веса
    expect(a2.bytes).toBe(0);
    expect(a2.cursor).toBeUndefined();
  });
});
