// Типы для hostOps/storage.js — реализации общей для хоста и эмулятора.
// Реализация — чистый JS (эмулятор берёт её как <script type="module">),
// типы здесь, чтобы хост и тесты видели контракт.

export type StorageOp = 'get' | 'set' | 'remove' | 'keys';

// Минимальная форма Storage, которую даёт хост (в тестах — Map-обёртка).
// clear() существует у настоящего localStorage, но модуль его никогда не зовёт.
export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
  key(index: number): string | null;
  length: number;
  clear(): void;
}

export type StorageErrorCode = 'invalid' | 'limit' | 'denied' | 'unavailable';

export type StorageCallResult =
  | { ok: true; data: Record<string, unknown> }
  | { ok: false; error: { code: StorageErrorCode; message: string } };

export interface StorageCallInput {
  op: StorageOp;
  args: Record<string, unknown>;
  granted: string[];
  ownerId: string;
  pluginId: string;
  store: StorageLike;
}

export function handleStorageCall(input: StorageCallInput): StorageCallResult;

// Стереть ровно свой префикс; возвращает число удалённых ключей.
export function removePluginStorage(input: { ownerId: string; pluginId: string; store: StorageLike }): number;

// Удалить ключи текущего владельца, чей pluginId не в keepPluginIds.
export function collectStorageGarbage(input: { ownerId: string; keepPluginIds: string[]; store: StorageLike }): number;

export const STORAGE_PREFIX: string;
export const KEY_MAX_LEN: number;
export const VALUE_MAX_BYTES: number;
export const KEYS_MAX_PER_PLUGIN: number;
export const PLUGIN_BYTES_MAX: number;
export const ORIGIN_BYTES_MAX: number;
