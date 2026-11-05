// Тест меню «Вики» чата (S7b → WM-2): флаг выключен — ни контролов, ни запросов вики;
// ряда из пяти кнопок больше нет — действия живут ОДНИМ меню (WikiChatMenu на базе
// ToolbarOverflowMenu): «Разобрать этот чат» · «Открыть хронику» (только при chroniclePath) ·
// «Важный» · «Не разбирать» · «Откатить всё из этого чата» (danger, ConfirmDialog).
// «Разобрать» — POST analyze, 200 → исход человеческим текстом (applied «Разобрано — записано в
// вики», awaiting_human «Есть спорное — во „Входящих“», nothing_new/up_to_date «Нового нет»,
// отказ «Не удалось разобрать[: модель не ответила]» + подпись пункта «Разобрать ещё раз»;
// сырая причина бэка — только в title, окна и проходы в видимый текст не попадают); 409 —
// текст по коду (wikiErrorText); 202 → опрос analyze-status до running:false; «идёт»
// восстанавливается при монтировании; двойное нажатие — один запрос; гард поколения.
// Точка статуса на триггере гаснет по открытию меню: отметка cc_wiki_seen_<chat> = finishedAt
// последнего исхода (сбой localStorage — точка горит); пока идёт разбор — горит всегда.
// Внешнее открытие (open: counter+anchor) и headless — без своего триггера.
// «Откатить всё» — REST только после подтверждения, sourceRef без диапазона окна.
// «Открыть хронику» (OPEN-1): запрос в стор вики + 'cc-open-note'.
// Достижимость: РЕАЛЬНАЯ шапка (ChatHeaderBar) с РЕАЛЬНЫМ манифестом рисует старый вклад
// wiki-chat-actions — теперь это то же меню, без ряда (его снимет WM-3).
//
// Стор вики и флаги — настоящие; мокан только транспорт (offline.request) и signalr.
// Шапка — через renderToStaticMarkup (эффекты в SSR не работают — запросов нет).
//
// Мутации WM-2 (вывод — в отчёте задачи): (1) не гасить точку по открытию (убрать onClickCapture) →
// «точка гаснет» краснеет; (2) не писать отметку в localStorage → «после перезагрузки» краснеет;
// (3) отказ без analyzeRefusalText (сырая причина в тексте) → «отказ по-человечески» краснеет;
// (4) снять гард seq в analyze/poll → «гард поколения»/«гард опроса»; (5) звать doRetract мимо
// подтверждения → «0 вызовов до подтверждения»; (6) писать отметку в чужой чат (ключ не по session.id)
// → «смена чата не пишет отметку»; (7) running гасить как итог → «пока идёт разбор точка горит».

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../components/chat/__tests__/fakeDom';
import { C } from '../../../lib/design';
import { setAllFlags } from '../../../lib/featureFlags';
import { setAllSubsystems } from '../../../lib/subsystems';
import { WikiChatMenu, requestNoteIngest } from '../WikiChatActions';
import { retractResultText } from '../wiki/wikiLabels';
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
const TRIGGER_TITLE = 'Действия «Вики» в этом чате';
const OK = 'Разобрано — записано в вики';

// Меню в тестах — боттом-шит (isMobile): дропдауну нужен якорь с размерами, а fakeDom их не считает
function menuEl(session: ChatHeaderSummaryCtx['session'], extra: Record<string, unknown> = {}) {
  return createElement(WikiChatMenu, { session, hasMessages: true, online: true, isMobile: true, ...extra });
}

