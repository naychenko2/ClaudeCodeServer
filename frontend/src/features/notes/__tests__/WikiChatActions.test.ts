// Тест логики действий чата (S7b): флаг выключен — ни контролов, ни запросов вики;
// «Разобрать» — POST analyze, 200 → исход текстом; 409 task_execution/marked_skip — текст
// отказа по коду (wikiErrorText), не сырой код; 202 → опрос analyze-status до running:false и
// показ last.outcome, и после завершенного опроса — перечитка chroniclePath (кнопка
// «Открыть хронику» без перезагрузки); «идёт» восстанавливается при монтировании без клика;
// двойное нажатие — один запрос; гард поколения — ответ прежнего чата (и POST, и опроса)
// не попадает на экран нового; «Откатить всё» — REST только после подтверждения модалкой,
// sourceRef без диапазона окна, итог — согласованный по числу («1 пакет откатан»,
// «2 пакета откатано», «5 пакетов откатано»); requestNoteIngest — POST ingest/note с noteId,
// 409 packages_limit, 404 и 400 wiki_note_id — текстом, не бросает.
// «Открыть хронику» (OPEN-1): клик — запрос в стор вики (section — 'projects/{projectId}'
// из session.projectId, path — chroniclePath из списка) + переход в «Заметки» каналом
// 'cc-open-note'; без projectId чата — ни запроса, ни перехода. Открытие самой страницы —
// в WikiTab.test (там же и «запрос ждёт загрузки дерева»).
// Плюс достижимость из UI: РЕАЛЬНАЯ шапка чата (ChatHeaderBar) с РЕАЛЬНЫМ манифестом «Заметок»
// рисует вклад wiki-chat-actions в ряду действий (десктоп и мобильная ветка); при выключенном
// флаге — нет (п.1 вердикта ревью 98278913).
//
// Стор вики и флаги — настоящие; мокан только транспорт (offline.request) и signalr —
// тот же приём, что в WikiJournal.test. Модалка ConfirmDialog — портал в document.body.
// Шапка монтируется через renderToStaticMarkup (паттерн stripShortcuts.test): эффекты в SSR
// не работают — ни одного запроса; проверяется ровно состав ряда.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) убрать гард seq в analyze .then → «ответ прежнего чата» краснеет;
// (2) звать doRetract прямо из кнопки, минуя подтверждение → «0 вызовов до подтверждения» краснеет;
// (3) послать sourceRef с диапазоном («session:{id}#m1-9») → «карточка без диапазона» краснеет;
// (4) показать сырой код вместо wikiErrorText → «409 текстом» краснеет;
// (5) в опросе не останавливаться на running:false (сcheduling всегда) → «опрос останавливается» краснеет;
// (6) убрать useSlotItem('chat-header-action','wiki-chat-actions') в ChatHeaderBar → «ряд шапки» краснеет;
// (7) убрать гард seq в poll .then → «гард опроса» краснеет;
// (8) не перечитывать chroniclePath после завершенного опроса → «со ссылкой» краснеет;
// (9) вернуть несклонённые формы в retractResultText → «склонение» краснеет.
// OPEN-1 (этот прогон): (10) не класть запрос в стор (убрать requestOpenWikiPage) и
// (11) не диспатчить 'cc-open-note' → оба краснят «200 applied — … клик — запрос в сторе
// и переход»; (12) снять гейт session.projectId → «без projectId» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../components/chat/__tests__/fakeDom';
import { setAllFlags } from '../../../lib/featureFlags';
import { setAllSubsystems } from '../../../lib/subsystems';
import { WikiChatActions, requestNoteIngest, retractResultText } from '../WikiChatActions';
import { manifest as notesManifest } from '../manifest';
import { ChatHeaderBar } from '../../../components/chat/ChatHeaderBar';
import { registerSubsystem, type ChatHeaderSummaryCtx } from '../../../lib/subsystems/registryCore';
// Стор вики — настоящий (тот же экземпляр, что у компонента через кит): запрос «Открыть
// хронику» проверяется в самом сторе; тестам прямой импорт разрешён (notesStoreImportGuard)
import { __test as wikiStore, consumeOpenWikiPage } from '../../../lib/wiki';

