// W13 · Мобила 360: строка «Раздел: …» в шторке панели и состояния мобильной строки
// (макет notes-wiki-panels-v1, п. 1 и таблица «Шторка 360», кадры mob-closed / mob-journal /
// mob-health / mob-empty-wiki).
//
// Строка раздела — только чтение того же значения, по которому тело шторки работает уже:
// selectedKey, до выбора — defaultSectionKey. Своего состояния нет, поэтому правила п. 1
// проверяются как отсутствие изменений: до selectedKey доходят только строки, у которых
// есть раздел (строка дерева; открытие страницы — через стор, OPEN-1). Заметка из
// «Других заметок» (OTHER_KEY — ключа нет) и клик по заголовку узла строку не меняют.
//
// Стор вики НЕ мокан: мокан только его транспорт (offline.request), signalr, флаги и
// брейкпоинты (мобильная ширина — управляемый флаг, образец TodoPlanCard.test.ts) —
// проверка идёт по настоящему потоку «ответ REST → стор → экран».
//
// Мутация этого прогона (вывод — в отчёте задачи): «не обновлять раздел при смене строки»
// (в OPEN-1-эффекте не звать selectSection) → «страница из дерева меняет строку» краснеет;
// после отката — зелёный.

import { act, createElement } from 'react';
import type { ComponentType, ReactNode } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { C } from '../../../../lib/design';
import { WikiTab } from '../WikiTab';
import { WIKI_SUBTABS } from '../wikiTabs';
import { WikiUnifiedTree } from '../WikiUnifiedTree';
import type { NoteSummary } from '../../../../types';
// Стор — настоящий (тот же экземпляр, что у компонента через кит хоста)
import { __test as wikiStore, requestOpenWikiPage } from '../../../../lib/wiki';

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
const flag = vi.hoisted(() => ({ on: true }));
vi.mock('../../../../lib/featureFlags', async (orig) => ({
  ...await orig<typeof import('../../../../lib/featureFlags')>(),
  useFeature: () => flag.on,
  FLAGS: { llmWiki: 'llm-wiki' },
}));
// Мобильная ширина — управляемый флаг (в fakeDom matchMedia всегда false)
const mobile = vi.hoisted(() => ({ value: true }));
vi.mock('../../../../lib/breakpoints', async (orig) => ({
  ...await orig<typeof import('../../../../lib/breakpoints')>(),
  useIsMobile: () => mobile.value,
}));

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

const PROJECT_ID = '11121314-1516-1718-191a-1b1c1d1e1f10';
const SECOND_ID = '41424344-4546-4748-49ab-1c1d1e1f1000';
const PAGE_X = `Проекты/${SECOND_ID}/Хроники/x.md`;

const sec = (key: string, over: Record<string, unknown> = {}) => ({
  key, name: null, orphan: false, local: false, sphereReserved: false, pageCount: 0, ...over,
});

// defaultSectionKey берёт первый живой проект — «AI Home»: он первым в списке разделов
const TREE = {
  initialized: true,
  sections: [
    sec(`projects/${PROJECT_ID}`, { name: 'AI Home', pageCount: 1 }),
    sec(`projects/${SECOND_ID}`, { name: 'Вторая вики', pageCount: 1 }),
    sec('common', { name: 'Общее' }),
    sec('personal', { name: 'Личное' }),
  ],
};

const CATALOG_MAIN = { key: `projects/${PROJECT_ID}`, name: 'AI Home', orphan: false, pages: [] };
const CATALOG_SECOND = {
  key: `projects/${SECOND_ID}`, name: 'Вторая вики', orphan: false,
  pages: [{
    pageKey: PAGE_X, title: 'Хроника X', summary: null, type: 'chronicle',
    status: 'active', sourcesCount: 1, updated: null,
  }],
};
// Страница с заменой, которую человек не вернул: она поднимает точку «Журнала»
const PAGE_DOC = {
  pageKey: PAGE_X,
  content: '---\ntype: chronicle\nstatus: active\nupdated: 2026-11-01\n---\n\n# Хроника X\n\n## Сводка\n- Заменённая строка\n',
  drift: false,
  overrides: [{
    op: 'supersede', statement: 'Заменённая строка', previous: 'Моя строка',
    target: null, at: '2026-11-01T10:00:00Z', packageId: 'pkg-1',
  }],
};

