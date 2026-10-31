// Рендер-тест «Журнала» (S7c-1): политика по операциям пакета (auto → «применилось само»,
// inbox → «через Входящие»); «Входящие» (proposed) в журнале нет; откат по источнику вызывает
// REST только после подтверждения модалкой (c3); отказ отката пакета показан как есть, с
// перечнем изменённых страниц (c2); кнопки заблокированы на время запроса; пустое состояние;
// гонка поколений загрузки.
// Доработка S7c-1 (вердикт cd9ab04b): ref бэка — реальной формы с диапазоном окна
// («session:{id}#m{a}-{b}», захват в WikiChatIngestService): в POST retract уходит карточка без диапазона
// «session:{id}», модалка до подтверждения считает применённые пакеты источника по
// нормальной форме (окна одного чата — все); после успешного отката открытая страница
// перечитана и показывает состояние после отката, закрытая — нет (c1/c2).
//
// Мокан только транспорт (offline.request) и signalr — тот же приём, что в WikiInbox.test.
// Модалка ConfirmDialog рендерится порталом в document.body — её узлы ищутся от body.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) sourceRefOf — слать ref как есть (старая версия) → «окна одного чата» краснеет;
//  (2) в revert/retract не звать reReadOpenPage → «открытая страница перечитана» краснеет;
//  (3) звать reReadOpenPage безусловно (без счётчика открытых) → «закрытая не перечитывается»
//      и «после закрытия — без перечитки» краснеют;
//  (4) звать doRetract прямо из кнопки, минуя подтверждение → «0 вызовов до подтверждения» краснеет;
//  (5) убрать JOURNAL_STATUSES (показывать все пакеты) → «proposed в журнале нет» краснеет;
//  (6) не показывать pages отказа отката (убрать <ul> из actionError) → «перечень страниц» краснеет;
//  (7) убрать проверку loadSeq → «гонка поколений» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiJournal } from '../WikiJournal';

vi.mock('../../../../lib/signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', () => ({
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));
// Нативный <select> в fakeDom не живёт: react-dom при рендере читает select.options,
// чего у FNode нет. Тесты не про контрол фильтра — Select подменён заглушкой.
vi.mock('../../../../components/ui/Select', () => ({
  Select: () => null,
}));

import { request } from '../../../../lib/offline';
import {
  __test as wikiStore, getWikiPageSnapshot, loadWikiPage, useWikiPage,
} from '../../../../lib/wiki';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const CHAT_ID = '7f3a2c11-2222-3333-4444-555555555555';
// Реальная форма ref бэка: захват окна пишет карточку с диапазоном — «session:{id}#m{from}-{to}»
const refWindow = (from: number, to: number) => `session:${CHAT_ID}#m${from}-${to}`;

const op = (over: Record<string, unknown>) => ({
  index: 0, op: 'append', page: 'projects/p1/page-a', target: null,
  text: 'Вера ведёт QA редактора видео.', class: 'session×append',
  decision: 'auto', reason: null,
  baseHashes: {}, inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
  ...over,
});

// Пакеты как их отдаёт GET /api/wiki/packages без inbox: применённое «само», применённое
// через «Входящие», откатанный, отбитый проверками и proposed (последний — не журнал).
// sources — реальной формы бэка, с диапазоном окна (захват в WikiChatIngestService)
const PKG_AUTO = {
  id: 'pkg-auto', section: 'projects/p1', status: 'applied', statusReason: null,
  createdAt: '2026-10-30T03:10:00Z', updatedAt: '2026-10-30T03:12:00Z',
  sources: [{ origin: 'session', ref: refWindow(1, 9) }],
  operations: [op({})],
};
const PKG_INBOX = {
  ...PKG_AUTO, id: 'pkg-inbox', updatedAt: '2026-10-29T03:12:00Z',
  sources: [{ origin: 'session', ref: 'session:inbox-chat-0001#m1-4' }],
  operations: [op({
    decision: 'inbox', reason: 'Замещает утверждение', humanDecision: 'accepted', humanReason: null,
  })],
};
const PKG_REVERTED = {
  ...PKG_AUTO, id: 'pkg-rev', status: 'reverted', updatedAt: '2026-10-28T03:12:00Z',
  statusReason: 'откатан человеком', operations: [op({ page: 'projects/p1/page-c' })],
};
const PKG_CHECKS = {
  ...PKG_AUTO, id: 'pkg-chk', status: 'rejected_by_checks', updatedAt: '2026-10-27T03:12:00Z',
  statusReason: 'цитата не найдена дословно',
};
const PKG_PROPOSED = {
  ...PKG_AUTO, id: 'pkg-prop', status: 'proposed', updatedAt: '2026-10-26T03:12:00Z',
  sources: [{ origin: 'session', ref: 'session:ПРОПОSED-REF#m1-2' }],
};

const LIST = {
  packages: [PKG_AUTO, PKG_INBOX, PKG_REVERTED, PKG_CHECKS, PKG_PROPOSED],
  invalidCount: 0,
};

// Два применённых окна одного чата (дефект c1: сравнение целых ref давало 1 вместо 2)
const PKG_W2 = {
  ...PKG_AUTO, id: 'pkg-w2', updatedAt: '2026-10-30T03:20:00Z',
  sources: [{ origin: 'session', ref: refWindow(5, 9) }],
  operations: [op({ page: 'projects/p1/page-b' })],
};
const LIST_WINDOWS = { packages: [PKG_W2, PKG_AUTO], invalidCount: 0 };

const REVERT_OK = { restoredPages: ['projects/p1/page-a'] };
const RETRACT_OK = {
  revertedPackages: ['pkg-auto'], unlinkedPackages: [], remainingPackages: [],
  removedChronicles: [], failed: [], markedSkip: true,
};

// Транспорт для тестов отката: список, ответ отката и страница — по адресу запроса;
// неоговорённый запрос — падение теста (образец serve в lib/__tests__/wiki.test.ts).
// Страница отдаёт «до отката», а после первого POST-отката — «после отката»: бэк после
// отката отдаёт возвращённое состояние, и перечитка обязана его принести
function serveJournal(pageResponses: Record<string, unknown>) {
  let reverted = false;
  req.mockImplementation((url: string, init?: { method?: string }) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method === 'POST') {
      if (url.includes('/revert') || url.includes('/retract')) {
        reverted = true;
        return Promise.resolve(url.includes('/revert') ? REVERT_OK : RETRACT_OK);
      }
      return Promise.resolve(null);
    }
    if (url.startsWith('/api/wiki/packages')) return Promise.resolve(LIST);
    if (url.startsWith('/api/wiki/page')) {
      const m = /[?&]page=([^&]+)/.exec(url);
      const key = m ? decodeURIComponent(m[1]) : '';
      if (key in pageResponses) {
        return Promise.resolve(reverted ? { pageKey: key, content: 'после отката' } : pageResponses[key]);
      }
    }
    return Promise.resolve(null);
  });
}

