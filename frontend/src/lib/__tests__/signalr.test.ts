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
    notifyOnline: vi.fn(),
    notifyOffline: vi.fn(),
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
  notifyOnline: h.notifyOnline,
  notifyOffline: h.notifyOffline,
}));

let signalr: typeof import('../signalr');

// Эмуляция событий соединения
const fireReconnecting = () => h.fake.reconnectingCbs.forEach(cb => cb());
const fireReconnected = () => h.fake.reconnectedCbs.forEach(cb => cb());
const fireClose = () => h.fake.closeCbs.forEach(cb => cb());

beforeEach(async () => {
  // Синглтон connection и дебаунс — модульные, пересоздаём модуль на каждый тест
  vi.resetModules();
  vi.useFakeTimers();
  h.fake.state = h.HubConnectionState.Connected;
  h.fake.reconnectingCbs.length = 0;
  h.fake.reconnectedCbs.length = 0;
  h.fake.closeCbs.length = 0;
  h.fake.start.mockClear();
  h.fake.invoke.mockClear();
  h.notifyOnline.mockClear();
  h.notifyOffline.mockClear();

  signalr = await import('../signalr');
  // Создаёт connection и регистрирует onreconnecting/onreconnected/onclose
  signalr.getConnection();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('дебаунс офлайн-индикатора', () => {
  it('офлайн объявляется только через 2.5с после начала реконнекта', () => {
    fireReconnecting();
    vi.advanceTimersByTime(2499);
    expect(h.notifyOffline).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(h.notifyOffline).toHaveBeenCalledTimes(1);
  });

  it('быстрый реконнект отменяет офлайн — индикатор не мигает', () => {
    fireReconnecting();
    vi.advanceTimersByTime(1000);
    fireReconnected();
    vi.advanceTimersByTime(10_000);

    expect(h.notifyOffline).not.toHaveBeenCalled();
    expect(h.notifyOnline).toHaveBeenCalledTimes(1);
  });

  it('повторный onreconnecting не создаёт второй таймер', () => {
    fireReconnecting();
    vi.advanceTimersByTime(1000);
    fireReconnecting();
    vi.advanceTimersByTime(1500); // 2.5с от ПЕРВОГО события
    expect(h.notifyOffline).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(10_000);
    expect(h.notifyOffline).toHaveBeenCalledTimes(1);
  });

  it('onclose — офлайн сразу, без дебаунса', () => {
    fireClose();
    expect(h.notifyOffline).toHaveBeenCalledTimes(1);
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

  it('вечный Reconnecting → таймаут через 8с, invoke не вызывается', async () => {
    h.fake.state = h.HubConnectionState.Reconnecting;
    const p = signalr.joinSession('s1');
    const guarded = p.catch((e: Error) => e); // не даём unhandled rejection

    await vi.advanceTimersByTimeAsync(8000);

    expect(await guarded).toEqual(new Error('SignalR connect timeout'));
    expect(h.fake.invoke).not.toHaveBeenCalled();
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

  it('Reconnecting → Disconnected — ошибка без ожидания таймаута', async () => {
    h.fake.state = h.HubConnectionState.Reconnecting;
    const p = signalr.joinSession('s1');
    const guarded = p.catch((e: Error) => e);

    await vi.advanceTimersByTimeAsync(100);
    h.fake.state = h.HubConnectionState.Disconnected;
    await vi.advanceTimersByTimeAsync(100);

    expect(await guarded).toEqual(new Error('SignalR disconnected while waiting'));
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