// Тот же приём, что в WikiJournal.test: мокан только транспорт. Мок с сохранением
// остальных экспортов: компонент берёт эти модули через кит хоста, а цепочка кита шире
// собственных импортов теста (onFilesChanged, RequestTimeoutError…) — full-replacement
// мок рвётся на первом недостающем имени. Паттерн — как в NotesPage.test.ts.
vi.mock('../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
  joinProject: vi.fn(() => Promise.resolve()),
  onLocalModelMessage: vi.fn(() => () => {}),
}));
vi.mock('../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../lib/offline')>(),
  // по делу в этих тестах работает только request: транспорт запросов вики
  request: vi.fn(),
  readStoredUserId: () => 'u1',
}));
// useAccountUsage — useSyncExternalStore БЕЗ server-снимка: в SSR он падает сам по себе;
// аккаунт-бейдж к ряду действий отношения не имеет
vi.mock('../../../lib/accountUsage', () => ({
  useAccountUsage: () => null,
  accountSnapshotsFor: () => [],
}));

// Манифест «Заметок» — РЕАЛЬНЫЙ (тот же объект, что грузит хост через MF-remote):
// регистрация повторяет то, что делает host при загрузке remote
registerSubsystem(notesManifest);

import { request } from '../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const CHAT_A = 'chat-aaaaaaaa-0000-0000-0000-000000000001';
const CHAT_B = 'chat-bbbbbbbb-0000-0000-0000-000000000002';
// проектный чат: хроника бывает только у проектного дома, и раздел запроса берётся из
// projectId — без него кнопка не шлёт запроса (проверяется отдельным тестом OPEN-1)
const sessionA = { id: CHAT_A, projectId: 'p1' } as unknown as ChatHeaderSummaryCtx['session'];
const sessionB = { id: CHAT_B, projectId: 'p1' } as unknown as ChatHeaderSummaryCtx['session'];

const RETRACT_OK = {
  revertedPackages: ['pkg-1'], unlinkedPackages: [], remainingPackages: [],
  removedChronicles: ['Проекты/p1/Хроники/x.md'], failed: [], markedSkip: true,
};

// Транспорт по адресу запроса; неоговорённый — пустое тело (падает сам тест, не молчит).
// Порядок проверок важен: '/analyze-status' раньше '/analyze' (суффикс входит в суффикс)
function serve(opts: {
  analyze?: unknown;
  status?: unknown;
  chats?: unknown;
  retract?: unknown;
} = {}) {
  const counts = { analyze: 0, status: 0, chats: 0, retract: 0 };
  const val = (v: unknown) => (typeof v === 'function' ? (v as () => unknown)() : v);
  req.mockImplementation((url: string, init?: { method?: string; body?: string }) => {
    const u = String(url);
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method === 'GET' && u.includes('/analyze-status')) {
      counts.status++;
      return Promise.resolve(val(opts.status ?? { running: false, last: null }));
    }
    if (method === 'POST' && u.endsWith('/analyze')) {
      counts.analyze++;
      return Promise.resolve(val(opts.analyze ?? { outcome: 'nothing_new', reason: null, packageIds: [] }));
    }
    if (method === 'GET' && u === '/wiki/chats') {
      counts.chats++;
      // val и здесь: список может меняться от вызова к вызову (хроника появляется после
      // применённого разбора — тест «со ссылкой»)
      return Promise.resolve(val(opts.chats ?? { items: [], counts: {} }));
    }
    if (method === 'POST' && u.endsWith('/retract')) {
      counts.retract++;
      return Promise.resolve(val(opts.retract ?? RETRACT_OK));
    }
    return Promise.resolve(null);
  });
  return counts;
}

const roots: Root[] = [];

function render(session: ChatHeaderSummaryCtx['session']): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiChatActions, { session, hasMessages: true, online: true }));
  roots.push(root);
  return container;
}

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

// Продвинуть таймеры опроса и прогнать микротask-цепочки ответа
async function tick(ms: number) {
  await act(async () => { vi.advanceTimersByTime(ms); });
  await settle();
}

function texts(container: AnyNode): string[] {
  return textNodes(container).map(t => t.trim()).filter(Boolean);
}

function has(container: AnyNode, needle: string): boolean {
  return texts(container).join(' ').includes(needle);
}

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

function buttonByExactText(root: AnyNode, text: string): AnyNode | null {
  return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === text) ?? null;
}

