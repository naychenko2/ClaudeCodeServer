// S5e: сноски в MarkdownViewer (ADR-035 §12б, макет M2). [^sN] в тексте — маркер accent:
// клик/Enter открывает поповер с цитатой и источником; определения выносятся из потока текста,
// блок «Источники» собирается из карточки. Цель — только по белому списку форм: session:…#m…
// → «Открыть в чате» (якоря сообщения в маршруте чата во фронте ещё нет — открывается сам чат),
// task:… → ссылка задачи, [[…]]/путь → переход через onWikilink; javascript:, data:, //host —
// остаются текстом. Содержимое недоверенно: сырой <script> в цитате не становится DOM-узлом,
// длинные неразрывные цитаты не ломают вёрстку (overflow-wrap).
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) не сверять цель с белым списком (любая цель становится ссылкой) → краснеет
//      «неизвестная цель не становится ссылкой»;
//  (2) не рендерить маркер fnref (ссылка остаётся текстом) → краснеет «маркер в тексте…».
// Примечание: «не выносить определение из потока» мутацией не проверялось — в текущем
// пайплайне (remark-rehype 11.1.2) footnoteDefinition без нашего выноса молча выбрасывается
// и в тексте не появляется; вынос — гарантия поведения и корректных офсетов blockPos, а не
// видимый поведенческий дифф.

import { act } from 'react';
import { createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../chat/__tests__/fakeDom';
import { MarkdownViewer } from '../MarkdownViewer';

const openChatSpy = vi.hoisted(() => vi.fn());
const getTaskSpy = vi.hoisted(() => vi.fn());
const openTaskSpy = vi.hoisted(() => vi.fn());
const wikilinkSpy = vi.hoisted(() => vi.fn());

// Транспорт переходов мокан: проверяется решение «куда идти» (id чата, задача, цель wiki),
// а не сам переход (тот же приём, что в TaskCreatedView.test с openTaskInSection).
vi.mock('../../lib/openChat', () => ({ openChatById: openChatSpy }));
vi.mock('../../lib/api', () => ({ api: { tasks: { get: (id: string) => getTaskSpy(id) } } }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: openTaskSpy }));

type Node = NonNullable<ReturnType<typeof findNodeByAttr>>;
let root: Root | null = null;

beforeEach(() => {
  installFakeDom();
  openChatSpy.mockClear();
  getTaskSpy.mockClear();
  openTaskSpy.mockClear();
  wikilinkSpy.mockClear();
});
afterEach(() => {
  if (root) act(() => { root!.unmount(); });
  root = null;
  uninstallFakeDom();
});

function render(content: string): Node {
  const m = mount(createElement(MarkdownViewer, { content, onWikilink: (t: string) => { wikilinkSpy(t); } }));
  root = m.root;
  return m.container;
}

// Порталы (поповер, шторка) живут на document.body, не в контейнере монтирования
function bodyRoot(): Node {
  return (globalThis as unknown as { document: { body: Node } }).document.body;
}

function click(el: Node): void {
  const p = reactProps(el);
  expect(p, `у узла ${el.getAttribute('data-testid') ?? el.nodeName} нет props`).toBeTruthy();
  act(() => { (p!.onClick as (e: unknown) => void)({ preventDefault: () => {}, currentTarget: el }); });
}

function keyDown(el: Node, key: string): void {
  const p = reactProps(el);
  expect(p).toBeTruthy();
  act(() => { (p!.onKeyDown as (e: unknown) => void)({ key, preventDefault: () => {}, currentTarget: el }); });
}

// Esc — как человек: клавиатура не привязана к маркеру, обработчик висит на document
function pressEscape(): void {
  const doc = (globalThis as unknown as { document: { listeners?: Record<string, Set<(e: unknown) => void>> } }).document;
  act(() => { for (const fn of [...(doc.listeners?.keydown ?? [])]) fn({ key: 'Escape' }); });
}

