import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  placeFab, unionBox, PLACE_CORNER, FAB_FULL, FAB_SMALL, FAB_EDGE_INSET, setFabObstacle, getFabObstacles,
  subscribeFabObstacle, fabObstacleTestHooks, isFabNoRaise,
  stepFabSettle, initialFabSettle, fabSpotBlocker, PLACE_HIDDEN, FAB_SETTLE_MS, FAB_CONFIRM_MS, FAB_MOUNT_SETTLE_MS,
  FAB_MOBILE_RIGHT,
  type FabPlacement, type FabSettle, type FabBlocker,
} from '../ai/fabObstacle';
import { CHAT_GUTTER_MOBILE, SCROLL_DOWN_SIZE } from '../design';

// Телефон: круглешок на одной вертикальной оси с кнопкой «Вниз» чата, и подъём над ней
// по-прежнему срабатывает (ось смещена внутрь — горизонтальное пересечение сохраняется)
describe('FAB на телефоне — общая ось с кнопкой «Вниз»', () => {
  const vw = 390, vh = 844;
  const composer = box(0, vh - 150, vw, vh);
  const scrollRight = vw - CHAT_GUTTER_MOBILE;
  const scrollDown = box(scrollRight - SCROLL_DOWN_SIZE, composer.top - 14 - SCROLL_DOWN_SIZE, scrollRight, composer.top - 14);
  const fabRight = vw - FAB_MOBILE_RIGHT;
  const fabBottom = vh - 20;

  it('центры совпадают по X', () => {
    expect(fabRight - FAB_SMALL / 2).toBe(scrollRight - SCROLL_DOWN_SIZE / 2);
  });

  it('над композером и «Вниз» кнопка поднимается, не прячется и их не накрывает', () => {
    const p = placeFab(unionBox([composer, scrollDown]), fabRight, fabBottom, vw);
    expect(p.hidden).toBe(false);
    expect(p.small).toBe(true);
    // Нижний край поднятого круга — выше верха кнопки «Вниз»
    expect(fabBottom - p.raise).toBeLessThanOrEqual(scrollDown.top);
  });
});

// Место круглешка AI относительно композера. Главный критерий — кнопка не накрывает
// препятствие ни в одной ветке: проверяем пересечение итогового круга с композером.
type Box = { left: number; top: number; right: number; bottom: number };
const box = (left: number, top: number, right: number, bottom: number): Box => ({ left, top, right, bottom });

// Итоговый прямоугольник кнопки по месту и базовому углу
function fabBox(p: ReturnType<typeof placeFab>, right: number, bottom: number, vw: number): Box {
  const size = p.small ? FAB_SMALL : FAB_FULL;
  const r = p.edge ? vw - FAB_EDGE_INSET : right;
  const b = bottom - p.raise;
  return box(r - size, b - size, r, b);
}
const overlaps = (a: Box, b: Box) =>
  Math.min(a.right, b.right) > Math.max(a.left, b.left) && Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top);

