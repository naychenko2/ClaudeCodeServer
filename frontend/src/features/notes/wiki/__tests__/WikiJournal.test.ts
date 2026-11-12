// Рендер-тест «Журнала» (S7c-2): данные — GET /wiki/journal (S7c-0), фильтры — параметры
// запроса; политика по status/policy записи («применилось само» / «через Входящие»); имя источника
// вместо сырой карточки; invalidLines — числом; отказы 400/403/404 — текстом, не кодом; подвкладка
// «Журнал» в реестре ведёт на этот экран; откат по источнику — только после подтверждения модалкой,
// карточка без диапазона; окна одного чата считаются по нормальной форме; кнопки заблокированы на
// время запроса; пустое состояние; гонка поколений загрузки при смене фильтра (c3);
// раздел из пропа sectionScope (W9b): с пропом — запрос с section и без Select раздела,
// 'all'/null — без section; снятие пропа — прежняя раскладка со своим Select; смена пропа —
// перезапрос и гонка поколений; отказ с пропом — текстом, как без пропа.
//
// Вид «Решения за ночь» (J-F2): переключатель вида и запрос view=night с окном последней
// ночи [вчера, завтра); группы 2/1/1/0 — ровно 3 заголовка и 4 строки операций, пустая группа
// не показывается; все пусты — «За ночь решений нет»; содержимое строки (claim, «почему»
// судьи, причина спора) и ссылка на страницу — раздел берётся из ключа страницы по границе
// «/»; «Откатить пакет» — только у принятых (у «Ждут судью» его нет), один POST с id пакета
// строки; отказ — текстом; гонка поколений между видами.
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
// (6) убрать проверку loadSeq в .then → «гонка поколений» краснеет;
// (7) подставить label вместо genitive в кнопку/заголовок модалки → «падеж кнопки и модалки» и
//     «откат по источнику» краснеют (доработка H4);
// (8) убрать countWord (всегда «N применённых записей») → «1 применённая запись» и
//     «2 применённые записи» краснеют (согласование чисел);
// (9) тост отката — прежний текст («N стр. очищено», «остались (другой источник)») → «тост отката:
//     согласование и контракт unlinked/remaining» краснеет (W4-4);
// (10) opText — вернуть сырой e.op для неизвестного кода → «неизвестный op» краснеет (W4-4);
// (11) убрать строку окна (take/linesRead) → «окно журнала» краснеет (W4-4);
// (12) снять проверку ключа в .then (loadSeq) → «гонка поколений фильтра «ИИ поменял моё»»
//     краснеет (F6b);
// (13) не слать overrodeHuman в loadWikiJournal (lib/wiki.ts) → «чип шлёт overrodeHuman=true»
//     краснеет (F6b);
// (14) не вести «Вернуть мою» на страницу (убрать requestOpenWikiPage) → ««Вернуть мою» ведёт
//     на страницу» краснеет (F6b).
// (16) снять проверку поколения в .then (loadSeq) → «смена пропа a → b» краснеет (W9b).
// (17) строка ночи не рендерит judgeReason/reason → «содержимое строки» краснеет (J-F2);
// (18) «Откатить пакет» во всех группах → «у „Ждут судью“ нет „Откатить пакет“» краснеет (J-F2);
// (19) не отфильтровывать пустые группы → «ровно 3 заголовка» краснеет (J-F2);
// (20) снять проверку поколения в .then вида ночи → «гонка видов» краснеет (J-F2);
// (21) pageSectionKey — startsWith без границы «/» → «с похожим префиксом» краснеет (J-F2).

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiJournal } from '../WikiJournal';
import { WIKI_SUBTABS } from '../wikiTabs';

// Тост отката уходит событием 'cc-local-toast' (lib/toast): шпион на showToast читает, что
// именно сказано человеку
const toast = vi.hoisted(() => ({ calls: [] as Array<[string, string, string | undefined]> }));
vi.mock('../../../../lib/toast', () => ({
  showToast: (title: string, body: string, kind?: string) => { toast.calls.push([title, body, kind]); },
}));

