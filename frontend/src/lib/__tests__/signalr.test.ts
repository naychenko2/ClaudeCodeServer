import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// --- Мок @microsoft/signalr: одна фейковая connection с ручным управлением состоянием ---

const h = vi.hoisted(() => {
  const HubConnectionState = {
    Disconnected: 'Disconnected',
    Connecting: 'Connecting',
    Connected: 'Connected',
    Disconnecting: 'Disconnecting',
    Reconnecting: 'Reconnecting',
  } as const;

  const fake = {
    state: HubConnectionState.Connected as string,
    start: vi.fn(async () => { fake.state = HubConnectionState.Connected; }),
    invoke: vi.fn(async () => {}),
    on: vi.fn(),
    off: vi.fn(),
    // Колбэки, зарегистрированные signalr.ts — дёргаем их из тестов как «события» соединения
    reconnectingCbs: [] as Array<() => void>,
    reconnectedCbs: [] as Array<() => void>,
    closeCbs: [] as Array<() => void>,
    onreconnecting(cb: () => void) { fake.reconnectingCbs.push(cb); },
    onreconnected(cb: () => void) { fake.reconnectedCbs.push(cb); },
    onclose(cb: () => void) { fake.closeCbs.push(cb); },
  };

  return {
    HubConnectionState,
    fake,
    // Управляемое тестом бинарное состояние связи (имитация поведения offline.ts)
    isOnline: true,
    setConnectionState: vi.fn((v: string) => { h.isOnline = v === 'online'; }),
  };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: h.HubConnectionState,
  HubConnectionBuilder: class {
    withUrl() { return this; }
    withServerTimeout() { return this; }
    withKeepAliveInterval() { return this; }
    withAutomaticReconnect() { return this; }
    build() { return h.fake; }
  },
}));

vi.mock('../offline', () => ({
  setConnectionState: h.setConnectionState,
}));

let signalr: typeof import('../signalr');

// Эмуляция событий соединения
const fireReconnecting = () => h.fake.reconnectingCbs.forEach(cb => cb());
const fireReconnected = () => h.fake.reconnectedCbs.forEach(cb => cb());
const fireClose = () => h.fake.closeCbs.forEach(cb => cb());