function mountMenu(session: ChatHeaderSummaryCtx['session'], extra: Record<string, unknown> = {}): Cont {
  // один живой экземпляр на тест: повторный installFakeDom даёт новый document, и портал
  // прежнего корня остался бы в старом
  for (const r of roots.splice(0)) act(() => r.unmount());
  installFakeDom();
  // открытый ToolbarOverflowMenu ищет прокручиваемых предков через getComputedStyle — в node он заглушен
  const g = globalThis as Record<string, unknown>;
  if (!g.getComputedStyle) g.getComputedStyle = () => ({ overflowY: 'visible' });
  const { root, container } = mount(menuEl(session, extra));
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

// Пункт меню по вхождению подписи (у строки два текстовых блока — label и sublabel)
function item(label: string): AnyNode | null {
  return allNodes(docBody()).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').includes(label)) ?? null;
}

// Открыть меню как человек: захват клика по обёртке гасит точку (в fakeDom события не всплывают —
// зовём обработчик обёртки сами), затем клик по триггеру
function openMenu(c: Cont) {
  const wrap = findNodeByAttr(c, 'data-testid', 'wiki-chat-menu');
  if (!wrap) throw new Error('нет обёртки меню');
  act(() => { (reactProps(wrap) as { onClickCapture?: () => void }).onClickCapture?.(); });
  click(findNodeByAttr(c, 'title', TRIGGER_TITLE), 'триггер');
}

// Меню закрывается после действия пункта: для проверки текста — открыть, если закрыто
async function ensureOpen(c: Cont) {
  if (!has(docBody(), 'Разобрать этот чат')) { openMenu(c); await settle(); }
}
async function menuHas(c: Cont, needle: string): Promise<boolean> {
  await ensureOpen(c);
  return has(docBody(), needle);
}

// Точка статуса на триггере: span с absolute-позиционированием (msgDot)
function triggerDot(c: Cont): AnyNode | null {
  const trigger = findNodeByAttr(c, 'title', TRIGGER_TITLE);
  if (!trigger) throw new Error('нет кнопки-триггера «Вики»');
  return allNodes(trigger).find(n => n.nodeName === 'SPAN'
    && (n as { style?: Record<string, string> }).style?.position === 'absolute') ?? null;
}

// localStorage в node нет: Map-заглушка (сбой хранилища — отдельным тестом)
function stubStorage(broken = false): Map<string, string> {
  const store = new Map<string, string>();
  (globalThis as Record<string, unknown>).localStorage = broken
    ? { getItem() { throw new Error('storage'); }, setItem() { throw new Error('storage'); }, removeItem() {} }
    : {
        getItem: (k: string) => store.get(k) ?? null,
        setItem: (k: string, v: string) => { store.set(k, v); },
        removeItem: (k: string) => { store.delete(k); },
      };
  return store;
}

const LAST_APPLIED = { outcome: 'applied', reason: null, packageIds: [], finishedAt: '2026-11-05T01:00:00Z' };

beforeEach(() => {
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
  // запрос «Открыть хронику» — модульное состояние стора: каждый тест начинает с чистого
  wikiStore.reset();
  setAllFlags({ 'llm-wiki': true });
  stubStorage();
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  delete (globalThis as Record<string, unknown>).localStorage;
  delete ((globalThis as { window?: Record<string, unknown> }).window ?? {} as Record<string, unknown>).innerWidth;
  vi.useRealTimers();
  uninstallFakeDom();
});

describe('Меню «Вики» в чате (S7b → WM-2)', () => {
  it('флаг llm-wiki выключен — ни триггера, ни запросов вики', async () => {
    setAllFlags({});
    serve();
    const c = mountMenu(sessionA);
    await settle();
    expect(findNodeByAttr(c, 'title', TRIGGER_TITLE)).toBeNull();
    expect(req.mock.calls.filter(([u]) => String(u).startsWith('/wiki'))).toHaveLength(0);
  });

  it('флаг включён — один триггер «Вики», пункты меню по составу; ряда кнопок нет; без chroniclePath «Открыть хронику» нет', async () => {
    serve();
    const c = mountMenu(sessionA);
    await settle();
    for (const id of ['analyze', 'chronicle', 'retract', 'mark-important', 'mark-skip', 'status', 'retract-status', 'mark-error']) {
      expect(buttonByTestId(c, `wiki-chat-${id}`)).toBeNull();
    }
    expect(findNodeByAttr(c, 'title', TRIGGER_TITLE)).not.toBeNull();
    openMenu(c);
    await settle();
    const labels = allNodes(docBody()).filter(n => n.nodeName === 'BUTTON').map(n => textNodes(n).join(' '));
    expect(labels.some(t => t.includes('Разобрать этот чат'))).toBe(true);
    expect(labels.some(t => t.includes('Важный'))).toBe(true);
    expect(labels.some(t => t.includes('Не разбирать'))).toBe(true);
    expect(buttonByExactText(docBody(), 'Откатить всё из этого чата')).not.toBeNull();
    expect(item('Открыть хронику')).toBeNull();
  });

  it('открытое меню: «Откатить» — цветом C.dangerText; хроника из списка даёт пункт «Открыть хронику»', async () => {
    serve({ chats: { items: [CHATRECORD], counts: {} } });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    expect(item('Открыть хронику')).not.toBeNull();
    expect((buttonByExactText(docBody(), 'Откатить всё из этого чата') as { style?: { color?: string } }).style?.color).toBe(C.dangerText);
  });

  it('200 applied — «Разобрано — записано в вики»; «Открыть хронику»: клик — запрос в сторе и переход', async () => {
    serve({
      analyze: { outcome: 'applied', reason: null, packageIds: ['pkg-1'] },
      chats: { items: [CHATRECORD], counts: {} },
    });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    expect(await menuHas(c, OK)).toBe(true);
    const chron = item('Открыть хронику');
    expect(chron).not.toBeNull();
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
    const c1 = mountMenu(sessionA);
    await settle();
    openMenu(c1);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    expect(await menuHas(c1, 'Чаты исполнения задач не разбираются')).toBe(true);
    expect(has(docBody(), 'task_execution')).toBe(false);

    serve({ analyze: () => Promise.reject({ status: 409, body: { code: 'marked_skip', message: 'skip mark set' } }) });
    const c2 = mountMenu(sessionB);
    await settle();
    openMenu(c2);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    expect(await menuHas(c2, 'Чат помечен «не разбирать» — сними пометку')).toBe(true);
    expect(has(docBody(), 'marked_skip')).toBe(false);
  });

  async function analyzeOnce(session: ChatHeaderSummaryCtx['session']): Promise<Cont> {
    const c = mountMenu(session);
    await settle();
    openMenu(c);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    return c;
  }

  it('awaiting_human и nothing_new/up_to_date — человеческие тексты', async () => {
    serve({ analyze: { outcome: 'awaiting_human', reason: null, packageIds: ['p'] } });
    expect(await menuHas(await analyzeOnce(sessionA), 'Есть спорное — во «Входящих»')).toBe(true);
    serve({ analyze: { outcome: 'nothing_new', reason: null, packageIds: [] } });
    expect(await menuHas(await analyzeOnce(sessionB), 'Нового нет')).toBe(true);
    serve({ analyze: { outcome: 'up_to_date', reason: null, packageIds: [] } });
    const c = await analyzeOnce(sessionA);
    expect(await menuHas(c, 'Нового нет')).toBe(true);
    expect(has(docBody(), 'нечего добавить')).toBe(false);
  });

  it('отказ по-человечески: «пустой ответ» — «модель не ответила»; чужая причина — без хвоста; сырая строка и окна — только в title', async () => {
    const RAW1 = 'пустой ответ модели в окне m11-m381, проход A';
    serve({ analyze: { outcome: 'rejected', reason: RAW1, packageIds: [] } });
    const c1 = await analyzeOnce(sessionA);
    expect(await menuHas(c1, 'Не удалось разобрать: модель не ответила')).toBe(true);
    expect(has(docBody(), 'Разобрать ещё раз')).toBe(true); // подпись пункта
    expect(has(docBody(), 'm11')).toBe(false);
    expect(has(docBody(), 'проход')).toBe(false);
    expect(findNodeByAttr(docBody(), 'title', RAW1)).not.toBeNull(); // сырая причина — только в title

    const RAW2 = 'нет утверждений с цитатами в окнах m1-m9, проход B';
    serve({ analyze: { outcome: 'rejected', reason: RAW2, packageIds: [] } });
    const c2 = await analyzeOnce(sessionB);
    expect(await menuHas(c2, 'Не удалось разобрать')).toBe(true);
    expect(has(docBody(), 'нет утверждений')).toBe(false);
    expect(has(docBody(), 'm1-m9')).toBe(false);
    // неизвестный будущий исход — тоже отказ, без выдуманного смысла
    serve({ analyze: { outcome: 'weird_new_outcome', reason: null, packageIds: [] } });
    const c3 = await analyzeOnce(sessionA);
    expect(await menuHas(c3, 'Разобрать ещё раз')).toBe(true);
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
        return { running: false, last: LAST_APPLIED };
      },
    });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    expect(await menuHas(c, 'разбираю…')).toBe(true);
    expect(counts.status).toBe(1);
    await tick(4000);
    expect(has(docBody(), 'разбираю…')).toBe(true);
    expect(counts.status).toBe(2);
    await tick(4000);
    expect(has(docBody(), OK)).toBe(true);
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
        return { running: false, last: LAST_APPLIED };
      },
    });
    const c = mountMenu(sessionA);
    await settle();
    expect(await menuHas(c, 'разбираю…')).toBe(true);
    expect(counts.analyze).toBe(0); // восстановление — без нового POST analyze
    await tick(4000);
    expect(has(docBody(), OK)).toBe(true);
    // новое «идёт» после размонтирования опросить некому
    const counts2 = serve({ status: () => ({ running: true, last: null }) });
    const c2 = mountMenu(sessionA);
    await settle();
    expect(await menuHas(c2, 'разбираю…')).toBe(true);
    const before = counts2.status; // 1 — начальный при монтировании
    act(() => { roots[roots.length - 1].unmount(); });
    await tick(4000);
    await tick(4000);
    expect(counts2.status).toBe(before);
  });

  it('двойное нажатие «Разобрать» — один POST: пока идёт, пункт выключен', async () => {
    let release: (v: unknown) => void = () => {};
    const counts = serve({ analyze: () => new Promise<unknown>(res => { release = res; }) });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    openMenu(c); // меню закрылось после действия — открываем снова: пункт обязан быть выключен
    await settle();
    expect((reactProps(item('Разобрать этот чат')!) as { disabled?: boolean }).disabled).toBe(true);
    click(item('Разобрать этот чат'), 'анализ повторно');
    expect(counts.analyze).toBe(1);
    expect(has(docBody(), 'разбираю…')).toBe(true);
    release({ outcome: 'nothing_new', reason: null, packageIds: [] });
    await settle();
    expect(has(docBody(), 'Нового нет')).toBe(true);
  });

  it('гард поколения: ответ прежнего чата не попадает на экран нового', async () => {
    let release: (v: unknown) => void = () => {};
    serve({ analyze: () => new Promise<unknown>(res => { release = res; }) });
    const container = mountMenu(sessionA);
    const root = roots[roots.length - 1];
    await settle();
    openMenu(container);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    // смена чата до ответа прежнего: новое поколение — запоздалый ответ CHAT_A отбрасывается
    act(() => { root.render(menuEl(sessionB)); });
    await settle();
    release({ outcome: 'applied', reason: null, packageIds: ['pkg-1'] });
    await settle();
    expect(await menuHas(container, OK)).toBe(false);
  });

  it('«Откатить всё из этого чата»: 0 вызовов до подтверждения; после — sourceRef без диапазона', async () => {
    const counts = serve();
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё из этого чата'), 'откат');
    await settle();
    expect(counts.retract).toBe(0);
    expect(has(docBody(), 'Чат получит пометку «не разбирать»')).toBe(true);
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтвердить откат');
    await settle();
    expect(counts.retract).toBe(1);
    const call = postCalls('/retract')[0];
    const body = JSON.parse(String((call[1] as { body: string }).body));
    expect(body.sourceRef).toBe(`session:${CHAT_A}`);
    expect(String(body.sourceRef)).not.toContain('#m');
    expect(await menuHas(c, '1 пакет откатан')).toBe(true);
    expect(has(docBody(), 'чат помечен «не разбирать»')).toBe(true);
  });
});