function click(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick!(undefined); });
}

const postCalls = (suffix: string) => req.mock.calls.filter(([u, init]) =>
  String(u).endsWith(suffix) && (init as { method?: string } | undefined)?.method === 'POST');

const CHATRECORD = {
  sessionId: CHAT_A, name: 'Рефью звука', zone: 'triage', score: 9, signals: [], reason: null,
  nothingNewStreak: 0, triagedAt: '2026-11-01T00:00:00Z', running: false, cursor: 12,
  chroniclePath: `Проекты/p1/Хроники/${CHAT_A}.md`,
};

beforeEach(() => {
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
  // запрос «Открыть хронику» — модульное состояние стора: каждый тест начинает с чистого
  wikiStore.reset();
  setAllFlags({ 'llm-wiki': true });
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  vi.useRealTimers();
  uninstallFakeDom();
});

describe('Действия «Вики» в чате (S7b)', () => {
  it('флаг llm-wiki выключен — ни действий, ни запросов вики', async () => {
    setAllFlags({});
    serve();
    const c = render(sessionA);
    await settle();
    expect(buttonByTestId(c, 'wiki-chat-analyze')).toBeNull();
    expect(buttonByTestId(c, 'wiki-chat-retract')).toBeNull();
    expect(req.mock.calls.filter(([u]) => String(u).startsWith('/wiki'))).toHaveLength(0);
  });

  it('флаг включён — «Разобрать этот чат» и «Откатить всё из этого чата»; без chroniclePath «Открыть хронику» нет', async () => {
    serve();
    const c = render(sessionA);
    await settle();
    expect(buttonByExactText(c, 'Разобрать этот чат')).not.toBeNull();
    expect(buttonByExactText(c, 'Откатить всё из этого чата')).not.toBeNull();
    expect(buttonByTestId(c, 'wiki-chat-chronicle')).toBeNull();
  });

  it('200 applied — «готово»; хроника из списка даёт кнопку «Открыть хронику»: клик — запрос в сторе и переход', async () => {
    serve({
      analyze: { outcome: 'applied', reason: null, packageIds: ['pkg-1'] },
      chats: { items: [CHATRECORD], counts: {} },
    });
    const c = render(sessionA);
    await settle();
    click(buttonByExactText(c, 'Разобрать этот чат'), 'анализ');
    await settle();
    expect(has(c, 'готово')).toBe(true);
    const chron = buttonByTestId(c, 'wiki-chat-chronicle');
    expect(chron).not.toBeNull();
    // (а) OPEN-1: клик — запрос в сторе (раздел из projectId чата, путь из списка) и
    // переход в «Заметки» существующим каналом 'cc-open-note' (слушатель App, App.tsx:357)
    let opens = 0;
    const onOpen = () => { opens++; };
    const w = globalThis as { window: { addEventListener: Function; removeEventListener: Function } };
    w.window.addEventListener('cc-open-note', onOpen);
    click(chron, 'хроника');
    w.window.removeEventListener('cc-open-note', onOpen);
    expect(consumeOpenWikiPage()).toEqual({ section: 'projects/p1', path: `Проекты/p1/Хроники/${CHAT_A}.md` });
    expect(opens).toBe(1);
  });

  it('409 task_execution и marked_skip — текст отказа по коду, не сырой код', async () => {
    serve({ analyze: () => Promise.reject({ status: 409, body: { code: 'task_execution', message: 'chat is an executor session' } }) });
    const c1 = render(sessionA);
    await settle();
    click(buttonByExactText(c1, 'Разобрать этот чат'), 'анализ');
    await settle();
    expect(has(c1, 'Чаты исполнения задач не разбираются')).toBe(true);
    expect(has(c1, 'task_execution')).toBe(false);

    serve({ analyze: () => Promise.reject({ status: 409, body: { code: 'marked_skip', message: 'skip mark set' } }) });
    const c2 = render(sessionB);
    await settle();
    click(buttonByExactText(c2, 'Разобрать этот чат'), 'анализ');
    await settle();
    expect(has(c2, 'Чат помечен «не разбирать» — сними пометку')).toBe(true);
    expect(has(c2, 'marked_skip')).toBe(false);
  });

  it('nothing_new и rejected — «нечего добавить» и «отказ» с причиной', async () => {
    serve({ analyze: { outcome: 'nothing_new', reason: null, packageIds: [] } });
    const c1 = render(sessionA);
    await settle();
    click(buttonByExactText(c1, 'Разобрать этот чат'), 'анализ');
    await settle();
    expect(has(c1, 'нечего добавить')).toBe(true);

    serve({ analyze: { outcome: 'rejected', reason: 'нет утверждений с цитатами', packageIds: [] } });
    const c2 = render(sessionB);
    await settle();
    click(buttonByExactText(c2, 'Разобрать этот чат'), 'анализ');
    await settle();
    expect(has(c2, 'отказ: нет утверждений с цитатами')).toBe(true);
  });

  it('202 — опрос analyze-status до running:false, показывает last.outcome и останавливается', async () => {
    vi.useFakeTimers();
    let n = 0;
    const counts = serve({
      analyze: { running: true },
      status: () => {
        n++;
        if (n === 1) return { running: false, last: null }; // начальная проверка при монтировании
        if (n === 2) return { running: true, last: null }; // первый опрос — ещё идёт
        return { running: false, last: { outcome: 'applied', reason: null, packageIds: ['pkg-1'], finishedAt: '2026-11-01T00:00:00Z' } };
      },
    });
    const c = render(sessionA);
    await settle();
    click(buttonByExactText(c, 'Разобрать этот чат'), 'анализ');
    await settle();
    expect(has(c, 'разбираю…')).toBe(true);
    expect(counts.status).toBe(1);
    await tick(4000);
    expect(has(c, 'разбираю…')).toBe(true);
    expect(counts.status).toBe(2);
    await tick(4000);
    expect(has(c, 'готово')).toBe(true);
    // running:false — следующего опроса нет: счётчик не растёт на дальнейших таймерах
    await tick(4000);
    await tick(4000);
    expect(counts.status).toBe(3);
  });

  it('«идёт» восстанавливается при монтировании из analyze-status, без клика; опрос снимается при размонтировании', async () => {
    vi.useFakeTimers();
    let n = 0;
    const counts = serve({
      status: () => {
        n++;
        if (n === 1) return { running: true, last: null };
        return { running: false, last: { outcome: 'applied', reason: null, packageIds: [], finishedAt: '2026-11-01T00:00:00Z' } };
      },
    });
    const c = render(sessionA);
    await settle();
    expect(has(c, 'разбираю…')).toBe(true);
    expect(counts.analyze).toBe(0); // восстановление — без нового POST analyze
    await tick(4000);
    expect(has(c, 'готово')).toBe(true);
    // новое «идёт» после размонтирования опросить некому
    const counts2 = serve({ status: () => ({ running: true, last: null }) });
    const c2 = render(sessionA);
    await settle();
    expect(has(c2, 'разбираю…')).toBe(true);
    const before = counts2.status; // 1 — начальный при монтировании
    act(() => { roots[roots.length - 1].unmount(); });
    await tick(4000);
    await tick(4000);
    expect(counts2.status).toBe(before);
  });

  it('двойное нажатие «Разобрать» — один POST, кнопка выключена пока идёт', async () => {
    let release: (v: unknown) => void = () => {};
    const counts = serve({ analyze: () => new Promise<unknown>(res => { release = res; }) });
    const c = render(sessionA);
    await settle();
    const btn = buttonByExactText(c, 'Разобрать этот чат');
    click(btn, 'анализ');
    click(btn, 'анализ повторно');
    expect(counts.analyze).toBe(1);
    expect(has(c, 'разбираю…')).toBe(true);
    release({ outcome: 'nothing_new', reason: null, packageIds: [] });
    await settle();
    expect(has(c, 'нечего добавить')).toBe(true);
  });

  it('гард поколения: ответ прежнего чата не попадает на экран нового', async () => {
    let release: (v: unknown) => void = () => {};
    serve({ analyze: () => new Promise<unknown>(res => { release = res; }) });
    installFakeDom();
    const { root, container } = mount(createElement(WikiChatActions, { session: sessionA, hasMessages: true, online: true }));
    roots.push(root);
    await settle();
    click(buttonByExactText(container, 'Разобрать этот чат'), 'анализ');
    // смена чата до ответа прежнего: новое поколение — запоздалый ответ CHAT_A отбрасывается
    act(() => { root.render(createElement(WikiChatActions, { session: sessionB, hasMessages: true, online: true })); });
    await settle();
    release({ outcome: 'applied', reason: null, packageIds: ['pkg-1'] });
    await settle();
    expect(has(container, 'готово')).toBe(false);
  });

  it('«Откатить всё из этого чата»: 0 вызовов до подтверждения; после — sourceRef без диапазона', async () => {
    const counts = serve();
    const c = render(sessionA);
    await settle();
    click(buttonByExactText(c, 'Откатить всё из этого чата'), 'откат');
    await settle();
    expect(counts.retract).toBe(0);
    // подтверждение спрашивает явно — с обещанием пометки
    expect(has(docBody(), 'Чат получит пометку «не разбирать»')).toBe(true);
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтвердить откат');
    await settle();
    expect(counts.retract).toBe(1);
    const call = postCalls('/retract')[0];
    const body = JSON.parse(String((call[1] as { body: string }).body));
    expect(body.sourceRef).toBe(`session:${CHAT_A}`);
    expect(String(body.sourceRef)).not.toContain('#m');
    expect(has(c, '1 пакет откатан')).toBe(true);
    expect(has(c, 'чат помечен «не разбирать»')).toBe(true);
  });
});

