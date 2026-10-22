import { getMeSnapshot } from '../../lib/defaultPersona';
import { collectStorageGarbage, removePluginStorage } from './hostOps/storage.js';
import type { StorageLike } from './hostOps/storage';

// Жизненный цикл данных storage.* плагина в этом браузере. Работает только по
// ключам (clear() не зовём никогда); без владельца — ничего не делает.

// Удаление плагина = «забыть»: стираем ровно его префикс.
export function eraseOnUninstall(pluginId: string, store: StorageLike = localStorage): number {
  const ownerId = getMeSnapshot().userId;
  return ownerId ? removePluginStorage({ ownerId, pluginId, store }) : 0;
}

// Ленивая сборка мусора: звать ТОЛЬКО после успешного GET /api/plugins.
export function collectOnListLoaded(pluginIds: string[], store?: StorageLike): number {
  const ownerId = getMeSnapshot().userId;
  const target = store ?? (typeof localStorage === 'undefined' ? null : localStorage);
  return ownerId && target ? collectStorageGarbage({ ownerId, keepPluginIds: pluginIds, store: target }) : 0;
}