// Мок с сохранением остальных экспортов: цепочка мокованного доходит до кита хоста
// (WikiJournal → ./WikiInbox → kit) — full-replacement мок рвётся на
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
  __test as wikiStore, consumeOpenWikiPage, getWikiPageSnapshot, loadWikiPage, loadWikiTree, useWikiPage,
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
// неоговорённый запрос отвечает null (он не роняет тест сам: проверки — в утверждениях).
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
  toast.calls.length = 0;
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
    // кнопка — родительный падеж источника (хвост ревью H4): не «из этого чат»
    expect(textNodes(buttonByTestId(c, 'wiki-journal-retract')!).join('').trim()).toBe('Откатить всё из этого чата');
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    // заголовок модалки — тот же падеж
    expect(has(docBody(), 'Откатить всё из этого чата?')).toBe(true);
    // подтверждение спросили, бэка ещё не звали
    expect(postCalls()).toHaveLength(0);
    expect(has(docBody(), 'Снимется то, что Хранитель взял из этого источника')).toBe(true);
    expect(has(docBody(), '1 применённая запись')).toBe(true);
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
    // сравнение по нормальной форме источника, а не целым ref: два окна — «2», не «1»;
    // с «2» согласование «применённые записи» (согласование чисел, хвост ревью)
    expect(has(docBody(), '2 применённые записи')).toBe(true);
    expect(has(docBody(), '1 применённая запись')).toBe(false);
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(JSON.parse((posts[0][1] as { body: string }).body)).toMatchObject({
      sourceRef: `session:${CHAT_ID}`,
    });
  });

  it('падеж кнопки и модалки по типу источника: «из этой заметки», «из этого файла» (хвост ревью H4)', async () => {
    const NOTE = { ...ENTRY_AUTO, ts: '2026-10-30T06:10:00Z', origin: 'note', source: 'note:note-1', sourceName: 'Заметка о правке' };
    const FILE = { ...ENTRY_AUTO, ts: '2026-10-30T06:00:00Z', origin: 'file', source: 'file:frontend/src/app.ts', sourceName: null };
    seedJournal({ ...RESP, entries: [NOTE, FILE] });
    const c = render();
    await settle();
    openEntry(c, 0);
    await settle();
    expect(textNodes(buttonByTestId(c, 'wiki-journal-retract')!).join('').trim()).toBe('Откатить всё из этой заметки');
    click(buttonByTestId(c, 'wiki-journal-retract'), 'откат по заметке');
    await settle();
    expect(has(docBody(), 'Откатить всё из этой заметки?')).toBe(true);
    click(buttonByExactText(docBody(), 'Отмена'), 'закрыть модалку');
    await settle();
    click(buttonByTestId(c, 'wiki-journal-back'), 'назад к списку');
    await settle();
    openEntry(c, 1);
    await settle();
    expect(textNodes(buttonByTestId(c, 'wiki-journal-retract')!).join('').trim()).toBe('Откатить всё из этого файла');
    click(buttonByTestId(c, 'wiki-journal-retract'), 'откат по файлу');
    await settle();
    expect(has(docBody(), 'Откатить всё из этого файла?')).toBe(true);
  });

  it('согласование чисел в модалке: пять применённых записей — «5 применённых записей»', async () => {
    // пять окон одного чата — все применённые: форма «5+» той же согласованной цепочки
    const windowEntry = (from: number, to: number, i: number) => ({
      ...ENTRY_AUTO, ts: `2026-10-30T03:${20 - i}:00Z`, source: refWindow(from, to),
      pages: [`projects/p1/page-w${i}`],
    });
    seedJournal({
      entries: [windowEntry(9, 10, 1), windowEntry(7, 8, 2), windowEntry(5, 6, 3), windowEntry(3, 4, 4), windowEntry(1, 2, 5)],
      take: 200, linesRead: 5, invalidLines: 0,
    });
    const c = render();
    await settle();
    openEntry(c, 0);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'откат по чату');
    await settle();
    expect(has(docBody(), '5 применённых записей')).toBe(true);
    expect(has(docBody(), '5 применённые записи')).toBe(false);
  });

  // Откат по источнику с заданным исходом: открыть запись, подтвердить, вернуть текст тоста
  async function retractToast(result: unknown): Promise<[string, string, string | undefined]> {
    seedJournal(RESP);
    const c = render();
    await settle();
    openEntry(c);
    await settle();
    click(buttonByTestId(c, 'wiki-journal-retract'), 'Откатить всё из этого чата');
    await settle();
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return Promise.resolve(result);
      if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
      return Promise.resolve(null);
    });
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтверждение отката');
    await settle();
    expect(toast.calls).toHaveLength(1);
    return toast.calls[0];
  }

  it('тост отката: числа согласованы, а unlinked/remaining — это пакеты, не страницы и не «другой источник»', async () => {
    const [title, body, kind] = await retractToast({
      revertedPackages: ['a'], unlinkedPackages: ['b'], remainingPackages: ['c'],
      removedChronicles: ['h'], failed: [], markedSkip: false,
    });
    expect(title).toBe('Откат по источнику');
    expect(body).toBe('1 пакет откатан, 1 пакет очищен от сносок, 1 хроника снята, 1 пакет остался с необработанными страницами.');
    expect(kind).toBe('success');
  });

  it('тост отката: «2 — осталось/очищено», «5 — откатано», пометка и частичный исход', async () => {
    const [, body, kind] = await retractToast({
      revertedPackages: ['a', 'b', 'c', 'd', 'e'], unlinkedPackages: ['f', 'g'], remainingPackages: ['h', 'i'],
      removedChronicles: [], failed: ['страница x не обработана'], markedSkip: true,
    });
    expect(body).toBe(
      '5 пакетов откатано, 2 пакета очищено от сносок, 2 пакета осталось с необработанными страницами, '
      + 'источник помечен «не разбирать». Частично: страница x не обработана',
    );
    expect(kind).toBe('error');
    expect(body).not.toContain('стр.');
    expect(body).not.toContain('другой источник');
  });

  it('неизвестный op получает нейтральную подпись, а не сырое слово бэка', async () => {
    const FUTURE = { ...ENTRY_RELINK, op: 'future_op_x' };
    seedJournal({ ...RESP, entries: [FUTURE] });
    const c = render();
    await settle();
    expect(has(c, 'другое событие')).toBe(true);
    expect(has(c, 'future_op_x')).toBe(false);
  });

  it('окно журнала видно: take и linesRead; нечитаемые строки — по всему окну, не по фильтру', async () => {
    seedJournal({ ...RESP, take: 200, linesRead: 7, invalidLines: 2 });
    const c = render();
    await settle();
    expect(has(c, 'последние 200 строк, прочитано 7; показано записей: 5')).toBe(true);
    expect(has(c, 'по всему прочитанному окну (7 строк)')).toBe(true);
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

// ─── «ИИ поменял моё» (F6b): серверный фильтр, вид действия в строке, «Вернуть мою» ───
// Мок — серверный фильтр: с overrodeHuman=true ответ несёт только помеченные записи (бэк
// сужает окно сам, фронт по списку не фильтрует). Фикстура: 5 записей, из них 2 overrode —
// «заменил» и «оспорил». Пара statement/previous в строке журнала не несётся: «Вернуть мою»
// ведёт на страницу каналом requestOpenWikiPage (стор держит один запрос).

const ENTRY_REPLACED = { ...ENTRY_AUTO, ts: '2026-10-30T05:48:00Z', overrodeHuman: true, overrodeKind: 'replaced' };
const ENTRY_DISPUTED = { ...ENTRY_INBOX, ts: '2026-10-30T05:31:00Z', overrodeHuman: true, overrodeKind: 'disputed' };
const RESP_MIX = {
  entries: [ENTRY_REPLACED, ENTRY_DISPUTED, ENTRY_AUTO, ENTRY_CHECKS, ENTRY_RELINK],
  take: 200, linesRead: 5, invalidLines: 0,
};
const RESP_OVR = { entries: [ENTRY_REPLACED, ENTRY_DISPUTED], take: 200, linesRead: 5, invalidLines: 0 };

function seedMixed() {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/journal')) {
      return Promise.resolve(String(url).includes('overrodeHuman=true') ? RESP_OVR : RESP_MIX);
    }
    return Promise.resolve(null);
  });
}

