// Минимальная обёртка над IndexedDB для офлайн-кэша.
// Один стор "meta": ключ — URL запроса, значение — { data, savedAt }.
// При недоступности IndexedDB (приватный режим и т.п.) операции мягко отклоняются —
// вызывающий код оборачивает их в .catch и продолжает работать без кэша.

const DB_NAME = 'ccs-offline';
const DB_VERSION = 1;
const STORES = ['meta'] as const;
type StoreName = (typeof STORES)[number];

let _dbPromise: Promise<IDBDatabase> | null = null;

function openDb(): Promise<IDBDatabase> {
  if (_dbPromise) return _dbPromise;
  _dbPromise = new Promise((resolve, reject) => {
    if (typeof indexedDB === 'undefined') {
      reject(new Error('IndexedDB недоступен'));
      return;
    }
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      for (const s of STORES) if (!db.objectStoreNames.contains(s)) db.createObjectStore(s);
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
  // Если открытие упало — сбрасываем промис, чтобы следующая попытка могла повториться
  _dbPromise.catch(() => { _dbPromise = null; });
  return _dbPromise;
}

function tx<T>(store: StoreName, mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return openDb().then(db => new Promise<T>((resolve, reject) => {
    const t = db.transaction(store, mode);
    const req = fn(t.objectStore(store));
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  }));
}

export interface CacheEntry<T = unknown> {
  data: T;
  savedAt: number;
}

export function idbGet<T = unknown>(key: string): Promise<CacheEntry<T> | undefined> {
  return tx('meta', 'readonly', s => s.get(key) as IDBRequest<CacheEntry<T> | undefined>);
}

export function idbSet<T = unknown>(key: string, entry: CacheEntry<T>): Promise<IDBValidKey> {
  return tx('meta', 'readwrite', s => s.put(entry, key));
}

export function idbDelete(key: string): Promise<undefined> {
  return tx('meta', 'readwrite', s => s.delete(key) as IDBRequest<undefined>);
}

export function idbKeys(): Promise<string[]> {
  return tx('meta', 'readonly', s => s.getAllKeys() as IDBRequest<IDBValidKey[]>)
    .then(keys => keys.map(String));
}
