// Рендер-тест вкладки «Вики» (S5c): дерево разделов из четырёх групп с живыми именами и
// пометкой сироты; пустая вики — «Вики пока пустая» без кнопки (вики заводится сама,
// LI-3) и тот же экран при заведённой пустой вики; перечитывание дерева при показе
// пустого экрана; каталог выбранного раздела (тип, summary, бейдж статуса, число
// источников); отказ контракта — русским текстом, не сырым кодом.
//
// Стор S5a НЕ мокан: мокан только его транспорт (offline.request) и signalr — так проверка
// идёт по настоящему потоку «ответ REST → стор → экран».
//
// Мутации (проверены прогоном, вывод — в отчёте задачи):
//  (1) не класть группу «Сферы» в дерево → «четыре группы» краснеет;
//  (2) не ставить пометку сироте (badge → undefined) → «сирота помечена» краснеет;
//  (3) initialized: true в фиксированном дереве → «пустая вики» краснеет;
//  (4) показать сырой код отказа вместо wikiErrorText → «отказ русским текстом» краснеет.
// OPEN-1 (этот прогон): (5) не звать consumeOpenWikiPage в обработчике запроса →
// «берёт запрос» краснеет; (6) не звать selectSection → «открывает названный раздел»
// краснеет (страница чужого раздела не показывается).
// Все четыре прогнаны (вывод — в отчёте задачи); тест про флаг llmWiki — отдельный,
// он проверяет gate сам: при выключенном флаге дерево не рисуется.

import { act } from 'react';
import { createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiTab } from '../WikiTab';
import { WIKI_SUBTABS } from '../wikiTabs';
import { useState } from 'react';
// Стор — настоящий (тот же экземпляр, что у компонента через кит): запрос проверяется
// в самом сторе; тестам прямой импорт разрешён (notesStoreImportGuard)
import { __test as wikiStore, consumeOpenWikiPage, requestOpenWikiPage, retractWikiSource } from '../../../../lib/wiki';

// Мок с сохранением остальных экспортов: компонент берёт эти модули через кит хоста,
// а цепочка кита шире собственных импортов теста (registryCore → subscribeFlags,
// sync → onPassiveEvict) — full-replacement мок рвётся на первом недостающем имени.
// Паттерн — как в NotesPage.test.ts.
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
// Флаг фичи — управляемый: тест 6 проверяет, что без него вкладки нет вовсе
const flag = vi.hoisted(() => ({ on: true }));
vi.mock('../../../../lib/featureFlags', async (orig) => ({
  ...await orig<typeof import('../../../../lib/featureFlags')>(),
  useFeature: () => flag.on,
  FLAGS: { llmWiki: 'llm-wiki' },
}));

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

const PROJECT_ID = '11121314-1516-1718-191a-1b1c1d1e1f10';
const ORPHAN_ID = '21222324-2526-2728-292a-2b2c2d2e2f20';
const LOCAL_ID = '31323334-3536-3738-393a-3b3c3d3e3f30';

const sec = (key: string, over: Record<string, unknown> = {}) => ({
  key, name: null, orphan: false, local: false, sphereReserved: false, pageCount: 0, ...over,
});

// Дерево как его отдаёт GET /wiki/tree: постоянные разделы всегда, сирота — name = null
const TREE = {
  initialized: true,
  sections: [
    sec('common', { name: 'Общее', pageCount: 12 }),
    sec('personal', { name: 'Личное', pageCount: 31 }),
    sec(`projects/${PROJECT_ID}`, { name: 'AI Home', pageCount: 142 }),
    sec(`projects/${ORPHAN_ID}`, { orphan: true, pageCount: 9 }),
    sec(`projects/${LOCAL_ID}`, { name: 'Домашний NAS', local: true }),
    sec('spheres/bank', { name: 'Банк', sphereReserved: true }),
  ],
};

const EMPTY_TREE = { ...TREE, initialized: false };

const CATALOG = {
  key: `projects/${PROJECT_ID}`, name: 'AI Home', orphan: false,
  pages: [{
    pageKey: 'reviu-fronta', title: 'Ревью фронта', summary: 'Что закрыто в ревью фронта',
    type: 'decision', status: 'active', sourcesCount: 4, updated: '2026-10-20T10:00:00Z',
  }],
};

function route(tree: typeof TREE) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/tree')) return Promise.resolve(tree);
    if (url.startsWith('/wiki/section')) return Promise.resolve(CATALOG);
    if (url.startsWith('/wiki/initialize')) return Promise.resolve({ initialized: true });
    return Promise.resolve(null);
  });
}

const roots: Root[] = [];

function render(tree: typeof TREE) {
  route(tree);
  installFakeDom();
  const { root, container } = mount(createElement(WikiTab));
  roots.push(root);
  return container;
}

// Прогнать все промисы, которые запустили эффекты (загрузка дерева → выбор раздела →
// каталог): каждый шаг — новый микротask, act обёртывает приходящие обновления стора
async function settle() {
  for (let i = 0; i < 8; i++) await act(async () => { await Promise.resolve(); });
}

