/* eslint-disable react-hooks/rules-of-hooks --
   Хук здесь гоняется вне React: модуль react замокан мини-раннером ниже, порядок
   вызова хуков и жизненный цикл эффектов держит openChat. */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
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
  // Докачка по курсору (B5). По умолчанию сервер «старый»: отвечает плоским массивом из
  // getHistory — прежние тесты идут как раньше; тесты курсора переопределяют ответ конвертом
  getHistoryTail: vi.fn<(sid: string, q: { after: number; fp?: string }) => Promise<unknown>>(async () => []),
  joinSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  leaveSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  sendMessage: vi.fn<() => Promise<'started' | 'queued' | 'queued-preempted'>>(async () => 'started'),
  preemptForPending: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  // Обработчики сообщений хаба — через них тест играет роль сервера
  messageHandlers: [] as Array<(msg: unknown) => void>,
  // Обработчики восстановления соединения — тест играет реконнект хаба
  reconnectHandlers: [] as Array<() => Promise<void> | void>,
}));

// Стаб document: окружение node, а реконсиляция по фокусу вешается на visibilitychange.
// Ставим ДО импорта useSession — обработчик регистрируется один раз при первом вызове хука.
const visibilityHandlers: Array<() => void> = [];
(globalThis as unknown as { document: unknown }).document = {
  visibilityState: 'visible',
  addEventListener: (type: string, h: () => void) => {
    if (type === 'visibilitychange') visibilityHandlers.push(h);
  },
};

vi.mock('../../lib/api', () => ({
  api: {
    chats: {
      getHistory: (sid: string) => m.getHistory(sid),
      getHistoryTail: (sid: string, q: { after: number; fp?: string }) => m.getHistoryTail(sid, q),
    },
    sessions: {
      getHistory: (_projectId: string, sid: string) => m.getHistory(sid),
      getHistoryTail: (_projectId: string, sid: string, q: { after: number; fp?: string }) => m.getHistoryTail(sid, q),
      cancelPending: vi.fn(async () => { }),
      preemptForPending: (sid: string) => m.preemptForPending(sid),
    },
  },
}));

