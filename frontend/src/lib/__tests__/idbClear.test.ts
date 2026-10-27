import { describe, it, expect, vi, afterEach } from 'vitest';

// Выход и смена аккаунта сносят БД целиком — вместе с любым offline:<owner>:*, а не по перечню ключей.
describe('idbClear', () => {
  afterEach(() => { vi.unstubAllGlobals(); vi.resetModules(); });

  it('удаляет всю базу ccs-offline', async () => {
    const deleted: string[] = [];
    const db = { close: vi.fn(), objectStoreNames: { contains: () => true }, onversionchange: null };
    vi.stubGlobal('indexedDB', {
      open: () => {
        const req: { result: unknown; onsuccess: null | (() => void) } = { result: db, onsuccess: null };
        queueMicrotask(() => req.onsuccess?.());
        return req;
      },
      deleteDatabase: (name: string) => {
        deleted.push(name);
        const req: { onsuccess: null | (() => void) } = { onsuccess: null };
        queueMicrotask(() => req.onsuccess?.());
        return req;
      },
    });
    const { idbClear } = await import('../idb');
    await idbClear();
    expect(deleted).toEqual(['ccs-offline']);
  });
});
