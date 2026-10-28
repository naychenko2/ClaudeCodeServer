// Рендер-тест вкладки «Вики» (S5c): дерево разделов из четырёх групп с живыми именами и
// пометкой сироты; пустая вики — «Вики не заведена» с кнопкой «Завести вики»; каталог
// выбранного раздела (тип, summary, бейдж статуса, число источников); отказ контракта —
// русским текстом, не сырым кодом.
//
// Стор S5a НЕ мокан: мокан только его транспорт (offline.request) и signalr — так проверка
// идёт по настоящему потоку «ответ REST → стор → экран».
//
// Мутации (проверены прогоном, вывод — в отчёте задачи):
//  (1) не класть группу «Сферы» в дерево → «четыре группы» краснеет;
//  (2) не ставить пометку сироте (badge → undefined) → «сирота помечена» краснеет;
//  (3) initialized: true в фиксированном дереве → «пустая вики» краснеет;
//  (4) показать сырой код отказа вместо wikiErrorText → «отказ русским текстом» краснеет.
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
import { __test as wikiStore } from '../../../../lib/wiki';

vi.mock('../../../../lib/signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', () => ({
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));
// Флаг фичи — управляемый: тест 6 проверяет, что без него вкладки нет вовсе
const flag = vi.hoisted(() => ({ on: true }));
vi.mock('../../../../lib/featureFlags', () => ({
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

// Дерево как его отдаёт GET /api/wiki/tree: постоянные разделы всегда, сирота — name = null
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
    if (url.startsWith('/api/wiki/tree')) return Promise.resolve(tree);
    if (url.startsWith('/api/wiki/section')) return Promise.resolve(CATALOG);
    if (url.startsWith('/api/wiki/initialize')) return Promise.resolve({ initialized: true });
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

  it('незаведённая вики: «Вики не заведена» с кнопкой «Завести вики»', async () => {
    const c = render(EMPTY_TREE);
    await settle();
    expect(has(c, 'Вики не заведена')).toBe(true);
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-init-btn');
    expect(btn).not.toBeNull();
    expect(textNodes(btn!).join('')).toContain('Завести вики');
  });

  it('отказ контракта показан русским текстом, а не сырым кодом', async () => {
    route(EMPTY_TREE);
    req.mockImplementation((url: string) => {
      if (url.startsWith('/api/wiki/tree')) return Promise.resolve(EMPTY_TREE);
      // 409 {code,message} — форма отказа S2f (не 401/404, у них тело пустое)
      if (url.startsWith('/api/wiki/initialize')) {
        return Promise.reject({ status: 409, body: { code: 'wiki_section_off', message: 'локальный проект' } });
      }
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    clickTestId(container, 'wiki-init-btn');
    await settle();
    expect(has(container, 'Вики локальных проектов — отдельной фичей')).toBe(true);
    expect(has(container, 'wiki_section_off')).toBe(false);
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
    if (url.startsWith('/api/wiki/tree')) return Promise.resolve(TREE);
    if (url.startsWith('/api/wiki/section')) return Promise.resolve(CATALOG);
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
    expect(String(deleteCalls()[0][0])).toContain('/api/wiki/section');
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
    req.mockImplementation((url: string) => {
      if (url.startsWith('/api/wiki/tree')) return Promise.resolve(EMPTY_TREE);
      if (url.startsWith('/api/wiki/initialize')) {
        return Promise.reject({ status: 409, body: { code: 'wiki_section_off', message: 'x' } });
      }
      return Promise.resolve(null);
    });
    installFakeDom();
    const { root, container } = mount(createElement(WikiTab));
    roots.push(root);
    await settle();
    clickTestId(container, 'wiki-init-btn');
    await settle();
    expect(has(container, 'Вики локальных проектов — отдельной фичей')).toBe(true);
    expect(buttonByText(container, 'Повторить')).toBeNull();
    click(buttonByText(container, 'Закрыть'), 'Закрыть');
    await settle();
    expect(has(container, 'Вики локальных проектов — отдельной фичей')).toBe(false);
  });

  it('у ошибки загрузки «Повторить» перечитывает дерево', async () => {
    let fail = true;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/api/wiki/tree')) {
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
