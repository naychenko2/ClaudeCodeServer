// Минимальная обёртка над IndexedDB для офлайн-кэша.
// Сторы:
//   "meta"        — пассивный GET-кэш и служебные ключи; ключ out-of-line (URL / фикс. строка).
//   "outbox"      — очередь офлайн-мутаций задач; in-line key 'taskId' (одна запись на задачу).
//   "noteContent" — редактируемый слой контента заметок; in-line key 'localKey'.
//   "notesOutbox" — очередь офлайн-мутаций заметок; in-line key 'opId' (FIFO).
// При недоступности IndexedDB (приватный режим и т.п.) операции мягко отклоняются —
// вызывающий код оборачивает их в .catch и продолжает работать без кэша.

const DB_NAME = 'ccs-offline';
const DB_VERSION = 2;

// Описание сторов: имя → keyPath (null = out-of-line, ключ передаётся отдельным аргументом).
const STORE_DEFS: Record<string, string | null> = {
  meta: null,
  outbox: 'taskId',
  noteContent: 'localKey',
  notesOutbox: 'opId',
};
type StoreName = keyof typeof STORE_DEFS;

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
      for (const [name, keyPath] of Object.entries(STORE_DEFS)) {
        if (db.objectStoreNames.contains(name)) continue;
        db.createObjectStore(name, keyPath ? { keyPath } : undefined);
      }
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

// === Типизированные сторы с in-line ключом (outbox / noteContent / notesOutbox) ===
// Значения содержат ключ внутри себя (keyPath), поэтому put без отдельного аргумента-ключа.

// Задачи-outbox: одна запись на задачу (keyPath 'taskId') — естественный коалесинг.
export function outboxPut<T extends { taskId: string }>(op: T): Promise<IDBValidKey> {
  return tx('outbox', 'readwrite', s => s.put(op));
}
export function outboxGet<T = unknown>(taskId: string): Promise<T | undefined> {
  return tx('outbox', 'readonly', s => s.get(taskId) as IDBRequest<T | undefined>);
}
export function outboxDelete(taskId: string): Promise<undefined> {
  return tx('outbox', 'readwrite', s => s.delete(taskId) as IDBRequest<undefined>);
}
export function outboxAll<T = unknown>(): Promise<T[]> {
  return tx('outbox', 'readonly', s => s.getAll() as IDBRequest<T[]>);
}

// Контент заметок (keyPath 'localKey').
export function noteContentPut<T extends { localKey: string }>(rec: T): Promise<IDBValidKey> {
  return tx('noteContent', 'readwrite', s => s.put(rec));
}
export function noteContentGet<T = unknown>(localKey: string): Promise<T | undefined> {
  return tx('noteContent', 'readonly', s => s.get(localKey) as IDBRequest<T | undefined>);
}
export function noteContentDelete(localKey: string): Promise<undefined> {
  return tx('noteContent', 'readwrite', s => s.delete(localKey) as IDBRequest<undefined>);
}
export function noteContentAll<T = unknown>(): Promise<T[]> {
  return tx('noteContent', 'readonly', s => s.getAll() as IDBRequest<T[]>);
}

// Очередь мутаций заметок (keyPath 'opId', монотонный — FIFO по возрастанию).
export function notesOutboxPut<T extends { opId: number }>(op: T): Promise<IDBValidKey> {
  return tx('notesOutbox', 'readwrite', s => s.put(op));
}
export function notesOutboxDelete(opId: number): Promise<undefined> {
  return tx('notesOutbox', 'readwrite', s => s.delete(opId) as IDBRequest<undefined>);
}
export function notesOutboxAll<T = unknown>(): Promise<T[]> {
  return tx('notesOutbox', 'readonly', s => s.getAll() as IDBRequest<T[]>);
}

// Полная очистка кэша (при logout/смене сервера).
// Закрывает и удаляет саму БД — следующее обращение пересоздаст её чистой.
export function idbClear(): Promise<void> {
  return openDb().then(db => {
    db.close();
    _dbPromise = null;
    return new Promise<void>((resolve, reject) => {
      const req = indexedDB.deleteDatabase(DB_NAME);
      req.onsuccess = () => resolve();
      req.onerror = () => reject(req.error);
      req.onblocked = () => resolve(); // другая вкладка держит БД — пусть GC уберёт позже
    });
  }).catch(() => { /* IndexedDB недоступна — не критично */ });
}