vi.mock('../../lib/signalr', () => ({
  joinSession: (sid: string) => m.joinSession(sid),
  joinProject: vi.fn(async () => { }),
  leaveSession: (sid: string) => m.leaveSession(sid),
  onMessage: (h: (msg: unknown) => void) => { m.messageHandlers.push(h); return () => { }; },
  onReconnected: (h: () => Promise<void> | void) => { m.reconnectHandlers.push(h); return () => { }; },
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

const { useSession, waitDiag } = await import('../useSession');
const { toolLiveness } = await import('../../lib/toolTiming');

// Сервер шлёт статус в сессию (в т.ч. replay в ответ на JoinSession)
const emitStatus = (sid: string, status: string) =>
  m.messageHandlers.forEach(h => h({ type: 'status_changed', sessionId: sid, status }));

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

// Переждать окно ожидания входа в группу (JOIN_WAIT_MS в useSession) — для случаев,
// когда join намеренно подвешен и история должна сниматься не дожидаясь его
const waitPastJoinWindow = async () => { await new Promise(r => setTimeout(r, 500)); await flush(); }

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
  m.getHistoryTail.mockReset();
  m.getHistoryTail.mockImplementation((sid: string) => m.getHistory(sid));
  m.joinSession.mockReset();
  m.joinSession.mockResolvedValue(undefined);
  m.leaveSession.mockClear();
  m.sendMessage.mockClear();
  m.sendMessage.mockResolvedValue('started');
});

describe('useSession: реконсиляция isWaiting с серверным статусом', () => {
  // Сколько раз переспрашивали статус ИМЕННО этой сессии (чужие чаты из соседних тестов
  // могут висеть в модульном сторе — считаем адресно)
  const joinsFor = (sid: string) => m.joinSession.mock.calls.filter(c => c[0] === sid).length;

  let warn: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    warn = vi.spyOn(console, 'warn').mockImplementation(() => { });
  });
  afterEach(() => {
    vi.useRealTimers();
    warn.mockRestore();
  });

  // Прокрутка цепочки промисов на фейковых таймерах
  const tick = async () => { for (let i = 0; i < 6; i++) await vi.advanceTimersByTimeAsync(1); };

  it('залипший isWaiting снимается по replay-статусу, который принёс poll', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await tick();
    await chat.first.send('привет');
    expect(chat.render().isWaiting).toBe(true);

    // Ход давно кончился, но result до клиента не доехал
    const before = joinsFor(sid);
    const mismatchesBefore = waitDiag.mismatches;
    await vi.advanceTimersByTimeAsync(25_000);
    expect(joinsFor(sid)).toBe(before + 1); // poll переспросил статус

    emitStatus(sid, 'active');              // replay от сервера
    expect(chat.render().isWaiting).toBe(false);
    expect(waitDiag.mismatches).toBe(mismatchesBefore + 1);
    expect(waitDiag.last).toMatchObject({ sid, client: true, server: 'active' });

    chat.unmount();
  });

  it('poll гаснет после снятия флага и не переспрашивает дальше', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await tick();
    await chat.first.send('привет');

    await vi.advanceTimersByTimeAsync(25_000);
    expect(vi.getTimerCount()).toBe(1); // ход идёт — таймер один, рендеры его не плодят
    chat.render(); chat.render();
    expect(vi.getTimerCount()).toBe(1);

    emitStatus(sid, 'active');
    expect(chat.render().isWaiting).toBe(false);
    expect(vi.getTimerCount()).toBe(0); // флаг снят — таймер погашен сразу

    const after = joinsFor(sid);
    await vi.advanceTimersByTimeAsync(120_000);
    expect(joinsFor(sid)).toBe(after); // занятых чатов не осталось — таймер погас

    chat.unmount();
  });

  it('закрытый чат не поллится, даже если у него висит isWaiting', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await tick();
    await chat.first.send('привет');
    chat.unmount(); // ушли в другой чат, ход по данным клиента ещё идёт

    const before = joinsFor(sid);
    await vi.advanceTimersByTimeAsync(120_000);
    expect(joinsFor(sid)).toBe(before);
  });

  it('возврат фокуса на вкладку ставит isWaiting, если ход на самом деле идёт', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await tick();
    expect(chat.render().isWaiting).toBe(false); // событие working прошло мимо клиента

    const before = joinsFor(sid);
    const mismatchesBefore = waitDiag.mismatches;
    visibilityHandlers.forEach(h => h());
    expect(joinsFor(sid)).toBe(before + 1);

    emitStatus(sid, 'working');
    expect(chat.render().isWaiting).toBe(true);
    expect(waitDiag.mismatches).toBe(mismatchesBefore + 1);
    expect(waitDiag.last).toMatchObject({ sid, client: false, server: 'working' });

    // Совпадающий статус расхождением не считается
    visibilityHandlers.forEach(h => h());
    emitStatus(sid, 'working');
    expect(waitDiag.mismatches).toBe(mismatchesBefore + 1);

    chat.unmount();
  });
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
    // Ждём дольше JOIN_WAIT_MS: пока хаб не ответил, снимок берётся не дожидаясь его —
    // ровно так и возникает дыра, ради которой нужен повторный снимок после входа
    await waitPastJoinWindow();

    expect(back.render().isWaiting).toBe(true); // дыра: конец хода прошёл мимо
    // 1 — открытие чата до ухода (join успел, повторный снимок не нужен), 2 — снимок при возврате
    expect(m.getHistory).toHaveBeenCalledTimes(2);

    // Ход завершился ровно в окне между снимком и входом в группу
    m.getHistory.mockResolvedValue([...DONE_TURN, user('второй'), text('ответ 2'), result()]);
    join.resolve();
    await flush();

    const st = back.render();
    expect(m.getHistory).toHaveBeenCalledTimes(3); // 3 — снимок ПОСЛЕ входа в группу
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

describe('useSession: второй наблюдатель уже присоединённого занятого чата', () => {
  it('открытие чата с isWaiting=true всё равно подтягивает историю (снята !isWaiting)', async () => {
    const sid = nextSid();
    m.getHistory.mockResolvedValue([...DONE_TURN]);
    const chat = openChat(sid);
    await flush();
    await chat.first.send('второй');
    expect(chat.render().isWaiting).toBe(true);
    expect(chat.render().isJoined).toBe(true);

    // Первый компонент (например ChatPanel) остаётся смонтированным — сессия не покидает
    // группу. Пока чат считался занятым, сервер дописал конец хода (карточка отчёта и т.п.).
    m.getHistory.mockResolvedValue([...DONE_TURN, user('второй'), text('ответ 2'), result()]);
    const before = m.getHistory.mock.calls.length;

    // Второй наблюдатель того же sessionId (ArtifactsPanel) монтируется, пока сессия уже joined.
    const second = openChat(sid);
    await flush();

    expect(m.getHistory.mock.calls.length).toBeGreaterThan(before);
    const st = second.render();
    expect(st.isWaiting).toBe(false);
    expect(st.items.at(-1)!.kind).toBe('result');

    chat.unmount();
    second.unmount();
  });
});

