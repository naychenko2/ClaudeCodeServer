// Последняя запущенная в чате механика «Обсудить с командой» — для бейджа на карточках
// списка чатов и в шапке. Механики целиком фронтовые (бэкенд не участвует), поэтому и
// «последнюю механику» держим на клиенте: localStorage per-chat + подписки (паттерн
// featureFlags.ts). Пишется при запуске механики (Composer) и ретроактивно из истории
// открытого чата (детект по тексту хода).

import { useSyncExternalStore } from 'react';
import { TEAM_MECHANICS, type TeamMechanicId } from '../features/team/teamMechanics';

const PREFIX = 'cc_last_mechanic:';

// Кэш в памяти поверх localStorage: стабильные снапшоты для useSyncExternalStore
const _cache = new Map<string, TeamMechanicId | null>();
const _listeners = new Set<() => void>();
// Монотонная версия — общий снапшот для списков (хуки нельзя звать в .map,
// поэтому список подписывается один раз и перечитывает getLastMechanic по chatId)
let _version = 0;

function isValidId(v: string | null): v is TeamMechanicId {
  return v !== null && TEAM_MECHANICS.some(m => m.id === v);
}

export function getLastMechanic(chatId: string): TeamMechanicId | null {
  if (_cache.has(chatId)) return _cache.get(chatId)!;
  let value: TeamMechanicId | null = null;
  try {
    const raw = localStorage.getItem(PREFIX + chatId);
    if (isValidId(raw)) value = raw;
  } catch { /* приватный режим/квота */ }
  _cache.set(chatId, value);
  return value;
}

export function setLastMechanic(chatId: string, id: TeamMechanicId): void {
  if (_cache.get(chatId) === id) return; // без изменений — не дёргаем подписчиков
  _cache.set(chatId, id);
  try { localStorage.setItem(PREFIX + chatId, id); } catch { /* приватный режим/квота */ }
  _version++;
  _listeners.forEach(fn => fn());
}

function subscribe(fn: () => void): () => void {
  _listeners.add(fn);
  return () => _listeners.delete(fn);
}

// Одна подписка на весь список: возвращает версию стора, ре-рендерит список при любом
// изменении — внутри .map читаем getLastMechanic(chatId) напрямую (хук в цикле нельзя)
export function useLastMechanicVersion(): number {
  return useSyncExternalStore(subscribe, () => _version, () => _version);
}
