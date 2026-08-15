// Screen Wake Lock на время активной петли разговора: без него телефон гасит экран,
// а вместе с ним встаёт распознавание речи (Web Speech в фоне не слушает) — «разговор
// на прогулке» обрывается сам собой через полминуты.
//
// Блокировку браузер снимает при уходе вкладки в фон, поэтому её переполучаем по
// visibilitychange. Без API (десктопный Safari, старый Android) — тихая деградация.

type WakeLockSentinelLike = { released: boolean; release(): Promise<void>; addEventListener?: (t: string, cb: () => void) => void };
type WakeLockApi = { request(type: 'screen'): Promise<WakeLockSentinelLike> };

let sentinel: WakeLockSentinelLike | null = null;
let wanted = false;
let visibilityAttached = false;

function api(): WakeLockApi | null {
  if (typeof navigator === 'undefined') return null;
  return (navigator as Navigator & { wakeLock?: WakeLockApi }).wakeLock ?? null;
}

async function acquire(): Promise<void> {
  const wl = api();
  if (!wl || sentinel) return;
  try {
    sentinel = await wl.request('screen');
    // Браузер мог снять блокировку сам (свернули вкладку) — забываем ссылку, чтобы
    // следующее переполучение не считало её живой
    sentinel.addEventListener?.('release', () => { sentinel = null; });
  } catch { /* отказ (не жест, фон, политика) — работаем без блокировки */ }
}

function onVisibility() {
  if (!wanted) return;
  if (typeof document !== 'undefined' && document.visibilityState === 'visible') void acquire();
}

export function requestWakeLock(): void {
  wanted = true;
  if (!visibilityAttached && typeof document !== 'undefined') {
    visibilityAttached = true;
    document.addEventListener('visibilitychange', onVisibility);
  }
  void acquire();
}

export function releaseWakeLock(): void {
  wanted = false;
  const s = sentinel;
  sentinel = null;
  try { void s?.release().catch(() => { /* уже отпущена */ }); } catch { /* noop */ }
  if (visibilityAttached && typeof document !== 'undefined') {
    visibilityAttached = false;
    document.removeEventListener('visibilitychange', onVisibility);
  }
}
