import { getMeSnapshot } from '../../lib/defaultPersona';
import { collectStorageGarbage, removePluginStorage } from './hostOps/storage.js';
import type { StorageLike } from './hostOps/storage';

// Жизненный цикл данных storage.* плагина в этом браузере. Работает только по
// ключам (clear() не зовём никогда); без владельца — ничего не делает.

// Обращение к браузерному хранилище может бросить SecurityError: при отключённом
// хранилище (Firefox dom.storage.enabled=false) и в приватном режиме сам геттер
// window.localStorage бросает при чтении — typeof от этого не спасает (R2-д, F-д2).
// Для lifecycle это тихий no-op: список плагинов обязан открываться и без
// хранилища — сборка мусора побочный эффект, а не причина ронять список.
// Настоящая ошибка не прячется — она идёт вверх, как раньше.
// F-д3 (минор 1): тихий null — ТОЛЬКО на SecurityError. TypeError геттера
// (расширения, прокси) и прочее не глотается: иначе сборка мусора молча не идёт,
// а разработчик ничего не видит.
function safeLocalStorage(): StorageLike | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch (e) {
    if ((e as { name?: string })?.name === 'SecurityError') return null; // хранилище отключено
    throw e;
  }
}

// SecurityError от операции с store — тихий ноль; прочее — без изменений.
// F-д3 (минор 2): instanceof Error не нужен: исключение из другого realm не
// instanceof Error, но name у него тот же — проверяем только name.
function quietSecurity(fn: () => number): number {
  try {
    return fn();
  } catch (e) {
    if ((e as { name?: string })?.name === 'SecurityError') return 0;
    throw e;
  }
}

// Удаление плагина = «забыть»: стираем ровно его префикс.
export function eraseOnUninstall(pluginId: string, store?: StorageLike): number {
  const ownerId = getMeSnapshot().userId;
  const target = store ?? safeLocalStorage();
  return ownerId && target ? quietSecurity(() => removePluginStorage({ ownerId, pluginId, store: target })) : 0;
}

// Ленивая сборка мусора: звать ТОЛЬКО после успешного GET /api/plugins.
export function collectOnListLoaded(pluginIds: string[], store?: StorageLike): number {
  const ownerId = getMeSnapshot().userId;
  const target = store ?? safeLocalStorage();
  return ownerId && target ? quietSecurity(() => collectStorageGarbage({ ownerId, keepPluginIds: pluginIds, store: target })) : 0;
}