describe('Точка статуса и «увидел» (WM-2)', () => {
  const statusDone = (finishedAt: string | null) => ({ running: false, last: { ...LAST_APPLIED, finishedAt } });

  it('итог показан — точка на триггере; открытие меню гасит её; после перезагрузки (тот же finishedAt) не горит, у нового исхода — горит', async () => {
    const store = stubStorage();
    serve({ status: statusDone('F1') });
    const c = mountMenu(sessionA);
    await settle();
    expect(triggerDot(c)).not.toBeNull();
    expect((triggerDot(c) as { style?: Record<string, string> }).style?.background).toBe(C.success);
    openMenu(c);
    await settle();
    expect(triggerDot(c)).toBeNull();
    expect(store.get(`cc_wiki_seen_${CHAT_A}`)).toBe('F1');
    // «перезагрузка»: тот же исход — точки нет
    act(() => { roots.pop()!.unmount(); });
    const c2 = mountMenu(sessionA);
    await settle();
    expect(triggerDot(c2)).toBeNull();
    // новый исход (другой finishedAt) — точка горит снова
    act(() => { roots.pop()!.unmount(); });
    serve({ status: statusDone('F2') });
    const c3 = mountMenu(sessionA);
    await settle();
    expect(triggerDot(c3)).not.toBeNull();
  });

  it('пока идёт разбор точка горит всегда — и после открытия меню, и после перезагрузки', async () => {
    serve({ status: { running: true, last: null } });
    const c = mountMenu(sessionA);
    await settle();
    expect(triggerDot(c)).not.toBeNull();
    openMenu(c);
    await settle();
    expect(triggerDot(c)).not.toBeNull();
  });

  it('прямой 200 без finishedAt: итог — точка; открытие меню гасит её до перезагрузки, в хранилище ничего не пишется', async () => {
    const store = stubStorage();
    serve({ analyze: { outcome: 'applied', reason: null, packageIds: [] } });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    expect(triggerDot(c)).not.toBeNull();
    openMenu(c);
    await settle();
    expect(triggerDot(c)).toBeNull();
    expect(store.size).toBe(0);
  });

  it('сбой localStorage — точка горит, исключения наружу нет', async () => {
    stubStorage(true);
    serve({ status: statusDone('F1') });
    const c = mountMenu(sessionA);
    await settle();
    expect(triggerDot(c)).not.toBeNull();
    expect(() => openMenu(c)).not.toThrow();
    await settle();
  });

  it('смена чата при открытом меню не записывает отметку прежнего чата в новый', async () => {
    const store = stubStorage();
    let n = 0;
    serve({ status: () => (++n === 1 ? statusDone('F1') : { running: false, last: null }) });
    const c = mountMenu(sessionA);
    const root = roots[roots.length - 1];
    await settle();
    act(() => { root.render(menuEl(sessionB)); });
    await settle();
    openMenu(c); // открыли меню уже у B: итога у B нет, чужой F1 писать некуда
    await settle();
    expect(store.size).toBe(0);
    expect(triggerDot(c)).toBeNull();
  });

  it('откат: приглушённая точка; открытие меню гасит её', async () => {
    serve();
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё из этого чата'), 'откат');
    await settle();
    click(buttonByExactText(docBody(), 'Откатить всё'), 'подтвердить откат');
    await settle();
    expect((triggerDot(c) as { style?: Record<string, string> }).style?.background).toBe(C.textMuted);
    openMenu(c);
    await settle();
    expect(triggerDot(c)).toBeNull();
  });
});