function chipNode(container: AnyNode): AnyNode | null {
  return allNodes(container).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').includes('ИИ поменял моё')) ?? null;
}

describe('«Журнал»: «ИИ поменял моё» (F6b)', () => {
  it('чип шлёт overrodeHuman=true; из 5 записей серверного фильтра остаёт 2 с метками вида и кнопками', async () => {
    seedMixed();
    const c = render();
    await settle();
    expect(rows(c)).toHaveLength(5);
    // число на чипе — overrode-записи текущего окна (тот же хвост, что с фильтром и без)
    const chip = chipNode(c);
    expect(chip).not.toBeNull();
    expect(textNodes(chip!).join('')).toContain('2');
    click(chip, 'чип «ИИ поменял моё»');
    await settle();
    const calls = getCalls('/wiki/journal');
    expect(calls).toHaveLength(2);
    // мутация (13): не слать overrodeHuman в loadWikiJournal — этот адрес краснеет
    expect(String(calls[1][0])).toBe('/wiki/journal?overrodeHuman=true');
    // фронт показывает ответ, а не фильтрует список сам
    expect(rows(c)).toHaveLength(2);
    expect(has(c, 'заменил')).toBe(true);
    expect(has(c, 'оспорил')).toBe(true);
    expect(buttonByTestId(c, 'wiki-journal-restore')).not.toBeNull();
    expect(buttonByTestId(c, 'wiki-journal-open-page')).not.toBeNull();
    // снятие чипа — запрос без параметра, список целый
    click(chipNode(c), 'чип «ИИ поменял моё», снятие');
    await settle();
    expect(String(getCalls('/wiki/journal')[2][0])).toBe('/wiki/journal');
    expect(rows(c)).toHaveLength(5);
  });

  it('фильтр накапливается с фильтром раздела: section и overrodeHuman в одном запросе', async () => {
    seedMixed();
    const c = render();
    await settle();
    click(chipNode(c), 'чип «ИИ поменял моё»');
    await settle();
    sel.value = 'projects/p1';
    click(buttonByTestId(c, 'mock-select-Все разделы'), 'фильтр раздела');
    await settle();
    const calls = getCalls('/wiki/journal');
    expect(calls).toHaveLength(3);
    // порядок параметров — детерминированный порядок loadWikiJournal (section, …, overrodeHuman)
    expect(String(calls[2][0])).toBe('/wiki/journal?section=projects%2Fp1&overrodeHuman=true');
  });

  it('«Вернуть мою» ведёт на страницу; двойной клик — одно открытие', async () => {
    seedMixed();
    const c = render();
    await settle();
    click(chipNode(c), 'чип «ИИ поменял моё»');
    await settle();
    // мутация (14): убрать requestOpenWikiPage из rowOpenPage — consume ниже даёт null
    click(buttonByTestId(c, 'wiki-journal-restore'), 'Вернуть мою');
    expect(consumeOpenWikiPage()).toMatchObject({ section: 'projects/p1', path: 'projects/p1/page-a' });
    // двойной клик: стор замещает запрос, а не копит дубли — второе consume пусто
    click(buttonByTestId(c, 'wiki-journal-restore'), 'Вернуть мою, второй клик');
    click(buttonByTestId(c, 'wiki-journal-restore'), 'Вернуть мою, третий клик');
    expect(consumeOpenWikiPage()).toMatchObject({ section: 'projects/p1', path: 'projects/p1/page-a' });
    expect(consumeOpenWikiPage()).toBeNull();
  });

  it('«Открыть страницу» спора ведёт на ту же страницу: возвращать нечего, текст человека на месте', async () => {
    seedMixed();
    const c = render();
    await settle();
    click(chipNode(c), 'чип «ИИ поменял моё»');
    await settle();
    click(buttonByTestId(c, 'wiki-journal-open-page'), 'Открыть страницу');
    expect(consumeOpenWikiPage()).toMatchObject({ section: 'projects/p1', path: 'projects/p1/page-a' });
  });

  it('запись вида показывает метку и в раскрытой записи', async () => {
    seedMixed();
    const c = render();
    await settle();
    click(chipNode(c), 'чип «ИИ поменял моё»');
    await settle();
    openEntry(c, 0);
    await settle();
    expect(has(c, 'заменил')).toBe(true);
  });

  // TL-4: «Вернуть мою» ведёт на overridePageKey; reverted — без метки и кнопки; без страниц — без кнопки
  // Мутация (15): вернуть pages[0] в rowOpenPage → тест «замена на второй странице» краснеет
  async function openOnly(entry: Record<string, unknown>) {
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/journal')) {
        return Promise.resolve(String(url).includes('overrodeHuman=true')
          ? { entries: [entry], take: 200, linesRead: 1, invalidLines: 0 }
          : RESP_MIX);
      }
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    click(chipNode(c), 'чип «ИИ поменял моё»');
    await settle();
    return c;
  }

  it('замена на второй странице: «Вернуть мою» ведёт на overridePageKey, а не на pages[0]', async () => {
    const c = await openOnly({ ...ENTRY_REPLACED, pages: ['projects/p1/page-a', 'projects/p1/page-b'], overridePageKey: 'projects/p1/page-b', overrideReverted: false });
    click(buttonByTestId(c, 'wiki-journal-restore'), 'Вернуть мою');
    expect(consumeOpenWikiPage()).toMatchObject({ section: 'projects/p1', path: 'projects/p1/page-b' });
  });

  it('overridePageKey = null: ведёт на pages[0], как раньше', async () => {
    const c = await openOnly({ ...ENTRY_REPLACED, pages: ['projects/p1/page-a', 'projects/p1/page-b'], overridePageKey: null });
    click(buttonByTestId(c, 'wiki-journal-restore'), 'Вернуть мою');
    expect(consumeOpenWikiPage()).toMatchObject({ path: 'projects/p1/page-a' });
  });

  it('overrideReverted: нет метки «заменил» и кнопки, запись остаётся в списке', async () => {
    const c = await openOnly({ ...ENTRY_REPLACED, overridePageKey: 'projects/p1/page-a', overrideReverted: true });
    expect(rows(c)).toHaveLength(1);
    expect(has(c, 'заменил')).toBe(false);
    expect(buttonByTestId(c, 'wiki-journal-restore')).toBeNull();
    expect(buttonByTestId(c, 'wiki-journal-open-page')).toBeNull();
  });

  it('запись без страниц: кнопки «Вернуть мою» нет', async () => {
    const c = await openOnly({ ...ENTRY_REPLACED, pages: [], overridePageKey: null, overrideReverted: false });
    expect(rows(c)).toHaveLength(1);
    expect(buttonByTestId(c, 'wiki-journal-restore')).toBeNull();
  });

  it('нераспознанный вид overrodeHuman остаётся без метки и кнопки: словарь не выдумывает смысл', async () => {
    const UNKNOWN = { ...ENTRY_AUTO, ts: '2026-10-30T05:00:00Z', overrodeHuman: true, overrodeKind: 'future_kind' };
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/journal')) {
        return Promise.resolve(String(url).includes('overrodeHuman=true')
          ? { entries: [UNKNOWN], take: 200, linesRead: 5, invalidLines: 0 }
          : RESP_MIX);
      }
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    click(chipNode(c), 'чип «ИИ поменял моё»');
    await settle();
    expect(rows(c)).toHaveLength(1);
    expect(has(c, 'заменил')).toBe(false);
    expect(has(c, 'оспорил')).toBe(false);
    expect(buttonByTestId(c, 'wiki-journal-restore')).toBeNull();
    expect(buttonByTestId(c, 'wiki-journal-open-page')).toBeNull();
  });

  it('гонка поколений фильтра «ИИ поменял моё»: задержанный ответ без фильтра не затеряет отфильтрованный', async () => {
    const LATE = { ...ENTRY_AUTO, ts: '2026-10-30T06:00:00Z', sourceName: 'ЗАПАЗДАЛЫЙ-A' };
    let resolveLate: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/journal')) {
        n++;
        // первый (без фильтра) — задержан; второй (после чипа) — отвечает сразу
        if (n === 1) return new Promise(res => { resolveLate = res; });
        return Promise.resolve(RESP_OVR);
      }
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    expect(rows(c)).toHaveLength(0);
    click(chipNode(c), 'чип «ИИ поменял моё»');
    await settle();
    expect(rows(c)).toHaveLength(2);
    // мутация (12): снять проверку loadSeq в .then — поздний ответ перезатрёт список
    resolveLate({ ...RESP_MIX, entries: [LATE, ...RESP_MIX.entries] });
    await settle();
    expect(rows(c)).toHaveLength(2);
    expect(has(c, 'ЗАПАЗДАЛЫЙ-A')).toBe(false);
  });
});

