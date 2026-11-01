// Рендер-тест «Журнала» (S7c-2): данные — GET /wiki/journal (S7c-0), фильтры — параметры
// запроса; политика по status/policy записи («применилось само» / «через Входящие»); имя источника
// вместо сырой карточки; invalidLines — числом; отказы 400/403/404 — текстом, не кодом; подвкладка
// «Журнал» в реестре ведёт на этот экран; откат по источнику — только после подтверждения модалкой,
// карточка без диапазона; окна одного чата считаются по нормальной форме; кнопки заблокированы на
// время запроса; пустое состояние; гонка поколений загрузки при смене фильтра (c3).
//
// Мокан только транспорт (offline.request) и signalr — тот же приём, что в WikiInbox.test.
// Модалка ConfirmDialog рендерится порталом в document.body — её узлы ищутся от body.
// Нативный <select> в fakeDom не живёт (react-dom при рендере читает select.options, чего у
// FNode нет): Select подменён кнопкой, зовущей onChange со значением из `sel` — тестам нужна
// смена фильтра.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) entrySourceRef — слать ref как есть (с диапазоном) → «карточка без диапазона» и «окна одного
//     чата» краснеют;
// (2) в retract не звать reReadOpenPage → «открытая страница перечитана» краснеет;
// (3) звать reReadOpenPage безусловно (без счётчика открытых) → «закрытая не перечитывается» краснеет;
// (4) звать doRetract прямо из кнопки, минуя подтверждение → «0 вызовов до подтверждения» краснеет;
// (5) не переводить отказ (показывать сырой код) → «400 показан текстом» краснеет;
// (6) убрать проверку loadSeq в .then → «гонка поколений» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiJournal } from '../WikiJournal';
import { WIKI_SUBTABS } from '../wikiTabs';

