// Детский режим: стор персон не ходит в /api/personas (ADR-041, И1) и не роняет необработанным исключением.
import { describe, it, expect, vi, beforeEach } from 'vitest';

const list = vi.fn();
vi.mock('./api', () => ({ api: { personas: { list: (...a: unknown[]) => list(...a) } } }));
vi.mock('./signalr', () => ({ joinUser: () => Promise.resolve(), onMessage: () => () => {}, onReconnected: () => () => {} }));
vi.mock('./offline', () => ({ readStoredUserId: () => null }));

describe('стор персон в детском режиме', () => {
  beforeEach(() => { vi.resetModules(); list.mockReset(); });

  it('не запрашивает список', async () => {
    const m = await import('./personas');
    m.setPersonasChildMode(true);
    await m.ensurePersonasLoaded();
    await m.reloadPersonas();
    expect(list).not.toHaveBeenCalled();
  });

  it('отказ сервера не даёт необработанного исключения', async () => {
    list.mockRejectedValue(new Error('403 child_mode'));
    const m = await import('./personas');
    await expect(m.ensurePersonasLoaded()).resolves.toBeUndefined();
  });

  it('после выхода из детского режима список снова грузится (один запрос)', async () => {
    list.mockResolvedValue([]);
    const m = await import('./personas');
    m.setPersonasChildMode(true);
    m.setPersonasChildMode(false);
    await m.ensurePersonasLoaded();
    expect(list).toHaveBeenCalledTimes(1);
  });
});