// ─── W9b: раздел из пропа (панель: раздел выбирает чип в шапке, Select в теле дублировал бы его) ───
// Смена пропа — ререндер того же корня: родитель (панель) рендерит подвкладку с новым
// sectionScope; состояние компонента сохраняется, эффект с новым действующим разделом
// перезапрашивает журнал.

describe('«Журнал»: раздел из пропа sectionScope (W9b)', () => {
  function renderScoped(scope: string | null | 'all' | undefined): { root: Root; container: Cont } {
    installFakeDom();
    const { root, container } = mount(createElement(WikiJournal, { sectionScope: scope }));
    roots.push(root);
    return { root, container };
  }

  it('проп задан: запрос с section, Select раздела нет; источник и чип на месте; без пропа — прежний вид', async () => {
    seedJournal(RESP);
    const { root, container } = renderScoped('projects/p1');
    await settle();
    const first = getCalls('/wiki/journal');
    expect(first).toHaveLength(1);
    expect(String(first[0][0])).toBe('/wiki/journal?section=projects%2Fp1');
    // раздел выбирает чип в шапке панели — дублирующий Select в теле не рисуется
    expect(buttonByTestId(container, 'mock-select-Все разделы')).toBeNull();
    // остальные фильтры живы
    expect(buttonByTestId(container, 'mock-select-Все источники')).not.toBeNull();
    expect(chipNode(container)).not.toBeNull();
    expect(rows(container)).toHaveLength(5);
    // 'all' — весь журнал: новый запрос без section (действующий раздел изменился)
    act(() => root.render(createElement(WikiJournal, { sectionScope: 'all' })));
    await settle();
    expect(String(getCalls('/wiki/journal')[1][0])).toBe('/wiki/journal');
    // null — тот же действующий раздел, что 'all': эффекта нет — лишнего запроса нет
    // (эффект держит действующий раздел, а не проп как таковой)
    act(() => root.render(createElement(WikiJournal, { sectionScope: null })));
    await settle();
    expect(getCalls('/wiki/journal')).toHaveLength(2);
    // проп снят (старая раскладка без флага) — свой Select на месте; действующий раздел снова
    // «все» — лишнего запроса нет, данные на экране те же
    act(() => root.render(createElement(WikiJournal, { sectionScope: undefined })));
    await settle();
    expect(buttonByTestId(container, 'mock-select-Все разделы')).not.toBeNull();
    expect(getCalls('/wiki/journal')).toHaveLength(2);
    expect(rows(container)).toHaveLength(5);
  });

  it('смена пропа a → b: ответ прежнего раздела не перезатрёт новый', async () => {
    const B = { ...ENTRY_AUTO, ts: '2026-10-30T04:00:00Z', sourceName: 'СВЕЖИЙ-B' };
    const LATE = { ...ENTRY_AUTO, ts: '2026-10-30T05:00:00Z', sourceName: 'ЗАПАЗДАЛЫЙ-A' };
    let resolveLate: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/journal')) {
        n++;
        // первый (section 'projects/p1') задержан; второй (после смены пропа на 'common') отвечает сразу
        if (n === 1) return new Promise(res => { resolveLate = res; });
        return Promise.resolve({ entries: [B], take: 200, linesRead: 1, invalidLines: 0 });
      }
      return Promise.resolve(null);
    });
    const { root, container } = renderScoped('projects/p1');
    await settle();
    const first = getCalls('/wiki/journal');
    expect(first).toHaveLength(1);
    expect(String(first[0][0])).toBe('/wiki/journal?section=projects%2Fp1');
    // первый ещё не ответил — экрана данных нет
    expect(rows(container)).toHaveLength(0);
    // панель меняет раздел, пока прежний запрос в пути
    act(() => root.render(createElement(WikiJournal, { sectionScope: 'common' })));
    await settle();
    const second = getCalls('/wiki/journal');
    expect(second).toHaveLength(2);
    expect(String(second[1][0])).toBe('/wiki/journal?section=common');
    expect(has(container, 'СВЕЖИЙ-B')).toBe(true);
    // мутация (16): снять проверку loadSeq в .then — запоздалый ответ 'projects/p1'
    // перезатрёт 'common' и этот assert краснеет
    resolveLate({ entries: [LATE], take: 200, linesRead: 1, invalidLines: 0 });
    await settle();
    expect(has(container, 'СВЕЖИЙ-B')).toBe(true);
    expect(has(container, 'ЗАПАЗДАЛЫЙ-A')).toBe(false);
  });

  it('отказ с пропом показан текстом, как без пропа', async () => {
    req.mockImplementation(() => Promise.reject({ status: 403, body: null }));
    const { container } = renderScoped('projects/p1');
    await settle();
    expect(has(container, 'Нет доступа')).toBe(true);
  });
});

