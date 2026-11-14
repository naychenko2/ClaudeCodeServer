// W4-FB2: выбор вида нового чата у кнопки «Новый» в тулбаре списка чатов
// (решение Андрея 12.11; само создание приватного чата — W4-FB3).
// Держится обе стороны флага:
//  • без onNewPrivate (обычный проект, все, кому флаг не передан) — прежний путь:
//    клик по «Новый» зовёт onNew ровно раз, слов выбора в разметке нет вовсе;
//  • с onNewPrivate — клик открывает меню «Обычный / Приватный» из ui/Menu, пункт
//    зовёт ровно свой колбэк и закрывает меню; Esc и клик мимо закрывают без вызова.
// Рендер — настоящий компонент в fake-DOM (паттерн PrivateChatToggle.test.ts): меню
// живёт порталом в document.body, поэтому его пункты и подложку ищем от body.
// Ступень десктопа в тесте — cozy (ResizeObserver в node нет, ширина остаётся 288):
// это та же кнопка NewButton, что в comfort и в шапке панели; мобильная ветка
// проверяется отдельными кейсами по isMobile.
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) пункт «Приватный чат» зовёт onNew вместо onNewPrivate → кейс «приватный зовёт
//     onNewPrivate, обычный — нет» красный;
// (2) убрать ранний return в onClick (открывать меню и без onNewPrivate) → оба кейса
//     «без onNewPrivate» красные.
import { createElement } from 'react';
import { act } from 'react';
import type { ComponentProps } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { installFakeDom, mount, reactProps, textNodes, findNodeByAttr } from '../chat/__tests__/fakeDom';
import { defaultChatFilters } from '../../lib/chatFilters';
import { ChatListToolbar } from '../ChatListToolbar';

installFakeDom();
// Menu при закрытии по Esc возвращает фокус и проверяет якоря через `instanceof
// HTMLElement` (ui/Menu.tsx:117) — в shared fake-DOM этого глобала нет, и кейс умирал
// бы на ReferenceError внутри Menu, а не на моём поведении. Заглушка (тот же приём,
// что vi.stubGlobal в calendarSharedChip.test.ts): узлы fake-DOM ей не являются,
// поэтому возврат фокуса просто не происходит, а закрытие — проверяется как задумано.
vi.stubGlobal('HTMLElement', class HTMLElement {});

type Mount = ReturnType<typeof mount>;
const body = () => (globalThis as { document: any }).document.body;
// data-testid кнопки-триггера — один на все три ступени тулбара (NewButton)
const NEW_BTN = 'chat-new-button';

let current: Mount['root'] | null = null;

function render(opts: { onNewPrivate?: () => void; isMobile?: boolean } = {}) {
  const onNew = vi.fn();
  const onNewPrivate = opts.onNewPrivate;
  const props: ComponentProps<typeof ChatListToolbar> = {
    onNew,
    sessions: [],
    filters: defaultChatFilters(),
    patch: vi.fn(),
    allPersonas: [],
    hiddenCount: 0,
    isMobile: opts.isMobile ?? false,
  };
  // Без флага — ровно тот же вызов, что был до шага: поля нет вовсе
  if (onNewPrivate) props.onNewPrivate = onNewPrivate;
  const m = mount(createElement(ChatListToolbar, props));
  current = m.root;
  return { ...m, onNew, onNewPrivate };
}

// Клик по кнопке «Новый»: rect снимает сам обработчик, здесь — currentTarget кнопки
// (в fake-DOM getBoundingClientRect — нулевой прямоугольник, Menu строит портал в body)
function clickNew(m: Mount) {
  const btn = findNodeByAttr(m.container, 'data-testid', NEW_BTN);
  expect(btn, 'кнопка «Новый» найдена').not.toBeNull();
  act(() => { (reactProps(btn as never)!.onClick as (e: unknown) => void)({ currentTarget: btn }); });
}

// Пункт меню — кнопка, в поддереве которой есть текст её подписи (у MenuItem своей
// testid-метки нет; подписи пунктов уникальны в этой разметке)
function findItemByLabel(label: string): any | null {
  let hit: any | null = null;
  const walk = (n: any): void => {
    if (hit || !n) return;
    if (n.nodeType === 1 && n.nodeName === 'BUTTON' && textNodes(n).includes(label)) { hit = n; return; }
    for (const c of n.childNodes ?? []) walk(c);
  };
  walk(body());
  return hit;
}
function clickItem(label: string) {
  const node = findItemByLabel(label);
  expect(node, `пункт «${label}» найден`).not.toBeNull();
  act(() => { (reactProps(node as never)!.onClick as (e: unknown) => void)({ currentTarget: node }); });
}