function texts(container: ReturnType<typeof mount>['container']): string[] {
  return textNodes(container).map(t => t.trim()).filter(Boolean);
}

// Поиск по склеенному тексту: JSX с интерполяцией рвёт одну подпись на несколько
// текстовых узлов ('4', ' ист.') — по отдельным узлам фраза не нашлась бы
function has(container: ReturnType<typeof mount>['container'], needle: string): boolean {
  return texts(container).join(' ').includes(needle);
}

function clickTestId(container: ReturnType<typeof mount>['container'], testId: string) {
  const node = findNodeByAttr(container, 'data-testid', testId);
  if (!node) throw new Error(`нет узла data-testid=${testId}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у ${testId} нет onClick`);
  act(() => { props.onClick(undefined); });
}

beforeEach(() => {
  wikiStore.reset();
  req.mockReset();
  flag.on = true;
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('Вкладка «Вики» (S5c)', () => {
  it('дерево разделов: четыре группы и живые имена разделов', async () => {
    const c = render(TREE);
    await settle();
    for (const group of ['Проекты', 'Сферы', 'Общее', 'Личное']) {
      expect(has(c, group)).toBe(true);
    }
    // Имена — живые из DTO, не из пути
    expect(has(c, 'AI Home')).toBe(true);
    expect(has(c, 'Домашний NAS')).toBe(true);
    // Раздел сферы не выбирается: его строка помечена как следующая фича
    expect(has(c, 'следующая фича')).toBe(true);
  });

  it('сирота остаётся в своей группе и помечена «проект удалён»', async () => {
    const c = render(TREE);
    await settle();
    expect(has(c, 'проект удалён')).toBe(true);
    // Живого имени удалённого проекта бэк не отдаёт — у строки свой заместитель
    expect(has(c, 'Удалённый проект')).toBe(true);
  });

  it('пустая вики: «Вики пока пустая» без кнопки «Завести вики» (LI-3)', async () => {
    const c = render(EMPTY_TREE);
    await settle();
    expect(has(c, 'Вики пока пустая')).toBe(true);
    expect(has(c, 'Она наполнится сама, когда вы разберёте чат или перенесёте заметку')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-empty')).not.toBeNull();
    // вики заводится сама (решение Андрея 04.11): кнопки и её POST здесь нет
    expect(has(c, 'Завести вики')).toBe(false);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-init-btn')).toBeNull();
    expect(req.mock.calls.some(([u]) => String(u).startsWith('/wiki/initialize'))).toBe(false);
  });

  it('заведённая вики без разделов — то же пустое состояние, не голый каталог (LI-3)', async () => {
    const c = render({ ...TREE, initialized: true, sections: [] });
    await settle();
    expect(has(c, 'Вики пока пустая')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-empty')).not.toBeNull();
    // прежний «Раздел не выбран» при пустом дереве не показывается: выбирать не из чего
    expect(has(c, 'Раздел не выбран')).toBe(false);
  });

  it('флаг llmWiki выключен — вкладки нет вовсе', async () => {
    flag.on = false;
    const c = render(TREE);
    await settle();
    expect(texts(c)).toEqual([]);
  });

  it('каталог выбранного раздела: тип, summary, бейдж статуса, число источников', async () => {
    const c = render(TREE);
    await settle();
    expect(has(c, 'Ревью фронта')).toBe(true);
    expect(has(c, 'Что закрыто в ревью фронта')).toBe(true);
    expect(has(c, 'актуальна')).toBe(true);
    expect(has(c, '4 ист.')).toBe(true);
    // Оглавление — из каталога раздела: префикс раздела над списком
    expect(has(c, 'Проекты / AI Home')).toBe(true);
  });
});

// ─── Доработка S5c: удаление сироты, отказы, реестр подвкладок ───

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}

// Модалка рендерится порталом в document.body — ищем от него, а не от контейнера
const bodyRoot = () => (globalThis as unknown as { document: { body: AnyNode } }).document.body;

function buttonByText(root: AnyNode, text: string): AnyNode | null {
  return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').includes(text)) ?? null;
}

function click(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick!(undefined); });
}

function selectOrphan(c: Cont) {
  const row = allNodes(c).find(n => n.getAttribute('data-testid') === 'wiki-section-row'
    && textNodes(n).join('').includes('Удалённый проект'));
  click(row ?? null, 'строка сироты');
}

const deleteCalls = () => req.mock.calls.filter(
  ([, init]) => (init as { method?: string } | undefined)?.method === 'DELETE',
);

function routeWithDelete(del: () => Promise<unknown>) {
  req.mockImplementation((url: string, init?: { method?: string }) => {
    if (init?.method === 'DELETE') return del();
    if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE);
    if (url.startsWith('/wiki/section')) return Promise.resolve(CATALOG);
    return Promise.resolve(null);
  });
}

function mountWithDelete(del: () => Promise<unknown>) {
  routeWithDelete(del);
  installFakeDom();
  const { root, container } = mount(createElement(WikiTab));
  roots.push(root);
  return container;
}