// Мок с сохранением остальных экспортов: цепочка мокованного доходит до кита хоста
// (WikiJournal → ./WikiInbox → ./WikiTab → kit) — full-replacement мок рвётся на
// первом недостающем имени. Паттерн — как в NotesPage.test.ts.
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
const sel = vi.hoisted(() => ({ value: '' }));
vi.mock('../../../../components/ui/Select', () => ({
  Select: ({ placeholder, onChange }: { placeholder?: string; onChange?: (v: string) => void }) =>
    createElement('button', { 'data-testid': `mock-select-${placeholder ?? ''}`, onClick: () => onChange?.(sel.value) }, placeholder ?? ''),
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

// Записи журнала — как их отдаёт GET /wiki/journal (контракт S7c-0): применённое «само»,
// применённое «через Входящие», retract (id пакетов в поле packages), отбитый проверками и
// системный relink без источника. Порядок — «новые сверху», как их отдаёт бэк.
const ENTRY_AUTO = {
  ts: '2026-10-30T03:12:00Z', op: 'apply', section: 'projects/p1', actor: 'keeper',
  source: refWindow(1, 9), origin: 'session', sourceName: 'Рефакторинг редактора видео',
  pages: ['projects/p1/page-a'], packages: [], status: 'applied', policy: 'auto',
  classes: ['session×append'],
};
const ENTRY_INBOX = {
  ...ENTRY_AUTO, ts: '2026-10-29T03:12:00Z', source: 'session:inbox-chat-0001#m1-4',
  sourceName: 'Спорный чат', policy: 'inbox',
};
const ENTRY_RETRACT = {
  ts: '2026-10-28T03:12:00Z', op: 'retract', section: 'projects/p1', actor: 'user',
  source: `session:${CHAT_ID}`, origin: 'session', sourceName: 'Рефакторинг редактора видео',
  pages: ['projects/p1/page-c'], packages: ['pkg-rev'], status: null, policy: null, classes: [],
};
const ENTRY_CHECKS = {
  ...ENTRY_AUTO, ts: '2026-10-27T03:12:00Z', sourceName: 'Отбитый проверками чат',
  status: 'rejected_by_checks',
};
const ENTRY_RELINK = {
  ts: '2026-10-26T03:12:00Z', op: 'relink', section: 'common', actor: 'keeper',
  source: null, origin: null, sourceName: null, pages: ['common/page-x'],
  packages: [], status: null, policy: null, classes: [],
};

const RESP = {
  entries: [ENTRY_AUTO, ENTRY_INBOX, ENTRY_RETRACT, ENTRY_CHECKS, ENTRY_RELINK],
  take: 200, linesRead: 5, invalidLines: 0,
};

// Два применённых окна одного чата (урок c1 S7c-1: сравнение целых ref давало 1 вместо 2)
const ENTRY_W2 = {
  ...ENTRY_AUTO, ts: '2026-10-30T03:20:00Z', source: refWindow(5, 9),
  pages: ['projects/p1/page-b'],
};
const RESP_WINDOWS = { entries: [ENTRY_W2, ENTRY_AUTO], take: 200, linesRead: 2, invalidLines: 0 };

const RETRACT_OK = {
  revertedPackages: ['pkg-auto'], unlinkedPackages: [], remainingPackages: [],
  removedChronicles: [], failed: [], markedSkip: true,
};

// Транспорт для тестов отката: журнал, ответ отката и страница — по адресу запроса;
// неоговорённый запрос — падение теста (образец serve в lib/__tests__/wiki.test.ts).
// Страница отдаёт «до отката», а после POST-отката — «после отката»: бэк после отката отдаёт
// возвращённое состояние, и перечитка обязана его принести
function serveJournal(pageResponses: Record<string, unknown>) {
  let retracted = false;
  req.mockImplementation((url: string, init?: { method?: string }) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method === 'POST') {
      if (url.includes('/retract')) {
        retracted = true;
        return Promise.resolve(RETRACT_OK);
      }
      return Promise.resolve(null);
    }
    if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
    if (url.startsWith('/wiki/page')) {
      const m = /[?&]page=([^&]+)/.exec(url);
      const key = m ? decodeURIComponent(m[1]) : '';
      if (key in pageResponses) {
        return Promise.resolve(retracted ? { pageKey: key, content: 'после отката' } : pageResponses[key]);
      }
    }
    return Promise.resolve(null);
  });
}

const roots: Root[] = [];

function seedJournal(data: unknown) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/journal')) return Promise.resolve(data);
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

// Точное совпадение текста: «Откатить всё» в модалке не должно схватить «Откатить всё из этого чата» строкой
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

function openEntry(c: AnyNode, index = 0) {
  const all = rows(c);
  click(all[index] ?? null, 'строка журнала');
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
  sel.value = '';
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

describe('«Журнал» (S7c-2)', () => {
  it('подвкладка «Журнал» в реестре ведёт на экран журнала', () => {
    const journal = WIKI_SUBTABS.find(t => t.id === 'journal')!;
    expect(journal.label).toBe('Журнал');
    expect(journal.component).toBe(WikiJournal);
  });

  it('данные — GET /wiki/journal; смена фильтра — новый запрос с параметром', async () => {
    seedJournal(RESP);
    const c = render();
    await settle();
    const first = getCalls('/wiki/journal');
    expect(first).toHaveLength(1);
    expect(String(first[0][0])).toBe('/wiki/journal');
    // фильтр источника — значение из закрытого перечня (бэк сужает окно сам, не фронт по списку)
    sel.value = 'note';
    click(buttonByTestId(c, 'mock-select-Все источники'), 'фильтр источника');
    await settle();
    const after = getCalls('/wiki/journal');
    expect(after).toHaveLength(2);
    expect(String(after[1][0])).toBe('/wiki/journal?origin=note');
    // фильтры накапливаются: второй добавляется к первому, а не заменяет его;
    // порядок параметров — детерминированный порядок loadWikiJournal (section, origin)
    sel.value = 'projects/p1';
    click(buttonByTestId(c, 'mock-select-Все разделы'), 'фильтр раздела');
    await settle();
    const third = getCalls('/wiki/journal');
    expect(third).toHaveLength(3);
    expect(String(third[2][0])).toBe('/wiki/journal?section=projects%2Fp1&origin=note');
  });

  it('политика по записи: applied+auto — «применилось само», applied+inbox — «через Входящие»; системный relink показан событием без источника', async () => {
    seedJournal(RESP);
    const c = render();
    await settle();
    // журнал — все события окна, ничего не отфильтровывается локально
    expect(rows(c)).toHaveLength(5);
    expect(has(c, 'применилось само')).toBe(true);
    expect(has(c, 'через Входящие')).toBe(true);
    expect(has(c, 'отбит проверками')).toBe(true);
    expect(has(c, 'откат по источнику')).toBe(true);
    expect(has(c, 'перелинкованы связи')).toBe(true);
  });

  it('имя источника вместо сырой карточки; без имени — карточка', async () => {
    const NO_NAME = {
      ...ENTRY_AUTO, ts: '2026-10-30T04:00:00Z', sourceName: null,
      source: 'session:9b000000-0000-0000-0000-000000000000#m1-2',
    };
    seedJournal({ ...RESP, entries: [NO_NAME, ...RESP.entries] });
    const c = render();
    await settle();
    // имя чата — данные, их узнаёт человек; сырой ref в списке нет
    expect(has(c, 'Рефакторинг редактора видео')).toBe(true);
    expect(has(c, 'session:9b000000-0000-0000-0000-000000000000#m1-2')).toBe(true);
  });

  it('запись журнала: класс операции и список изменённых страниц, без обещания результата', async () => {
    seedJournal(RESP);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    expect(has(c, 'Изменённые страницы')).toBe(true);
    expect(has(c, 'projects/p1/page-a')).toBe(true);
    expect(has(c, 'класс: session×append')).toBe(true);
  });

  it('нечитаемые строки — числом, они не притворяются записями', async () => {
    seedJournal({ ...RESP, invalidLines: 2 });
    const c = render();
    await settle();
    expect(has(c, 'Нечитаемых строк журнала: 2')).toBe(true);
  });

  it('отказ 400 показан текстом, не кодом', async () => {
    req.mockImplementation(() => Promise.reject({
      status: 400, body: { code: 'wiki_origin_key', message: 'источник «chat» не из закрытого перечня' },
    }));
    const c = render();
    await settle();
    expect(has(c, 'Источник не из списка')).toBe(true);
    expect(has(c, 'wiki_origin_key')).toBe(false);
  });

  it('отказ 403 показан текстом', async () => {
    // 403 ResolveSection — без тела: отличаем по статусу (хвост ревью S2f)
    req.mockImplementation(() => Promise.reject({ status: 403, body: null }));
    const c = render();
    await settle();
    expect(has(c, 'Нет доступа')).toBe(true);
  });

  it('отказ отката 404 «источника нет» показан текстом, не кодом', async () => {
    seedJournal(RESP);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') {
        return Promise.reject({ status: 404, body: { code: 'wiki_retract_source', message: 'такого источника в твоей вики нет' } });
      }
      if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
      return Promise.resolve(null);
    });
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    // отказ — русским текстом по коду (wikiLabels, S7c-2); сырой код не показан
    expect(has(c, 'Такого источника в твоей вики нет')).toBe(true);
    expect(has(c, 'wiki_retract_source')).toBe(false);
  });

  it('откат по источнику: до подтверждения — 0 вызовов REST; после — POST retract с карточкой без диапазона', async () => {
    seedJournal(RESP);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    // подтверждение спросили, бэка ещё не звали
    expect(postCalls()).toHaveLength(0);
    expect(has(docBody(), 'Снимется то, что Хранитель взял из этого источника')).toBe(true);
    expect(has(docBody(), '1 применённых записей')).toBe(true);
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(String(posts[0][0])).toContain('/wiki/packages/retract');
    // range «#m1-9» из ref бэка снят — бэк по карточке накрывает все окна чата
    expect(JSON.parse((posts[0][1] as { body: string }).body)).toMatchObject({
      sourceRef: `session:${CHAT_ID}`,
    });
  });

  it('окна одного чата: модалка до подтверждения считает все применённые записи источника', async () => {
    seedJournal(RESP_WINDOWS);
    const c = render();
    await settle();
    // самая свежая строка — окно m5-9; откат по нему снимает и окно m1-9 того же чата
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    // сравнение по нормальной форме источника, а не целым ref: два окна — «2», не «1»
    expect(has(docBody(), '2 применённых записей')).toBe(true);
    expect(has(docBody(), '1 применённых записей')).toBe(false);
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(JSON.parse((posts[0][1] as { body: string }).body)).toMatchObject({
      sourceRef: `session:${CHAT_ID}`,
    });
  });

  it('пока откат в пути, кнопка записи заблокирована', async () => {
    seedJournal(RESP);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    let resolvePost: (v: unknown) => void = () => {};
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return new Promise<unknown>(res => { resolvePost = res; });
      if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
      return Promise.resolve(null);
    });
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    // POST ещё висит: кнопка записи заблокирована (урок S5b/S5e)
    expect((reactProps(buttonByTestId(c, 'wiki-journal-retract')!) as { disabled?: boolean }).disabled).toBe(true);
    resolvePost(RETRACT_OK);
    await settle();
  });

  it('пусто — «Журнал пустой», а не пустой экран', async () => {
    seedJournal({ entries: [], take: 200, linesRead: 0, invalidLines: 0 });
    const c = render();
    await settle();
    expect(has(c, 'Журнал пустой')).toBe(true);
    expect(rows(c)).toHaveLength(0);
  });

  it('гонка поколений: смена фильтра с задержанным ответом — ответ прежнего запроса не возвращается', async () => {
    const A = { ...ENTRY_AUTO, sourceName: 'СПИСОК-А' };
    const B = { ...ENTRY_AUTO, ts: '2026-10-30T04:00:00Z', sourceName: 'СВЕЖИЙ-B' };
    const LATE = { ...ENTRY_AUTO, ts: '2026-10-30T05:00:00Z', sourceName: 'ЗАПАЗДАЛЫЙ-A' };
    let resolveLate: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/journal')) {
        n++;
        // первый (без фильтра) — задержан; второй (после смены фильтра) — отвечает сразу
        if (n === 1) return new Promise(res => { resolveLate = res; });
        return Promise.resolve({ entries: [B], take: 200, linesRead: 1, invalidLines: 0 });
      }
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    // первый ещё не ответил — экрана данных нет
    expect(rows(c)).toHaveLength(0);
    sel.value = 'projects/p1';
    click(buttonByTestId(c, 'mock-select-Все разделы'), 'фильтр раздела');
    await settle();
    expect(has(c, 'СВЕЖИЙ-B')).toBe(true);
    // поздний ответ прежнего запроса не затеряет новый список
    resolveLate({ entries: [LATE], take: 200, linesRead: 1, invalidLines: 0 });
    await settle();
    expect(has(c, 'СВЕЖИЙ-B')).toBe(true);
    expect(has(c, 'ЗАПАЗДАЛЫЙ-A')).toBe(false);
    void A;
  });
});