// Заметка, чей источник не совпал ни с одним разделом: её узел — «Другие заметки»,
// у узла раздела вики нет (OTHER_KEY → null в onSelectNote)
const NOTE_OTHER: NoteSummary = {
  id: 'n-other', title: 'Заметка из других', source: 'unknown-source', sourceLabel: 'Неизвестный источник',
  path: 'notes/other.md', tags: [], createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-01T00:00:00Z',
};

function route() {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/tree')) return Promise.resolve(TREE);
    if (url.startsWith('/wiki/initialize')) return Promise.resolve({ initialized: true });
    if (url.startsWith('/wiki/section')) {
      if (url.includes(SECOND_ID)) return Promise.resolve(CATALOG_SECOND);
      if (url.includes(PROJECT_ID)) return Promise.resolve(CATALOG_MAIN);
      return Promise.resolve({ key: 'common', name: 'Общее', orphan: false, pages: [] });
    }
    if (url.startsWith('/wiki/page?')) return Promise.resolve(PAGE_DOC);
    // «Здоровье»: отчёта нет (404) — экран «нет данных», строка кнопки при этом на месте
    if (url.startsWith('/wiki/health')) return Promise.reject({ status: 404 });
    return Promise.resolve(null);
  });
}

const roots: Root[] = [];

type Cont = ReturnType<typeof mount>['container'];

function mountUnified(opts: { drawerTree?: (close: () => void) => ReactNode } = {}): Cont {
  route();
  installFakeDom();
  const { root, container } = mount(createElement(WikiTab, { unified: true, ...opts }));
  roots.push(root);
  return container;
}

// Прогнать промисы эффектов (загрузка дерева → выбор раздела → каталоги; у дерева в
// шторке — ещё и его собственных каталогов через allSettled)
async function settle() {
  for (let i = 0; i < 12; i++) await act(async () => { await Promise.resolve(); });
}

function allNodes(root: Cont, out: Cont[] = []): Cont[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}

function hasTexts(nodes: string[], needle: string): boolean {
  return nodes.join(' ').includes(needle);
}

function clickProps(node: Cont | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick(undefined); });
}

function clickTestId(c: Cont, testId: string) {
  clickProps(findNodeByAttr(c, 'data-testid', testId), testId);
}

function clickAria(c: Cont, label: string) {
  clickProps(findNodeByAttr(c, 'aria-label', label), `aria-label=${label}`);
}

function clickRowByText(c: Cont, testId: string, text: string) {
  const row = allNodes(c).find(n => n.getAttribute('data-testid') === testId
    && hasTexts(textNodes(n), text));
  clickProps(row ?? null, `строка ${testId} «${text}»`);
}

// Текст строки раздела в открытой шторке панели ('' — строки нет)
function lineText(c: Cont): string {
  const n = findNodeByAttr(c, 'data-testid', 'wiki-drawer-section-line');
  return n ? textNodes(n).join(' ') : '';
}

// Тела панелей — заглушки: здесь проверяется строка раздела и обвязка шторки, не тела
// (образец подмены — «подвкладка с хуком» в WikiTab.test.ts). restore — вернуть настоящие.
function stubPanels(except: string[] = []): () => void {
  function StubPanel({ selectedKey }: { selectedKey: string | null }) {
    return createElement('div', { 'data-testid': 'stub-panel' }, `panel ${selectedKey ?? ''}`);
  }
  const saved = WIKI_SUBTABS.map(t => t.component);
  WIKI_SUBTABS.forEach(t => { if (t.component && !except.includes(t.id)) t.component = StubPanel; });
  return () => WIKI_SUBTABS.forEach((t, i) => { t.component = saved[i]; });
}