describe('«Разобрать в базу» у заметки (S7b, логика NoteView)', () => {
  it('POST ingest/note на нужный noteId; исход — текстом', async () => {
    req.mockImplementation((url: string) => {
      if (String(url) === '/wiki/ingest/note') {
        return Promise.resolve({ outcome: 'awaiting_human', packageId: 'pkg-9', reason: null });
      }
      return Promise.resolve(null);
    });
    const text = await requestNoteIngest('note-1');
    const calls = postCalls('/ingest/note');
    expect(calls).toHaveLength(1);
    expect(String(calls[0][0])).toBe('/wiki/ingest/note');
    expect(JSON.parse(String((calls[0][1] as { body: string }).body)).noteId).toBe('note-1');
    expect(text).toBe('пакет во «Входящих»: ждёт твоего решения');
  });

  it('409 packages_limit — текстом; 404 — текстом, функция не бросает (экран не падает)', async () => {
    req.mockImplementation(() => Promise.reject({ status: 409, body: { code: 'packages_limit', message: 'open packages limit reached' } }));
    expect(await requestNoteIngest('note-1')).toContain('Открытых пакетов слишком много');

    req.mockImplementation(() => Promise.reject({ status: 404 }));
    expect(await requestNoteIngest('note-1')).toBe('Не найдено');
  });

  it('400 wiki_note_id — текстом, функция не бросает', async () => {
    req.mockImplementation(() => Promise.reject({ status: 400, body: { code: 'wiki_note_id', message: 'note id required' } }));
    expect(await requestNoteIngest('note-1')).toBe('Нужен id заметки');
  });
});