beforeEach(async () => {
  // Синглтон connection — модульный, пересоздаём модуль на каждый тест
  vi.resetModules();
  vi.useFakeTimers();
  h.fake.state = h.HubConnectionState.Connected;
  h.fake.reconnectingCbs.length = 0;
  h.fake.reconnectedCbs.length = 0;
  h.fake.closeCbs.length = 0;
  h.fake.start.mockClear();
  h.fake.invoke.mockClear();
  h.isOnline = true;
  h.setConnectionState.mockClear();

  signalr = await import('../signalr');
  // Создаёт connection и регистрирует onreconnecting/onreconnected/onclose
  signalr.getConnection();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('события соединения → глобальный online/offline', () => {
  it('onclose → сразу offline', () => {
    fireClose();
    expect(h.setConnectionState).toHaveBeenCalledWith('offline');
  });

  it('onreconnected → сразу online', () => {
    fireClose();
    h.setConnectionState.mockClear();

    fireReconnected();
    expect(h.setConnectionState).toHaveBeenCalledWith('online');
  });

  it('onreconnecting → глобальный статус не трогается (UI не мигает)', () => {
    fireReconnecting();
    expect(h.setConnectionState).not.toHaveBeenCalled();
  });
});

describe('ensureConnected через joinSession', () => {
  it('при подключённом соединении сразу вызывает invoke JoinSession', async () => {
    await signalr.joinSession('s1');
    expect(h.fake.invoke).toHaveBeenCalledWith('JoinSession', 's1');
    expect(h.fake.start).not.toHaveBeenCalled();
  });

  it('из Disconnected сначала стартует соединение', async () => {
    h.fake.state = h.HubConnectionState.Disconnected;
    await signalr.joinSession('s1');
    expect(h.fake.start).toHaveBeenCalledTimes(1);
    expect(h.fake.invoke).toHaveBeenCalledWith('JoinSession', 's1');
  });

  it('успешный start() из Disconnected поднимает флаг связи в online', async () => {
    // onreconnected при старте из Disconnected не срабатывает (это не reconnect),
    // и без явного setConnectionState('online') флаг остаётся offline при
    // заведомо живом хабе. Это и был дефект «Повторить врёт» на экране логина.
    h.fake.state = h.HubConnectionState.Disconnected;
    h.setConnectionState.mockClear();

    await signalr.joinSession('s1');

    expect(h.setConnectionState).toHaveBeenCalledWith('online');
  });

  it('вечный Reconnecting → таймаут через 8с, invoke не вызывается, флаг связи → offline', async () => {
    h.fake.state = h.HubConnectionState.Reconnecting;
    const p = signalr.joinSession('s1');
    const guarded = p.catch((e: Error) => e); // не даём unhandled rejection

    await vi.advanceTimersByTimeAsync(8000);

    expect(await guarded).toEqual(new Error('SignalR connect timeout'));
    expect(h.fake.invoke).not.toHaveBeenCalled();
    // Хвост ревью: при неподнявшемся хабе UI должен видеть честный offline,
    // иначе зонд возврата (живёт только в offline) не запустится.
    expect(h.setConnectionState).toHaveBeenCalledWith('offline');
  });

  it('Reconnecting → Connected в пределах таймаута — дожидаемся и вызываем invoke', async () => {
    h.fake.state = h.HubConnectionState.Reconnecting;
    const p = signalr.joinSession('s1');

    await vi.advanceTimersByTimeAsync(200);
    h.fake.state = h.HubConnectionState.Connected;
    await vi.advanceTimersByTimeAsync(100);

    await p;
    expect(h.fake.invoke).toHaveBeenCalledWith('JoinSession', 's1');
  });

  it('Reconnecting → Disconnected — ошибка без ожидания таймаута, флаг связи → offline', async () => {
    h.fake.state = h.HubConnectionState.Reconnecting;
    const p = signalr.joinSession('s1');
    const guarded = p.catch((e: Error) => e);

    await vi.advanceTimersByTimeAsync(100);
    h.fake.state = h.HubConnectionState.Disconnected;
    await vi.advanceTimersByTimeAsync(100);

    expect(await guarded).toEqual(new Error('SignalR disconnected while waiting'));
    expect(h.setConnectionState).toHaveBeenCalledWith('offline');
  });
});

describe('вызовы хаба', () => {
  it('leaveSession шлёт invoke только при подключённом соединении', async () => {
    await signalr.leaveSession('s1');
    expect(h.fake.invoke).toHaveBeenCalledWith('LeaveSession', 's1');

    h.fake.invoke.mockClear();
    h.fake.state = h.HubConnectionState.Disconnected;
    await signalr.leaveSession('s1');
    expect(h.fake.invoke).not.toHaveBeenCalled();
  });

  it('sendMessage передаёт mode ?? null и флаг auto', async () => {
    await signalr.sendMessage('s1', 'привет', ['a.ts']);
    expect(h.fake.invoke).toHaveBeenCalledWith('SendMessage', 's1', 'привет', ['a.ts'], null, false);

    await signalr.sendMessage('s1', 'привет', [], 'plan');
    expect(h.fake.invoke).toHaveBeenCalledWith('SendMessage', 's1', 'привет', [], 'plan', false);

    await signalr.sendMessage('s1', 'итог', [], undefined, true);
    expect(h.fake.invoke).toHaveBeenCalledWith('SendMessage', 's1', 'итог', [], null, true);
  });

  it('respondPermission передаёт behavior', async () => {
    await signalr.respondPermission('s1', 'r1', 'allow_always');
    expect(h.fake.invoke).toHaveBeenCalledWith('RespondPermission', 's1', 'r1', 'allow_always');
  });
});

describe('onReconnected-подписки', () => {
  it('колбэки вызываются при реконнекте, отписка работает', () => {
    const a = vi.fn();
    const b = vi.fn();
    const unsubA = signalr.onReconnected(a);
    signalr.onReconnected(b);

    fireReconnected();
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(1);

    unsubA();
    fireReconnected();
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).toHaveBeenCalledTimes(2);
  });

  it('упавший колбэк не блокирует остальных', () => {
    const bad = vi.fn(() => { throw new Error('boom'); });
    const good = vi.fn();
    signalr.onReconnected(bad);
    signalr.onReconnected(good);

    expect(() => fireReconnected()).not.toThrow();
    expect(good).toHaveBeenCalledTimes(1);
  });
});
