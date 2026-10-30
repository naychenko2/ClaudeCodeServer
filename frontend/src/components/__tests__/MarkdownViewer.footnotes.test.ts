// S5e + доработка по ревью 684d71c0: сноски вики в общем компоненте — только по явному
// включению. БЕЗ пропа wikiFootnotes [^id] не трогается вовсе: обычную GFM-сноску целиком
// и байт-в-байт рендерит remark-gfm (маркер sup > a + секция Footnotes) — регресс всех
// заметок (NoteView/DocComments/manifest). С пропом перехватываются только [^id] с записью
// в карте, данные (цитата + ref источника) приходят от вызывающего — компонент сам их из
// текста не выводит. Поповер с интерактивным содержимым — role=dialog: на десктопе открывается
// наведением и закрывается с задержкой (панель продлевает жизнь сама), клик по маркеру
// пришпиливает, повторный клик закрывает; при открытии кликом/Enter фокус уходит внутрь
// панели, Esc закрывает и возвращает его маркеру; на мобильной шторке — focus-trap (Tab не
// уходит за неё). Битый percent-encoding в схеме (fnref:%zz, wikilink:%zz) не роняет рендер.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) «перехватывать [^id] всегда» (не сверять с картой) → краснеет «без пропа… GFM…»;
//  (2) «закрывать сразу на mouseleave» (без задержки) → краснеет «поповер живой, пока…»;
//  (3) «фокус не переносить» (убрать перенос внутрь панели) → краснеет «Enter переносит…».
// Прежние мутации S5e (белый список целей, рендер маркера) остаются в силе: тесты на них
// в этом же файле.

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
type WikiMap = Record<string, { quote: string; source: string }>;
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

