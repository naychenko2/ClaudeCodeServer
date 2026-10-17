import type { PromptCacheState } from './promptCache';

// Напоминание «кэш скоро остынет» — один тост на якорь кэша. Якорь — момент последнего
// вызова с кэшем: двигает его только новый ход родного Claude, компакт-ход — нет, поэтому
// пара «чат + якорь» и есть «это остывание уже показывали». Хранение — sessionStorage
// вкладки: на чат одна запись с последним показанным якорем, мусор не копится.

const KEY_PREFIX = 'cc-cache-toast:';

type ToastStorage = Pick<Storage, 'getItem' | 'setItem'>;

function defaultStorage(): ToastStorage | null {
  try { return typeof window !== 'undefined' ? window.sessionStorage : null; } catch { return null; }
}

// Пора ли показать тост, и если да — сразу отметить показ (вторая шапка того же чата или
// следующий тик таймера уже увидят отметку). Пока идёт ход, якорь вот-вот сдвинется — молчим;
// на cold/unknown/warm — тоже. Без хранилища (приватный режим) тост не показывается вовсе:
// лучше промолчать, чем напоминать на каждой перерисовке.
export function takeExpiringCacheToast(
  sessionId: string | undefined, state: PromptCacheState, isWaiting: boolean,
  storage: ToastStorage | null = defaultStorage(),
): boolean {
  if (!sessionId || isWaiting || state.status !== 'expiring' || state.anchorAtMs === undefined || !storage) return false;
  const key = KEY_PREFIX + sessionId;
  const anchor = String(state.anchorAtMs);
  try {
    if (storage.getItem(key) === anchor) return false;
    storage.setItem(key, anchor);
  } catch {
    return false;
  }
  return true;
}
