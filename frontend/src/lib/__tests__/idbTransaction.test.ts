// Тесты промисов транзакций офлайн-кэша (lib/idb).
//
// Дефект (дефектная карточка 034a96c3, профиль CDP): промис резолвился по успеху
// ЗАПРОСА (req.onsuccess), а сама транзакция могла не состояться — прерваться по
// квоте при переполнении IndexedDB, при смене версии БД или onblocked. Обработчиков на
// транзакции не было, и такой обрыв оставлял промис навсегда нерешённым: он висел в
// памяти вместе с замыканием и данными записи, а вызовы глотали это .catch'ем.
// Поток ответов, пишущихся в кэш (доска агентов — ~0,86 МБ на ответ), копил такие
// висящие записи. Теперь резолв — на коммите транзакции, обрыв реджектит.
//
// Мок IndexedDB собственный: fake-indexeddb в проекте нет, нужен тут контроль над
// моментом коммита и прерывания, а не поведение настоящей БД.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

interface Req { onsuccess: (() => void) | null; onerror: (() => void) | null; result: unknown; error: unknown }
interface Tx {
  oncomplete: (() => void) | null;
  onabort: (() => void) | null;
  onerror: (() => void) | null;
  error: unknown;
  objectStore: (name: string) => { put: (v: unknown, k?: string) => Req; get: (k: string) => Req; delete: (k: string) => Req };
}

// Последние созданные транзакция и запрос — тест управляет их судьбой вручную
let lastTx: Tx;
let lastReq: Req;

function installIndexedDb(): void {
  lastTx = {
    oncomplete: null, onabort: null, onerror: null, error: null,
    objectStore: () => ({
      put: () => lastReq,
      get: () => lastReq,
      delete: () => lastReq,
    }),
  };
  lastReq = { onsuccess: null, onerror: null, result: undefined, error: null };

  const db = {
    close: () => {},
    transaction: () => lastTx,
    objectStoreNames: { contains: () => false },
    createObjectStore: () => ({}),
  };
  const openReq: { result: unknown; error: unknown } = { result: db, error: null };
  // Открытие БД асинхронно, а обработчик навешивает вызывающий модуль — поэтому
  // успех раздаём сеттеру: как только модуль повесил onsuccess, он тут же вызывается.
  Object.defineProperty(openReq, 'onsuccess', {
    get: () => null,
    set: (fn: (() => void) | null) => { queueMicrotask(() => fn?.()); },
  });
  Object.defineProperty(openReq, 'onupgradeneeded', { get: () => null, set: () => {} });
  Object.defineProperty(openReq, 'onerror', { get: () => null, set: () => {} });

  vi.stubGlobal('indexedDB', { open: () => openReq, deleteDatabase: () => ({}) });
}

// Разгон цепочки промисов: open → .then(db) → создание транзакции
async function flush(times = 8): Promise<void> {
  for (let i = 0; i < times; i++) await Promise.resolve();
}

beforeEach(() => {
  vi.resetModules();
  installIndexedDb();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('idb: итог транзакции, а не запроса', () => {
  it('запись резолвится только после коммита транзакции', async () => {
    const { idbSet } = await import('../idb');
    const p = idbSet('/api/x', { data: { a: 1 }, savedAt: 1 });
    let done = false;
    void p.then(() => { done = true; });
    await flush();

    // успех запроса ещё ничего не значит
    lastReq.result = '/api/x';
    lastReq.onsuccess?.();
    await flush();
    expect(done).toBe(false);

    lastTx.oncomplete?.();
    await expect(p).resolves.toBe('/api/x');
  });

  it('прерванная транзакция реджектит промис, а не оставляет его висеть', async () => {
    const { idbSet } = await import('../idb');
    const p = idbSet('/api/x', { data: { a: 1 }, savedAt: 1 });
    await flush();

    lastReq.onsuccess?.();
    lastTx.error = new Error('QuotaExceededError');
    lastTx.onabort?.();

    await expect(p).rejects.toThrow('QuotaExceededError');
  });

  it('ошибка транзакции (не прерывание) тоже реджектит', async () => {
    const { idbSet } = await import('../idb');
    const p = idbSet('/api/x', { data: { a: 1 }, savedAt: 1 });
    await flush();

    lastTx.error = new Error('UnknownError');
    lastTx.onerror?.();

    await expect(p).rejects.toThrow('UnknownError');
  });

  it('прерывание без причины даёт AbortError, а не вечное ожидание', async () => {
    const { idbSet } = await import('../idb');
    const p = idbSet('/api/x', { data: { a: 1 }, savedAt: 1 });
    await flush();

    lastTx.onabort?.();

    await expect(p).rejects.toThrow(/прервана|Abort/i);
  });

  it('чтение из кэша тоже ждёт коммита', async () => {
    const { idbGet } = await import('../idb');
    const p = idbGet('/api/x');
    let done = false;
    void p.then(() => { done = true; });
    await flush();

    lastReq.result = { data: 'v', savedAt: 5 };
    lastReq.onsuccess?.();
    await flush();
    expect(done).toBe(false);

    lastTx.oncomplete?.();
    await expect(p).resolves.toEqual({ data: 'v', savedAt: 5 });
  });
});