const roots: Root[] = [];

function seedList(data: { packages: unknown[]; invalidCount: number }) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/api/wiki/packages')) return Promise.resolve(data);
    return Promise.resolve(null);
  });
}

function render(): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiJournal));
  roots.push(root);
  return container;
}

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(container: AnyNode): string[] {
  return textNodes(container).map(t => t.trim()).filter(Boolean);
}

function has(container: AnyNode, needle: string): boolean {
  return texts(container).join(' ').includes(needle);
}

// Модалка — портал в document.body: её узлы ищутся от body, не от container
function docBody(): AnyNode {
  return (globalThis as { document: { body: AnyNode } }).document.body;
}

function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}

function buttonByTestId(container: AnyNode, testId: string): AnyNode | null {
  return findNodeByAttr(container, 'data-testid', testId);
}

// Точное совпадение текста: «Откатить» в модалке не должно схватить «Откатить пакет» строкой
function buttonByExactText(root: AnyNode, text: string): AnyNode | null {
  return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === text) ?? null;
}

function click(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick!(undefined); });
}

function rows(container: AnyNode): AnyNode[] {
  return allNodes(container).filter(n => n.getAttribute('data-testid') === 'wiki-journal-row');
}

function openEntry(c: AnyNode, testId = 'wiki-journal-row') {
  const row = findNodeByAttr(c, 'data-testid', testId);
  click(row, 'строка журнала');
}

const postCalls = () => req.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === 'POST');
const getCalls = (prefix: string) => req.mock.calls.filter(([url, init]) =>
  String(url).startsWith(prefix) && !((init as { method?: string } | undefined)?.method === 'POST'));

// Открытый экран страницы: тот же шов, что читает витрина (useWikiPage), — подписка и
// есть «открытое состояние» для перечитки после отката. Рендерит содержимое снимка.
function OpenPageProbe({ sectionKey, pageKey }: { sectionKey: string; pageKey: string }) {
  const page = useWikiPage(sectionKey, pageKey);
  return createElement('span', { 'data-testid': 'open-page-probe' }, page ? page.content : '');
}

beforeEach(() => {
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
  // стор вики — модульный (тот же транспорт, что мокается здесь): каждый тест с чистого
  // состояния, открытые страницы и поколения — из прошлого теста не переносятся
  wikiStore.reset();
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  wikiStore.reset();
  uninstallFakeDom();
});

