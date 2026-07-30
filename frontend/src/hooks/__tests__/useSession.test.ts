/* eslint-disable react-hooks/rules-of-hooks --
   Хук здесь гоняется вне React: модуль react замокан мини-раннером ниже, порядок
   вызова хуков и жизненный цикл эффектов держит openChat. */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ChatItem } from '../../types';

// Тесты гонки «снапшот истории → вход в SignalR-группу» и залипания isWaiting
// при переключении между чатами. Гоняется НАСТОЯЩИЙ useSession: react, lib/api и
// lib/signalr замоканы, поэтому joinAndLoadHistory/reloadHistory работают как в бою.

// --- Мини-раннер React-хуков (окружение node: ни jsdom, ни testing-library нет) ---
// Держит слоты useEffect с их deps: повторный render с теми же deps эффект не
// перезапускает — значит render() можно звать как «прочитать текущее состояние».
const rt = vi.hoisted(() => {
  interface Slot { deps?: unknown[]; cleanup?: (() => void) | void }
  interface Host { slots: Slot[]; i: number }
  const state = { current: null as Host | null };

  const sameDeps = (a?: unknown[], b?: unknown[]) =>
    !!a && !!b && a.length === b.length && a.every((d, k) => Object.is(d, b[k]));

  return {
    state,
    useEffect(effect: () => (() => void) | void, deps?: unknown[]) {
      const host = state.current;
      if (!host) return; // вызов вне render — эффектов нет
      const slot = host.slots[host.i++];
      if (slot && sameDeps(slot.deps, deps)) return;
      slot?.cleanup?.();
      host.slots[host.i - 1] = { deps, cleanup: effect() };
    },
  };
});

vi.mock('react', () => ({
  useState: (init: unknown) => [typeof init === 'function' ? (init as () => unknown)() : init, () => { }],
  useCallback: (fn: unknown) => fn,
  useMemo: (fn: () => unknown) => fn(),
  useRef: (v: unknown) => ({ current: v }),
  useEffect: rt.useEffect,
  useSyncExternalStore: (_sub: unknown, get: () => unknown) => get(),
}));

// --- Моки транспорта: история по REST и хаб SignalR ---

const m = vi.hoisted(() => ({
  getHistory: vi.fn<(sid: string) => Promise<unknown[]>>(async () => []),
  joinSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  leaveSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  sendMessage: vi.fn<() => Promise<'started' | 'queued'>>(async () => 'started'),
}));

vi.mock('../../lib/api', () => ({
  api: {
    chats: { getHistory: (sid: string) => m.getHistory(sid) },
    sessions: {
      getHistory: (_projectId: string, sid: string) => m.getHistory(sid),
      cancelPending: vi.fn(async () => { }),
    },
  },
}));

vi.mock('../../lib/signalr', () => ({
  joinSession: (sid: string) => m.joinSession(sid),
  joinProject: vi.fn(async () => { }),
  leaveSession: (sid: string) => m.leaveSession(sid),
  onMessage: vi.fn(() => () => { }),
  onReconnected: vi.fn(() => () => { }),
  sendMessage: (...args: unknown[]) => m.sendMessage(...(args as [])),
  respondPermission: vi.fn(async () => { }),
  interruptSession: vi.fn(async () => { }),
  compactSession: vi.fn(async () => { }),
  answerQuestion: vi.fn(async () => { }),
  respondPlan: vi.fn(async () => { }),
  respondTeamPlan: vi.fn(async () => { }),
  respondTeamEscalation: vi.fn(async () => { }),
  setMode: vi.fn(async () => { }),
}));

const { useSession } = await import('../useSession');

// --- Хелперы ---

// Стор useSession живёт на уровне модуля и переживает тесты — у каждого свой sid
let _sid = 0;
const nextSid = () => `sess_${++_sid}`;

// Открытие чата: монтируем «компонент» с хуком. render() — чтение текущего
// состояния без перезапуска эффекта, unmount() — уход в другой чат.
function openChat(sid: string, projectId?: string) {
  const host = { slots: [] as Array<{ deps?: unknown[]; cleanup?: (() => void) | void }>, i: 0 };
  const render = () => {
    host.i = 0;
    rt.state.current = host;
    try { return useSession(sid, projectId); }
    finally { rt.state.current = null; }
  };
  return { first: render(), render, unmount: () => { for (const s of host.slots) s?.cleanup?.(); } };
}

// Прокрутка микро- и макрозадач: цепочка «история → join → снапшот после join»
const flush = async () => { for (let i = 0; i < 6; i++) await new Promise(r => setTimeout(r, 0)); }

function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}

// Сырые записи истории (как отдаёт сервер) — normalizeHistory приводит их к ChatItem
const user = (text: string) => ({ kind: 'user_message', text });
const text = (t: string) => ({ kind: 'text', text: t });
const result = () => ({ kind: 'result', subtype: 'success', durationMs: 10, numTurns: 1 });
const falCost = () => ({ kind: 'fal_cost', requestId: 'r1', costUsd: 0.01 });
const errorItem = () => ({ kind: 'error', text: 'сорвалось' });

const DONE_TURN = [user('первый'), text('ответ 1'), result()];

// Чат с завершённым ходом, из которого отправили сообщение и ушли в другой чат:
// isWaiting=true, в группе SignalR нас больше нет — ровно та ситуация, где конец
// хода пролетает мимо клиента.
async function leaveMidTurn(sid: string) {
  m.getHistory.mockResolvedValue([...DONE_TURN]);
  const chat = openChat(sid);
  await flush();
  await chat.first.send('второй');
  expect(chat.render().isWaiting).toBe(true);
  chat.unmount();
  expect(chat.render().isJoined).toBe(false);
}