describe('«Журнал»: открытая страница после отката (доработка S7c-1)', () => {
  const SECTION = 'projects/p1';
  const PAGE = 'projects/p1/page-a';
  const PAGE_BEFORE = { pageKey: 'page-a', content: 'до отката' };

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

  it('открытая страница после отката по источнику перечитана и показывает состояние после отката', async () => {
    serveJournal({ [PAGE]: PAGE_BEFORE });
    const page = await openPage();
    expect(getWikiPageSnapshot(SECTION, PAGE)?.content).toBe('до отката');

    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const retracts = postCalls().filter(p => String(p[0]).includes('/retract'));
    expect(retracts).toHaveLength(1);
    // страница под открытым экраном перечитана и отдаёт состояние после отката
    expect(getWikiPageSnapshot(SECTION, PAGE)?.content).toBe('после отката');
    expect(getCalls('/wiki/page').length).toBeGreaterThanOrEqual(2);
    expect(probeText(page.probe)).toBe('после отката');
  });

  it('закрытая страница не перечитывается: после закрытия откат не зовёт её перечитку', async () => {
    serveJournal({ [PAGE]: PAGE_BEFORE });
    const page = await openPage();
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    // пока страница открыта — она перечитана
    expect(getWikiPageSnapshot(SECTION, PAGE)?.content).toBe('после отката');
    const readsWhileOpen = getCalls('/wiki/page').length;
    expect(readsWhileOpen).toBeGreaterThanOrEqual(2);

    // закрыли экран — откат больше не перечитывает закрытую (хвост «перечитка подняла
    // поколение для закрытой страницы»)
    act(() => page.root.unmount());
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    expect(postCalls().filter(p => String(p[0]).includes('/retract'))).toHaveLength(2);
    expect(getCalls('/wiki/page').length).toBe(readsWhileOpen);
  });
});