describe('Внешнее открытие и headless (WM-2)', () => {
  it('headless: триггера нет; открытие — по open.counter; пункт зовёт onClose', async () => {
    const counts = serve({ analyze: { outcome: 'nothing_new', reason: null, packageIds: [] } });
    const onClose = vi.fn();
    const c = mountMenu(sessionA, { headless: true, onClose });
    const root = roots[roots.length - 1];
    await settle();
    expect(findNodeByAttr(c, 'title', TRIGGER_TITLE)).toBeNull();
    expect(has(docBody(), 'Разобрать этот чат')).toBe(false);
    act(() => { root.render(menuEl(sessionA, { headless: true, onClose, open: { counter: 1, anchor: null } })); });
    await settle();
    expect(has(docBody(), 'Разобрать этот чат')).toBe(true);
    click(item('Разобрать этот чат'), 'анализ');
    await settle();
    expect(counts.analyze).toBe(1);
    expect(onClose).toHaveBeenCalledTimes(1);
    // тот же counter — повторного открытия нет; новый — открывает снова
    act(() => { root.render(menuEl(sessionA, { headless: true, onClose, open: { counter: 1, anchor: null } })); });
    await settle();
    expect(has(docBody(), 'Разобрать этот чат')).toBe(false);
    act(() => { root.render(menuEl(sessionA, { headless: true, onClose, open: { counter: 2, anchor: null } })); });
    await settle();
    expect(has(docBody(), 'Разобрать этот чат')).toBe(true);
  });

  it('внешнее открытие при показанном итоге гасит точку так же, как клик по триггеру', async () => {
    serve({ status: { running: false, last: LAST_APPLIED } });
    const c = mountMenu(sessionA);
    const root = roots[roots.length - 1];
    await settle();
    expect(triggerDot(c)).not.toBeNull();
    act(() => { root.render(menuEl(sessionA, { open: { counter: 1, anchor: null } })); });
    await settle();
    expect(triggerDot(c)).toBeNull();
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

  it('личная заметка: 200 rejected «личный раздел — с Ф2» — понятный текст, не строка бэка (доработка H5)', async () => {
    // бэкенд не менялся: заявка личной заметки — 200 {outcome:'rejected', reason:…}
    // (WikiIngestService.cs:180); фронту нужен текст с путём решения, а не внутренняя ссылка на Ф2
    req.mockImplementation(() => Promise.resolve({ outcome: 'rejected', packageId: null, reason: 'личный раздел — с Ф2' }));
    expect(await requestNoteIngest('note-1')).toBe('Личный раздел пока не разбирается — разбор доступен для заметок проекта.');
    // чужая причина остаётся как есть: её смысла не выдумываем
    req.mockImplementation(() => Promise.resolve({ outcome: 'rejected', packageId: null, reason: 'карточки источника нет на диске' }));
    expect(await requestNoteIngest('note-1')).toBe('отказ: карточки источника нет на диске');
  });
});

describe('Опрос после 202: ссылка на хронику и гард поколения (доработка S7b, п.2 и п.4)', () => {
  it('202 → опрос → итог со ссылкой: пункт «Открыть хронику» появляется без перезагрузки', async () => {
    vi.useFakeTimers();
    let nStatus = 0;
    let nChats = 0;
    serve({
      analyze: { running: true },
      status: () => {
        nStatus++;
        if (nStatus === 1) return { running: false, last: null }; // проверка при монтировании
        return { running: false, last: { ...LAST_APPLIED, packageIds: ['pkg-1'] } };
      },
      // хроника появляется в списке только после применённого разбора (первый вызов — пустой)
      chats: () => ({ items: nChats++ === 0 ? [] : [CHATRECORD], counts: {} }),
    });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    expect(item('Открыть хронику')).toBeNull(); // до разбора ссылки нет
    click(item('Разобрать этот чат'), 'анализ');
    await settle(); // 202 обработан: опрос поставил таймер — только теперь его двигать
    await tick(4000); // опрос: running:false + applied → перечитка списка чатов
    expect(await menuHas(c, OK)).toBe(true);
    const chron = item('Открыть хронику');
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

  // OPEN-1: внепроективный чат — даже с путём из списка пункт не шлёт запроса без раздела и не будит переход
  it('без projectId чата — ни запроса в сторе, ни перехода (раздел взять не из чего)', async () => {
    serve({ chats: { items: [CHATRECORD], counts: {} } });
    const sessionNoProject = { id: CHAT_A } as unknown as ChatHeaderSummaryCtx['session'];
    const c = mountMenu(sessionNoProject);
    await settle();
    openMenu(c);
    await settle();
    const chron = item('Открыть хронику');
    expect(chron).not.toBeNull(); // ссылка есть — пункт есть
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
    const container = mountMenu(sessionA);
    const root = roots[roots.length - 1];
    await settle();
    expect(await menuHas(container, 'разбираю…')).toBe(true);
    await tick(4000); // опрос A отправлен и висит (промис держим)
    // смена чата ДО ответа опроса: новое поколение — запоздалый ответ A отбрасывается в .then
    act(() => { root.render(menuEl(sessionB)); });
    await settle();
    release({ running: false, last: LAST_APPLIED });
    await settle();
    expect(await menuHas(container, OK)).toBe(false); // исход A не на экране B
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
  it('очищенные и оставшиеся пакеты (контракт: id пакетов, не страницы) — тоже по числу', () => {
    expect(retractResultText({ ...mk(0), unlinkedPackages: ['a'] })).toBe('1 пакет очищен от сносок.');
    expect(retractResultText({ ...mk(0), unlinkedPackages: ['a', 'b', 'c'] })).toBe('3 пакета очищено от сносок.');
    expect(retractResultText({ ...mk(0), remainingPackages: ['a', 'b'] })).toBe('2 пакета осталось с необработанными страницами.');
    expect(retractResultText({ ...mk(0), remainingPackages: ['a', 'b', 'c', 'd', 'e'] })).toBe('5 пакетов осталось с необработанными страницами.');
  });
});

describe('Достижимость из шапки чата (старый вклад wiki-chat-actions до WM-3)', () => {
  // Окружение для SSR-рендера реальной шапки: window с шириной (useWindowWidth — тот же
  // источник, по которому сама шапка решает isCompact), localStorage, Notification.
  // Эффекты в SSR не работают — запросов нет; проверяется состав ряда действий.
  function withBrowserStub<T>(width: number, fn: () => T): T {
    const g = globalThis as Record<string, unknown>;
    const saved = { window: g.window, localStorage: g.localStorage, Notification: g.Notification };
    g.window = Object.assign(new EventTarget(), {
      innerWidth: width, innerHeight: 900,
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

  function headerHtml(isMobile: boolean, compact = false): string {
    return renderToStaticMarkup(createElement(ChatHeaderBar, {
      session: { id: CHAT_A, name: 'Рефью звука' },
      project: null, hasMessages: true, online: true,
      cost: { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 },
      falCost: { total: 0, count: 0, byModel: new Map() },
      glifCost: { count: 0, credits: 0, hasCredits: false, byType: new Map() },
      billing: null, rateWindows: [],
      isMobile, isCompacting: false, canCompact: false, onCompact: () => {},
      ctxEstimate: { window: 200000, fresh: false, level: 'normal' }, isWaiting: false, items: [],
      persona: null, agent: null, participants: [],
      island: false, compact, contextBar: null, scrolled: false,
    } as never));
  }

  const ROW_TESTIDS = ['wiki-chat-analyze', 'wiki-chat-retract', 'wiki-chat-chronicle', 'wiki-chat-mark-important', 'wiki-chat-mark-skip'];

  it('шапка на десктопе 1440: кнопка-меню «Вики» вместо ряда — ни одного data-testid ряда', () => {
    setAllSubsystems(['notes']);
    setAllFlags({ 'llm-wiki': true });
    const html = withBrowserStub(1440, () => headerHtml(false));
    expect(html).toContain(`title="${TRIGGER_TITLE}"`);
    for (const id of ROW_TESTIDS) expect(html).not.toContain(id);
    expect(html).not.toContain('Откатить всё из этого чата'); // пункты — только в открытом меню
  });

  it('шапка на 360: та же одна кнопка-меню', () => {
    setAllSubsystems(['notes']);
    setAllFlags({ 'llm-wiki': true });
    const html = withBrowserStub(360, () => headerHtml(true));
    expect(html).toContain(TRIGGER_TITLE);
    for (const id of ROW_TESTIDS) expect(html).not.toContain(id);
  });

  it('шапка с compact при широком окне: тоже меню', () => {
    setAllSubsystems(['notes']);
    setAllFlags({ 'llm-wiki': true });
    const html = withBrowserStub(1280, () => headerHtml(false, true));
    expect(html).toContain(`title="${TRIGGER_TITLE}"`);
    for (const id of ROW_TESTIDS) expect(html).not.toContain(id);
  });

  it('флаг llm-wiki выключен — в шапке нет меню «Вики»', () => {
    setAllSubsystems(['notes']); // подсистема активна — нет именно из-за выключенного флага
    setAllFlags({});
    expect(withBrowserStub(1440, () => headerHtml(false))).not.toContain(TRIGGER_TITLE);
    expect(withBrowserStub(360, () => headerHtml(true))).not.toContain(TRIGGER_TITLE);
  });

  it('манифест: вклад wiki-chat-menu в слоте chat-header-action и старый wiki-chat-actions (до WM-3)', () => {
    const names = (notesManifest.slots?.['chat-header-action'] ?? []).map((i: { name: string }) => i.name);
    expect(names).toContain('wiki-chat-menu');
    expect(names).toContain('wiki-chat-actions');
  });
});

// Пометки чата «Важный» / «Не разбирать» (F3): отмечаемые пункты меню, взаимоисключающие
// (один cmark), состояние — из списка отбора (поле mark), клик ставит/снимает через
// setWikiChatMark (PUT /wiki/chats/{id}/mark), у чата исполнения задач пункты неактивны,
// отказ — текстом без смены состояния.
describe('Пометки чата «Важный» / «Не разбирать» (F3)', () => {
  const TASK_REASON = 'ступень 0: чат исполнения задачи';
  type MarkItem = typeof CHATRECORD & { mark: string | null };

  function serveMarks(rec: Partial<MarkItem>, put?: (body: { mark: string | null }) => unknown) {
    const puts: Array<string | null> = [];
    req.mockImplementation((url: string, init?: { method?: string; body?: string }) => {
      const u = String(url);
      const method = (init?.method ?? 'GET').toUpperCase();
      if (method === 'PUT' && u.endsWith('/mark')) {
        const body = JSON.parse(init!.body!) as { mark: string | null };
        puts.push(body.mark);
        return put ? Promise.resolve().then(() => put(body)) : Promise.resolve({ mark: body.mark });
      }
      if (method === 'GET' && u === '/wiki/chats') {
        return Promise.resolve({ items: [{ ...CHATRECORD, mark: null, ...rec }], counts: {} });
      }
      if (method === 'GET' && u.includes('/analyze-status')) return Promise.resolve({ running: false, last: null });
      return Promise.resolve(null);
    });
    return puts;
  }

  // Отмеченный пункт — accent-цветом (флаг active у ItemRow) и с галочкой справа
  const pressed = (label: string) => (item(label) as { style?: { color?: string } }).style?.color === C.accent;
  const disabled = (label: string) => (reactProps(item(label)!) as { disabled?: boolean }).disabled === true;

  it('состояние берётся из списка отбора: у чата с mark=skip отмечен «Не разбирать»', async () => {
    serveMarks({ mark: 'skip' });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    expect(pressed('Не разбирать')).toBe(true);
    expect(pressed('Важный')).toBe(false);
  });

  it('«Не разбирать» ставит пометку ровно одним вызовом, повторный клик снимает (null)', async () => {
    const puts = serveMarks({ mark: null });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(item('Не разбирать'), 'не разбирать');
    await settle();
    expect(puts).toEqual(['skip']);
    openMenu(c);
    await settle();
    expect(pressed('Не разбирать')).toBe(true);
    click(item('Не разбирать'), 'не разбирать повторно');
    await settle();
    expect(puts).toEqual(['skip', null]);
    openMenu(c);
    await settle();
    expect(pressed('Не разбирать')).toBe(false);
  });

  it('«Важный» и «Не разбирать» взаимоисключающие: ответ бэка переключает отметку', async () => {
    const puts = serveMarks({ mark: 'important' });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    expect(pressed('Важный')).toBe(true);
    click(item('Не разбирать'), 'не разбирать');
    await settle();
    expect(puts).toEqual(['skip']);
    openMenu(c);
    await settle();
    expect(pressed('Не разбирать')).toBe(true);
    expect(pressed('Важный')).toBe(false);
  });

  it('у чата исполнения задач пункты неактивны и запроса нет', async () => {
    const puts = serveMarks({ reason: TASK_REASON });
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    expect(disabled('Важный')).toBe(true);
    expect(disabled('Не разбирать')).toBe(true);
    click(item('Не разбирать'), 'не разбирать');
    await settle();
    expect(puts).toEqual([]);
  });

  it('отказ (409 wiki_llm_wiki_off) — текстом, отметка не меняется', async () => {
    const puts = serveMarks({ mark: 'important' }, () => Promise.reject({ status: 409, body: { code: 'wiki_llm_wiki_off', message: 'off' } }));
    const c = mountMenu(sessionA);
    await settle();
    openMenu(c);
    await settle();
    click(item('Не разбирать'), 'не разбирать');
    await settle();
    expect(puts).toEqual(['skip']);
    openMenu(c);
    await settle();
    expect(pressed('Важный')).toBe(true);
    expect(pressed('Не разбирать')).toBe(false);
    expect(has(docBody(), 'Вики выключена в настройках')).toBe(true);
  });
});
