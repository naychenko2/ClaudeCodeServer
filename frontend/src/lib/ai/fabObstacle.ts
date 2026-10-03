// Нижнее препятствие для круглешка AI (FAB): композер чата или футер мастера персон.
// Кнопка стоит в правом нижнем углу и ужимается, когда препятствие доходит до её угла
// (узкое окно, широкая колонка чата). Вверх, над препятствием, она уходит только если
// угол занят и ужатой (телефон: композер во всю ширину) — контролы не накрывает никогда.
//
// Владелец публикует сюда свой узел, замер пересечения делает сам FAB: только он знает
// свою геометрию, а она зависит от режима панелей. Узел, а не прямоугольник — препятствие
// меняет и высоту (композер растёт), и ширину (открылась панель), и следить за этим
// удобнее наблюдателем на месте замера.
//
// Слоты: у препятствия бывают соседи, которые тоже нельзя накрывать, — кнопка «Вниз»
// чата висит прямо над композером у его правого края. Каждый сосед пишет в свой слот,
// а FAB уступает их объединению: иначе, поднявшись над композером, лёг бы на «Вниз».
//
// Владельцы слота: в один слот пишут несколько (мастер персон поверх чата пишет в `main`
// поверх композера). Слот — стек: действует последний опубликованный узел, а каждый
// владелец снимает только СВОЙ. Иначе закрытие мастера сносило бы композер чата, и FAB
// ложился бы на его контролы.
//
// Слот `intro-card` — карточка-приглашение проекта на телефоне: прибита к низу списка
// чатов и «Команды», её кнопки во всю ширину. Отдельный слот, а не `main`: композер
// чата смонтирован рядом (скрыт) и, опубликовавшись позже, перебил бы карточку в стеке.
//
// noRaise — над препятствием не лента, а форма (футер мастера персон): подниматься над
// ним некуда, кнопка легла бы на поля шага. Не влезла в угол — прячется.
type Listener = () => void;
export type FabObstacleSlot = 'main' | 'scroll-down' | 'intro-card';
export interface FabObstacleOptions { noRaise?: boolean }

const nodes = new Map<FabObstacleSlot, HTMLElement[]>();
const subs = new Set<Listener>();
let noRaiseNodes = new WeakSet<HTMLElement>();

const top = (slot: FabObstacleSlot) => nodes.get(slot)?.at(-1) ?? null;

function notifyIfChanged(slot: FabObstacleSlot, before: HTMLElement | null) {
  if (top(slot) !== before) for (const f of subs) f();
}

// Опубликовать узел в слот; возвращает снятие — оно убирает только этот узел
export function setFabObstacle(el: HTMLElement, slot: FabObstacleSlot = 'main', opts: FabObstacleOptions = {}): () => void {
  const before = top(slot);
  if (opts.noRaise) noRaiseNodes.add(el); else noRaiseNodes.delete(el);
  const stack = (nodes.get(slot) ?? []).filter(n => n !== el);
  stack.push(el);
  nodes.set(slot, stack);
  notifyIfChanged(slot, before);
  return () => {
    const cur = nodes.get(slot);
    if (!cur?.includes(el)) return;
    const was = top(slot);
    const rest = cur.filter(n => n !== el);
    if (rest.length) nodes.set(slot, rest); else nodes.delete(slot);
    notifyIfChanged(slot, was);
  };
}

export function getFabObstacles(): HTMLElement[] {
  return [...nodes.keys()].map(top).filter((n): n is HTMLElement => n !== null);
}

// Запрещает ли препятствие подъём над собой (см. FabObstacleOptions)
export function isFabNoRaise(el: HTMLElement): boolean {
  return noRaiseNodes.has(el);
}

export function subscribeFabObstacle(f: Listener): () => void {
  subs.add(f);
  return () => { subs.delete(f); };
}

// Только для тестов: состояние модульное и живёт между тестами. Дубль узла в стеке через
// публичный API не виден (снятие вычищает все копии), поэтому стек отдаём копией
export const fabObstacleTestHooks = {
  stack: (slot: FabObstacleSlot = 'main'): HTMLElement[] => [...(nodes.get(slot) ?? [])],
  reset: () => { nodes.clear(); subs.clear(); noRaiseNodes = new WeakSet(); },
};

// Полный и ужатый размеры круглешка
export const FAB_FULL = 54;
export const FAB_SMALL = 36;
// Зазор между кнопкой и препятствием: ужимаемся, не дожидаясь касания впритык
export const FAB_CLEARANCE = 10;
// Отступ от края окна у прижатой кнопки — тот же, что ставит PanelZone в компактном режиме
export const FAB_EDGE_INSET = 6;
// small — ужатый круг; edge — прижат к краю окна (FAB_EDGE_INSET); raise — подъём над
// базовым углом, px; hidden — места нет нигде
export interface FabPlacement { small: boolean; edge: boolean; raise: number; hidden: boolean }
export const PLACE_CORNER: FabPlacement = { small: false, edge: false, raise: 0, hidden: false };