describe('Вкладка «Вики»: удаление сироты (доработка S5c)', () => {
  it('«Удалить раздел» открывает модалку; «Отмена» не удаляет; подтверждение — один DELETE', async () => {
    const c = mountWithDelete(() => Promise.resolve({ deleted: true }));
    await settle();
    selectOrphan(c);
    await settle();
    click(findNodeByAttr(c, 'data-testid', 'wiki-delete-section-btn'), 'Удалить раздел');
    await settle();
    // модалка называет, что удаляется
    expect(has(bodyRoot(), 'Удалить раздел удалённого проекта?')).toBe(true);
    expect(has(bodyRoot(), '9 стр. удалятся')).toBe(true);
    // Отмена — ничего не удалено, модалка закрылась
    click(buttonByText(bodyRoot(), 'Отмена'), 'Отмена');
    await settle();
    expect(deleteCalls()).toHaveLength(0);
    expect(has(bodyRoot(), 'удалятся из вики')).toBe(false);
    // Повторно открыть и подтвердить — ровно один DELETE
    click(findNodeByAttr(c, 'data-testid', 'wiki-delete-section-btn'), 'Удалить раздел');
    await settle();
    const confirmBtn = allNodes(bodyRoot()).filter(n => n.nodeName === 'BUTTON'
      && textNodes(n).join('').includes('Удалить раздел')).pop() ?? null;
    click(confirmBtn, 'подтвердить');
    await settle();
    expect(deleteCalls()).toHaveLength(1);
    expect(String(deleteCalls()[0][0])).toContain('/wiki/section');
  });

  it('у не-сироты кнопки «Удалить раздел» нет', async () => {
    const c = mountWithDelete(() => Promise.resolve({ deleted: true }));
    await settle();
    // выбран живой проект «AI Home» — не сирота
    expect(has(c, 'Проекты / AI Home')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-delete-section-btn')).toBeNull();
  });

  it('отказ 409 wiki_not_orphan виден русским текстом, unhandled rejection нет', async () => {
    const unhandled: unknown[] = [];
    const onUnhandled = (e: unknown) => { unhandled.push(e); };
    process.on('unhandledRejection', onUnhandled);
    try {
      const c = mountWithDelete(() => Promise.reject({ status: 409, body: { code: 'wiki_not_orphan', message: 'x' } }));
      await settle();
      selectOrphan(c);
      await settle();
      click(findNodeByAttr(c, 'data-testid', 'wiki-delete-section-btn'), 'Удалить раздел');
      await settle();
      const confirmBtn = allNodes(bodyRoot()).filter(n => n.nodeName === 'BUTTON'
        && textNodes(n).join('').includes('Удалить раздел')).pop() ?? null;
      click(confirmBtn, 'подтвердить');
      await settle();
      await new Promise(r => setTimeout(r, 10));
      expect(has(c, 'Удалить можно только раздел удалённого проекта')).toBe(true);
      expect(has(c, 'wiki_not_orphan')).toBe(false);
      expect(unhandled).toEqual([]);
    } finally {
      process.off('unhandledRejection', onUnhandled);
    }
  });
});

// Заглушка подвкладки с хуком внутри — он не должен считаться в WikiTab
function StubInbox({ selectedKey }: { selectedKey: string | null }) {
  const [n] = useState(7);
  return createElement('div', { 'data-testid': 'stub-inbox' }, `раздел=${selectedKey} n=${n}`);
}

describe('Вкладка «Вики»: ошибки и подвкладки (доработка S5c)', () => {
  it('у ошибки действия — «Закрыть» (сбрасывает), «Повторить» нет', async () => {
    // отказ действия — DELETE сироты 409: прежний путь через «Завести вики» убран (LI-3),
    // поведение отказа действия тот же
    const c = mountWithDelete(() => Promise.reject({ status: 409, body: { code: 'wiki_not_orphan', message: 'x' } }));
    await settle();
    selectOrphan(c);
    await settle();
    click(findNodeByAttr(c, 'data-testid', 'wiki-delete-section-btn'), 'Удалить раздел');
    await settle();
    const confirmBtn = allNodes(bodyRoot()).filter(n => n.nodeName === 'BUTTON'
      && textNodes(n).join('').includes('Удалить раздел')).pop() ?? null;
    click(confirmBtn, 'подтвердить');
    await settle();
    expect(has(c, 'Удалить можно только раздел удалённого проекта')).toBe(true);
    expect(buttonByText(c, 'Повторить')).toBeNull();
    click(buttonByText(c, 'Закрыть'), 'Закрыть');
    await settle();
    expect(has(c, 'Удалить можно только раздел удалённого проекта')).toBe(false);
  });

  it('у ошибки загрузки «Повторить» перечитывает дерево', async () => {
    let fail = true;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/tree')) {
        return fail ? Promise.reject({ status: 500, body: null }) : Promise.resolve(TREE);
      }
      return Promise.resolve(CATALOG);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-error')).not.toBeNull();
    fail = false;
    click(buttonByText(container, 'Повторить'), 'Повторить');
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-error')).toBeNull();
    expect(has(container, 'AI Home')).toBe(true);
  });

  it('подвкладка с хуком: переключение туда-обратно не бросает, раздел передаётся', async () => {
    (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (cb: () => void) => { cb(); return 1; };
    const inbox = WIKI_SUBTABS.find(t => t.id === 'inbox')!;
    const saved = inbox.component;
    inbox.component = StubInbox;
    try {
      const c = render(TREE);
      await settle();
      const pills = () => findNodeByAttr(c, 'aria-label', 'Входящие');
      click(pills(), 'Входящие');
      await settle();
      expect(has(c, `раздел=projects/${PROJECT_ID}`)).toBe(true);
      click(findNodeByAttr(c, 'aria-label', 'Страницы'), 'Страницы');
      await settle();
      expect(findNodeByAttr(c, 'data-testid', 'stub-inbox')).toBeNull();
      click(pills(), 'Входящие');
      await settle();
      expect(findNodeByAttr(c, 'data-testid', 'stub-inbox')).not.toBeNull();
    } finally {
      inbox.component = saved;
      delete (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame;
    }
  });
});

// ─── OPEN-1: запрос открыть страницу из шапки чата ───
// Путь страницы — та же форма, что chroniclePath из GET /api/wiki/chats и pageKey из
// каталога: полный путь относительно pages/ («Проекты/{id}/Хроники/{sessionId}.md»).
// Запрошенный раздел — НЕ дефолтный (defaultSectionKey берёт первый живой проект «AI
// Home»): отсутствие selectSection открыло бы страницу в чужом разделе — она не показалась бы.

const SECOND_ID = '41424344-4546-4748-49ab-1c1d1e1f1000';
const CHRONICLE_PATH = `Проекты/${SECOND_ID}/Хроники/chat-x.md`;
const TREE2 = { ...TREE, sections: [...TREE.sections, sec(`projects/${SECOND_ID}`, { name: 'Вторая вики', pageCount: 2 })] };
const CATALOG_SECOND = {
  key: `projects/${SECOND_ID}`, name: 'Вторая вики', orphan: false,
  pages: [{ pageKey: CHRONICLE_PATH, title: 'Хроника chat-x', summary: null, type: 'chronicle', status: 'active', sourcesCount: 2, updated: null }],
};
const CATALOG_EMPTY = { key: 'common', name: 'Общее', orphan: false, pages: [] };
const PAGE_CHRONICLE = {
  pageKey: CHRONICLE_PATH,
  content: '---\ntype: chronicle\nstatus: active\nupdated: 2026-10-30\n---\n\n# Хроника chat-x\n\n## Сводка\n- Содержание хроники chat-x.\n',
  drift: false,
};

// Роутинг: страница существует только в запрошенном разделе; прочие страницы — 404
function routeOpen(tree: typeof TREE) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/tree')) return Promise.resolve(tree);
    if (url.startsWith('/wiki/section')) {
      if (url.includes(`section=projects/${SECOND_ID}`)) return Promise.resolve(CATALOG_SECOND);
      if (url.includes('section=common')) return Promise.resolve(CATALOG_EMPTY);
      return Promise.resolve(CATALOG);
    }
    if (url.startsWith('/wiki/page?')) {
      if (url.includes(`page=${encodeURIComponent(CHRONICLE_PATH)}`)) return Promise.resolve(PAGE_CHRONICLE);
      return Promise.reject({ status: 404 });
    }
    return Promise.resolve(null);
  });
}