// Подложка «клика мимо» — единственный fixed-инсет див под body (ui/Menu.tsx:163);
// React пишет стиль как объект с строковыми значениями, поэтому сравнение по полям
function clickBackdrop() {
  let hit: any | null = null;
  const walk = (n: any): void => {
    if (hit || !n) return;
    if (n.nodeType === 1 && String(n.style?.position) === 'fixed' && String(n.style?.inset) === '0') { hit = n; return; }
    for (const c of n.childNodes ?? []) walk(c);
  };
  walk(body());
  expect(hit, 'подложка меню найдена').not.toBeNull();
  act(() => { (reactProps(hit as never)!.onClick as () => void)(); });
}

// document.listeners — настоящая подписка fake-DOM: без неё кейс про Esc проверял бы
// вакуум (обработчика нет — и «закрылось» верно). Зовём всех слушателей keydown, как
// это делает браузер: реагирует только тот, чьё окно сейчас открыто
function pressKey(key: string) {
  const doc = (globalThis as { document: any }).document;
  const handlers = [...(doc.listeners['keydown'] ?? new Set<unknown>())];
  expect(handlers.length, 'на document есть хотя бы обработчик клавиш').toBeGreaterThan(0);
  act(() => {
    for (const h of handlers) (h as (e: unknown) => void)({ key, defaultPrevented: false, preventDefault: () => {} });
  });
}

const menuIsOpen = () => findItemByLabel('Приватный чат') !== null;

afterEach(() => {
  const r = current;
  current = null;
  if (r) act(() => { r.unmount(); });
});

describe('без onNewPrivate — поведение прежнее (обычный проект, все ступени)', () => {
  for (const isMobile of [false, true]) {
    it(`клик по «Новый» (isMobile=${isMobile}) — ровно один onNew, выбора нет`, () => {
      const m = render({ isMobile });
      clickNew(m);
      expect(m.onNew).toHaveBeenCalledTimes(1);
      expect(textNodes(m.container).join(' ')).not.toContain('Приватный');
      expect(textNodes(body()).join(' ')).not.toContain('Приватный');
      expect(menuIsOpen()).toBe(false);
      // Кнопка без выбора не disclosure: атрибута нет вовсе (тот же DOM, что и до шага)
      expect(findNodeByAttr(m.container, 'data-testid', NEW_BTN)!.getAttribute('aria-expanded')).toBeNull();
    });
  }
});

describe('с onNewPrivate — выбор вида чата в меню', () => {
  for (const isMobile of [false, true]) {
    it(`клик по «Новый» (isMobile=${isMobile}) открывает оба пункта с пояснениями`, () => {
      const m = render({ onNewPrivate: vi.fn(), isMobile });
      clickNew(m);
      const text = textNodes(body()).join(' ');
      expect(text).toContain('Обычный чат');
      expect(text).toContain('Видят участники проекта');
      expect(text).toContain('Приватный чат');
      expect(text).toContain('Видите только вы');
      // Пока меню открыто — ни один чат ещё не создан
      expect(m.onNew).not.toHaveBeenCalled();
      expect(m.onNewPrivate).not.toHaveBeenCalled();
      // …и триггер читается как раскрытый
      expect(findNodeByAttr(m.container, 'data-testid', NEW_BTN)!.getAttribute('aria-expanded')).toBe('true');
    });

    it(`«Приватный чат» (isMobile=${isMobile}) — только onNewPrivate, и меню закрыто`, () => {
      const onNewPrivate = vi.fn();
      const m = render({ onNewPrivate, isMobile });
      clickNew(m);
      clickItem('Приватный чат');
      expect(onNewPrivate).toHaveBeenCalledTimes(1);
      expect(m.onNew).not.toHaveBeenCalled();
      expect(menuIsOpen()).toBe(false);
    });

    it(`«Обычный чат» (isMobile=${isMobile}) — только onNew, и меню закрыто`, () => {
      const onNewPrivate = vi.fn();
      const m = render({ onNewPrivate, isMobile });
      clickNew(m);
      clickItem('Обычный чат');
      expect(m.onNew).toHaveBeenCalledTimes(1);
      expect(onNewPrivate).not.toHaveBeenCalled();
      expect(menuIsOpen()).toBe(false);
    });

    it(`Esc (isMobile=${isMobile}) — меню закрыто, ни один чат не создан`, () => {
      const m = render({ onNewPrivate: vi.fn(), isMobile });
      clickNew(m);
      expect(menuIsOpen()).toBe(true);
      pressKey('Escape');
      expect(menuIsOpen()).toBe(false);
      expect(m.onNew).not.toHaveBeenCalled();
      expect(m.onNewPrivate).not.toHaveBeenCalled();
    });

    it(`клик мимо (isMobile=${isMobile}) — меню закрыто, ни один чат не создан`, () => {
      const m = render({ onNewPrivate: vi.fn(), isMobile });
      clickNew(m);
      expect(menuIsOpen()).toBe(true);
      clickBackdrop();
      expect(menuIsOpen()).toBe(false);
      expect(m.onNew).not.toHaveBeenCalled();
      expect(m.onNewPrivate).not.toHaveBeenCalled();
    });
  }
});