describe('Опрос после 202: ссылка на хронику и гард поколения (доработка S7b, п.2 и п.4)', () => {
  it('202 → опрос → «готово» со ссылкой: «Открыть хронику» появляется без перезагрузки', async () => {
    vi.useFakeTimers();
    let nStatus = 0;
    let nChats = 0;
    serve({
      analyze: { running: true },
      status: () => {
        nStatus++;
        if (nStatus === 1) return { running: false, last: null }; // проверка при монтировании
        return { running: false, last: { outcome: 'applied', reason: null, packageIds: ['pkg-1'], finishedAt: '2026-11-01T00:00:00Z' } };
      },
      // хроника появляется в списке только после применённого разбора (первый вызов — пустой)
      chats: () => ({ items: nChats++ === 0 ? [] : [CHATRECORD], counts: {} }),
    });
    const c = render(sessionA);
    await settle();
    expect(buttonByTestId(c, 'wiki-chat-chronicle')).toBeNull(); // до разбора ссылки нет
    click(buttonByExactText(c, 'Разобрать этот чат'), 'анализ');
    await settle(); // 202 обработан: опрос поставил таймер — только теперь его двигать
    await tick(4000); // опрос: running:false + applied → перечитка списка чатов
    expect(has(c, 'готово')).toBe(true);
    const chron = buttonByTestId(c, 'wiki-chat-chronicle');
    expect(chron).not.toBeNull(); // та же монтированная панель, без remount
    let opens = 0;
    const onOpen = () => { opens++; };
    const w = globalThis as { window: { addEventListener: Function; removeEventListener: Function } };
    w.window.addEventListener('cc-open-note', onOpen);
    click(chron, 'хроника');
    w.window.removeEventListener('cc-open-note', onOpen);
    expect(consumeOpenWikiPage()).toEqual({ section: 'projects/p1', path: `Проекты/p1/Хроники/${CHAT_A}.md` });
    expect(opens).toBe(1);
  });

  // OPEN-1: внепроективный чат (хроника бывает только у проектного дома) — даже с путём
  // из списка кнопка не шлёт запроса без раздела и не будит переход
  it('без projectId чата — ни запроса в сторе, ни перехода (раздел взять не из чего)', async () => {
    serve({
      analyze: { outcome: 'applied', reason: null, packageIds: ['pkg-1'] },
      chats: { items: [CHATRECORD], counts: {} },
    });
    const sessionNoProject = { id: CHAT_A } as unknown as ChatHeaderSummaryCtx['session'];
    const c = render(sessionNoProject);
    await settle();
    const chron = buttonByTestId(c, 'wiki-chat-chronicle');
    expect(chron).not.toBeNull(); // ссылка есть — кнопка есть
    let opens = 0;
    const onOpen = () => { opens++; };
    const w = globalThis as { window: { addEventListener: Function; removeEventListener: Function } };
    w.window.addEventListener('cc-open-note', onOpen);
    click(chron, 'хроника');
    w.window.removeEventListener('cc-open-note', onOpen);
    expect(consumeOpenWikiPage()).toBeNull();
    expect(opens).toBe(0);
  });

  it('гард опроса: ответ статуса прежнего чата не попадает на экран нового', async () => {
    vi.useFakeTimers();
    let n = 0;
    let release: (v: unknown) => void = () => {};
    req.mockImplementation((url: string, init?: { method?: string }) => {
      const u = String(url);
      const m = (init?.method ?? 'GET').toUpperCase();
      if (m === 'GET' && u.includes('/analyze-status')) {
        n++;
        if (n === 1) return Promise.resolve({ running: true, last: null }); // монтирование A: «идёт» → опрос
        if (n === 2) return new Promise<unknown>(res => { release = res; }); // опрос A — в полёте
        return Promise.resolve({ running: false, last: null }); // проверка при монтировании B
      }
      if (m === 'GET' && u === '/wiki/chats') return Promise.resolve({ items: [], counts: {} });
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiChatActions, { session: sessionA, hasMessages: true, online: true }));
    roots.push(root);
    await settle();
    expect(has(container, 'разбираю…')).toBe(true);
    await tick(4000); // опрос A отправлен и висит (промис держим)
    // смена чата ДО ответа опроса: новое поколение — запоздалый ответ A отбрасывается в .then
    act(() => { root.render(createElement(WikiChatActions, { session: sessionB, hasMessages: true, online: true })); });
    await settle();
    release({ running: false, last: { outcome: 'applied', reason: null, packageIds: ['pkg-1'], finishedAt: '2026-11-01T00:00:00Z' } });
    await settle();
    expect(has(container, 'готово')).toBe(false); // исход A не на экране B
  });
});