// render() перечислил бы mockImplementation своим route() — здесь свой роутинг и свой маунт
function mountOpen(tree: typeof TREE): Cont {
  routeOpen(tree);
  installFakeDom();
  const { root, container } = mount(createElement(WikiTab));
  roots.push(root);
  return container;
}

describe('Вкладка «Вики»: запрос открыть страницу из шапки чата (OPEN-1)', () => {
  it('смонтированная вкладка с запросом: открывает названный раздел и страницу и берёт запрос', async () => {
    const c = mountOpen(TREE2);
    await settle();
    // запрос приходит уже смонтированной вкладке — тот же порядок, что клик в шапке чата
    act(() => { requestOpenWikiPage({ section: `projects/${SECOND_ID}`, path: CHRONICLE_PATH }); });
    await settle();
    expect(has(c, 'Содержание хроники chat-x')).toBe(true); // открыт названный раздел и его страница
    expect(consumeOpenWikiPage()).toBeNull(); // запрос забран, не остался
  });

  it('повторный запрос на ту же страницу открывает её снова (не залипает)', async () => {
    const c = mountOpen(TREE2);
    await settle();
    act(() => { requestOpenWikiPage({ section: `projects/${SECOND_ID}`, path: CHRONICLE_PATH }); });
    await settle();
    expect(has(c, 'Содержание хроники chat-x')).toBe(true);
    // пользователь ушёл со страницы: выбрал в дереве «Общее»
    clickSectionRow(c, 'Общее');
    await settle();
    expect(has(c, 'Содержание хроники chat-x')).toBe(false);
    // тот же запрос снова — страница открывается заново
    act(() => { requestOpenWikiPage({ section: `projects/${SECOND_ID}`, path: CHRONICLE_PATH }); });
    await settle();
    expect(has(c, 'Содержание хроники chat-x')).toBe(true);
    expect(consumeOpenWikiPage()).toBeNull();
  });

  it('запрос, пришедший пока дерево грузится, ждёт загрузки (не теряется)', async () => {
    let releaseTree: (v: unknown) => void = () => {};
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/tree')) return new Promise<unknown>((res) => { releaseTree = res; });
      if (url.startsWith('/wiki/section')) {
        if (url.includes(`section=projects/${SECOND_ID}`)) return Promise.resolve(CATALOG_SECOND);
        if (url.includes('section=common')) return Promise.resolve(CATALOG_EMPTY);
        return Promise.resolve(CATALOG);
      }
      if (url.startsWith('/wiki/page?')) {
        if (url.includes(`page=${encodeURIComponent(CHRONICLE_PATH)}`)) return Promise.resolve(PAGE_CHRONICLE);
        return Promise.reject({ status: 404 });
      }
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    act(() => { requestOpenWikiPage({ section: `projects/${SECOND_ID}`, path: CHRONICLE_PATH }); });
    await settle();
    // дерево ещё не прочитано — страница не открыта, но и запрос не потерян
    expect(has(container, 'Содержание хроники chat-x')).toBe(false);
    releaseTree(TREE2);
    await settle();
    expect(has(container, 'Содержание хроники chat-x')).toBe(true); // дождался дерева и открыл
    expect(consumeOpenWikiPage()).toBeNull();
  });

  it('чужий раздел: экран не роняется — текст отказа и живой раздел; запрос не остаётся', async () => {
    const c = mountOpen(TREE); // в этом дереве запрошенного раздела нет
    await settle();
    act(() => { requestOpenWikiPage({ section: 'projects/нет-такого', path: 'Проекты/нет-такого/Хроники/x.md' }); });
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-error')).not.toBeNull();
    expect(has(c, 'Не найдено')).toBe(true); // отказ — текстом (wikiErrorText 404), не кодом
    expect(has(c, 'Ревью фронта')).toBe(true); // дефолтный выбор вернул на живой раздел: экран жив
    expect(consumeOpenWikiPage()).toBeNull();
  });
});