const CHAT_PAGE = [
  'Решение принято в чате.[^s1]',
  '',
  '[^s1]: session:abc#m3-7 — «дословная цитата из чата»',
].join('\n');

describe('MarkdownViewer: сноски (S5e)', () => {
  it('маркер в тексте; поповер с цитатой; «Открыть в чате» целит чат abc', () => {
    const container = render(CHAT_PAGE);
    const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-s1');
    expect(marker, 'маркера сноски в тексте нет').toBeTruthy();
    expect(textNodes(marker!).join('')).toBe('1'); // нумерацию sN показываем числом (макет)
    click(marker!);
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    expect(pop, 'поповер не открылся').toBeTruthy();
    expect(textNodes(pop!).join('')).toContain('«дословная цитата из чата»');
    expect(textNodes(pop!).join('')).toContain('#m3-7');
    const btn = findNodeByAttr(pop!, 'data-testid', 'fn-open-chat-s1');
    expect(btn, 'кнопки «Открыть в чате» нет').toBeTruthy();
    click(btn!);
    expect(openChatSpy).toHaveBeenCalledWith('abc');
  });

  it('цель task: — ссылка задачи, открывает её в её разделе', async () => {
    getTaskSpy.mockResolvedValue({ id: 'x123', projectId: 'p1' });
    const md = ['Снято по задаче.[^s2]', '', '[^s2]: task:x123 — «цитата из итога задачи»'].join('\n');
    const container = render(md);
    click(findNodeByAttr(container, 'data-testid', 'fn-ref-s2')!);
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    const link = findNodeByAttr(pop!, 'data-testid', 'fn-open-task-s2');
    expect(link, 'ссылки-карточки задачи нет').toBeTruthy();
    click(link!);
    await act(async () => { await Promise.resolve(); });
    expect(getTaskSpy).toHaveBeenCalledWith('x123');
    expect(openTaskSpy).toHaveBeenCalledWith({ id: 'x123', projectId: 'p1' });
  });

  it('неизвестная цель не становится ссылкой: javascript: остаётся текстом', () => {
    const md = ['Что-то.[^s3]', '', '[^s3]: javascript:alert(1) — «три»'].join('\n');
    const container = render(md);
    click(findNodeByAttr(container, 'data-testid', 'fn-ref-s3')!);
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    expect(pop, 'поповер не открылся').toBeTruthy();
    expect(textNodes(pop!).join('')).toContain('javascript:alert(1)');
    expect(findNodeByAttr(pop!, 'data-testid', 'fn-open-chat-s3')).toBeNull();
    expect(findNodeByAttr(pop!, 'data-testid', 'fn-open-task-s3')).toBeNull();
    expect(findNodeByAttr(pop!, 'data-testid', 'fn-open-note-s3')).toBeNull();
  });

  it('цель-заметка [[…]] — переход через onWikilink', () => {
    const md = ['См. в вики.[^s5]', '', '[^s5]: [[Некоторая Заметка]] — «цитата из заметки»'].join('\n');
    const container = render(md);
    click(findNodeByAttr(container, 'data-testid', 'fn-ref-s5')!);
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    const link = findNodeByAttr(pop!, 'data-testid', 'fn-open-note-s5');
    expect(link, 'ссылки на заметку нет').toBeTruthy();
    click(link!);
    expect(wikilinkSpy).toHaveBeenCalledWith('Некоторая Заметка');
  });

  it('определение не остаётся в тексте; блок «Источники» собран из карточки', () => {
    const container = render(CHAT_PAGE);
    const txt = textNodes(container).join('');
    expect(txt, 'сырое определение осталось в тексте').not.toContain('[^s1]');
    // цель в тексте страницы не печатается сырой: для чата — «чат #m3-7», не «session:…»
    expect(txt, 'определение рендерится в потоке, а не вынесено в блок').not.toContain('session:abc#m3-7');
    expect(findNodeByAttr(container, 'data-testid', 'md-sources'), 'блока «Источники» нет').toBeTruthy();
    const def = findNodeByAttr(container, 'data-testid', 'fn-def-s1');
    expect(def, 'строки источника нет').toBeTruthy();
    expect(textNodes(def!).join('')).toContain('«дословная цитата из чата»');
    expect(findNodeByAttr(def!, 'data-testid', 'fn-open-chat-s1'), 'действия в строке источника нет').toBeTruthy();
  });

  it('сырой <script> в цитате сноски не становится DOM-узлом', () => {
    const md = ['Текст.[^s4]', '', '[^s4]: session:abc#m1-2 — «в цитате <script>alert(1)</script> код»'].join('\n');
    const container = render(md);
    click(findNodeByAttr(container, 'data-testid', 'fn-ref-s4')!);
    expect(textNodes(bodyRoot()).join('')).toContain('<script>alert(1)</script>');
    expect(bodyRoot().querySelectorAll('script')).toHaveLength(0);
  });

  it('сноска в code-заборе не обрабатывается (зеркало бэкового контракта)', () => {
    const md = ['Пример:', '```', '[^s1]: session:x#m1-2 — «q»', '```'].join('\n');
    const container = render(md);
    expect(findNodeByAttr(container, 'data-testid', 'fn-def-s1'), 'определение из кода не вынеслось').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'md-sources'), 'блока «Источники» из кода нет').toBeNull();
    expect(textNodes(container).join('')).toContain('[^s1]');
  });

  it('ссылка без определения не оживает: остаётся исходным текстом', () => {
    const container = render('Текст со ссылкой[^s9] на ничто.');
    expect(findNodeByAttr(container, 'data-testid', 'fn-ref-s9'), 'маркера без определения нет').toBeNull();
    expect(textNodes(container).join('')).toContain('[^s9]');
  });

  it('поповер открывается с Enter, закрывается с Esc; aria-describedby связывает маркер, фокус не теряется', () => {
    const container = render(CHAT_PAGE);
    const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-s1');
    expect(marker).toBeTruthy();
    keyDown(marker!, 'Enter');
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'Enter не открыл поповер').toBeTruthy();
    expect(marker!.getAttribute('aria-describedby'), 'маркер не связан с поповером').toBe('cc-fn-popover');
    let focused = 0;
    (marker as Node & { focus: () => void }).focus = () => { focused += 1; };
    pressEscape();
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'Esc не закрыл поповер').toBeNull();
    expect(marker!.getAttribute('aria-describedby'), 'связь не снята после закрытия').toBeNull();
    expect(focused, 'фокус не вернулся маркеру').toBe(1);
  });

  it('мобильный тап открывает шторку; тап вне (затемнение) закрывает', () => {
    const win = (globalThis as unknown as { window: { matchMedia: unknown } }).window;
    const prev = win.matchMedia;
    win.matchMedia = () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    try {
      const container = render(CHAT_PAGE);
      click(findNodeByAttr(container, 'data-testid', 'fn-ref-s1')!);
      const sheet = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-sheet');
      expect(sheet, 'шторка не открылась').toBeTruthy();
      expect(sheet!.getAttribute('role')).toBe('dialog');
      expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'на мобиле — шторка, не карточка').toBeNull();
      const scrim = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-scrim');
      expect(scrim, 'затемнения вне шторки нет').toBeTruthy();
      click(scrim!);
      expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-sheet'), 'тап вне не закрыл шторку').toBeNull();
    } finally {
      win.matchMedia = prev;
    }
  });

  it('длинная неразрывная цитата не ломает вёрстку: overflow-wrap anywhere', () => {
    const long = 'а'.repeat(300);
    const md = ['Длинное.[^s6]', '', `[^s6]: session:abc#m1-2 — «${long}»`].join('\n');
    const container = render(md);
    const q = findNodeByAttr(container, 'data-testid', 'fn-quote-s6');
    expect(q, 'котировки в строке источника нет').toBeTruthy();
    expect(q!.style.overflowWrap, 'цитата не защищена от переполнения').toBe('anywhere');
  });
});