describe('Склонение итога отката (доработка S7b, п.3)', () => {
  const mk = (n: number) => ({
    revertedPackages: Array.from({ length: n }, (_, i) => `p${i}`),
    unlinkedPackages: [] as string[], remainingPackages: [] as string[],
    removedChronicles: Array.from({ length: n }, (_, i) => `Проекты/p1/Хроники/x${i}.md`),
    failed: [] as string[], markedSkip: false,
  });
  it('пакеты и хроники: 1 / 2–4 / 5+ — по числу, 11–14 — как 5+', () => {
    expect(retractResultText(mk(1))).toBe('1 пакет откатан, 1 хроника снята.');
    expect(retractResultText(mk(2))).toBe('2 пакета откатано, 2 хроники снято.');
    expect(retractResultText(mk(4))).toBe('4 пакета откатано, 4 хроники снято.');
    expect(retractResultText(mk(5))).toBe('5 пакетов откатано, 5 хроник снято.');
    expect(retractResultText(mk(11))).toBe('11 пакетов откатано, 11 хроник снято.');
    expect(retractResultText(mk(21))).toBe('21 пакет откатан, 21 хроника снята.');
  });
  it('страницы и «осталось (другой источник)» — тоже по числу', () => {
    expect(retractResultText({ ...mk(0), unlinkedPackages: ['a'] })).toBe('1 стр. очищена от сносок.');
    expect(retractResultText({ ...mk(0), unlinkedPackages: ['a', 'b', 'c'] })).toBe('3 стр. очищено от сносок.');
    expect(retractResultText({ ...mk(0), remainingPackages: ['a', 'b'] })).toBe('2 пакета осталось (другой источник).');
    expect(retractResultText({ ...mk(0), remainingPackages: ['a', 'b', 'c', 'd', 'e'] })).toBe('5 пакетов осталось (другой источник).');
  });
});