// ─── Доработка S5b M3: гонка открытия страницы ───
// Мутация этого прогона (вывод — в отчёте задачи): в catch openPage закрыть страницу и
// показать ошибку без сверки поколения → «поздний отказ прежней страницы» краснеет.

const PAGE_B_TEXT = `---
type: topic
status: active
updated: 2026-10-28
---

# Страница B

## Сводка
- Содержание страницы B.
`;

const CATALOG_A = {
  key: `projects/${PROJECT_ID}`, name: 'AI Home', orphan: false,
  pages: [{ pageKey: 'page-a', title: 'Страница A', summary: null, type: 'topic', status: 'active', sourcesCount: 0, updated: null }],
};
const CATALOG_COMMON = {
  key: 'common', name: 'Общее', orphan: false,
  pages: [{ pageKey: 'page-b', title: 'Страница B', summary: null, type: 'topic', status: 'active', sourcesCount: 0, updated: null }],
};

function clickPageRow(c: Cont, title: string) {
  const row = allNodes(c).find(n => n.getAttribute('data-testid') === 'wiki-page-row'
    && textNodes(n).join('').includes(title));
  click(row ?? null, `строку страницы «${title}»`);
}

function clickSectionRow(c: Cont, name: string) {
  const row = allNodes(c).find(n => n.getAttribute('data-testid') === 'wiki-section-row'
    && textNodes(n).join('').includes(name));
  click(row ?? null, `строку раздела «${name}»`);
}

describe('Вкладка «Вики»: гонка открытия страницы (доработка S5b M3)', () => {
  it('поздний отказ прежней страницы не закрывает открытую свежую и не показывает свою ошибку', async () => {
    let rejectA: (e: unknown) => void = () => {};
    const pendingA = new Promise<unknown>((_, rej) => { rejectA = rej; });
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE);
      if (url.startsWith('/wiki/page?')) {
        if (url.includes('page=page-a')) return pendingA;
        return Promise.resolve({ pageKey: 'page-b', content: PAGE_B_TEXT, drift: false });
      }
      if (url.startsWith('/wiki/section')) {
        return Promise.resolve(url.includes('section=common') ? CATALOG_COMMON : CATALOG_A);
      }
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    // A открыта и ещё читается; пользователь уходит в «Общее» и открывает B
    clickPageRow(container, 'Страница A');
    await settle();
    clickSectionRow(container, 'Общее');
    await settle();
    clickPageRow(container, 'Страница B');
    await settle();
    expect(has(container, 'Содержание страницы B')).toBe(true);
    // поздний отказ A: он не относится к уже прочитанной B — её не закрывает, своей
    // ошибки не показывает
    rejectA({ status: 404 });
    await settle();
    expect(has(container, 'Содержание страницы B')).toBe(true);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-error')).toBeNull();
  });
});