describe('«Журнал» (S7c-1)', () => {
  it('политика по операциям: auto — «применилось само», через «Входящие» — «через Входящие»; proposed не показан', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    // журнал — applied/rejected_by_checks/reverted; proposed («Входящие») здесь нет (c2)
    expect(rows(c)).toHaveLength(4);
    expect(has(c, 'ПРОПОSED-REF')).toBe(false);
    expect(has(c, 'применилось само')).toBe(true);
    expect(has(c, 'через Входящие')).toBe(true);
    expect(has(c, 'отбит проверками')).toBe(true);
  });

  it('откат по источнику: до подтверждения — 0 вызовов REST; после — POST retract с карточкой без диапазона', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    // подтверждение спросили, бэка ещё не звали (c3)
    expect(postCalls()).toHaveLength(0);
    expect(has(docBody(), 'Снимется то, что Хранитель взял из этого источника')).toBe(true);
    expect(has(docBody(), '1 применённых пакетов')).toBe(true);
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(String(posts[0][0])).toContain('/api/wiki/packages/retract');
    // c1: range «#m1-9» из ref бэка снят — бэк по карточке накрывает все окна чата
    expect(JSON.parse((posts[0][1] as { body: string }).body)).toMatchObject({
      sourceRef: `session:${CHAT_ID}`,
    });
  });

  it('окна одного чата: модалка до подтверждения считает все применённые пакеты источника (c1)', async () => {
    seedList(LIST_WINDOWS);
    const c = render();
    await settle();
    // самая свежая строка — окно m5-9; откат по нему снимает и окно m1-9 того же чата
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    // c1: сравнение по нормальной форме источника, а не целым ref: два окна — «2», не «1»
    expect(has(docBody(), '2 применённых пакетов')).toBe(true);
    expect(has(docBody(), '1 применённых пакетов')).toBe(false);
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(JSON.parse((posts[0][1] as { body: string }).body)).toMatchObject({
      sourceRef: `session:${CHAT_ID}`,
    });
  });

  it('отказ отката пакета показан как есть, с перечнем изменённых страниц (c2)', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    // подтверждение заранее показывает, что снимется: страницы пакета
    click(buttonByTestId(c, 'wiki-journal-revert'), 'Откатить пакет');
    await settle();
    expect(has(docBody(), 'Страницы вернутся к состоянию до этого пакета')).toBe(true);
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') {
        return Promise.reject({
          status: 409,
          body: { code: 'wiki_revert_changed', message: 'x', pages: ['projects/p1/page-b'] },
        });
      }
      if (url.startsWith('/api/wiki/packages')) return Promise.resolve(LIST);
      return Promise.resolve(null);
    });
    click(buttonByExactText(docBody(), 'Откатить'), 'подтверждение отката пакета');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(String(posts[0][0])).toContain('/pkg-auto/revert');
    // отказ — русским текстом по коду, с перечнем страниц бэка; сырой код не показан
    expect(has(c, 'Страницу меняли после этого пакета')).toBe(true);
    expect(has(c, 'projects/p1/page-b')).toBe(true);
    expect(has(c, 'wiki_revert_changed')).toBe(false);
  });

  it('запись журнала: «предполагаемое изменение» через DiffView, без обещания результата', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    expect(has(c, 'Предполагаемое изменение')).toBe(true);
    expect(has(c, 'Вера ведёт QA редактора видео.')).toBe(true);
    expect(has(c, 'projects/p1/page-a')).toBe(true);
  });

  it('пока откат в пути, кнопки записи заблокированы', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    let resolvePost: (v: unknown) => void = () => {};
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return new Promise<unknown>(res => { resolvePost = res; });
      if (url.startsWith('/api/wiki/packages')) return Promise.resolve(LIST);
      return Promise.resolve(null);
    });
    click(buttonByTestId(c, 'wiki-journal-revert'), 'Откатить пакет');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить'), 'подтверждение отката пакета');
    await settle();
    // POST ещё висит: кнопки записи заблокированы (урок S5b/S5e)
    expect((reactProps(buttonByTestId(c, 'wiki-journal-revert')!) as { disabled?: boolean }).disabled).toBe(true);
    expect((reactProps(buttonByTestId(c, 'wiki-journal-retract')!) as { disabled?: boolean }).disabled).toBe(true);
    resolvePost(REVERT_OK);
    await settle();
  });

  it('пусто — «Журнал пустой», а не пустой экран', async () => {
    seedList({ packages: [], invalidCount: 0 });
    const c = render();
    await settle();
    expect(has(c, 'Журнал пустой')).toBe(true);
    expect(rows(c)).toHaveLength(0);
  });

  it('гонка поколений: поздний ответ прежней загрузки не затирает свежий список', async () => {
    const A = { ...PKG_AUTO, sources: [{ origin: 'session', ref: 'session:СПИСОК-А#m1-2' }] };
    const B = { ...PKG_AUTO, id: 'pkg-b', sources: [{ origin: 'session', ref: 'session:ЗАПАЗДАЛЫЙ-B#m1-2' }] };
    const C = { ...PKG_AUTO, id: 'pkg-c', sources: [{ origin: 'session', ref: 'session:СВЕЖИЙ-C#m1-2' }] };
    let resolveB: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/api/wiki/packages')) {
        n++;
        if (n === 1) return Promise.resolve({ packages: [A], invalidCount: 0 });
        if (n === 2) return new Promise(res => { resolveB = res; });
        return Promise.resolve({ packages: [C], invalidCount: 0 });
      }
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    expect(has(c, 'СПИСОК-А')).toBe(true);
    openEntry(c); await settle();
    click(buttonByTestId(c, 'wiki-journal-back'), 'Назад'); await settle();
    openEntry(c); await settle();
    click(buttonByTestId(c, 'wiki-journal-back'), 'Назад'); await settle();
    expect(has(c, 'СВЕЖИЙ-C')).toBe(true);
    resolveB({ packages: [B], invalidCount: 0 });
    await settle();
    expect(has(c, 'СВЕЖИЙ-C')).toBe(true);
    expect(has(c, 'ЗАПАЗДАЛЫЙ-B')).toBe(false);
  });
});