describe('useSession: членство в группе', () => {
  const joinsFor = (sid: string) => m.joinSession.mock.calls.filter(c => c[0] === sid).length;

  it('чат с упавшим первым join перезаходит в группу после реконнекта', async () => {
    const sid = nextSid();
    m.joinSession.mockRejectedValueOnce(new Error('офлайн'));

    const chat = openChat(sid);
    await flush();
    expect(chat.render().isJoined).toBe(false); // в группу не попали, повторов нет

    // Соединение восстановилось: сессию смотрят — перезаходим, хотя isJoined=false
    const before = joinsFor(sid);
    await m.reconnectHandlers[0]!();
    await flush();

    expect(joinsFor(sid)).toBe(before + 1);
    expect(chat.render().isJoined).toBe(true);

    chat.unmount();
  });

  it('реплей active в окне send не сбрасывает ожидание, после окна — сбрасывает', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await flush();

    // Хаб реплеит статус на каждый JoinSession — тут «active», ход ещё не стартовал
    m.joinSession.mockImplementationOnce(async () => { emitStatus(sid, 'active'); });
    await chat.first.send('привет');
    expect(chat.render().isWaiting).toBe(true);

    // Окно отправки закрыто — статус снова управляет флагом
    emitStatus(sid, 'active');
    expect(chat.render().isWaiting).toBe(false);

    chat.unmount();
  });
});

describe('useSession: прерывание хода ради очереди', () => {
  // Сервер шлёт конец прогона (процесс убит — result по такому ходу не приходит)
  const emitExited = (sid: string) =>
    m.messageHandlers.forEach(h => h({ type: 'exited', sessionId: sid }));

  it('исход queued-preempted отмечает ход прерванным, а не аварийным', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await flush();

    m.sendMessage.mockResolvedValueOnce('queued-preempted');
    await chat.first.send('не спрашивай, делай');

    // Отметка стоит ДО прихода exited — редьюсер не сочтёт смерть прогона аварией
    expect(chat.render().items.at(-1)).toEqual({ kind: 'interrupted', ts: expect.any(Number) });
    emitExited(sid);
    expect(chat.render().items.some(i => i.kind === 'session_ended')).toBe(false);

    chat.unmount();
  });

  it('exited, обогнавший ответ хаба, тоже не рисует ложную аварию', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await flush();

    // Гонка: сервер убил ход и разослал exited раньше, чем вернул исход отправки
    m.sendMessage.mockImplementationOnce(async () => {
      emitExited(sid);
      return 'queued-preempted';
    });
    await chat.first.send('не спрашивай, делай');

    expect(chat.render().items.some(i => i.kind === 'session_ended')).toBe(false);
    expect(chat.render().items.filter(i => i.kind === 'interrupted')).toHaveLength(1);

    chat.unmount();
  });

  it('дельты, долетевшие после отметки, не возвращают ложную аварию', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await flush();

    m.sendMessage.mockResolvedValueOnce('queued-preempted');
    await chat.first.send('не спрашивай, делай');

    // Текст, бывший в полёте на момент kill, долетает уже после отметки и снова
    // становится хвостом ленты — проверка «последний элемент interrupted» тут врала бы
    m.messageHandlers.forEach(h => h({ type: 'text_delta', sessionId: sid, text: 'хвост убитого хода' }));
    expect(chat.render().items.at(-1)?.kind).toBe('text');

    emitExited(sid);
    expect(chat.render().items.some(i => i.kind === 'session_ended')).toBe(false);

    chat.unmount();
  });

  it('отметка переживает замену ленты историей с сервера', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await flush();

    m.sendMessage.mockResolvedValueOnce('queued-preempted');
    await chat.first.send('не спрашивай, делай');
    expect(chat.render().items.some(i => i.kind === 'interrupted')).toBe(true);

    // Блип связи внутри окна преемпта: история с сервера новее и заменяет ленту целиком,
    // а маркер «Прервано» live-only — в history его нет, из ленты он исчезает
    m.getHistory.mockResolvedValue([user('не спрашивай, делай'), text('хвост убитого хода')]);
    for (const h of m.reconnectHandlers) await h();
    await flush();
    expect(chat.render().items.some(i => i.kind === 'interrupted')).toBe(false);

    // Факт прерывания держится вне ленты — exited убитого хода не читается как авария
    emitExited(sid);
    expect(chat.render().items.some(i => i.kind === 'session_ended')).toBe(false);

    chat.unmount();
  });

  it('аварийный exited вне окна отправки по-прежнему виден как авария', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await flush();

    await chat.first.send('обычное'); // 'started' по умолчанию — ход идёт
    emitExited(sid);

    expect(chat.render().items.some(i => i.kind === 'session_ended')).toBe(true);

    chat.unmount();
  });

  it('отказ перебоя снимает оптимистичную отметку', async () => {
    const sid = nextSid();
    const chat = openChat(sid);
    await flush();

    // 409: ход уже кончился сам либо чат залип без живого прогона — прерывания не было
    m.preemptForPending.mockRejectedValueOnce(new Error('409'));
    await chat.first.preemptForPending();

    expect(chat.render().items.some(i => i.kind === 'interrupted')).toBe(false);

    chat.unmount();
  });
});

