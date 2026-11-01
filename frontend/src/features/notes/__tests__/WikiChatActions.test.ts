// Тест логики действий чата (S7b): флаг выключен — ни контролов, ни запросов вики;
// «Разобрать» — POST analyze, 200 → исход текстом; 409 task_execution/marked_skip — текст
// отказа по коду (wikiErrorText), не сырой код; 202 → опрос analyze-status до running:false и
// показ last.outcome; «идёт» восстанавливается при монтировании без клика; двойное нажатие —
// один запрос; гард поколения — ответ прежнего чата не попадает на экран нового; «Откатить
// всё» — REST только после подтверждения модалкой, sourceRef без диапазона окна;
// requestNoteIngest — POST ingest/note с noteId, 409 packages_limit и 404 — текстом, не бросает.
//
// Стор вики и флаги — настоящие; мокан только транспорт (offline.request) и signalr —
// тот же приём, что в WikiJournal.test. Модалка ConfirmDialog — портал в document.body.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) убрать гард seq в analyze .then → «ответ прежнего чата» краснеет;
// (2) звать doRetract прямо из кнопки, минуя подтверждение → «0 вызовов до подтверждения» краснеет;
// (3) послать sourceRef с диапазоном («session:{id}#m1-9») → «карточка без диапазона» краснеет;
// (4) показать сырой код вместо wikiErrorText → «409 текстом» краснеет;
// (5) в опросе не останавливаться на running:false (сcheduling всегда) → «опрос останавливается» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../components/chat/__tests__/fakeDom';
import { setAllFlags } from '../../../lib/featureFlags';
import { WikiChatActions, requestNoteIngest } from '../WikiChatActions';
import type { ChatHeaderSummaryCtx } from '../../../lib/subsystems/registryCore';

vi.mock('../../../lib/signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../lib/offline', () => ({
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

import { request } from '../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const CHAT_A = 'chat-aaaaaaaa-0000-0000-0000-000000000001';
const CHAT_B = 'chat-bbbbbbbb-0000-0000-0000-000000000002';
const sessionA = { id: CHAT_A } as unknown as ChatHeaderSummaryCtx['session'];
const sessionB = { id: CHAT_B } as unknown as ChatHeaderSummaryCtx['session'];

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
    if (method === 'GET' && u === '/api/wiki/chats') {
      counts.chats++;
      return Promise.resolve(opts.chats ?? { items: [], counts: {} });
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
    expect(req.mock.calls.filter(([u]) => String(u).startsWith('/api/wiki'))).toHaveLength(0);
  });

  it('флаг включён — «Разобрать этот чат» и «Откатить всё из этого чата»; без chroniclePath «Открыть хронику» нет', async () => {
    serve();
    const c = render(sessionA);
    await settle();
    expect(buttonByExactText(c, 'Разобрать этот чат')).not.toBeNull();
    expect(buttonByExactText(c, 'Откатить всё из этого чата')).not.toBeNull();
    expect(buttonByTestId(c, 'wiki-chat-chronicle')).toBeNull();
  });

  it('200 applied — «готово»; после применённого хроника из списка даёт кнопку «Открыть хронику» с path', async () => {
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
    let detail: unknown = null;
    const onOpen = (e: { detail?: unknown }) => { detail = e.detail; };
    (globalThis as { window: { addEventListener: Function } }).window.addEventListener('cc-open-wiki-page', onOpen);
    click(chron, 'хроника');
    (globalThis as { window: { removeEventListener: Function } }).window.removeEventListener('cc-open-wiki-page', onOpen);
    expect(detail).toEqual({ path: `Проекты/p1/Хроники/${CHAT_A}.md` });
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
    expect(has(c, '1 пакетов откатано')).toBe(true);
    expect(has(c, 'чат помечен «не разбирать»')).toBe(true);
  });
});

describe('«Разобрать в базу» у заметки (S7b, логика NoteView)', () => {
  it('POST ingest/note на нужный noteId; исход — текстом', async () => {
    req.mockImplementation((url: string) => {
      if (String(url) === '/api/wiki/ingest/note') {
        return Promise.resolve({ outcome: 'awaiting_human', packageId: 'pkg-9', reason: null });
      }
      return Promise.resolve(null);
    });
    const text = await requestNoteIngest('note-1');
    const calls = postCalls('/ingest/note');
    expect(calls).toHaveLength(1);
    expect(String(calls[0][0])).toBe('/api/wiki/ingest/note');
    expect(JSON.parse(String((calls[0][1] as { body: string }).body)).noteId).toBe('note-1');
    expect(text).toBe('пакет во «Входящих»: ждёт твоего решения');
  });

  it('409 packages_limit — текстом; 404 — текстом, функция не бросает (экран не падает)', async () => {
    req.mockImplementation(() => Promise.reject({ status: 409, body: { code: 'packages_limit', message: 'open packages limit reached' } }));
    expect(await requestNoteIngest('note-1')).toContain('Открытых пакетов слишком много');

    req.mockImplementation(() => Promise.reject({ status: 404 }));
    expect(await requestNoteIngest('note-1')).toBe('Не найдено');
  });
});