export interface Box { left: number; top: number; right: number; bottom: number }

// controlsAt — реальные контролы под прямоугольником кандидата (замер DOM делает FAB):
// размещение знает о препятствиях только от тех, кто их опубликовал, а экран с кнопкой в
// углу, которая ничего не публикует, иначе снова дал бы наезд. noRaise — см. FabObstacleOptions
export interface PlaceFabOptions { noRaise?: boolean; controlsAt?: (b: Box) => readonly Box[] }

// Объединение прямоугольников препятствий; пустые (элемент скрыт) не в счёт
export function unionBox(boxes: readonly Box[]): Box | null {
  let u: Box | null = null;
  for (const b of boxes) {
    if (b.right - b.left === 0 && b.bottom - b.top === 0) continue;
    u = u
      ? { left: Math.min(u.left, b.left), top: Math.min(u.top, b.top), right: Math.max(u.right, b.right), bottom: Math.max(u.bottom, b.bottom) }
      : { left: b.left, top: b.top, right: b.right, bottom: b.bottom };
  }
  return u;
}

// Непустое препятствие или null: скрытый элемент даёт нулевой прямоугольник
function solidBox(o: Box | null): Box | null {
  return o && !(o.right - o.left === 0 && o.bottom - o.top === 0) ? o : null;
}

// Задевает ли препятствие круг size, поднятый на raise, у правой кромки r (с зазором)
function hitsObstacle(ob: Box | null, size: number, raise: number, r: number, bottom: number): boolean {
  return !!ob && ob.right > r - size - FAB_CLEARANCE && ob.left < r + FAB_CLEARANCE &&
    ob.bottom > bottom - raise - size - FAB_CLEARANCE && ob.top < bottom - raise + FAB_CLEARANCE;
}

// Контролы под таким кругом (замер DOM — у вызывающего, см. PlaceFabOptions.controlsAt)
function controlsUnder(opts: PlaceFabOptions, size: number, raise: number, r: number, bottom: number): readonly Box[] {
  return opts.controlsAt?.({ left: r - size, top: bottom - raise - size, right: r, bottom: bottom - raise }) ?? [];
}

// Где стоять кнопке. Главное правило — кнопка НИКОГДА не накрывает препятствие с его
// контролами (отправка, полосы над полем, пилюля собеседника); «стоять в углу» вторично.
// Уровни по порядку:
//   1. полный круг в углу не задевает препятствие — стоим так;
//   2. задевает, а ужатый нет — ужимаемся, из угла не уходим;
//   3. ужатый тоже задевает — прижимаемся к краю окна (под рельсу панелей, как в
//      компактном режиме PanelZone), если там чисто;
//   4. угол занят и так (телефон, композер во всю ширину) — поднимаемся над
//      препятствием на зазор; не влезаем и туда (композер в весь экран) — прячемся.
// Каждая ступень занята и тогда, когда под кандидатом лежит контрол из controlsAt: такие
// контролы в углу становятся частью препятствия, через которое поднимаемся. Подъём один:
// под поднятой кнопкой опять контрол — прячемся, а не карабкаемся по экрану. У препятствия
// с noRaise ступени 4 нет вовсе — сразу прячемся.
// right/bottom — базовый угол кнопки (правый-нижний край круга без подъёма) в координатах
// окна; препятствие null или нулевое — угол свободен.
export function placeFab(o: Box | null, right: number, bottom: number, viewportW: number, opts: PlaceFabOptions = {}): FabPlacement {
  const ob = solidBox(o);
  const hits = (size: number, raise: number, r = right) => hitsObstacle(ob, size, raise, r, bottom);
  const controls = (size: number, raise: number, r = right) => controlsUnder(opts, size, raise, r, bottom);
  const free = (size: number, raise: number, r = right) => !hits(size, raise, r) && controls(size, raise, r).length === 0;
  if (free(FAB_FULL, 0)) return PLACE_CORNER;
  if (free(FAB_SMALL, 0)) return { small: true, edge: false, raise: 0, hidden: false };
  const edgeR = viewportW - FAB_EDGE_INSET;
  const edgeOk = edgeR > right;
  if (edgeOk && free(FAB_SMALL, 0, edgeR)) return { small: true, edge: true, raise: 0, hidden: false };
  if (opts.noRaise) return { small: true, edge: false, raise: 0, hidden: true };
  // Пол подъёма — препятствие вместе с контролами, занявшими угол (и край, если пробовали)
  const floor = unionBox([
    ...(ob ? [ob] : []), ...controls(FAB_SMALL, 0), ...(edgeOk ? controls(FAB_SMALL, 0, edgeR) : []),
  ]);
  if (!floor) return { small: true, edge: false, raise: 0, hidden: true };
  const raise = Math.ceil(bottom - (floor.top - FAB_CLEARANCE));
  const hidden = bottom - raise - FAB_SMALL < FAB_CLEARANCE || controls(FAB_SMALL, raise).length > 0;
  return { small: true, edge: false, raise, hidden };
}