describe('useSession: «Стоп» на глазах', () => {
  it('живая отметка «прервано» несёт время — у оборванной карточки есть длительность', async () => {
    const sid = nextSid();
    // Ход идёт: Bash стартовал и результата не прислал
    m.getHistory.mockResolvedValue([user('в'), { kind: 'tool_use', id: 't1', name: 'Bash', input: {}, result: null }]);
    const chat = openChat(sid);
    await flush();

    const before = Date.now();
    chat.render().interrupt();
    const items = chat.render().items;

    // Без ts пометки toolLiveness не знает момента обрыва, и «прервано» до F5 шло без времени
    expect(items.at(-1)).toEqual({ kind: 'interrupted', ts: expect.any(Number) });
    const l = toolLiveness(items as Parameters<typeof toolLiveness>[0], false);
    expect(l.dead.has('t1')).toBe(true);
    expect(l.abortedAt?.get('t1')).toBeGreaterThanOrEqual(before);

    chat.unmount();
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
    // Один снимок: в группу вошли до его снятия, значит повторный не нужен
    expect(m.getHistory).toHaveBeenCalledTimes(1);

    // Возврат в уже присоединённый чат: второй снимок той же истории
    const again = openChat(sid);
    await flush();

    const st = again.render();
    expect(m.getHistory).toHaveBeenCalledTimes(2); // перезагрузки не размножаются
    expect(st.items).toBe(itemsAfterJoin);          // состояние не пересоздано
  });
});