describe('placeFab', () => {
  it('без препятствия — полный круг в углу', () => {
    expect(placeFab(null, 1580, 880, 1600)).toEqual(PLACE_CORNER);
  });

  it('композер далеко от угла (десктоп, колонка по центру) — угол, без подъёма', () => {
    expect(placeFab(box(500, 700, 1300, 880), 1580, 880, 1600)).toEqual(PLACE_CORNER);
  });

  it('композер задевает полный круг, но не ужатый — ужимается в углу', () => {
    // Правая кромка композера на 1520: полный круг (1526..1580) с зазором задет, ужатый (1544..) — нет
    const ob = box(400, 700, 1520, 880);
    const p = placeFab(ob, 1580, 880, 1600);
    expect(p).toEqual({ small: true, edge: false, raise: 0, hidden: false });
    expect(overlaps(fabBox(p, 1580, 880, 1600), ob)).toBe(false);
  });

  it('ужатый в углу задевает, а у края окна чисто (планшет, рельса справа) — прижат к краю', () => {
    // 1180x820: угол right=1160, композер до 1115 — ужатый (1124..1160) с зазором задет
    const ob = box(420, 500, 1115, 800);
    const p = placeFab(ob, 1160, 800, 1180);
    expect(p).toEqual({ small: true, edge: true, raise: 0, hidden: false });
    expect(overlaps(fabBox(p, 1160, 800, 1180), ob)).toBe(false);
  });

  it('композер во всю ширину (телефон) — поднимается над ним и не накрывает', () => {
    const ob = box(16, 530, 374, 832);
    const p = placeFab(ob, 374, 824, 390);
    expect(p.small).toBe(true);
    expect(p.hidden).toBe(false);
    expect(p.raise).toBeGreaterThan(0);
    expect(overlaps(fabBox(p, 374, 824, 390), ob)).toBe(false);
  });

  it('телефон, чат отлистан вверх — поднимается и над кнопкой «Вниз», не только над композером', () => {
    // 390x844: композер 16..374, кнопка «Вниз» 44px у правого края над композером (bottom: composerH + 14)
    const composer = box(16, 730, 374, 832);
    const scrollDown = box(330, 672, 374, 716);
    const p = placeFab(unionBox([composer, scrollDown]), 374, 824, 390);
    expect(p.hidden).toBe(false);
    const fab = fabBox(p, 374, 824, 390);
    expect(overlaps(fab, composer)).toBe(false);
    expect(overlaps(fab, scrollDown)).toBe(false);
  });

  it('unionBox: пустые прямоугольники (скрытый элемент) не расширяют объединение', () => {
    expect(unionBox([])).toBeNull();
    expect(unionBox([box(0, 0, 0, 0), box(10, 20, 30, 40)])).toEqual(box(10, 20, 30, 40));
    expect(unionBox([box(16, 730, 374, 832), box(330, 672, 374, 716)])).toEqual(box(16, 672, 374, 832));
  });

  it('композер в весь экран — места нет, кнопка прячется', () => {
    const p = placeFab(box(0, 20, 390, 844), 374, 824, 390);
    expect(p.hidden).toBe(true);
  });
});

describe('placeFab: реальные контролы под кнопкой и запрет подъёма', () => {
  // Пробник DOM: контролы, пересекающие прямоугольник кандидата
  const probe = (...controls: Box[]) => (b: Box) => controls.filter(c => overlaps(c, b));

  it('неопубликованная кнопка во всю ширину в углу («Позже») — поднимается над ней', () => {
    // 390x844, препятствий нет; «Позже» 13..377 × 788..831 (замер Киры)
    const later = box(13, 788, 377, 831);
    const p = placeFab(null, 374, 824, 390, { controlsAt: probe(later) });
    expect(p.hidden).toBe(false);
    expect(p.raise).toBeGreaterThan(0);
    expect(overlaps(fabBox(p, 374, 824, 390), later)).toBe(false);
  });

  it('над поднятой кнопкой снова контрол — прячется, а не карабкается и не накрывает', () => {
    const later = box(13, 788, 377, 831);
    const meet = box(13, 740, 377, 780); // «Познакомиться» прямо над «Позже»
    const p = placeFab(null, 374, 824, 390, { controlsAt: probe(later, meet) });
    expect(p.hidden).toBe(true);
  });

  it('контрол только у полного круга — ужимается в углу, как от препятствия', () => {
    // Кнопка левее угла: полный круг (1526..1580) её задевает, ужатый (1544..1580) — нет
    const btn = box(1490, 840, 1530, 870);
    const p = placeFab(null, 1580, 880, 1600, { controlsAt: probe(btn) });
    expect(p).toEqual({ small: true, edge: false, raise: 0, hidden: false });
  });

  it('футер мастера с noRaise во всю ширину (телефон) — прячется, над футером не встаёт', () => {
    const footer = box(0, 790, 390, 844);
    const p = placeFab(footer, 374, 824, 390, { noRaise: true });
    expect(p).toEqual({ small: true, edge: false, raise: 0, hidden: true });
  });

  it('футер мастера с noRaise, но угол свободен ужатой (планшет) — ужатие, не прятки', () => {
    // Футер до 750: полный круг (746..800) с зазором задет, ужатый (764..800) — нет
    const footer = box(200, 1110, 750, 1180);
    const p = placeFab(footer, 800, 1160, 820, { noRaise: true });
    expect(p.hidden).toBe(false);
    expect(overlaps(fabBox(p, 800, 1160, 820), footer)).toBe(false);
  });
});