beforeEach(() => {
  m.getHistory.mockReset();
  m.getHistory.mockResolvedValue([]);
  m.joinSession.mockReset();
  m.joinSession.mockResolvedValue(undefined);
  m.leaveSession.mockClear();
  m.sendMessage.mockClear();
  m.sendMessage.mockResolvedValue('started');
});

describe('useSession: возврат в чат после пропущенного конца хода', () => {
  it('result, пришедший пока мы были вне группы, снимает isWaiting и подтягивает ленту', async () => {
    const sid = nextSid();
    await leaveMidTurn(sid);

    // Ход завершился, пока чат был закрыт
    m.getHistory.mockResolvedValue([...DONE_TURN, user('второй'), text('ответ 2'), result()]);

    const back = openChat(sid);
    await flush();

    const st = back.render();
    expect(st.isWaiting).toBe(false);
    expect(st.items).toHaveLength(6);
    expect(st.items.at(-1)!.kind).toBe('result');
  });

  it('хвост истории не result/error (ход идёт) — isWaiting не сбрасывается', async () => {
    const sid = nextSid();
    await leaveMidTurn(sid);

    // Ход всё ещё идёт: сервер дописал реплику, но конца хода нет
    m.getHistory.mockResolvedValue([...DONE_TURN, user('второй'), text('печатаю…')]);

    const back = openChat(sid);
    await flush();

    const st = back.render();
    expect(st.isWaiting).toBe(true);
    expect(st.items).toHaveLength(5); // лента всё равно обновилась
  });

  it('error в хвосте — тоже конец хода, ожидание снимается', async () => {
    const sid = nextSid();
    await leaveMidTurn(sid);

    m.getHistory.mockResolvedValue([...DONE_TURN, user('второй'), errorItem()]);

    const back = openChat(sid);
    await flush();

    expect(back.render().isWaiting).toBe(false);
  });

  it('fal_cost, дописанный после result, не маскирует конец хода', async () => {
    const sid = nextSid();
    await leaveMidTurn(sid);

    m.getHistory.mockResolvedValue([...DONE_TURN, user('второй'), text('ответ 2'), result(), falCost()]);

    const back = openChat(sid);
    await flush();

    const st = back.render();
    expect(st.isWaiting).toBe(false);
    expect(st.items.at(-1)!.kind).toBe('fal_cost');
  });
});

describe('useSession: снапшот после входа в группу', () => {
  it('событие, попавшее в дыру «снимок снят — в группу ещё не вошли», подбирается вторым снапшотом', async () => {
    const sid = nextSid();
    await leaveMidTurn(sid);

    // Первый снапшот — ход ещё идёт (result не записан)
    const midTurn = [...DONE_TURN, user('второй'), text('печатаю…')];
    m.getHistory.mockResolvedValue(midTurn);

    // Вход в группу подвешен: пока он идёт, события сессии до клиента не доходят
    const join = deferred<void>();
    m.joinSession.mockReturnValueOnce(join.promise);

    const back = openChat(sid);
    await flush();

    expect(back.render().isWaiting).toBe(true); // дыра: конец хода прошёл мимо
    // 1-2 — открытие чата до ухода (снимок + снапшот после join), 3 — снимок при возврате
    expect(m.getHistory).toHaveBeenCalledTimes(3);

    // Ход завершился ровно в окне между снимком и входом в группу
    m.getHistory.mockResolvedValue([...DONE_TURN, user('второй'), text('ответ 2'), result()]);
    join.resolve();
    await flush();

    const st = back.render();
    expect(m.getHistory).toHaveBeenCalledTimes(4); // 4 — снапшот ПОСЛЕ входа в группу
    expect(st.isJoined).toBe(true);
    expect(st.isWaiting).toBe(false);
    expect(st.items.at(-1)!.kind).toBe('result');
  });

  it('снапшот после join не откатывает ленту назад, если сервер отстал от живой', async () => {
    const sid = nextSid();
    await leaveMidTurn(sid);

    const full = [...DONE_TURN, user('второй'), text('ответ 2'), result()];
    m.getHistory.mockResolvedValueOnce(full);      // снимок при возврате — полный
    m.getHistory.mockResolvedValue([...DONE_TURN]); // а снапшот после join отстал

    const back = openChat(sid);
    await flush();

    const st = back.render();
    expect(st.items).toHaveLength(6);
    expect(st.items.at(-1)!.kind).toBe('result');
  });
});

describe('useSession: идемпотентность повторного снапшота', () => {
  it('та же история второй раз не меняет ленту и не запускает цикл перезагрузок', async () => {
    const sid = nextSid();
    const history = [...DONE_TURN];
    m.getHistory.mockResolvedValue(history);

    const chat = openChat(sid);
    await flush();

    const itemsAfterJoin: ChatItem[] = chat.render().items;
    expect(itemsAfterJoin).toHaveLength(3);
    // Снимок при открытии + снимок после входа в группу — ровно два обращения
    expect(m.getHistory).toHaveBeenCalledTimes(2);

    // Возврат в уже присоединённый чат: третий снимок той же истории
    const again = openChat(sid);
    await flush();

    const st = again.render();
    expect(m.getHistory).toHaveBeenCalledTimes(3); // перезагрузки не размножаются
    expect(st.items).toBe(itemsAfterJoin);          // состояние не пересоздано
  });
});