describe('useSession: дедуп запросов истории (перф-2 F4)', () => {
  const historyCallsFor = () => m.getHistory.mock.calls.length;

  it('два наблюдателя одного чата при монтировании → один запрос истории', async () => {
    const sid = nextSid();
    const gate = deferred<unknown[]>();
    m.getHistory.mockReturnValue(gate.promise);

    const a = openChat(sid);
    await flush(); // join успел, история ещё летит
    const b = openChat(sid); // второй зритель (ArtifactsPanel) идёт в reloadHistory
    await flush();

    expect(historyCallsFor()).toBe(1);
    gate.resolve([...DONE_TURN]);
    await flush();
    expect(historyCallsFor()).toBe(1);
    expect(a.render().items).toHaveLength(3);

    a.unmount(); b.unmount();
  });

  it('повторное открытие чата → ровно один запрос (не ноль и не два)', async () => {
    const sid = nextSid();
    m.getHistory.mockResolvedValue([...DONE_TURN]);

    const first = openChat(sid);
    await flush();
    expect(historyCallsFor()).toBe(1);
    first.unmount();

    const again = openChat(sid);
    await flush();
    expect(historyCallsFor()).toBe(2); // первое открытие + одно повторное
    expect(again.render().items).toHaveLength(3);
    again.unmount();
  });

  it('replay active у смотримого чата подтягивает историю (лечит потерянные события)', async () => {
    const sid = nextSid();
    m.getHistory.mockResolvedValue([...DONE_TURN]);
    const chat = openChat(sid);
    await flush();
    expect(historyCallsFor()).toBe(1);

    emitStatus(sid, 'active');
    await flush();
    expect(historyCallsFor()).toBe(2);

    chat.unmount();
    emitStatus(sid, 'active'); // зрителей нет — историю не качаем
    await flush();
    expect(historyCallsFor()).toBe(2);
  });

  it('fresh-вызов во время летящего запроса → ровно два запроса, без цепочки', async () => {
    const sid = nextSid();
    // Вход в группу подвешен дольше окна ожидания: после снятия истории придёт fresh-снимок
    const join = deferred<void>();
    m.joinSession.mockReturnValueOnce(join.promise);
    m.getHistory.mockResolvedValue([...DONE_TURN]);

    const chat = openChat(sid);
    await waitPastJoinWindow();
    expect(historyCallsFor()).toBe(1); // снимок при открытии

    // Идёт обычная перезагрузка (replay active у смотримого чата) — запрос завис
    const gate = deferred<unknown[]>();
    m.getHistory.mockReturnValueOnce(gate.promise);
    emitStatus(sid, 'active');
    await flush();
    expect(historyCallsFor()).toBe(2);

    // Вход в группу состоялся: fresh-снимок не должен схлопнуться с уже летящим запросом
    join.resolve();
    await flush();
    expect(historyCallsFor()).toBe(2); // ждёт летящий, своего запроса ещё нет

    gate.resolve([...DONE_TURN]);
    await flush();
    expect(historyCallsFor()).toBe(3); // летящий + ровно один fresh после него
    await flush();
    expect(historyCallsFor()).toBe(3); // бесконечной цепочки нет

    chat.unmount();
  });

  it('конец хода (isWaiting=true) во время летящего снимка → fresh-снимок после него', async () => {
    const sid = nextSid();
    // Снимок при открытии подвешен: запрос истории летит, ход ещё не кончился
    const gate = deferred<unknown[]>();
    m.getHistory.mockReturnValue(gate.promise);

    const chat = openChat(sid);
    await flush();
    expect(historyCallsFor()).toBe(1); // снимок при открытии — в полёте

    // Ход ещё идёт: isWaiting=true, когда придёт его конец (replay active)
    await chat.first.send('в ход');
    expect(chat.render().isWaiting).toBe(true);

    // Конец хода: prev.isWaiting=true → fresh. Ждём летящий снимок, потом берём новый
    emitStatus(sid, 'active');
    await flush();
    expect(historyCallsFor()).toBe(1); // fresh ещё не стрелял — ждёт летящий

    gate.resolve([...DONE_TURN]);
    await flush();
    expect(historyCallsFor()).toBe(2); // летящий + ровно один fresh-снимок после него
    await flush();
    expect(historyCallsFor()).toBe(2); // бесконечной цепочки нет

    chat.unmount();
  });

  it('replay active у простаивающего чата (isWaiting=false) схлопывается с летящей загрузкой', async () => {
    const sid = nextSid();
    // Снимок при открытии подвешен: запрос истории летит, чат простаивает (isWaiting=false)
    const gate = deferred<unknown[]>();
    m.getHistory.mockReturnValue(gate.promise);

    const chat = openChat(sid);
    await flush();
    expect(historyCallsFor()).toBe(1); // снимок при открытии — в полёте

    // Чат простаивает: сообщения не отправляли, isWaiting=false
    expect(chat.render().isWaiting).toBe(false);

    // Replay active (открытие простаивающего чата): prev.isWaiting=false → не fresh,
    // схлопывается с уже летящей загрузкой открытия
    emitStatus(sid, 'active');
    await flush();
    expect(historyCallsFor()).toBe(1); // дополнительного fetch нет

    gate.resolve([...DONE_TURN]);
    await flush();
    expect(historyCallsFor()).toBe(1); // всё ещё один — схлопнулось
    await flush();
    expect(historyCallsFor()).toBe(1);

    chat.unmount();
  });
});

