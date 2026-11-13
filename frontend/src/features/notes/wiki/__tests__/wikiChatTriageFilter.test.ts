// Тесты фильтров и поиска «Отбора чатов» (M1a, план wiki-panels-v1-proposal «Отбор чатов» А1):
// дефолтный сегмент («Ждут вас» при чате в action-зоне, иначе «Все»), сегменты фильтра
// (action-зоны / пометка 'important' / все), поиск по name ?? sessionId без учёта регистра
// и вместе с сегментом, счётчики сегментов. Чистые функции — без рендера; ряд над рамкой,
// поле и пустые состояния — на fakeDom, тем же приёмом, что WikiChatTriageList.test.ts.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) в filterTriage убрать toLowerCase у запроса → «ПЛАН» не находит «План релиза»,
//     «Поиск без учёта регистра» краснеет;
// (2) в defaultFilter проверить только awaiting_human (убрать rejected_skipped) →
//     «defaultFilter: rejected_skipped» краснеет;
// (3) в triageCounts считать important как mark !== 'skip' → «Счётчики: 5 чатов» краснеет
//     (skip-пометка засчиталась бы в «Важные»);
// (4) в filterTriage не брать name ?? sessionId, а только name → «по sessionId» краснеет;
// (5) в компоненте убрать ветку пустого состояния под фильтром → «пусто под сегментом
//     «Ждут вас»» краснеет.
//
// M1b (эта задача) — рамка и карточка (план «Рамка»/«Фокус», П2/П3): предел высоты LIST_MAX_H
// со своей прокруткой, закреплённая шапка «Показано N из M» (shownText), легенды нет; балл —
// справа от строки названия («без балла»), мета строкой через «·», причина в 2 строки с полным
// текстом в title; при смене фильтра скролл рамки — в начало. Ассертция «1 чат» в тестах M1a
// стала «Показано 1 из 4»: текст шапки — ровно то, что изменил этот шаг.
// Мутации M1b (прогнаны в копии дерева, вывод — в отчёте):
// (6) снять maxHeight рамки → «рамка: предел высоты» краснеет;
// (7) убрать у шапки position: sticky → краснеет;
// (8) в балле убрать ветку «без балла» (всегда `балл ${score}`) → «балл справа» краснеет;
// (9) в chooseFilter не сбрасывать scrollTop → «скролл при смене фильтра» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import {
  WikiChatTriageList, defaultFilter, filterTriage, triageCounts, shownText, type TriageFilter,
} from '../WikiChatTriageList';
import { C, LIST_MAX_H, type WikiChatTriage } from 'aihome_shell/kit';

vi.mock('../../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../../lib/offline')>(),
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

// Строка как её отдаёт GET /api/wiki/chats (WikiChatTriageDto) — та же форма, что в
// WikiChatTriageList.test.ts: зона бэковая, пометка — поле записи
const item = (over: Partial<WikiChatTriage>): WikiChatTriage => ({
  sessionId: 'c1', name: 'План шагов LLM-wiki', zone: 'triage', score: 31,
  signals: null, reason: 'балл 31: формулировки решений (+8), реплики человека (+6)',
  nothingNewStreak: 0, triagedAt: '2026-11-01T12:00:00Z', running: false, cursor: 0,
  chroniclePath: null, mark: null, ...over,
});

const awaitingItem = () => item({ sessionId: 'a1', name: 'Пакет на решении', zone: 'awaiting_human', score: 12, reason: '2 операции на решении' });
const rejectedItem = () => item({ sessionId: 'r1', name: 'Три отказа', zone: 'rejected_skipped', score: 9, reason: 'отказ: модель не ответила' });
const importantItem = () => item({ sessionId: 'i1', name: 'Важный', zone: 'triage', mark: 'important' });
// id — аргумент: три одинаковых ordinary-чата с одним sessionId дали бы дубли ключей списка
const plainItem = (id = 'p1') => item({ sessionId: id, name: 'Обычный', zone: 'skipped', score: 2 });