// ─── S5b-дельта (W4-2): осознанное закрытие страницы ───
// закрытие (смена раздела / «Назад» страницы) поднимает поколение openSeq: поздний отказ
// запроса закрытой страницы не относится к новому состоянию и не пишет actionError.
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) selectSection закрывает страницу без поднятия gen → «смена раздела» краснеет;
// (2) «Назад» страницы зовёт setOpenPage мимо closePage → «Назад страницы» краснеет.

const PAGE_A_TEXT = `---
type: topic
status: active
updated: 2026-10-28
---

# Страница A

## Сводка
- Содержание страницы A.
`;

describe('Вкладка «Вики»: осознанное закрытие страницы (S5b-дельта, W4-2)', () => {
  it('закрытие сменой раздела: поздний отказ закрытой страницы не пишет ошибку в каталог нового раздела', async () => {
    let rejectA: (e: unknown) => void = () => {};
    const pendingA = new Promise<unknown>((_, rej) => { rejectA = rej; });
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE);
      if (url.startsWith('/wiki/page?')) return pendingA;
      if (url.startsWith('/wiki/section')) {
        return Promise.resolve(url.includes('section=common') ? CATALOG_COMMON : CATALOG_A);
      }
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    // страница A открыта и ещё читается
    clickPageRow(container, 'Страница A');
    await settle();
    // пользователь осознанно уходит в «Общее»: страница закрыта, её запрос ещё идёт
    clickSectionRow(container, 'Общее');
    await settle();
    expect(has(container, 'Страница B')).toBe(true); // каталог нового раздела жив
    // поздний отказ A: страница закрыта осознанно — отказ не относится к этому состоянию
    rejectA({ status: 404 });
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-error')).toBeNull();
    expect(has(container, 'Страница B')).toBe(true);
  });

  it('за кнопкой «Назад» страницы: поздний отказ перечитки не показывается над каталогом', async () => {
    let rejectSecond: (e: unknown) => void = () => {};
    let pageCalls = 0;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE);
      if (url.startsWith('/wiki/section')) return Promise.resolve(CATALOG_A);
      if (url.startsWith('/wiki/page?')) {
        pageCalls += 1;
        if (pageCalls === 1) return Promise.resolve({ pageKey: 'page-a', content: PAGE_A_TEXT, drift: false });
        return new Promise<unknown>((_, rej) => { rejectSecond = rej; });
      }
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    clickPageRow(container, 'Страница A');
    await settle();
    expect(has(container, 'Содержание страницы A')).toBe(true); // страница открыта
    clickTestId(container, 'wiki-page-back'); // «Назад» — осознанное закрытие, возврат к каталогу
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-page-back')).toBeNull();
    // открытие заново: пока идёт перечитка, страница показана из снимка стора — запрос в пути
    clickPageRow(container, 'Страница A');
    await settle();
    expect(has(container, 'Содержание страницы A')).toBe(true);
    clickTestId(container, 'wiki-page-back'); // осознанно закрыть снова, пока запрос идёт
    await settle();
    rejectSecond({ status: 404 });
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-error')).toBeNull();
    expect(has(container, 'Содержание страницы A')).toBe(false); // остались в каталоге
  });
});

// ─── S5b-дельта (W4-5, дефект 3f3449e4): возврат к каталогу, когда стор снял открытую страницу ───
// 404 перечитки после отката снимает страницу из стора (W4-1), а намерение «страница
// открыта» живёт в компоненте: без возврата центр висел бы на скелетоне без выхода.
// Различие «грузится / снята» — факт стора (useWikiPageGone), НЕ отсутствие снимка:
// пока ответа нет, скелетон правильный. Отметка принадлежит ключу снятой страницы —
// новая открытая страница ей не закрывается.
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) убрать эффект возврата в WikiTab (не снимать openedPage) →
//     «ОткатСнесОткрытуюСтраницу» краснеет (центра-каталога нет, строки нет);
// (2) закрывать страницу по отсутствию снимка вместо факта (openedPage && !pageSnap) →
//     «ПокаСтраницаГрузится» и «СнятиеПрежнейНеЗакрываетНовую» краснеют
//     (новая страница закрыта, ещё не прочитанная).

const RETRACT_W45_OK = {
  revertedPackages: [], unlinkedPackages: [], remainingPackages: [],
  removedChronicles: [], failed: [], markedSkip: false,
};

// Строка каталога по заголовку (без клика): её наличие — признак «центр — каталог»
function pageRow(c: Cont, title: string): AnyNode | null {
  return allNodes(c).find(n => n.getAttribute('data-testid') === 'wiki-page-row'
    && textNodes(n).join('').includes(title)) ?? null;
}