// ─── Вид «Решения за ночь» (J-F2) ───
// Строка ночи — операция пакета с вердиктом (контракт J-F0): раздела в ней нет — только ключ
// страницы, поэтому ссылка проверяет и derivation раздела из дерева по границе «/». Дерево
// поднимается до рендера: без него ссылка на страницу не строится (и это тоже проверяется).
// Окно запроса — локальные [вчера, завтра): asserted не точными датами (полночь посреди
// теста сдвинула бы их), а границей: from ≤ сегодня ≤ to и ровно 2 дня между ними.

const ND_ACCEPTED_A = {
  packageId: 'pkg-a1', page: 'projects/p1/page-a', claim: 'Утверждение А',
  reasonCode: 'source_not_newer', judgeDecision: 'accepted', judgeReason: 'подтверждено источником',
  judgeAt: '2026-11-12T02:10:00Z', judgeCostUsd: 0.0012, judgeModel: 'claude-x',
  reason: 'заменить мою формулировку',
};
const ND_ACCEPTED_B = { ...ND_ACCEPTED_A, packageId: 'pkg-a2', page: 'projects/p1/page-b', claim: 'Утверждение Б', judgeAt: '2026-11-12T02:14:00Z' };
const ND_REJECTED = { ...ND_ACCEPTED_A, packageId: 'pkg-r1', page: 'common/page-c', claim: 'Утверждение В', judgeDecision: 'rejected', judgeReason: 'противоречит странице' };
// Отказ кода (в т.ч. «судья не вынес решения за 3 попытки»): judgeModel='code', не вердикт модели
const ND_CODE = { ...ND_ACCEPTED_A, packageId: 'pkg-c1', page: 'common/page-d', claim: null, judgeReason: 'судья не вынес решения за 3 попытки', judgeModel: 'code' };
const ND_WAITING = {
  ...ND_ACCEPTED_A, packageId: 'pkg-w1', page: 'projects/p1/page-e', claim: 'Утверждение Г',
  judgeDecision: null, judgeReason: null, judgeAt: null, judgeCostUsd: null, judgeModel: null,
  reason: 'ждёт решения',
};