function render(content: string, wiki?: WikiMap): Node {
  const m = mount(createElement(MarkdownViewer, { content, onWikilink: (t: string) => { wikilinkSpy(t); }, wikiFootnotes: wiki }));
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

// Наведение мыши: вход в маркер/панель и уход из них — те же пропсы, что ставит компонент
function hover(el: Node): void {
  const p = reactProps(el);
  expect(p).toBeTruthy();
  act(() => { (p!.onMouseEnter as (e: unknown) => void)({ currentTarget: el }); });
}
function unhover(el: Node): void {
  const p = reactProps(el);
  expect(p).toBeTruthy();
  act(() => { (p!.onMouseLeave as (e: unknown) => void)({ currentTarget: el }); });
}

// Tab внутри панели: возвращаем, был ли preventDefault (focus-trap держит фокус внутри)
function pressTab(el: Node): boolean {
  const p = reactProps(el);
  expect(p).toBeTruthy();
  let prevented = false;
  act(() => { (p!.onKeyDown as (e: unknown) => void)({ key: 'Tab', shiftKey: false, preventDefault: () => { prevented = true; } }); });
  return prevented;
}

// Esc — как человек: клавиатура не привязана к маркеру, обработчик висит на document
function pressEscape(): void {
  const doc = (globalThis as unknown as { document: { listeners?: Record<string, Set<(e: unknown) => void>> } }).document;
  act(() => { for (const fn of [...(doc.listeners?.keydown ?? [])]) fn({ key: 'Escape' }); });
}

// Кто сейчас в фокусе: fakeDom двигает document.activeElement из focus()
function docActive(): Node | null {
  return (globalThis as unknown as { document: { activeElement: Node | null } }).document.activeElement;
}

// Реальные таймеры: задержка закрытия поповера (250 мс) меряется как в браузере
async function wait(ms: number): Promise<void> {
  await act(async () => { await new Promise(r => setTimeout(r, ms)); });
}

const CHAT_PAGE = [
  'Решение принято в чате.[^s1]',
  '',
  '[^s1]: session:abc#m3-7 — «дословная цитата из чата»',
].join('\n');
// Те же данные, что передаёт вызывающий вики-страницы (решение по ревью: компонент сам
// их из текста не выводит — форма цели в странице бэком не гарантирована)
const CHAT_MAP: WikiMap = { s1: { quote: 'дословная цитата из чата', source: 'session:abc#m3-7' } };

// Обычная GFM-сноска: определение без цитаты — не синтаксис вики
const GFM_PAGE = [
  'Решение принято[^1]',
  '',
  '[^1]: Обычное пояснение',
].join('\n');

describe('MarkdownViewer: сноски (S5e + доработка)', () => {
  it('регресс: без пропа [^id] не трогается — GFM рендерит сноску сама (sup > a + секция)', () => {
    const container = render(GFM_PAGE);
    expect(findNodeByAttr(container, 'data-testid', 'fn-ref-1'), 'без карты компонента не должно быть своего маркера').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'md-sources'), 'блок «Источники» — только с картой').toBeNull();
    const sup = container.querySelectorAll('sup');
    expect(sup.length, 'маркера sup для сноски нет').toBe(1);
    const links = sup[0].querySelectorAll('a');
    expect(links.length, 'внутри sup нет ссылки').toBe(1);
    expect(links[0].getAttribute('href'), 'ссылка не ведёт к определению').toBe('#user-content-fn-1');
    const sections = container.querySelectorAll('section');
    expect(sections.length, 'секции Footnotes нет').toBe(1);
    expect(textNodes(sections[0]).join('')).toContain('Обычное пояснение');
  });

  it('с пропом и неизвестным id — то же GFM: своего маркера нет', () => {
    const container = render(GFM_PAGE, { s2: { quote: 'чужая цитата', source: 'session:x#m1-2' } });
    expect(findNodeByAttr(container, 'data-testid', 'fn-ref-1'), 'id вне карты не перехватывается').toBeNull();
    expect(container.querySelectorAll('sup').length).toBe(1);
    // GFM-секция определения жива (своих блоков поверх неё может быть больше — это не ломает)
    const sections = container.querySelectorAll('section');
    expect(sections.some(s => textNodes(s).join('').includes('Обычное пояснение'))).toBe(true);
  });

  it('с картой: маркер в тексте; поповер с цитатой; «Открыть в чате» целит чат abc', () => {
    const container = render(CHAT_PAGE, CHAT_MAP);
    const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-s1');
    expect(marker, 'маркера сноски в тексте нет').toBeTruthy();
    expect(textNodes(marker!).join('')).toBe('1'); // нумерацию sN показываем числом (макет)
    click(marker!);
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    expect(pop, 'поповер не открылся').toBeTruthy();
    expect(pop!.getAttribute('role'), 'содержимое интерактивно — это не tooltip').toBe('dialog');
    expect(textNodes(pop!).join('')).toContain('«дословная цитата из чата»');
    expect(textNodes(pop!).join('')).toContain('#m3-7');
    const btn = findNodeByAttr(pop!, 'data-testid', 'fn-open-chat-s1');
    expect(btn, 'кнопки «Открыть в чате» нет').toBeTruthy();
    click(btn!);
    expect(openChatSpy).toHaveBeenCalledWith('abc');
  });

  it('определение не остаётся в тексте; «Источники» собраны из карты вызывающего', () => {
    const container = render(CHAT_PAGE, CHAT_MAP);
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

  it('цель task: — ссылка задачи, открывает её в её разделе', async () => {
    getTaskSpy.mockResolvedValue({ id: 'x123', projectId: 'p1' });
    const md = ['Снято по задаче.[^s2]', '', '[^s2]: task:x123 — «цитата из итога задачи»'].join('\n');
    const container = render(md, { s2: { quote: 'цитата из итога задачи', source: 'task:x123' } });
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
    const container = render(md, { s3: { quote: 'три', source: 'javascript:alert(1)' } });
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
    const container = render(md, { s5: { quote: 'цитата из заметки', source: '[[Некоторая Заметка]]' } });
    click(findNodeByAttr(container, 'data-testid', 'fn-ref-s5')!);
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    const link = findNodeByAttr(pop!, 'data-testid', 'fn-open-note-s5');
    expect(link, 'ссылки на заметку нет').toBeTruthy();
    click(link!);
    expect(wikilinkSpy).toHaveBeenCalledWith('Некоторая Заметка');
  });

  it('сырой <script> в цитате сноски не становится DOM-узлом', () => {
    const md = ['Текст.[^s4]', '', '[^s4]: session:abc#m1-2 — «в цитате <script>alert(1)</script> код»'].join('\n');
    const container = render(md, { s4: { quote: 'в цитате <script>alert(1)</script> код', source: 'session:abc#m1-2' } });
    click(findNodeByAttr(container, 'data-testid', 'fn-ref-s4')!);
    expect(textNodes(bodyRoot()).join('')).toContain('<script>alert(1)</script>');
    expect(bodyRoot().querySelectorAll('script')).toHaveLength(0);
  });

  it('сноска в code-заборе не обрабатывается (зеркало бэкового контракта)', () => {
    // даже с картой в заборе нет маркера: определение в коде — пример синтаксиса, не сноска
    const md = ['Пример:', '```', '[^s1]: session:x#m1-2 — «q»', '```'].join('\n');
    const container = render(md, { s1: { quote: 'q', source: 'session:x#m1-2' } });
    expect(findNodeByAttr(container, 'data-testid', 'fn-ref-s1'), 'маркер из кода не ожил').toBeNull();
    expect(textNodes(container).join('')).toContain('[^s1]');
  });

  it('ссылка без определения и без карты не оживает: своего маркера нет', () => {
    const container = render('Текст со ссылкой[^s9] на ничто.');
    expect(findNodeByAttr(container, 'data-testid', 'fn-ref-s9'), 'маркера без определения нет').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'md-sources'), 'блока без карты нет').toBeNull();
  });

  it('Enter переносит фокус внутрь панели; Esc закрывает и возвращает его маркеру', () => {
    const container = render(CHAT_PAGE, CHAT_MAP);
    const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-s1');
    expect(marker).toBeTruthy();
    keyDown(marker!, 'Enter');
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    expect(pop, 'Enter не открыл поповер').toBeTruthy();
    expect(docActive(), 'фокус не перенесён в панель').toBe(pop);
    const desc = marker!.getAttribute('aria-describedby');
    expect(desc, 'маркер не связан с поповером').toBeTruthy();
    expect(pop!.getAttribute('id'), 'маркер описывает id не своей панели').toBe(desc);
    pressEscape();
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'Esc не закрыл поповер').toBeNull();
    expect(marker!.getAttribute('aria-describedby'), 'связь не снята после закрытия').toBeNull();
    expect(docActive(), 'фокус не вернулся маркеру').toBe(marker);
  });

  it('наведение открывает; уход мыши закрывает с задержкой, а поповер живой, пока курсор в нём', async () => {
    const container = render(CHAT_PAGE, CHAT_MAP);
    const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-s1')!;
    hover(marker);
    const pop = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover');
    expect(pop, 'наведение не открыло поповер').toBeTruthy();
    expect(docActive(), 'наведение не должно двигать клавиатурный фокус').not.toBe(pop);
    unhover(marker);
    await wait(120);
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'поповер закрылся раньше задержки').toBeTruthy();
    // курсор дошёл до панели — таймер снят, кнопка внутри достижима
    hover(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover')!);
    await wait(400);
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'поповер закрылся, хотя курсор в нём').toBeTruthy();
    // и всё-таки закрывается, когда курсор ушёл
    unhover(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover')!);
    await wait(400);
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'уход из поповера не закрыл его').toBeNull();
  });

  it('клик пришпиливает поповер: уход мыши его не закрывает; повторный клик закрывает и возвращает фокус', async () => {
    const container = render(CHAT_PAGE, CHAT_MAP);
    const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-s1')!;
    click(marker);
    expect(docActive(), 'фокус после клика не перенесён в панель').toBe(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'));
    unhover(marker);
    await wait(400);
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'пришпиленный поповер закрылся уходом мыши').toBeTruthy();
    click(marker); // повторный клик по маркеру — закрывает
    expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'повторный клик не закрыл поповер').toBeNull();
    expect(docActive(), 'фокус не вернулся маркеру').toBe(marker);
  });

  it('мобильный тап открывает шторку с фокусом внутри; Tab не уходит за неё; тап вне закрывает', () => {
    const win = (globalThis as unknown as { window: { matchMedia: unknown } }).window;
    const prev = win.matchMedia;
    win.matchMedia = () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    try {
      const container = render(CHAT_PAGE, CHAT_MAP);
      const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-s1')!;
      click(marker);
      const sheet = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-sheet');
      expect(sheet, 'шторка не открылась').toBeTruthy();
      expect(sheet!.getAttribute('role')).toBe('dialog');
      expect(docActive(), 'фокус не перенесён в шторку').toBe(sheet);
      expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-popover'), 'на мобиле — шторка, не карточка').toBeNull();
      // focus-trap: с последнего фокусируемого Tab.wrap — к первому, фокус остаётся внутри
      const btn = findNodeByAttr(sheet!, 'data-testid', 'fn-open-chat-s1')!;
      btn.focus();
      expect(pressTab(sheet!), 'Tab с последнего элемента ушёл за пределы шторки').toBe(true);
      expect(docActive(), 'focus-trap отдал фокус наружу').toBe(btn);
      const scrim = findNodeByAttr(bodyRoot(), 'data-testid', 'fn-scrim');
      expect(scrim, 'затемнения вне шторки нет').toBeTruthy();
      click(scrim!);
      expect(findNodeByAttr(bodyRoot(), 'data-testid', 'fn-sheet'), 'тап вне не закрыл шторку').toBeNull();
    } finally {
      win.matchMedia = prev;
    }
  });

  it('битый percent-encoding схемы не роняет рендер: fnref:%zz и wikilink:%zz', () => {
    // литеральная ссылка-схема в тексте (или битый id) — decode бросает URIError: рендер жив,
    // значение остаётся сырым
    const container = render('См. [метка](fnref:%zz) и [путь](wikilink:%zz).', { '%zz': { quote: 'q', source: 'session:x#m1-2' } });
    const marker = findNodeByAttr(container, 'data-testid', 'fn-ref-%zz');
    expect(marker, 'маркера fnref:%zz нет').toBeTruthy();
    const links = container.querySelectorAll('a');
    expect(links.some(a => textNodes(a).join('') === 'путь'), 'wikilink:%zz выпал из рендера').toBe(true);
  });

  it('длинная неразрывная цитата не ломает вёрстку: overflow-wrap anywhere', () => {
    const long = 'а'.repeat(300);
    const md = ['Длинное.[^s6]', '', `[^s6]: session:abc#m1-2 — «${long}»`].join('\n');
    const container = render(md, { s6: { quote: long, source: 'session:abc#m1-2' } });
    const q = findNodeByAttr(container, 'data-testid', 'fn-quote-s6');
    expect(q, 'котировки в строке источника нет').toBeTruthy();
    expect(q!.style.overflowWrap, 'цитата не защищена от переполнения').toBe('anywhere');
  });
});