describe('Вкладка «Вики»: возврат к каталогу, когда стор снял открытую страницу (W4-5, дефект 3f3449e4)', () => {
  it('откат снес открытую страницу: вкладка возвращается к каталогу раздела, unhandled нет', async () => {
    const unhandled: unknown[] = [];
    const onUnhandled = (e: unknown) => { unhandled.push(e); };
    process.on('unhandledRejection', onUnhandled);
    try {
      let pageCalls = 0;
      req.mockImplementation((url: string) => {
        if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE);
        if (url.startsWith('/wiki/section')) return Promise.resolve(CATALOG_A);
        if (url.startsWith('/wiki/page?')) {
          pageCalls += 1;
          // первый ответ — страница есть; перечитка после отката — 404: её снес откат
          if (pageCalls === 1) return Promise.resolve({ pageKey: 'page-a', content: PAGE_A_TEXT, drift: false });
          return Promise.reject({ status: 404 });
        }
        if (url.startsWith('/wiki/packages/retract')) return Promise.resolve(RETRACT_W45_OK);
        return Promise.resolve(null);
      });
      installFakeDom();
      const { root, container } = mount(createElement(WikiTab));
      roots.push(root);
      await settle();
      clickPageRow(container, 'Страница A');
      await settle();
      expect(has(container, 'Содержание страницы A')).toBe(true); // страница открыта
      // та же операция стора, что вызывает «Журнал»: откат перечитывает открытую и получает 404
      await act(async () => { await retractWikiSource('note:n1'); });
      await settle();
      // центр снова каталог раздела: строка на месте, страницы и её «Назад» нет
      expect(has(container, 'Содержание страницы A')).toBe(false);
      expect(findNodeByAttr(container, 'data-testid', 'wiki-page-back')).toBeNull();
      expect(pageRow(container, 'Страница A')).not.toBeNull();
      // 404 перечитки — не необработанное исключение: откат отдал свой результат
      await new Promise(r => setTimeout(r, 10));
      expect(unhandled).toEqual([]);
    } finally {
      process.off('unhandledRejection', onUnhandled);
    }
  });

  it('пока страница грузится — скелетон остаётся: её никто не снимал', async () => {
    let resolvePage: (v: unknown) => void = () => {};
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE);
      if (url.startsWith('/wiki/section')) return Promise.resolve(CATALOG_A);
      if (url.startsWith('/wiki/page?')) return new Promise<unknown>((res) => { resolvePage = res; });
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    clickPageRow(container, 'Страница A');
    await settle();
    // ответа ещё нет: страницы нет — и каталога тоже (центр — скелетон), страница открыта
    expect(has(container, 'Содержание страницы A')).toBe(false);
    expect(pageRow(container, 'Страница A')).toBeNull();
    // ответ пришёл — страница открыта: идущий запрос не был закрыт как «снятый»
    resolvePage({ pageKey: 'page-a', content: PAGE_A_TEXT, drift: false });
    await settle();
    expect(has(container, 'Содержание страницы A')).toBe(true);
  });

  it('снятие прежней страницы не закрывает новую: откат и открытие другой — в одном ходе', async () => {
    let pageA = 0;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE2);
      if (url.startsWith('/wiki/section')) {
        if (url.includes(`section=projects/${SECOND_ID}`)) return Promise.resolve(CATALOG_SECOND);
        if (url.includes('section=common')) return Promise.resolve(CATALOG_EMPTY);
        return Promise.resolve(CATALOG_A);
      }
      if (url.startsWith('/wiki/page?')) {
        if (url.includes('page=page-a')) {
          pageA += 1;
          if (pageA === 1) return Promise.resolve({ pageKey: 'page-a', content: PAGE_A_TEXT, drift: false });
          return Promise.reject({ status: 404 }); // перечитка после отката: A снесена
        }
        if (url.includes(`page=${encodeURIComponent(CHRONICLE_PATH)}`)) return Promise.resolve(PAGE_CHRONICLE);
        return Promise.reject({ status: 404 });
      }
      if (url.startsWith('/wiki/packages/retract')) return Promise.resolve(RETRACT_W45_OK);
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    clickPageRow(container, 'Страница A');
    await settle();
    expect(has(container, 'Содержание страницы A')).toBe(true); // A открыта и подписана
    // в одном act: откат снес A (её перечитка 404-ит) и запрос из шапки чата открывает B
    await act(async () => {
      const p = retractWikiSource('note:n1');
      requestOpenWikiPage({ section: `projects/${SECOND_ID}`, path: CHRONICLE_PATH });
      await p;
    });
    await settle();
    // новая страница жива: факт снятия принадлежит ключу A и до B не доходит
    expect(has(container, 'Содержание хроники chat-x')).toBe(true);
  });
});