// Фикстура c1: группы 2/1/1/0
const NIGHT_2110 = { judgeAccepted: [ND_ACCEPTED_A, ND_ACCEPTED_B], judgeRejected: [ND_REJECTED], codeRejected: [ND_CODE], waiting: [] };
const NIGHT_EMPTY = { judgeAccepted: [], judgeRejected: [], codeRejected: [], waiting: [] };
const NIGHT_ONLY_WAITING = { ...NIGHT_EMPTY, waiting: [ND_WAITING] };

// Дерево для ссылки на страницу: 'projects/p10' — раздел с похожим префиксом: граница
// ключа («/»), а не начало строки
const TREE_NIGHT = {
  initialized: true,
  sections: [
    { key: 'projects/p1', name: 'AI Home' },
    { key: 'projects/p10', name: 'Другой проект' },
    { key: 'common', name: null },
  ],
};

function serveNight(data: unknown) {
  req.mockImplementation((url: string) => {
    if (url === '/wiki/tree') return Promise.resolve(TREE_NIGHT);
    if (String(url).includes('view=night')) return Promise.resolve(data);
    if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
    return Promise.resolve(null);
  });
}

// Данные вида + поднятое дерево — до рендера (тот же порядок, что openPage выше)
async function seedNight(data: unknown) {
  serveNight(data);
  await act(async () => { await loadWikiTree(); });
}

function nightRows(container: AnyNode): AnyNode[] {
  return allNodes(container).filter(n => n.getAttribute('data-testid') === 'wiki-night-row');
}
function nightGroupCount(container: AnyNode): number {
  return allNodes(container).filter(n => n.getAttribute('data-testid') === 'wiki-night-group').length;
}
function segmentBtn(container: AnyNode, label: string): AnyNode | null {
  return allNodes(container).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === label) ?? null;
}
async function toNightView(c: AnyNode) {
  click(segmentBtn(c, 'Решения за ночь'), 'сегмент «Решения за ночь»');
  await settle();
}

