// Проба DOM под кандидатом места круглешка AI (для placeFab.controlsAt): какие реальные
// контролы лежат под ним, хотя их никто не опубликовал препятствием.
import type { Box } from './fabObstacle';

// Контролы, которые кнопка не вправе накрыть: всё, что принимает нажатие или ввод
export const FAB_CONTROL_SEL = [
  'button', 'input', 'textarea', 'select', 'a[href]', 'summary', 'label',
  '[role=button]', '[role=menuitem]', '[role=tab]', '[role=link]', '[role=switch]', '[role=checkbox]',
  '[contenteditable]:not([contenteditable=false])',
].join(', ');
// Сетка проб по прямоугольнику кандидата: 5×5 точек с шагом ~8 px на ужатом круге —
// мельче самых мелких кнопок (22 px), поэтому не проскочит ни одна
const FAB_PROBE_GRID = 5;
// Запас пробы за кромку круга: кнопка не встаёт к контролу впритык
const FAB_PROBE_PAD = 4;

const SCROLLS = new Set(['auto', 'scroll', 'overlay']);

// Контрол, которому кнопка не мешает:
//  - невидимый: прозрачен он сам или предок (действия строки, проявляющиеся по наведению) —
//    elementsFromPoint такие узлы видит, а человек нет;
//  - прокручиваемый: лежит внутри реально прокручиваемого контейнера (переполнен по
//    вертикали) — его отлистывают из-под кнопки, как под любым FAB. Учитывай мы ленту
//    чата, кнопка мигала бы в такт прокрутке.
// Прилипший (sticky) или fixed контрол внутри скроллера с содержимым не уезжает — это
// футер шторки или модалки, он прибит, как и всё вне скроллеров.
export function fabIgnoresControl(el: Element): boolean {
  return ignoresControlIn(el, getComputedStyle, document.body);
}

// Ядро fabIgnoresControl без глобального DOM: узлы и их стили приходят параметрами (тесты
// гоняют его на макетах — DOM-окружения у vitest в проекте нет). root — граница подъёма
export interface ProbeNode { parentElement: ProbeNode | null; scrollHeight: number; clientHeight: number }
export interface ProbeStyle { opacity: string; position: string; overflowY: string }
export function ignoresControlIn<N extends ProbeNode>(el: N, styleOf: (n: N) => ProbeStyle, root: N | null): boolean {
  let pinned = false;
  let scrolls = false;
  for (let n: N | null = el; n && n !== root; n = n.parentElement as N | null) {
    const cs = styleOf(n);
    if (cs.opacity === '0') return true;
    if (scrolls) continue; // выше скроллера ищем только прозрачность
    const parent = n.parentElement as N | null;
    if (cs.position === 'sticky' || cs.position === 'fixed') pinned = true;
    if (!pinned && parent && parent !== root && parent.scrollHeight > parent.clientHeight + 1
      && SCROLLS.has(styleOf(parent).overflowY)) scrolls = true;
  }
  return scrolls;
}

// Реальные контролы под прямоугольником кандидата. Сама кнопка и её балуны помечены
// data-cc-fab и не в счёт — иначе кнопка мешала бы сама себе. elementsFromPoint видит
// только то, что принимает указатель: visibility: hidden и pointer-events: none не мешают.
export function fabControlsAt(b: Box): Box[] {
  const seen = new Set<Element>();
  const out: Box[] = [];
  const l = b.left - FAB_PROBE_PAD, t = b.top - FAB_PROBE_PAD;
  const w = b.right - b.left + 2 * FAB_PROBE_PAD, h = b.bottom - b.top + 2 * FAB_PROBE_PAD;
  for (let i = 0; i < FAB_PROBE_GRID; i++) for (let j = 0; j < FAB_PROBE_GRID; j++) {
    const x = l + 1 + (w - 2) * i / (FAB_PROBE_GRID - 1);
    const y = t + 1 + (h - 2) * j / (FAB_PROBE_GRID - 1);
    for (const el of document.elementsFromPoint(x, y)) {
      const c = el.closest(FAB_CONTROL_SEL);
      if (!c || seen.has(c)) continue;
      seen.add(c);
      if (c.closest('[data-cc-fab]') || fabIgnoresControl(c)) continue;
      out.push(c.getBoundingClientRect());
    }
  }
  return out;
}