describe('stepFabSettle: гистерезис места', () => {
  // Места телефона из трасс Киры: над композером (647) и над «Вниз» (585)
  const low: FabPlacement = { small: true, edge: false, raise: 177, hidden: false };
  const high: FabPlacement = { small: true, edge: false, raise: 239, hidden: false };
  const edge: FabPlacement = { small: true, edge: true, raise: 0, hidden: false };
  const corner: FabPlacement = { small: true, edge: false, raise: 0, hidden: false };
  const shownAt = (p: FabPlacement): FabSettle => ({ shown: p, pending: null, since: 0, settleMs: FAB_SETTLE_MS });

  // Прогон последовательности замеров [мс, замер, что мешает показанному] → смены показанного места
  function run(s: FabSettle, steps: [number, FabPlacement, FabBlocker][]): FabPlacement[] {
    const changes: FabPlacement[] = [];
    for (const [t, m, b] of steps) {
      const { next } = stepFabSettle(s, m, b, t);
      if (next.shown !== s.shown) changes.push(next.shown);
      s = next;
    }
    return changes;
  }

  it('Д1: «Вниз» моргнула на прокрутке — кнопка не ходит туда-обратно, переезжает один раз', () => {
    // 585 → «Вниз» пропала (78 мс) → вернулась (112) → пропала насовсем (496); перезамер по таймеру
    const changes = run(shownAt(high), [
      [78, PLACE_HIDDEN, null], [96, low, null], [112, high, null],
      [496, low, null], [496 + FAB_SETTLE_MS, low, null],
    ]);
    expect(changes).toEqual([low]);
  });

  it('уход с безопасного места ждёт, пока устоит один и тот же замер', () => {
    const s = shownAt(high);
    const a = stepFabSettle(s, low, null, 1000);
    expect(a.next.shown).toBe(high);
    expect(a.recheckIn).toBe(FAB_SETTLE_MS);
    // Замер сменился до срока — отсчёт заново
    const b = stepFabSettle(a.next, corner, null, 1100);
    expect(b.next.shown).toBe(high);
    expect(b.recheckIn).toBe(FAB_SETTLE_MS);
    expect(stepFabSettle(b.next, corner, null, 1100 + FAB_SETTLE_MS).next.shown).toBe(corner);
  });

  it('препятствие наехало на показанное место — уходим сразу', () => {
    const { next, recheckIn } = stepFabSettle(shownAt(low), high, 'obstacle', 5);
    expect(next.shown).toBe(high);
    expect(recheckIn).toBeNull();
  });

  it('Д2: контрол под кнопкой в первом кадре экрана, через кадр ушёл в прокрутку — кнопка не мигает', () => {
    const changes = run(shownAt(corner), [[0, PLACE_HIDDEN, 'control'], [40, corner, null]]);
    expect(changes).toEqual([]);
  });

  it('контрол под кнопкой держится — прячемся после подтверждения, даже если замер менялся', () => {
    const changes = run(shownAt(corner), [
      [0, PLACE_HIDDEN, 'control'], [60, low, 'control'], [FAB_CONFIRM_MS, low, 'control'],
    ]);
    expect(changes).toEqual([low]);
  });

  it('Д3: после монтирования кнопка появляется один раз, уже на устоявшемся месте', () => {
    // Открытие по ссылке: карточка проекта 609 (275 мс) → скрыта → 742 → 683 → 647
    const s = initialFabSettle();
    const changes = run(s, [
      [0, high, null], [275, PLACE_HIDDEN, null], [292, { ...low, raise: 82 }, null],
      [300, { ...low, raise: 141 }, null], [488, low, null], [488 + FAB_MOUNT_SETTLE_MS, low, null],
    ]);
    expect(changes).toEqual([low]);
  });

  it('Д4: прижатая к краю кнопка не сходит с края посреди перехода отступа', () => {
    // Панель открылась: замер зовёт из режима края в угол, пока --cc-fab-inset ещё едет
    const changes = run(shownAt(edge), [[0, corner, null], [120, corner, null], [FAB_SETTLE_MS, corner, null]]);
    expect(changes).toEqual([corner]);
    expect(run(shownAt(edge), [[0, corner, null], [120, edge, null], [FAB_SETTLE_MS, corner, null]])).toEqual([]);
  });

  it('спрятанные места равны независимо от подъёма — пересчёт подъёма спрятанной не будит', () => {
    const s = shownAt(PLACE_HIDDEN);
    expect(stepFabSettle(s, { ...PLACE_HIDDEN, raise: 300 }, null, 0).next).toBe(s);
  });
});