export const PLACE_HIDDEN: FabPlacement = { small: true, edge: false, raise: 0, hidden: true };

export function samePlacement(a: FabPlacement, b: FabPlacement): boolean {
  if (a.hidden && b.hidden) return true; // спрятанной кнопке размер и подъём безразличны
  return a.small === b.small && a.edge === b.edge && a.raise === b.raise && a.hidden === b.hidden;
}

// Что мешает кнопке стоять там, где она стоит сейчас (p): 'obstacle' — опубликованное
// препятствие, 'control' — контрол из пробы DOM, null — место чистое. Спрятанной не мешает ничего
export type FabBlocker = 'obstacle' | 'control' | null;
export function fabSpotBlocker(p: FabPlacement, o: Box | null, right: number, bottom: number, viewportW: number, opts: PlaceFabOptions = {}): FabBlocker {
  if (p.hidden) return null;
  const size = p.small ? FAB_SMALL : FAB_FULL;
  const r = p.edge ? viewportW - FAB_EDGE_INSET : right;
  if (hitsObstacle(solidBox(o), size, p.raise, r, bottom)) return 'obstacle';
  return controlsUnder(opts, size, p.raise, r, bottom).length ? 'control' : null;
}

// Стабилизатор места (гистерезис). Замер честен только для своего кадра, а переходы
// проводят экран через промежуточные раскладки: «Вниз» чата моргает на плавной прокрутке,
// новый экран в первом кадре ещё без данных, открытие по ссылке сначала рисует список
// проекта. Каждый такой кадр двигал кнопку туда-обратно. Правило:
//   - место стало небезопасным из-за препятствия — уходим сразу (композер вырос, «Вниз»
//     появилась: геометрия опубликована владельцем, переходных кадров у неё нет);
//   - из-за контрола из пробы — после короткого подтверждения FAB_CONFIRM_MS: проба —
//     эвристика, и первый кадр экрана её обманывает (лента ещё не прокручиваемая);
//   - место безопасно — уходим, только когда новый замер устоял FAB_SETTLE_MS.
// Спрятанная кнопка безопасна всегда, поэтому и появляется только по устоявшемуся замеру;
// первое появление после монтирования ждёт дольше (FAB_MOUNT_SETTLE_MS) — приложение
// при загрузке проходит через несколько экранов подряд.
export const FAB_SETTLE_MS = 250;
export const FAB_CONFIRM_MS = 150;
export const FAB_MOUNT_SETTLE_MS = 600;

// shown — показанное место; pending — замер, который ждёт подтверждения, с момента since;
// settleMs — выдержка ухода с безопасного места (до первого показа — монтажная)
export interface FabSettle { shown: FabPlacement; pending: FabPlacement | null; since: number; settleMs: number }
export const initialFabSettle = (): FabSettle => ({ shown: PLACE_HIDDEN, pending: null, since: 0, settleMs: FAB_MOUNT_SETTLE_MS });

// Шаг стабилизатора на свежем замере measured; blocker — что мешает показанному месту
// (fabSpotBlocker). recheckIn — через сколько мс перемерить, чтобы подтвердить отложенный
// переход; null — ждать нечего
export function stepFabSettle(s: FabSettle, measured: FabPlacement, blocker: FabBlocker, now: number): { next: FabSettle; recheckIn: number | null } {
  if (samePlacement(measured, s.shown)) return { next: s.pending ? { ...s, pending: null } : s, recheckIn: null };
  const wait = blocker === 'obstacle' ? 0 : blocker === 'control' ? FAB_CONFIRM_MS : s.settleMs;
  // Уход с безопасного места ждёт, пока устоит ОДИН и тот же замер; уход из-под контрола —
  // пока держится сам контрол, куда бы ни звал замер
  const since = s.pending && (blocker === 'control' || samePlacement(s.pending, measured)) ? s.since : now;
  if (now - since >= wait) return { next: { shown: measured, pending: null, since: now, settleMs: FAB_SETTLE_MS }, recheckIn: null };
  return { next: { ...s, pending: measured, since }, recheckIn: wait - (now - since) };
}