describe('«Журнал»: вид «Решения за ночь» (J-F2)', () => {
  it('переключатель вида: «Решения за ночь» просит view=night с окном последней ночи; «Все записи» — обычный журнал', async () => {
    await seedNight(NIGHT_2110);
    const c = render();
    await settle();
    // дефолтный вид — прежний журнал: свои строки, ночных нет
    expect(rows(c)).toHaveLength(5);
    expect(nightRows(c)).toHaveLength(0);
    click(segmentBtn(c, 'Решения за ночь'), 'сегмент «Решения за ночь»');
    await settle();
    const calls = getCalls('/wiki/journal');
    expect(calls).toHaveLength(2);
    const m = /^\/wiki\/journal\?view=night&from=(\d{4}-\d{2}-\d{2})&to=(\d{4}-\d{2}-\d{2})$/.exec(String(calls[1][0]));
    expect(m).not.toBeNull();
    // окно накрывает ночь через полночь: from ≤ сегодня ≤ to, ровно 2 дня между ними
    const from = Date.parse(`${m![1]}T00:00:00Z`);
    const to = Date.parse(`${m![2]}T00:00:00Z`);
    const DAY = 86_400_000;
    const now = new Date();
    const today = Date.UTC(now.getFullYear(), now.getMonth(), now.getDate());
    expect(from).toBeLessThanOrEqual(today);
    expect(to).toBeGreaterThanOrEqual(today);
    expect(to - from).toBe(2 * DAY);
    expect(nightRows(c)).toHaveLength(4);
    // возврат к журналу — обычный запрос без view, его строки на месте
    click(segmentBtn(c, 'Все записи'), 'сегмент «Все записи»');
    await settle();
    expect(String(getCalls('/wiki/journal')[2][0])).toBe('/wiki/journal');
    expect(rows(c)).toHaveLength(5);
    expect(nightRows(c)).toHaveLength(0);
  });

  it('группы 2/1/1/0: ровно 3 заголовка групп и 4 строки операций; «Ждут судью» не показана', async () => {
    await seedNight(NIGHT_2110);
    const c = render();
    await settle();
    await toNightView(c);
    // мутация (19): не отфильтровывать пустые группы — «Ждут судью» добавилась бы четвёртой
    expect(nightGroupCount(c)).toBe(3);
    expect(nightRows(c)).toHaveLength(4);
    expect(has(c, 'Принято судьёй')).toBe(true);
    expect(has(c, 'Отклонено судьёй')).toBe(true);
    expect(has(c, 'Отклонено кодом')).toBe(true);
    expect(has(c, 'Ждут судью')).toBe(false);
  });

  it('все группы пусты — «За ночь решений нет», а не пустой экран', async () => {
    await seedNight(NIGHT_EMPTY);
    const c = render();
    await settle();
    await toNightView(c);
    expect(has(c, 'За ночь решений нет')).toBe(true);
    expect(nightGroupCount(c)).toBe(0);
    expect(nightRows(c)).toHaveLength(0);
  });

  it('содержимое строки: claim, «почему» судьи и причина спора; ссылка открывает страницу', async () => {
    await seedNight(NIGHT_2110);
    const c = render();
    await settle();
    await toNightView(c);
    // мутация (17): строка не рендерит judgeReason/reason — эти два assert краснеют
    expect(has(c, 'Утверждение А')).toBe(true);
    expect(has(c, 'почему: подтверждено источником')).toBe(true);
    expect(has(c, 'причина спора: заменить мою формулировку')).toBe(true);
    // раздела в строке нет — он берётся из ключа страницы; открытие — существующий канал
    click(buttonByTestId(c, 'wiki-night-open'), 'ссылка на страницу');
    expect(consumeOpenWikiPage()).toMatchObject({ section: 'projects/p1', path: 'projects/p1/page-a' });
  });

  it('страница раздела с похожим префиктом: раздел берётся по границе ключа, а не по началу строки', async () => {
    // 'projects/p1x' — не раздел: 'projects/p1' — начало его ключа, но не граница.
    // Без границы «/» строка получила бы ссылку в чужой раздел.
    const P1X = { ...ND_ACCEPTED_A, page: 'projects/p1x/y.md' };
    await seedNight({ ...NIGHT_EMPTY, judgeAccepted: [P1X] });
    const c = render();
    await settle();
    await toNightView(c);
    // мутация (21): startsWith без границы «/» — 'projects/p1' совпал бы, и этот assert краснеет
    expect(buttonByTestId(c, 'wiki-night-open')).toBeNull();
    expect(has(c, 'projects/p1x/y.md')).toBe(true);
  });

  it('граница пропускает похожий префикс раздела: «projects/p10» открывается в своём разделе', async () => {
    const P10 = { ...ND_ACCEPTED_A, page: 'projects/p10/x.md' };
    await seedNight({ ...NIGHT_EMPTY, judgeAccepted: [P10] });
    const c = render();
    await settle();
    await toNightView(c);
    click(buttonByTestId(c, 'wiki-night-open'), 'ссылка на страницу p10');
    expect(consumeOpenWikiPage()).toMatchObject({ section: 'projects/p10', path: 'projects/p10/x.md' });
  });

  it('страница раздела, которого нет в дереве: без ссылки, ключ показан текстом', async () => {
    const GHOST = { ...ND_ACCEPTED_A, page: 'projects/p9/ghost.md' };
    await seedNight({ ...NIGHT_EMPTY, judgeAccepted: [GHOST] });
    const c = render();
    await settle();
    await toNightView(c);
    expect(nightRows(c)).toHaveLength(1);
    expect(buttonByTestId(c, 'wiki-night-open')).toBeNull();
    expect(has(c, 'projects/p9/ghost.md')).toBe(true);
  });

  it('«Откатить пакет»: один POST с id пакета строки; кнопка — только у принятых судьёй', async () => {
    serveNight(NIGHT_2110);
    await act(async () => { await loadWikiTree(); });
    // откат отвечает успехом: список перечитывается после него
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return Promise.resolve({ restoredPages: ['projects/p1/page-a'] });
      if (url === '/wiki/tree') return Promise.resolve(TREE_NIGHT);
      if (String(url).includes('view=night')) return Promise.resolve(NIGHT_2110);
      if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    await toNightView(c);
    // 2 принятые строки — 2 кнопки; в отклонённых и отбитых кодом кнопки нет (откатывать нечего)
    const revertBtns = allNodes(c).filter(n => n.getAttribute('data-testid') === 'wiki-night-revert');
    expect(revertBtns).toHaveLength(2);
    click(revertBtns[0], 'Откатить пакет');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(String(posts[0][0])).toBe('/wiki/packages/pkg-a1/revert');
    // тост — согласованный итог по возвращённым страницам
    expect(toast.calls).toHaveLength(1);
    expect(toast.calls[0][1]).toBe('1 страница возвращена');
  });

  it('у «Ждут судью» нет «Откатить пакет»: их ещё не применили', async () => {
    await seedNight(NIGHT_ONLY_WAITING);
    const c = render();
    await settle();
    await toNightView(c);
    expect(has(c, 'Ждут судью')).toBe(true);
    expect(nightRows(c)).toHaveLength(1);
    // мутация (18): «Откатить пакет» во всех группах — этот assert краснеет
    expect(buttonByTestId(c, 'wiki-night-revert')).toBeNull();
  });

  it('отказ отката показан текстом, не кодом', async () => {
    serveNight(NIGHT_2110);
    await act(async () => { await loadWikiTree(); });
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') {
        return Promise.reject({ status: 400, body: { code: 'wiki_revert_status', message: 'пакет в «open»: откатывают только применённый пакет' } });
      }
      if (url === '/wiki/tree') return Promise.resolve(TREE_NIGHT);
      if (String(url).includes('view=night')) return Promise.resolve(NIGHT_2110);
      if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    await toNightView(c);
    click(buttonByTestId(c, 'wiki-night-revert'), 'Откатить пакет');
    await settle();
    expect(has(c, 'Пакет в этом состоянии откатать нельзя')).toBe(true);
    expect(has(c, 'wiki_revert_status')).toBe(false);
  });

  it('гонка видов: запоздалый ответ прежней ночи не перезатрёт свежие данные', async () => {
    let resolveLate: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string) => {
      if (url === '/wiki/tree') return Promise.resolve(TREE_NIGHT);
      if (String(url).includes('view=night')) {
        n++;
        // первая ночь — задержана; вторая (после возврата к журналу и обратно) отвечает сразу
        if (n === 1) return new Promise(res => { resolveLate = res; });
        return Promise.resolve(NIGHT_EMPTY);
      }
      if (url.startsWith('/wiki/journal')) return Promise.resolve(RESP);
      return Promise.resolve(null);
    });
    await act(async () => { await loadWikiTree(); });
    const c = render();
    await settle();
    click(segmentBtn(c, 'Решения за ночь'), 'сегмент «Решения за ночь»');
    await settle();
    expect(nightRows(c)).toHaveLength(0); // первый ещё не ответил
    click(segmentBtn(c, 'Все записи'), 'сегмент «Все записи»');
    await settle();
    expect(rows(c)).toHaveLength(5);
    click(segmentBtn(c, 'Решения за ночь'), 'сегмент ещё раз');
    await settle();
    expect(nightRows(c)).toHaveLength(0); // свежий ответ — пустое окно
    // мутация (20): снять проверку поколения в .then — запоздалый ответ дорисует 4 строки
    resolveLate(NIGHT_2110);
    await settle();
    expect(nightRows(c)).toHaveLength(0);
  });
});
