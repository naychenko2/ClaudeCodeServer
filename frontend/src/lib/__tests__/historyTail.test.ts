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

describe('applyHistoryReply: счётчик полученного receivedBytes в курсоре (F3-S1b)', () => {
  it('полный цикл (полная загрузка + хвост): счётчик = сумма весов ответов', () => {
    const full1 = turn1();
    // 1. Первая загрузка after=-1: сервер отдаёт всё — счётчик = вес ответа
    const q1 = { after: -1 };
    const r1 = serverTail(full1, q1);
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));
    expect(a1.receivedBytes).toBe(weight(full1));
    expect(a1.cursor?.receivedBytes).toBe(weight(full1));

    // 2. Вторая загрузка: курсор на якоре первого хода, сервер отдаёт только хвост.
    // Счётчик = прошлый + вес этого ответа — это сумма весов ответов, а НЕ вес склеенной
    // ленты: здесь отброшенных записей нет, и разница с весом ленты — только склейка JSON
    const q2 = cursorQuery(a1.cursor);
    const server2 = [...full1, ...turn2()];
    const r2 = serverTail(server2, q2);
    expect(r2.reset).toBe(false); // страховка: сценарий именно склейка
    const a2 = applyHistoryReply(a1.cursor, q2, r2, weight(r2.messages));
    expect(a2.raw).toEqual(server2);
    expect(a2.receivedBytes).toBe(weight(full1) + weight(turn2()));
    expect(a2.cursor?.receivedBytes).toBe(weight(full1) + weight(turn2()));
  });

  it('якорь в середине префикса (загрузка во время идущего хода): счётчик = полученное, завышение = вес отброшенного хвоста', () => {
    // Первая загрузка случилась во время идущего хода: после якоря в полной выдаче лежат
    // частичные записи незавершённого ответа — они попадут в raw клиента
    const partial2: Rec[] = [{ kind: 'text', text: 'ответ 2, идёт ход' }];
    const history1 = [...turn1(), ...partial2];
    const q1 = { after: -1 };
    const r1 = serverTail(history1, q1);
    expect(r1.anchorIndex).toBe(3); // страховка: якорь на конце завершённого хода, частичные — после него
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));
    expect(a1.receivedBytes).toBe(weight(history1)); // отбрасывать пока нечего: счётчик == вес raw

    // Ход 2 завершился, начался ход 3. Докачка №1: сервер отдаёт всё после якоря — снова с
    // частичными записями, что уже лежат в нашем raw после якоря. Из ленты они отброшены
    // (придут заново в хвосте), но их вес остаётся в счётчике
    const done2: Rec[] = [{ kind: 'result', subtype: 'success' }];
    const partial3: Rec[] = [{ kind: 'text', text: 'ответ 3, идёт ход' }];
    const history2 = [...history1, ...done2, ...partial3];
    const q2 = cursorQuery(a1.cursor);
    expect(q2.after).toBe(3); // страховка: запрос именно хвост от старого якоря
    const r2 = serverTail(history2, q2);
    expect(r2.reset).toBe(false); // страховка: сценарий именно склейка
    const a2 = applyHistoryReply(a1.cursor, q2, r2, weight(r2.messages));
    expect(a2.raw).toEqual(history2); // лента правильная: отброшенные частичные пришли заново в хвосте
    expect(a2.receivedBytes).toBe(weight(history1) + weight(r2.messages)); // сумма весов ответов
    expect(a2.receivedBytes).toBeGreaterThan(weight(a2.raw)); // верхняя оценка веса ленты
    expect(a2.receivedBytes - weight(a2.raw)).toBe(weight(partial2)); // завышение = вес отброшенного хвоста

    // Докачка №2: ход 3 завершился, начался ход 4 — завышение накапливается на вес новых
    // отброшенных записей (склейки подряд, без сброса)
    const done3: Rec[] = [{ kind: 'result', subtype: 'success' }];
    const partial4: Rec[] = [{ kind: 'text', text: 'ответ 4, идёт ход' }];
    const history3 = [...history2, ...done3, ...partial4];
    const q3 = cursorQuery(a2.cursor);
    expect(q3.after).toBe(5); // страховка: якорь сдвинулся на конец завершённого хода 2
    const r3 = serverTail(history3, q3);
    expect(r3.reset).toBe(false); // страховка: снова склейка, не reset
    const a3 = applyHistoryReply(a2.cursor, q3, r3, weight(r3.messages));
    expect(a3.raw).toEqual(history3);
    expect(a3.receivedBytes).toBe(weight(history1) + weight(r2.messages) + weight(r3.messages));
    expect(a3.receivedBytes).toBeGreaterThan(weight(a3.raw));
    expect(a3.receivedBytes - weight(a3.raw)).toBe(weight(partial2) + weight(partial3)); // накопление отброшенного
  });

  it('reset от сервера (отпечаток не совпал): счётчик только нового ответа, прошлый не прибавляется', () => {
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
    expect(a2.receivedBytes).toBe(weight(rewritten));
    expect(a2.cursor?.receivedBytes).toBe(weight(rewritten));
  });

  it('полная выдача после after=-1 при курсоре без якоря: счётчик только ответа', () => {
    // Курсор без якоря: у чата был только идущий ход — следующая загрузка снова полная
    const running = [{ kind: 'user_message', text: 'идёт ход' }];
    const q1 = { after: -1 };
    const r1 = serverTail(running, q1);
    expect(r1.anchorIndex).toBe(-1); // страховка: якоря правда нет
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));
    expect(a1.receivedBytes).toBe(weight(running));

    // Ход завершился: запрос снова after=-1, сервер отдаёт полную ленту —
    // счётчик начинается заново, прошлый к нему не прибавляется
    const done = [...running, { kind: 'result', subtype: 'success' }];
    const q2 = cursorQuery(a1.cursor);
    expect(q2).toEqual({ after: -1 }); // страховка: запрос действительно полный
    const r2 = serverTail(done, q2);
    const a2 = applyHistoryReply(a1.cursor, q2, r2, weight(r2.messages));
    expect(a2.receivedBytes).toBe(weight(done));
    expect(a2.cursor?.receivedBytes).toBe(weight(done));
  });

  it('плоский массив (старый сервер): счётчик возвращён, курсора нет', () => {
    const full1 = turn1();
    const q1 = { after: -1 };
    const r1 = serverTail(full1, q1);
    const a1 = applyHistoryReply(undefined, q1, r1, weight(r1.messages));

    // Старый сервер отвечает плоским массивом: следующая загрузка снова полная,
    // курсор сбрасывается — накопленный счётчик с ним уходит
    const flat = [...full1, ...turn2()];
    const a2 = applyHistoryReply(a1.cursor, cursorQuery(a1.cursor), flat, weight(flat));
    expect(a2.raw).toEqual(flat);
    expect(a2.receivedBytes).toBe(weight(flat));
    expect(a2.cursor).toBeUndefined();
  });

  it('вызов без веса (как в useSession.ts:62): счётчик 0, старый вызов компилируется', () => {
    const full1 = turn1();
    const q1 = { after: -1 };
    const r1 = serverTail(full1, q1);
    const a1 = applyHistoryReply(undefined, q1, r1); // 4-го аргумента нет — по умолчанию 0
    expect(a1.receivedBytes).toBe(0);
    expect(a1.cursor?.receivedBytes).toBe(0);

    const a2 = applyHistoryReply(a1.cursor, cursorQuery(a1.cursor), [...full1]); // плоский, без веса
    expect(a2.receivedBytes).toBe(0);
    expect(a2.cursor).toBeUndefined();
  });
});