describe('useSession: докачка истории по курсору (перф-2 B5)', () => {
  type Rec = { kind: string; [k: string]: unknown };
  type Q = { after: number; fp?: string };

  // Фейковый сервер: тот же алгоритм, что ChatHistoryTail.Compute на бэке. Якорь — последняя
  // завершающая запись хода; отпечаток — слепок префикса до неё (здесь сам JSON префикса)
  function serverTail(history: Rec[], q: Q) {
    let anchor = -1;
    history.forEach((r, i) => { if (r.kind === 'result' || r.kind === 'error' || r.kind === 'interrupted') anchor = i; });
    const fpOf = (i: number) => (i < 0 ? null : JSON.stringify(history.slice(0, i + 1)));
    const anchorFp = fpOf(anchor);
    if (q.after < 0 || q.after >= history.length || fpOf(q.after) !== q.fp)
      return { reset: true, messages: history, anchorIndex: anchor, anchorFp };
    return { reset: false, messages: history.slice(q.after + 1), anchorIndex: anchor, anchorFp };
  }

  const toolRunning = (): Rec => ({ kind: 'tool_use', id: 't1', name: 'Bash', input: {}, result: null });
  const toolDone = (): Rec => ({ kind: 'tool_use', id: 't1', name: 'Bash', input: {}, result: 'ok', startedAt: 1, finishedAt: 2 });

  // Ходы чата: завершённый первый с инструментом, дальше второй — идущий и законченный
  const turn1 = (): Rec[] => [
    { kind: 'user_message', text: 'первый' }, toolDone(), { kind: 'text', text: 'ответ 1' },
    { kind: 'result', subtype: 'success', durationMs: 10, numTurns: 1 },
  ];
  const turn2Running = (): Rec[] => [{ kind: 'user_message', text: 'второй' }, { ...toolRunning(), id: 't2' }, { kind: 'text', text: 'печат' }];
  const turn2Done = (): Rec[] => [
    { kind: 'user_message', text: 'второй' }, { ...toolDone(), id: 't2' }, { kind: 'text', text: 'печатаю ответ' },
    { kind: 'result', subtype: 'success', durationMs: 20, numTurns: 2 },
  ];

  const queries = () => m.getHistoryTail.mock.calls.map(c => c[1]);

  async function reopen(prev: { unmount: () => void } | null, sid: string) {
    prev?.unmount();
    const chat = openChat(sid);
    await flush();
    return chat;
  }

  it('склеенная из хвостов лента равна полной загрузке на каждом шаге (инструменты и стрим)', async () => {
    const { normalizeHistory } = await import('../../lib/chatReducer');
    const sid = nextSid();
    let server: Rec[] = turn1();
    m.getHistoryTail.mockImplementation(async (_s, q) => serverTail(server, q));

    // 1. первая загрузка — after=-1, сервер отдаёт всё и якорь
    let chat = await reopen(null, sid);
    expect(queries().at(-1)).toEqual({ after: -1 });
    expect(chat.render().items).toEqual(normalizeHistory(server));

    // 2. пришёл идущий ход: инструмент без результата и недописанный текст (хвост после якоря)
    server = [...turn1(), ...turn2Running()];
    chat = await reopen(chat, sid);
    expect(queries().at(-1)).toMatchObject({ after: 3 }); // курсор на result первого хода
    expect(chat.render().items).toEqual(normalizeHistory(server));

    // 3. ход завершился: tool_use переписан результатом, текст дописан — хвост после того же якоря
    server = [...turn1(), ...turn2Done()];
    chat = await reopen(chat, sid);
    expect(queries().at(-1)).toMatchObject({ after: 3 });
    expect(chat.render().items).toEqual(normalizeHistory(server));

    // 4. без изменений — курсор на конце, хвост пуст, лента та же
    const before = chat.render().items;
    chat = await reopen(chat, sid);
    expect(queries().at(-1)).toMatchObject({ after: 7 });
    expect(chat.render().items).toBe(before);
    chat.unmount();
  });

  it('несходящийся отпечаток → reset: лента заменяется полной историей сервера', async () => {
    const { normalizeHistory } = await import('../../lib/chatReducer');
    const sid = nextSid();
    let server: Rec[] = [...turn1(), ...turn2Done()];
    m.getHistoryTail.mockImplementation(async (_s, q) => serverTail(server, q));

    let chat = await reopen(null, sid);
    expect(chat.render().items).toEqual(normalizeHistory(server));

    // История переписана ДО курсора (правка, откат) и стала длиннее: префикс не совпадёт
    server = [{ kind: 'user_message', text: 'ПЕРЕПИСАНО' }, ...turn1().slice(1), ...turn2Done(), { kind: 'user_message', text: 'третий' }];
    chat = await reopen(chat, sid);
    expect(chat.render().items).toEqual(normalizeHistory(server));
    chat.unmount();
  });

  it('старый сервер (плоский массив) → полная загрузка, курсор не заводится', async () => {
    const sid = nextSid();
    m.getHistory.mockResolvedValue([...DONE_TURN]);

    let chat = await reopen(null, sid);
    chat = await reopen(chat, sid);

    expect(queries().every(q => q.after === -1)).toBe(true);
    expect(chat.render().items).toHaveLength(3);
    chat.unmount();
  });
});