describe('Достижимость из шапки чата (доработка S7b, п.1 вердикта)', () => {
  // Окружение для SSR-рендера реальной шапки: window с шириной (useWindowWidth),
  // localStorage (useActionVisibility/notify), Notification (isNotifySupported).
  // Эффекты в SSR не работают — запросов нет вовсе; проверяется ровно состав ряда действий.
  function withBrowserStub<T>(fn: () => T): T {
    const g = globalThis as Record<string, unknown>;
    const saved = { window: g.window, localStorage: g.localStorage, Notification: g.Notification };
    g.window = Object.assign(new EventTarget(), {
      innerWidth: 1440, innerHeight: 900,
      matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
      dispatchEvent: () => true,
    });
    g.localStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
    g.Notification = { permission: 'denied' };
    try {
      return fn();
    } finally {
      for (const k of ['window', 'localStorage', 'Notification'] as const) {
        if (saved[k] === undefined) delete g[k]; else g[k] = saved[k];
      }
    }
  }

  // Минимальный, но честный набор props: без проекта, без острова, без контекст-строки —
  // ряд действий от этого не меняется
  function headerHtml(isMobile: boolean): string {
    return renderToStaticMarkup(createElement(ChatHeaderBar, {
      session: { id: CHAT_A, name: 'Рефью звука' },
      project: null, hasMessages: true, online: true,
      cost: { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 },
      // пустые, но НЕ null статистики: badgeAvailable читает их безусловно (line 1995–1996)
      falCost: { total: 0, count: 0, byModel: new Map() },
      glifCost: { count: 0, credits: 0, hasCredits: false, byType: new Map() },
      billing: null, rateWindows: [],
      isMobile, isCompacting: false, canCompact: false, onCompact: () => {},
      // оценка без токенов: hasContextInfo читает её безусловно (line 1993) — форма живого
      // чата до первого хода, а не null
      ctxEstimate: { window: 200000, fresh: false, level: 'normal' }, isWaiting: false, items: [],
      persona: null, agent: null, participants: [],
      island: false, compact: false, contextBar: null, scrolled: false,
    } as never));
  }

  it('ряд шапки рисует вклад wiki-chat-actions: десктоп и мобильная раскладка', () => {
    // read-time гейт getSlotContributions: неактивная подсистема не отдаёт ни один вклад.
    // «Заметки» активны — как их делает хост сетом ключей от /auth/me
    setAllSubsystems(['notes']);
    setAllFlags({ 'llm-wiki': true });
    for (const isMobile of [false, true]) {
      const html = withBrowserStub(() => headerHtml(isMobile));
      expect(html, `isMobile=${isMobile}`).toContain('data-testid="wiki-chat-analyze"');
      expect(html, `isMobile=${isMobile}`).toContain('Разобрать этот чат');
      expect(html, `isMobile=${isMobile}`).toContain('Откатить всё из этого чата');
    }
  });

  it('флаг llm-wiki выключен — в шапке нет действий «Вики»', () => {
    setAllSubsystems(['notes']); // подсистема активна — нет именно из-за выключенного флага
    setAllFlags({});
    const html = withBrowserStub(() => headerHtml(false));
    expect(html).not.toContain('wiki-chat-analyze');
    expect(html).not.toContain('Разобрать этот чат');
    expect(html).not.toContain('Откатить всё из этого чата');
  });
});