// Настоящее единое дерево в шторке — та же wiring, что у мобильной drawerTree в NotesPage:
// выбор заметки идёт только в selectNote (раздел вики не трогает), открытие страницы —
// через стор (requestOpenWikiPage → OPEN-1 в WikiTab)
function realTreeInDrawer(close: () => void): ReactNode {
  return createElement(WikiUnifiedTree, {
    sections: TREE.sections,
    notes: [NOTE_OTHER],
    query: '',
    selectedNoteId: null,
    selectedPage: null,
    onSelectNote: () => {},
    onOpenPage: (section: string, page: string) => {
      requestOpenWikiPage({ section, path: page });
      close();
    },
  });
}

beforeEach(() => {
  wikiStore.reset();
  req.mockReset();
  flag.on = true;
  mobile.value = true;
  // PillSwitch меряет пилюлю через rAF (useLayoutEffect) — синхронный стенд, как в
  // «подвкладка с хуком» (WikiTab.test.ts)
  (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (cb: () => void) => { cb(); return 1; };
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  delete (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame;
  uninstallFakeDom();
});

describe('Мобильная шторка вики: строка раздела (W13)', () => {
  it('строка «Раздел: …» под переключателем панелей, раздел по умолчанию', async () => {
    const restore = stubPanels();
    try {
      const c = mountUnified();
      await settle();
      clickTestId(c, 'wiki-open-panel');
      await settle();
      const line = findNodeByAttr(c, 'data-testid', 'wiki-drawer-section-line');
      expect(line, 'строка раздела в шторке панели').not.toBeNull();
      const text = textNodes(line!).join(' ');
      // defaultSectionKey: первый живой проект
      expect(text, 'раздел назван').toContain('Раздел:');
      expect(text, 'имя раздела').toContain('AI Home');
      // место: под переключателем панелей (после его сегментов), не в шапке
      const drawer = findNodeByAttr(c, 'data-testid', 'wiki-drawer-right');
      expect(drawer).not.toBeNull();
      const order = allNodes(drawer!);
      const switchIdx = order.findIndex(n => n.getAttribute('aria-label') === 'Входящие');
      expect(switchIdx, 'переключатель панелей в шторке').toBeGreaterThanOrEqual(0);
      expect(order.indexOf(line!), 'строка под переключателем').toBeGreaterThan(switchIdx);
    } finally { restore(); }
  });

  it('смена раздела в дереве обновляет строку: старого имени не залипает', async () => {
    const restore = stubPanels();
    try {
      const c = mountUnified();
      await settle();
      clickTestId(c, 'wiki-open-panel');
      await settle();
      expect(lineText(c)).toContain('AI Home');
      clickTestId(c, 'wiki-open-panel'); // закрыть шторку (повторное нажатие)
      await settle();
      // сменить раздел в дереве (шторка слева) — строка раздела «Общее»
      clickTestId(c, 'wiki-open-tree');
      await settle();
      clickRowByText(c, 'wiki-section-row', 'Общее'); // onPick закрывает шторку дерева
      await settle();
      // открыть панель заново: строка — про новый раздел, прежнего имени нет
      clickTestId(c, 'wiki-open-panel');
      await settle();
      const text = lineText(c);
      expect(text, 'новое имя раздела').toContain('Общее');
      expect(text, 'прежнее имя не залипает').not.toContain('AI Home');
    } finally { restore(); }
  });

  it('страница из дерева меняет строку (путь мобили: requestOpenWikiPage → OPEN-1)', async () => {
    const restore = stubPanels();
    try {
      const c = mountUnified({ drawerTree: realTreeInDrawer });
      await settle();
      clickTestId(c, 'wiki-open-tree');
      await settle();
      // настоящая единая дерево в шторке: строка страницы → onOpenPage → запрос в стор
      clickRowByText(c, 'unified-row', 'Хроника X');
      await settle();
      clickTestId(c, 'wiki-open-panel');
      await settle();
      const text = lineText(c);
      expect(text, 'раздел открытой страницы').toContain('Вторая вики');
      expect(text, 'прежнее имя не залипает').not.toContain('AI Home');
    } finally { restore(); }
  });

  it('заметка из «Других заметок» строку не меняет (OTHER_KEY — ключа нет)', async () => {
    const restore = stubPanels();
    try {
      const c = mountUnified({ drawerTree: realTreeInDrawer });
      await settle();
      clickTestId(c, 'wiki-open-tree');
      await settle();
      clickRowByText(c, 'unified-row', 'Заметка из других');
      await settle();
      clickTestId(c, 'wiki-open-panel');
      await settle();
      expect(lineText(c), 'раздел прежний').toContain('AI Home');
    } finally { restore(); }
  });

  it('клик по заголовку узла строку не меняет: это сворачивание, не выбор', async () => {
    const restore = stubPanels();
    try {
      const c = mountUnified({ drawerTree: realTreeInDrawer });
      await settle();
      clickTestId(c, 'wiki-open-tree');
      await settle();
      clickRowByText(c, 'unified-group', 'Другие заметки');
      await settle();
      clickTestId(c, 'wiki-open-panel');
      await settle();
      expect(lineText(c), 'раздел прежний').toContain('AI Home');
    } finally { restore(); }
  });

  it('пустая вики: строки раздела нет (как бейджа на десктопе)', async () => {
    const restore = stubPanels();
    try {
      // свой роутинг и свой маунт: mountUnified перечислил бы его route() этот mock
      req.mockImplementation((url: string) => {
        if (url.startsWith('/wiki/tree')) return Promise.resolve({ ...TREE, initialized: false, sections: [] });
        return Promise.resolve(null);
      });
      installFakeDom();
      const { root, container } = mount(createElement(WikiTab, { unified: true }));
      roots.push(root);
      const c = container;
      await settle();
      clickTestId(c, 'wiki-open-panel');
      await settle();
      expect(findNodeByAttr(c, 'data-testid', 'wiki-drawer-section-line'), 'называть нечего').toBeNull();
    } finally { restore(); }
  });
});

describe('Мобильная шторка «Здоровья»: «Проверить сейчас» в теле (W13, проверка W11)', () => {
  it('кнопка первой строкой тела: во всю ширину, 40 px, с подписью', async () => {
    // health остаётся настоящим: проверяется его тело в шторке (без PanelHeader —
    // useHasPanelHeader ложно, и кнопка выбирает место в теле)
    const restore = stubPanels(['health']);
    try {
      const c = mountUnified();
      await settle();
      clickTestId(c, 'wiki-open-panel');
      await settle();
      clickAria(c, 'Здоровье');
      await settle();
      expect(findNodeByAttr(c, 'data-testid', 'wiki-health-run-line'),
        'строка тела «Проверить сейчас»').not.toBeNull();
      const btn = findNodeByAttr(c, 'data-testid', 'wiki-health-run');
      expect(btn).not.toBeNull();
      const style = (reactProps(btn!) as { style?: Record<string, unknown> }).style ?? {};
      expect(style.width, 'во всю ширину').toBe('100%');
      expect(style.minHeight, 'тач-цель 40 px').toBe(40);
      expect(hasTexts(textNodes(c), 'Проверяет всю вики, займёт около минуты'), 'подпись под кнопкой').toBe(true);
    } finally { restore(); }
  });
});

describe('Мобильная строка вики: кнопки и точка (W13)', () => {
  it('кнопки шторок — 40×40 (кадр mob-closed)', async () => {
    const c = mountUnified();
    await settle();
    for (const id of ['wiki-open-tree', 'wiki-open-panel']) {
      const btn = findNodeByAttr(c, 'data-testid', id);
      expect(btn, id).not.toBeNull();
      const style = (reactProps(btn!) as { style?: Record<string, unknown> }).style ?? {};
      expect(style.width, `${id}: ширина`).toBe(40);
      expect(style.height, `${id}: высота`).toBe(40);
    }
  });

  it('точка «Журнала» в мобильной строке — тон C.info (тот же, что на десктопной рельсе)', async () => {
    const c = mountUnified();
    await settle();
    act(() => { requestOpenWikiPage({ section: `projects/${SECOND_ID}`, path: PAGE_X }); });
    await settle();
    const dot = findNodeByAttr(c, 'data-testid', 'wiki-replacement-dot');
    expect(dot, 'точка при открытой странице с unverreturned заменой').not.toBeNull();
    const style = (reactProps(dot!) as { style?: Record<string, unknown> }).style ?? {};
    expect(style.background, 'тон info').toBe(C.info);
  });
});
