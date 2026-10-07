import { describe, it, expect, vi, beforeEach } from 'vitest';

const h = vi.hoisted(() => {
  const mk = () => ({
    state: 'Connected',
    start: vi.fn(async () => {}),
    stop: vi.fn(async () => {}),
    on: vi.fn(),
    off: vi.fn(),
    onclose: vi.fn(),
  });
  return { mk, built: [] as ReturnType<typeof mk>[] };
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected', Connecting: 'Connecting', Reconnecting: 'Reconnecting' },
  HubConnectionBuilder: class {
    withUrl() { return this; }
    withAutomaticReconnect() { return this; }
    build() { const c = h.mk(); h.built.push(c); return c; }
  },
}));
vi.mock('../offline', () => ({ readStoredToken: () => 'tok' }));
vi.mock('../deviceAgent', () => ({
  agentHubTicket: async () => 't',
  agentHubUrl: async (id: string) => `http://agent/${id}`,
  projectRouteOf: () => 'server',
}));

beforeEach(() => {
  vi.resetModules();
  h.built.length = 0;
});

describe('resetAgentHubs', () => {
  it('останавливает все живые соединения и сохраняет подписчиков', async () => {
    const mod = await import('../agentHub');
    const a = await mod.agentHub('p1');
    const b = await mod.agentHub('p2');
    const sub = vi.fn();
    mod.onAgentHubMessage(sub);

    await mod.resetAgentHubs();
    expect(a.stop).toHaveBeenCalledTimes(1);
    expect(b.stop).toHaveBeenCalledTimes(1);
    expect(mod.agentHubIfConnected('p1')).toBeNull();

    // подписчик жив: новое соединение доставляет ему события
    await mod.agentHub('p1');
    const handler = h.built[h.built.length - 1].on.mock.calls.find(c => c[0] === 'message')![1];
    handler({ type: 'x' });
    expect(sub).toHaveBeenCalledWith({ type: 'x' });
  });
});

describe('resetTerminalConnection', () => {
  it('зовёт stop у того же экземпляра и не пересоздаёт его', async () => {
    const mod = await import('../terminalSignalr');
    const off = mod.onTerminalMessage(() => {});
    expect(h.built).toHaveLength(1);
    await mod.resetTerminalConnection();
    expect(h.built[0].stop).toHaveBeenCalledTimes(1);
    mod.onTerminalMessage(() => {});
    expect(h.built).toHaveLength(1);
    off();
  });
});
