// Screen Wake Lock: экран не должен гаснуть, пока идёт работа, за которой человек следит.
// Два потребителя — активная петля разговора (без экрана встаёт распознавание: Web Speech
// в фоне не слушает) и идущий ход в открытом чате (планшет засыпал посреди ответа).
//
// Владельцы. Блокировка одна на вкладку, а просят её независимые места, поэтому держим
// набор владельцев: она жива, пока хочет хоть один. Без этого конец хода снимал бы экран
// посреди разговора.
//
// Эксклюзив. Петля разговора отпускает экран сама на затянувшемся ходе (телефон в кармане
// на прогулке — светить пять минут незачем), и владелец «ход» этот замысел бы отменил.
// Поэтому пока петля жива, она объявляет себя эксклюзивным владельцем: остальные заявки
// не учитываются вовсе, а не спорят с ней.
//
// Блокировку браузер снимает при уходе вкладки в фон, поэтому её переполучаем по
// visibilitychange. Без API (десктопный Safari, старый Android) — тихая деградация.

type WakeLockSentinelLike = { released: boolean; release(): Promise<void>; addEventListener?: (t: string, cb: () => void) => void };
type WakeLockApi = { request(type: 'screen'): Promise<WakeLockSentinelLike> };

let sentinel: WakeLockSentinelLike | null = null;
// Заявка в полёте: request асинхронен, и две подряд (петля + ход) без этого флага брали
// бы ДВЕ блокировки — вторая затирала бы ссылку на первую, и та не отпускалась никогда
let acquiring = false;
const owners = new Set<string>();
let exclusive: string | null = null;
let visibilityAttached = false;

function api(): WakeLockApi | null {
  if (typeof navigator === 'undefined') return null;
  return (navigator as Navigator & { wakeLock?: WakeLockApi }).wakeLock ?? null;
}

// Блокировка нужна, пока её хочет владелец: любой — либо эксклюзивный, если он объявлен
function wanted(): boolean {
  return exclusive === null ? owners.size > 0 : owners.has(exclusive);
}

async function acquire(): Promise<void> {
  const wl = api();
  if (!wl || sentinel || acquiring) return;
  acquiring = true;
  try {
    sentinel = await wl.request('screen');
    // Браузер мог снять блокировку сам (свернули вкладку) — забываем ссылку, чтобы
    // следующее переполучение не считало её живой
    sentinel.addEventListener?.('release', () => { sentinel = null; });
  } catch { /* отказ (не жест, фон, политика) — работаем без блокировки */ }
  finally { acquiring = false; }
  // Пока заявка летела, владелец мог расхотеть (короткий ход) — его release прошёл мимо
  // ещё не существовавшей блокировки, поэтому доводим состояние сами
  if (!wanted()) sync();
}

function onVisibility() {
  if (!wanted()) return;
  if (typeof document !== 'undefined' && document.visibilityState === 'visible') void acquire();
}

// Приводит фактическую блокировку к желаемому состоянию. Идемпотентна: зовётся на каждую
// смену состава владельцев, в том числе из горячего пути реестра сессий
function sync(): void {
  // Слушателя ставим только там, где он есть на деле: в тестовой среде document бывает
  // урезанной заглушкой (часть методов отсутствует), а падать в горячем пути реестра
  // сессий эта функция не вправе
  const d = typeof document === 'undefined' ? null : (document as Partial<Document>);
  const doc = typeof d?.addEventListener === 'function' && typeof d.removeEventListener === 'function' ? d : null;
  if (wanted()) {
    if (!visibilityAttached && doc) {
      visibilityAttached = true;
      doc.addEventListener!('visibilitychange', onVisibility);
    }
    void acquire();
    return;
  }
  const s = sentinel;
  sentinel = null;
  try { void s?.release().catch(() => { /* уже отпущена */ }); } catch { /* noop */ }
  if (visibilityAttached && doc) {
    visibilityAttached = false;
    doc.removeEventListener!('visibilitychange', onVisibility);
  }
}

export function requestWakeLock(owner = 'default'): void {
  owners.add(owner);
  sync();
}

export function releaseWakeLock(owner = 'default'): void {
  owners.delete(owner);
  sync();
}

// Пока владелец эксклюзивен, чужие заявки на блокировку не действуют (см. шапку)
export function setWakeLockExclusive(owner: string, on: boolean): void {
  if (on) exclusive = owner;
  else if (exclusive === owner) exclusive = null;
  sync();
}