describe('«Журнал»: открытая страница после отката (доработка S7c-1)', () => {
  const SECTION = 'projects/p1';
  const PAGE = 'projects/p1/page-a';
  const PAGE_URL = `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(PAGE)}`;
  const PAGE_BEFORE = { pageKey: 'page-a', content: 'до отката' };
  const PAGE_AFTER = { pageKey: 'page-a', content: 'после отката' };

  // Открыть страницу так же, как витрина: запрос стора + подписанный экран (useWikiPage)
  async function openPage(): Promise<{ root: Root; probe: AnyNode }> {
    await act(async () => { await loadWikiPage(SECTION, PAGE); });
    installFakeDom();
    const { root, container } = mount(createElement(OpenPageProbe, { sectionKey: SECTION, pageKey: PAGE }));
    roots.push(root);
    await settle();
    const probe = findNodeByAttr(container, 'data-testid', 'open-page-probe');
    if (!probe) throw new Error('нет экрана открытой страницы');
    return { root, probe };
  }

  const probeText = (probe: AnyNode) => textNodes(probe).join('');

  it('открытая страница после отката пакета и отката по источнику перечитана и показывает состояние после отката (c2)', async () => {
    serveJournal({ [PAGE]: PAGE_BEFORE });
    const page = await openPage();
    expect(getWikiPageSnapshot(SECTION, PAGE)?.content).toBe('до отката');

    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-revert'), 'Откатить пакет');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить'), 'подтверждение отката пакета');
    await settle();
    // c2: страница под открытым экраном перечитана и отдаёт состояние после отката
    expect(getWikiPageSnapshot(SECTION, PAGE)?.content).toBe('после отката');
    expect(getCalls('/api/wiki/page').length).toBeGreaterThanOrEqual(2);
    expect(probeText(page.probe)).toBe('после отката');

    // та же гарантия для отката по источнику: страница открыта — после успеха перечитана
    // (успешный откат пакета сам возвращает журнал к списку — openEntry снова сработает)
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const retracts = postCalls().filter(p => String(p[0]).includes('/retract'));
    expect(retracts).toHaveLength(1);
    expect(getWikiPageSnapshot(SECTION, PAGE)?.content).toBe('после отката');
    expect(getCalls('/api/wiki/page').length).toBeGreaterThanOrEqual(3);
    // и под открытым экраном — тоже новое состояние
    expect(probeText(page.probe)).toBe('после отката');
  });

  it('закрытая страница не перечитывается: после закрытия откат не зовёт её перечитку (c2)', async () => {
    serveJournal({ [PAGE]: PAGE_BEFORE });
    const page = await openPage();
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-revert'), 'Откатить пакет');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить'), 'подтверждение отката пакета');
    await settle();
    // пока страница открыта — она перечитана
    expect(getWikiPageSnapshot(SECTION, PAGE)?.content).toBe('после отката');
    const readsWhileOpen = getCalls('/api/wiki/page').length;
    expect(readsWhileOpen).toBeGreaterThanOrEqual(2);

    // закрыли экран — откат больше не перечитывает закрытую (хвост «перечитка подняла
    // поколение для закрытой страницы»)
    act(() => page.root.unmount());
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-revert'), 'Откатить пакет');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить'), 'подтверждение отката пакета');
    await settle();
    expect(postCalls().filter(p => String(p[0]).includes('/revert'))).toHaveLength(2);
    expect(getCalls('/api/wiki/page').length).toBe(readsWhileOpen);
  });
});