// ─── F1: единая раскладка «Заметки + Вики» (ADR-038) ───
// Мутации (вывод прогона — в отчёте задачи): (1) вернуть рельсу вики в центр WikiTab → «рельса живёт не здесь» краснеет;
// (2) вернуть PillSwitch подвкладок в unified → «в центре нет вкладок» краснеет;
// (3) панель не закрывать повторным нажатием → «повторное нажатие закрывает» краснеет.
describe('Единая раскладка (F1, unified)', () => {
  function renderUnified(tree: typeof TREE, treeHost: unknown = null) {
    route(tree);
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab, { unified: true, treeHost: treeHost as HTMLElement | null }));
    roots.push(root);
    return container;
  }
  const makeHost = () => (globalThis as unknown as { document: { createElement(t: string): never } }).document.createElement('div');

  it('дерево — в левой зоне (узел treeHost), в центре его нет; документ в центре; рельсы и панели в самом WikiTab нет (они в NotesPage)', async () => {
    installFakeDom();
    const host = makeHost();
    const c = renderUnified(TREE, host);
    await settle();
    expect(findNodeByAttr(host, 'data-testid', 'wiki-tree'), 'дерево в левой зоне').not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-tree'), 'в контентной зоне дерева нет').toBeNull();
    expect(has(c, 'Ревью фронта'), 'в центре каталог выбранного раздела').toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-rail'), 'рельса живёт в NotesPage, дубля здесь нет').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-panel')).toBeNull();
  });

  it('левая зона закрыта (treeHost = null): дерева нет нигде, центр цел', async () => {
    const c = renderUnified(TREE, null);
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-tree'), 'дерево без узла-хозяина не рисуется').toBeNull();
    expect(has(c, 'Ревью фронта')).toBe(true);
  });

  it('выбор раздела в дереве левой зоны меняет документ в центре', async () => {
    installFakeDom();
    const host = makeHost();
    const c = renderUnified(TREE, host);
    await settle();
    const doc = findNodeByAttr(c, 'data-testid', 'wiki-doc')!;
    expect(textNodes(doc).join(' '), 'выбран проект').toContain('AI Home');
    const rows: NonNullable<ReturnType<typeof findNodeByAttr>>[] = [];
    const walk = (n: NonNullable<ReturnType<typeof findNodeByAttr>>) => {
      if (n.getAttribute?.('data-testid') === 'wiki-section-row') rows.push(n);
      for (const ch of n.childNodes) walk(ch as never);
    };
    walk(host as never);
    const common = rows.find(r => textNodes(r).join(' ').includes('Общее'));
    expect(common, 'строка «Общее» в левой зоне').toBeDefined();
    const props = reactProps(common!) as { onClick?: (e: unknown) => void };
    act(() => { props.onClick!(undefined); });
    await settle();
    expect(textNodes(doc).join(' '), 'в центре раздел «Общее»').toContain('Общее');
  });

  it('в центре нет вкладок и «Страниц»: подвкладки ушли в рельсу', async () => {
    const c = renderUnified(TREE);
    await settle();
    const doc = findNodeByAttr(c, 'data-testid', 'wiki-doc')!;
    // роли, а не слова: переименование подписей вкладку-невидимку не спрячет
    for (const role of ['tab', 'tablist', 'navigation']) {
      expect(findNodeByAttr(doc, 'role', role), `в центре нет role=${role}`).toBeNull();
    }
    expect(textNodes(doc).join(' ')).not.toContain('Страницы');
    expect(textNodes(doc).join(' ')).not.toContain('Журнал');
  });

  it('без unified рельсы нет — прежняя раскладка с вкладками и колонкой дерева внутри', async () => {
    const c = render(TREE);
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-rail')).toBeNull();
    // прежние вкладки живы: их PillSwitch подписан именами подвкладок (иконки — вне текста)
    expect(has(c, 'Страницы') && has(c, 'Журнал'), 'вкладки подвкладок на месте').toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-tree'), 'дерево прежней раскладки — в центре').not.toBeNull();
  });
});

// ─── LI-3: перечитывание дерева при показе пустого экрана ───
// Пока вики не заведена, сервер заводит её при действиях человека вне этой вкладки
// («Разобрать этот чат», перенос заметки): повторный показ обязан перечитать дерево
// (дешёвый GET), иначе человек увидит устаревшее пустое состояние. Заведённую вики
// повторный показ не перечитывает: там нет устаревшего пустого снимка.
describe('Перечитывание дерева при показе пустого экрана (LI-3)', () => {
  const treeCalls = () => req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/tree')).length;

  it('пустая вики перечитывается при каждом показе; заведённая — нет', async () => {
    route(EMPTY_TREE);
    installFakeDom();
    const m1 = mount(createElement(WikiTab));
    await settle();
    expect(has(m1.container, 'Вики пока пустая')).toBe(true);
    expect(treeCalls(), 'первый показ: ровно один GET дерева').toBe(1);
    act(() => m1.root.unmount());
    // второй показ той же пустой вики: снимок устарел — экран перечитывает
    const m2 = mount(createElement(WikiTab));
    await settle();
    expect(treeCalls(), 'повторный показ пустой вики: дерево перечитан').toBe(2);
    expect(has(m2.container, 'Вики пока пустая')).toBe(true);
    act(() => m2.root.unmount());

    // заведённая вики: повторный показ не перечитывает — своего пустого состояния нет
    wikiStore.reset();
    req.mockClear();
    route(TREE);
    const m3 = mount(createElement(WikiTab));
    await settle();
    expect(treeCalls()).toBe(1);
    act(() => m3.root.unmount());
    const m4 = mount(createElement(WikiTab));
    await settle();
    expect(treeCalls(), 'заведённая вики при повторном показе не перечитывается').toBe(1);
    act(() => m4.root.unmount());
  });
});