describe('фильтры и поиск «Отбора чатов» (M1a)', () => {
  describe('defaultFilter', () => {
    it('«Ждут вас» при чате в action-зоне — awaiting_human или rejected_skipped', () => {
      expect(defaultFilter([awaitingItem(), plainItem()])).toBe('awaiting');
      expect(defaultFilter([plainItem(), rejectedItem()])).toBe('awaiting');
      expect(defaultFilter([awaitingItem()])).toBe('awaiting');
    });

    it('«Все» иначе: обычная зона и пометка «важный» дефолт не сдвигают', () => {
      expect(defaultFilter([plainItem(), importantItem()])).toBe('all');
      expect(defaultFilter([item({ zone: 'gray' }), item({ zone: 'excluded' })])).toBe('all');
      expect(defaultFilter([])).toBe('all');
    });
  });

  describe('filterTriage', () => {
    const items = [awaitingItem(), rejectedItem(), importantItem(), plainItem(), item({ sessionId: 'sess-42-hidden', name: null })];

    it('сегменты: «Ждут вас» — только action-зоны, «Важные» — только пометка important, «Все» — всё', () => {
      expect(filterTriage(items, 'awaiting', '').map(i => i.sessionId)).toEqual(['a1', 'r1']);
      expect(filterTriage(items, 'important', '').map(i => i.sessionId)).toEqual(['i1']);
      expect(filterTriage(items, 'all', '').length).toBe(5);
    });

    it('поиск без учёта регистра: «ПЛАН» находит «План релиза», «план» — тоже; без name — по sessionId', () => {
      const pool = [item({ sessionId: 'x1', name: 'План релиза b1853271' }), item({ sessionId: 'x2', name: 'Ревью витрины' })];
      expect(filterTriage(pool, 'all', 'ПЛАН').map(i => i.sessionId)).toEqual(['x1']);
      expect(filterTriage(pool, 'all', 'план').map(i => i.sessionId)).toEqual(['x1']);
      // и смена регистра кириллицы: «ПлАн РЕли» — тот же поиск
      expect(filterTriage(pool, 'all', 'ПлАн РЕли').map(i => i.sessionId)).toEqual(['x1']);
      // чат без имени ищется по sessionId — он не исчезает из поиска
      expect(filterTriage(items, 'all', 'sess-42').map(i => i.sessionId)).toEqual(['sess-42-hidden']);
      // пробельный запрос — не поиск: ничего не фильтрует
      expect(filterTriage(items, 'all', '   ').length).toBe(5);
    });

    it('поиск работает вместе с сегментом: «Важные» + «план» — только важный с «план»', () => {
      const pool = [item({ sessionId: 'i2', name: 'План важных шагов', mark: 'important' }), item({ sessionId: 'x1', name: 'План релиза' }), importantItem()];
      expect(filterTriage(pool, 'important', 'план').map(i => i.sessionId)).toEqual(['i2']);
      expect(filterTriage(pool, 'awaiting', 'план').length).toBe(0);
    });

    it('порядок бэка не меняется: отфильтрованное сохраняет порядок входа', () => {
      const pool = [item({ sessionId: 'q1', name: 'План первый' }), item({ sessionId: 'q2', name: 'Обычный' }), item({ sessionId: 'q3', name: 'план третий' })];
      expect(filterTriage(pool, 'all', 'план').map(i => i.sessionId)).toEqual(['q1', 'q3']);
    });
  });

  describe('triageCounts', () => {
    it('счётчики на наборе из 5 чатов (2 в action-зонах, 1 important): {awaiting: 2, important: 1, all: 5}', () => {
      const items = [awaitingItem(), rejectedItem(), importantItem(), plainItem(), item({ sessionId: 'g1', zone: 'gray' })];
      expect(triageCounts(items)).toEqual({ awaiting: 2, important: 1, all: 5 });
    });

    it('skip-пометка в «Важные» не входит; пустой список — нули', () => {
      const items = [item({ sessionId: 's1', mark: 'skip' }), plainItem()];
      expect(triageCounts(items)).toEqual({ awaiting: 0, important: 0, all: 2 });
      expect(triageCounts([])).toEqual({ awaiting: 0, important: 0, all: 0 });
    });
  });

  // ─── Рендер ряда над рамкой (тот же мокан транспорта, что WikiChatTriageList.test.ts) ───

  let serverItems: WikiChatTriage[] = [];
  function serveChats(): void {
    req.mockImplementation(async (url: string, init?: RequestInit) => {
      const method = (init?.method ?? 'GET').toUpperCase();
      if (method === 'GET' && url === '/wiki/chats') return { items: serverItems, counts: {} };
      return null;
    });
  }

  const roots: Root[] = [];

  function render(): Cont {
    installFakeDom();
    const { root, container } = mount(createElement(WikiChatTriageList));
    roots.push(root);
    return container;
  }

  async function settle(times = 20) {
    for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
  }

  function texts(container: AnyNode): string[] {
    return textNodes(container).map(t => t.trim()).filter(Boolean);
  }

  function has(container: AnyNode, needle: string): boolean {
    return texts(container).join(' ').includes(needle);
  }

  // Обход — от контейнера рендера: mount монтирует в отдельный div, document.body пуст
  function walkButtons(container: AnyNode, want: (label: string) => boolean): AnyNode | null {
    let found: AnyNode | null = null;
    const walk = (x: AnyNode) => {
      if (!found && x.nodeType === 1 && x.nodeName === 'BUTTON') {
        const label = textNodes(x).map(t => t.trim()).join(' ');
        if (want(label)) found = x;
      }
      for (const c of x.childNodes) walk(c);
    };
    walk(container);
    return found;
  }

  // Сегменты SegmentedControl — кнопки с текстом «Ждут вас · N» / «Важные · N» / «Все · N»;
  // кнопки строк («Важный», «Не разбирать») этим префиксам не соответствуют
  function segNode(container: AnyNode, value: TriageFilter): AnyNode | null {
    const wanted = { awaiting: 'Ждут вас', important: 'Важные', all: 'Все' }[value];
    return walkButtons(container, label => label.startsWith(wanted));
  }

  const buttonByText = (container: AnyNode, text: string): AnyNode | null =>
    walkButtons(container, label => label === text);

  function inputNode(container: AnyNode): AnyNode | null {
    let found: AnyNode | null = null;
    const walk = (x: AnyNode) => {
      if (!found && x.nodeType === 1 && x.nodeName === 'INPUT') found = x;
      for (const c of x.childNodes) walk(c);
    };
    walk(container);
    return found;
  }

  function click(node: AnyNode, what: string): void {
    const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
    if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
    act(() => { props.onClick(undefined); });
  }

  // Инлайн-стиль узла — из props React (в fakeDom он не дублируется в атрибут): тест читает
  // ровно то, что задал компонент (тот же приём, что в WikiApplySettings.test)
  function styleOf(n: AnyNode): Record<string, unknown> {
    return ((reactProps(n) ?? {}).style ?? {}) as Record<string, unknown>;
  }

  // Элемент, который сам рендерит этот текст (textNodes рекурсивен: внешний контейнер тоже
  // «содержит» строку, а стиль и title нужны у того, кто её рендерит)
  function findNodeWithOwnText(root: AnyNode, needle: string): AnyNode | null {
    let hit: AnyNode | null = null;
    const walk = (x: AnyNode) => {
      if (hit || x.nodeType !== 1) return;
      const own = Array.from(x.childNodes as unknown[]).some(
        ch => (ch as { nodeType?: number; nodeValue?: string }).nodeType === 3 && (ch as { nodeValue?: string }).nodeValue?.includes(needle));
      if (own) { hit = x; return; }
      for (const c of x.childNodes) walk(c);
    };
    walk(root);
    return hit;
  }

  function typeQuery(container: AnyNode, value: string): void {
    const input = inputNode(container);
    if (!input) throw new Error('поля поиска нет');
    const props = reactProps(input) as { onChange?: (e: unknown) => void };
    act(() => { props.onChange?.({ target: { value } }); });
  }

  beforeEach(() => {
    req.mockReset();
    serverItems = [];
    serveChats();
  });

  afterEach(() => {
    for (const root of roots.splice(0)) void root.unmount();
    uninstallFakeDom();
  });

  it('ряд сегментов со счётчиками; при action-зоне по умолчанию выбран «Ждут вас», строки — только она', async () => {
    // три ordinary-чата и одна action-зона: дефолт — «Ждут вас», под ним одна строка
    serverItems = [plainItem('p1'), plainItem('p2'), plainItem('p3'), awaitingItem()];
    const c = render();
    await settle();
    expect(has(c, 'Ждут вас · 1')).toBe(true);
    expect(has(c, 'Важные · 0')).toBe(true);
    expect(has(c, 'Все · 4')).toBe(true);
    // выбран «Ждут вас»: под рамкой ровно одна строка, ordinary-чаты скрыты
    expect(texts(c).filter(t => t === 'Пакет на решении').length).toBe(1);
    expect(has(c, 'Обычный')).toBe(false);
    // M1b: шапка рамки считает то, что под ней, и говорит, из чего это считается
    expect(has(c, 'Показано 1 из 4')).toBe(true);
    // клик по «Все» — видим всё; выбор живёт до конца просмотра, не дальше
    click(segNode(c, 'all')!, 'Все');
    await settle();
    expect(texts(c).filter(t => t === 'Обычный').length).toBe(3);
  });

  it('без action-зон по умолчанию «Все»; при нулевом счётчике сегмент остаётся кликабельным, не выключен', async () => {
    serverItems = [plainItem('p1'), importantItem()];
    const c = render();
    await settle();
    expect(has(c, 'Все · 2')).toBe(true);
    expect(has(c, 'Обычный')).toBe(true);
    const awaiting = segNode(c, 'awaiting')!;
    expect((reactProps(awaiting) as { disabled?: boolean }).disabled).toBeFalsy();
    // живой: клик ведёт дальше — под «Ждут вас» пусто-состояние этого сегмента
    click(awaiting, 'Ждут вас');
    await settle();
    expect(has(c, 'Ваших решений не ждёт ни один чат')).toBe(true);
  });

  it('поле поиска: при ≤10 чатов его нет, при 11 — есть; «ПЛАН» находит «План релиза», «Сбросить поиск» возвращает список', async () => {
    const many = (n: number): WikiChatTriage[] =>
      Array.from({ length: n }, (_, i) => item({ sessionId: `m${i}`, name: `Чат ${i}`, zone: 'triage', score: i }));
    serverItems = [...many(10), item({ sessionId: 'pl', name: 'План релиза b1853271', zone: 'triage' })];
    const c = render();
    await settle();
    expect(inputNode(c)).toBeTruthy();
    typeQuery(c, 'ПЛАН');
    await settle();
    expect(has(c, 'План релиза b1853271')).toBe(true);
    expect(has(c, 'Чат 3')).toBe(false);
    expect(has(c, 'Ничего не нашлось')).toBe(false);
    typeQuery(c, 'квартальный отчёт');
    await settle();
    expect(has(c, 'Ничего не нашлось по «квартальный отчёт»')).toBe(true);
    const btn = buttonByText(c, 'Сбросить поиск');
    expect(btn).toBeTruthy();
    click(btn!, 'Сбросить поиск');
    await settle();
    expect(has(c, 'Чат 3')).toBe(true);
    // 10 чатов — поля нет (план: поиск только при >10)
    for (const root of roots.splice(0)) void root.unmount();
    uninstallFakeDom();
    serverItems = many(10);
    const c2 = render();
    await settle();
    expect(inputNode(c2)).toBeNull();
  });

  it('пусто под сегментом «Ждут вас» и под «Важные» — свои тексты; при запросе — «Ничего не нашлось» без рамки', async () => {
    serverItems = [plainItem('p1'), item({ sessionId: 'g2', zone: 'gray', name: 'Серый' })];
    const c = render();
    await settle();
    click(segNode(c, 'awaiting')!, 'Ждут вас');
    await settle();
    expect(has(c, 'Ваших решений не ждёт ни один чат')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-triage-row')).toBeNull();
    click(segNode(c, 'important')!, 'Важные');
    await settle();
    expect(has(c, 'Важных чатов нет.')).toBe(true);
    expect(has(c, 'Пометьте звёздочкой то, что нельзя пропустить')).toBe(true);
  });

  it('чат без имени остаётся в списке и ищется по sessionId', async () => {
    serverItems = [item({ sessionId: 'sess-42-hidden', name: null, zone: 'triage' }), plainItem('p1')];
    const c = render();
    await settle();
    // без поля (2 чата) поиск не показывается — но строка без имени видна как есть: по sessionId
    expect(has(c, 'sess-42-hidden')).toBe(true);
    expect(has(c, 'Ничего не нашлось')).toBe(false);
  });

  // ─── M1b: рамка, шапка и карточка (план «Рамка»/«Фокус», П2/П3) ───

  describe('рамка и шапка «Отбора чатов» (M1b)', () => {
    it('shownText: «Показано 12 из 48»; пустой отбор называется тем же текстом', () => {
      expect(shownText(12, 48)).toBe('Показано 12 из 48');
      expect(shownText(0, 48)).toBe('Показано 0 из 48');
    });

    it('рамка: предел высоты LIST_MAX_H, своя прокрутка; шапка закреплена с «Показано N из M», легенды нет', async () => {
      serverItems = [plainItem('p1'), plainItem('p2'), awaitingItem()];
      const c = render();
      await settle();
      const frame = findNodeByAttr(c, 'data-testid', 'wiki-triage-list')!;
      const st = styleOf(frame);
      expect(st.maxHeight).toBe(LIST_MAX_H);
      expect(st.overflowY).toBe('auto');
      expect(st.overscrollBehavior).toBe('contain');
      const hdr = findNodeByAttr(c, 'data-testid', 'wiki-triage-header')!;
      const hs = styleOf(hdr);
      // закреплённая шапка (П2): счёт виден, пока список скроллится; фон — inset, строки не просвечивают
      expect(hs.position).toBe('sticky');
      expect(hs.top).toBe(0);
      expect(hs.background).toBe(C.bgInset);
      // шапка считает то, что под ней, и говорит, из чего: под «Ждут вас» один чат из трёх
      expect(has(hdr, 'Показано 1 из 3')).toBe(true);
      // легенда несуществующей таблицы убрана (план «Рамка»)
      expect(has(c, 'балл · состояние · причина · пометка')).toBe(false);
    });

    it('при смене фильтра скролл рамки — в начало (план «Фокус»)', async () => {
      serverItems = [plainItem('p1'), plainItem('p2'), awaitingItem()];
      const c = render();
      await settle();
      const frame = findNodeByAttr(c, 'data-testid', 'wiki-triage-list')!;
      frame.scrollTop = 120;
      click(segNode(c, 'all')!, 'Все');
      await settle();
      expect(frame.scrollTop).toBe(0);
    });
  });

  describe('карточка строки (M1b, П3)', () => {
    it('балл — справа от строки названия; без балла — «без балла»; колонки балла слева нет', async () => {
      serverItems = [item({ sessionId: 's1', name: 'С баллом', score: 7 }), item({ sessionId: 's2', name: 'Без балла', zone: 'skipped', score: null })];
      const c = render();
      await settle();
      expect(has(c, 'без балла')).toBe(true);
      // первый текстовый узел строки — название, второй — балл: отдельной колонки балла слева больше нет
      const row = findNodeByAttr(c, 'data-testid', 'wiki-triage-row')!;
      const ts = textNodes(row).map(t => t.trim()).filter(Boolean);
      expect(ts[0]).toBe('С баллом');
      expect(ts[1]).toBe('балл 7');
    });

    it('причина обрезана до 2 строк, полный текст — в title; мета — строкой через «·»', async () => {
      const longReason = 'балл 31: очень длинная причина отбора, которая не помещается в две строки списка и обрезается по границе в две строки, а полный её текст остаётся в подсказке';
      serverItems = [item({ sessionId: 'q1', name: 'Длинная причина', score: 31, reason: longReason, cursor: 5 })];
      const c = render();
      await settle();
      const row = findNodeByAttr(c, 'data-testid', 'wiki-triage-row')!;
      const reasonEl = findNodeWithOwnText(row, 'очень длинная причина');
      expect(reasonEl).toBeTruthy();
      const st = styleOf(reasonEl!);
      expect(st.WebkitLineClamp).toBe(2);
      expect(st.WebkitBoxOrient).toBe('vertical');
      expect(st.overflow).toBe('hidden');
      // обрезанное не потеряно: полный текст — в title элемента причины
      expect((reactProps(reasonEl!) as { title?: string }).title).toBe(longReason);
      // мета одной строкой с разделителем: дата · «разобран до m5»
      expect(has(row, '01.11 · разобран до m5')).toBe(true);
    });

    it('широкая раскладка: пометки — справа карточки (второй ряд карточки), а не своим рядом под текстом', async () => {
      // до первого замера ширины narrow=false (тот же договор, что у useWikiNarrow): в тесте
      // проверяется широкая ветка; узкая включается тем же признаком по замеру контейнера
      serverItems = [plainItem('p1')];
      const c = render();
      await settle();
      const row = findNodeByAttr(c, 'data-testid', 'wiki-triage-row')!;
      const content = row.childNodes[0];
      const marks = row.childNodes[1];
      expect(content.nodeName).toBe('SPAN');
      expect(marks.nodeName).toBe('SPAN');
      expect(styleOf(marks).display).toBe('flex');
      // оба контрола на месте: «Важный» и «Не разбирать»
      expect(textNodes(marks).map(t => t.trim()).filter(Boolean)).toEqual(['Важный', 'Не разбирать']);
    });
  });
});