describe('fabSpotBlocker: что мешает показанному месту', () => {
  const probe = (...controls: Box[]) => (b: Box) => controls.filter(c => overlaps(c, b));
  const composer = box(16, 730, 374, 832);

  it('спрятанной кнопке не мешает ничего', () => {
    expect(fabSpotBlocker(PLACE_HIDDEN, composer, 374, 824, 390)).toBeNull();
  });

  it('препятствие под поднятой кнопкой важнее контрола', () => {
    const p = placeFab(composer, 374, 824, 390);
    expect(fabSpotBlocker(p, composer, 374, 824, 390)).toBeNull();
    const grown = box(16, 600, 374, 832); // композер вырос
    expect(fabSpotBlocker(p, grown, 374, 824, 390, { controlsAt: probe(grown) })).toBe('obstacle');
  });

  it('контрол под местом из пробы — control', () => {
    expect(fabSpotBlocker(PLACE_CORNER, null, 374, 824, 390, { controlsAt: probe(box(13, 788, 377, 831)) })).toBe('control');
  });
});

describe('setFabObstacle: запрет подъёма', () => {
  afterEach(() => fabObstacleTestHooks.reset());
  const node = (name: string) => ({ name }) as unknown as HTMLElement;

  it('флаг noRaise живёт на узле и снимается переопубликацией без него', () => {
    const footer = node('footer');
    setFabObstacle(footer, 'main', { noRaise: true });
    expect(isFabNoRaise(footer)).toBe(true);
    setFabObstacle(footer);
    expect(isFabNoRaise(footer)).toBe(false);
  });
});

describe('setFabObstacle: владельцы слота', () => {
  // Сравнение идёт по ссылке — настоящий DOM-узел не нужен
  const node = (name: string) => ({ name }) as unknown as HTMLElement;

  // Стек и подписчики — модульное состояние: упавший посреди тест не должен тянуть
  // свои узлы в следующий
  afterEach(() => fabObstacleTestHooks.reset());

  it('повторная публикация того же узла не плодит дубль и поднимает его наверх', () => {
    const composer = node('composer');
    const footer = node('footer');
    setFabObstacle(composer);
    setFabObstacle(footer);
    setFabObstacle(composer); // композер вырос и переопубликовался
    expect(fabObstacleTestHooks.stack()).toEqual([footer, composer]);
    setFabObstacle(composer);
    expect(fabObstacleTestHooks.stack()).toEqual([footer, composer]);
    expect(getFabObstacles()).toEqual([composer]);
  });

  it('подписчик уведомляется только при смене вершины', () => {
    const composer = node('composer');
    const footer = node('footer');
    const f = vi.fn();
    subscribeFabObstacle(f);
    const releaseComposer = setFabObstacle(composer);
    expect(f).toHaveBeenCalledTimes(1);
    setFabObstacle(composer); // вершина та же
    expect(f).toHaveBeenCalledTimes(1);
    setFabObstacle(footer);
    expect(f).toHaveBeenCalledTimes(2);
    releaseComposer(); // снят узел не с вершины
    expect(f).toHaveBeenCalledTimes(2);
    setFabObstacle(node('scroll'), 'scroll-down'); // новый слот — вершина у него сменилась
    expect(f).toHaveBeenCalledTimes(3);
  });

  it('мастер персон поверх чата закрылся — композер чата остаётся препятствием', () => {
    const composer = node('composer');
    const footer = node('footer');
    const releaseComposer = setFabObstacle(composer);
    const releaseFooter = setFabObstacle(footer);
    expect(getFabObstacles()).toEqual([footer]);
    releaseFooter();
    expect(getFabObstacles()).toEqual([composer]);
    releaseComposer();
    expect(getFabObstacles()).toEqual([]);
  });

  it('чужое снятие не трогает действующий узел, слоты независимы', () => {
    const composer = node('composer');
    const footer = node('footer');
    const scrollDown = node('scroll-down');
    const releaseFooter = setFabObstacle(footer);
    const releaseComposer = setFabObstacle(composer);
    const releaseScroll = setFabObstacle(scrollDown, 'scroll-down');
    releaseFooter(); // мастер закрылся, пока сверху уже композер
    expect(getFabObstacles()).toEqual([composer, scrollDown]);
    releaseComposer();
    releaseScroll();
    expect(getFabObstacles()).toEqual([]);
  });
});
