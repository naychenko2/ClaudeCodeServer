// Рендер-тест фильтров «Заметки» (NotesPage, мобильный режим): чи́пы-фильтры — общий
// ui/Chip variant="toggle" (волна 2 «Обзор фич», Ф3.1). Клик переключает фильтр:
// «Избранное», тег, статус комментария. Фильтр = оператор в строке поиска (tag:/status:).
//
// Окружение node (без jsdom): fakeDom + mount + reactProps. Мобильный режим (useIsMobile=true):
// чи́пы живут в сайдбаре (inline, без порталов/IslandScaffold). Проверка «по тексту и роли»:
// ищем чип по подписи, сверяем, что это <button>, кликаем и смотрим, что selected переключился
// (background transparent → var(--c-accent) — заливка C.accent по плану).

import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { NoteDetail, NoteSummary } from '../../types';
import { NotesPage } from './NotesPage';
import { findNodeByAttr, installFakeDom, mount, reactProps, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import { setAllFlags } from '../../lib/featureFlags';
import { notesPanels, zoneOf } from '../../pages/workspace/panelStackState';
import { SECTION_CHIP_TITLE } from '../../components/ui/SectionChip';
// порог тач-цели мобилы — тот же токен, что в коде страницы (сверяем не магическое число)
import { TB } from '../../lib/design';
// очередь запроса «открыть страницу» — настоящая (не моканная): тестам прямой импорт разрешён
import { __test as wikiStore, consumeOpenWikiPage, requestOpenWikiPage } from '../../lib/wiki';

// fakeDom не экспортирует FNode — тип узла берём из сигнатуры reactProps.
type AnyNode = {
  nodeType: number; nodeName: string; nodeValue?: string;
  childNodes: AnyNode[]; style: Record<string, string>;
};
function textOf(n: AnyNode, out: string[] = []): string[] {
  if (n.nodeType === 3) out.push(n.nodeValue ?? '');
  for (const c of n.childNodes) textOf(c, out);
  return out;
}
function findButtonByText(container: AnyNode, text: string): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textOf(x).join(' ').includes(text)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// === Мок leaf-зависимостей (kit реэкспортирует из тех же модулей) ===
const { notesMock } = vi.hoisted(() => ({ notesMock: [] as NoteSummary[] }));
vi.mock('../../lib/notes', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/notes')>();
  return { ...orig, useNotes: () => notesMock, ensureNotesLoaded: async () => {} };
});
vi.mock('../../hooks/useOnline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../hooks/useOnline')>();
  return { ...orig, useOnline: () => true };
});
// Ширина/раскладка — переключаемые: существующие описания мобильные (по умолчанию),
// десктопный describe S5d-доработки ставит envMock сам — без этого десктопный путь
// вкладки («wikiOn», «switchMode», «mode») остался непокрытым (major ревью S5d)
const { envMock } = vi.hoisted(() => ({ envMock: { isMobile: true, width: 360 } }));
vi.mock('../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => envMock.isMobile, useWindowWidth: () => envMock.width };
});
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, notes: { ...orig.api.notes, caps: async () => ({ semantic: false }), list: async () => [], semantic: async () => ({ results: [] }) } } };
});
// HubHeader (в NotesPage) подписывается на onMessage → строит реальное SignalR-соединение
// '/hubs/session', которое в node не резолвится. no-op-подписка, как в FeatureBannerSheet.test.
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
// S5d: стор вики мокан на уровне хуков — настоящий поток «ответ REST → стор → экран» проверен
// в WikiTab.test; здесь проверяется связь вкладки со страницей заметок, а не стор.
// OPEN-1 не мокан: очередь запроса (requestOpenWikiPage/consumeOpenWikiPage/useWikiOpenRequest)
// настоящая — через spread orig; её контракт — в lib/__tests__/wiki.test.ts
const { wikiMock } = vi.hoisted(() => ({
  wikiMock: {
    tree: null as unknown, catalog: null as unknown, page: null as unknown,
    packages: [] as unknown[], healthCalls: [] as string[],
  },
}));
vi.mock('../../lib/wiki', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/wiki')>();
  return {
    ...orig,
    ensureWikiLoaded: async () => {},
    loadWikiCatalog: async () => wikiMock.catalog,
    loadWikiPage: async () => wikiMock.page,
    // S7a: «Входящие» — свои, экран списка зовёт listWikiPackages; здесь он пустой:
    // тест проверяет смену подвкладки, а не список пакетов
    listWikiPackages: async () => ({ packages: wikiMock.packages, invalidCount: 0 }),
    // W3: ключи, по которым «Здоровье» запросило отчёт, — проверка передачи ключа раздела
    getWikiHealth: async (key: string) => { wikiMock.healthCalls.push(key); return { section: key, content: '' }; },
    useWikiTree: () => wikiMock.tree,
    useWikiCatalog: () => wikiMock.catalog,
    useWikiPage: () => wikiMock.page,
  };
});
// S5d-доработка: NoteView читает заметку через офлайн-слой (getNoteForView) — подменяем
// фиксированной деталью: «центр — NoteView» проверяется кнопкой «Править», она есть
// только у загруженной заметки
const { noteMock } = vi.hoisted(() => ({ noteMock: { detail: null as unknown } }));
vi.mock('../../lib/notesOffline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/notesOffline')>();
  return { ...orig, getNoteForView: async () => noteMock.detail };
});
// DocComments внутри NoteView при монтировании зовёт ensurePersonasLoaded: в node стор
// уходит в offline и даёт unhandled OfflineError — no-op, как в других рендер-тестах
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return { ...orig, ensurePersonasLoaded: () => Promise.resolve() };
});

function note(id: string, title: string, tags: string[], annotation?: NoteSummary['annotation']): NoteSummary {
  return {
    id, title, source: 'personal', sourceLabel: 'Личный', path: '',
    tags, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    annotation: annotation ?? null,
  };
}

function mountNotes() {
  const { root, container } = mount(
    createElement(NotesPage, {
      auth: { user: 'u', token: 't' } as never,
      onLogout: () => {},
      onHubTab: () => {},
    }),
  );
  return { root, container };
}

// Окружение рендера NotesPage: fake-DOM + стабы хранилищ и слушателей, которые
// HubHeader/эффекты берут напрямую (общее для обоих describe)
function prepareEnv() {
  installFakeDom();
  // Раскладка по умолчанию — мобильная (существующие описания); десктопный describe
  // переопределяет envMock после вызова prepareEnv
  envMock.isMobile = true;
  envMock.width = 360;
  // HubHeader читает localStorage напрямую (не под guard'ом) — в node его нет, stub.
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {} });
  // fake-window не несёт addEventListener — HubHeader вешает слушателей в эффекте.
  const w = (globalThis as { window: Record<string, unknown> }).window;
  if (typeof w.addEventListener !== 'function') { w.addEventListener = () => {}; w.removeEventListener = () => {}; }
  // parseHash читает window.location.hash; эффект consume читает sessionStorage — оба под stub.
  if (!w.location) w.location = { hash: '' };
  // selectNote → navPush → window.history (state/pushState/replaceState, nav.ts:202):
  // в fake-window его нет — функциональный стаб, чтобы getNav() читал то, что записано
  if (!w.history) {
    const hist: { state: unknown; pushState: (s: unknown) => void; replaceState: (s: unknown) => void } = {
      state: null,
      pushState: (s: unknown) => { hist.state = s; },
      replaceState: (s: unknown) => { hist.state = s; },
    };
    w.history = hist;
  }
  vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {} });
}

describe('NotesPage: клик по ui/Chip переключает фильтр', () => {
  beforeEach(() => {
    prepareEnv();
    notesMock.length = 0;
    notesMock.push(
      note('n1', 'Н1', ['идея', 'избранное']),
      note('n2', 'Н2', ['план']),
    );
  });
  afterEach(() => {
    notesMock.length = 0;
    vi.unstubAllGlobals();
    uninstallFakeDom();
  });

  it('«Избранное»: чип button; клик → selected (background var(--c-accent))', () => {
    const { root, container } = mountNotes();
    const btn = findButtonByText(container, 'Избранное');
    expect(btn, 'чип «Избранное» найден').not.toBeNull();
    expect(btn!.nodeName, 'роль — button').toBe('BUTTON');
    expect(btn!.style.background, 'до клика — не выбран').toBe('transparent');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background, 'после клика — selected, заливка C.accent').toBe('var(--c-accent)');
    root.unmount();
  });

  // Теги и статусы живут внутри CollapseGroup «Фильтры» (defaultOpen=false, условный
  // рендер) — сначала раскрываем группу кликом по её кнопке.
  function expandFilters(container: AnyNode) {
    act(() => {
      const toggle = findButtonByText(container, 'Фильтры');
      (reactProps(toggle!)!.onClick as () => void)();
    });
  }

  it('тег «#идея»: чип button; клик → selected', () => {
    const { root, container } = mountNotes();
    expandFilters(container);
    // «#» и имя тега — отдельные text-ноды (JSX #{tag} · {count}); в fake-DOM join(' ')
    // вставляет пробел, поэтому ищем по имени тега, а не по «#идея»
    const btn = findButtonByText(container, 'идея');
    expect(btn, 'чип тега «#идея» найден').not.toBeNull();
    expect(btn!.nodeName).toBe('BUTTON');
    expect(btn!.style.background).toBe('transparent');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background).toBe('var(--c-accent)');
    root.unmount();
  });

  it('статус комментария «Открытые»: чип button; клик → selected', () => {
    notesMock.push(note('n3', 'Н3', [], { status: 'open', isReply: false } as never));
    const { root, container } = mountNotes();
    expandFilters(container);
    const btn = findButtonByText(container, 'Открытые');
    expect(btn, 'чип «Открытые» найден').not.toBeNull();
    expect(btn!.nodeName).toBe('BUTTON');
    expect(btn!.style.background).toBe('transparent');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background).toBe('var(--c-accent)');
    root.unmount();
  });

  it('повторный клик снимает фильтр (selected → transparent)', () => {
    const { root, container } = mountNotes();
    expandFilters(container);
    const btn = findButtonByText(container, 'идея');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background).toBe('var(--c-accent)');
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.style.background, 'повторный клик — снято').toBe('transparent');
    root.unmount();
  });

  it('M1: на мобиле у чипов minHeight 40px (Избранное, тег, статусы комментариев)', () => {
    notesMock.push(note('n3', 'Н3', [], { status: 'open', isReply: false } as never));
    const { root, container } = mountNotes();
    // «Избранное» — всегда в сайдбаре (mobile: useIsMobile=true в этом тесте)
    const fav = findButtonByText(container, 'Избранное');
    expect(fav, 'чип «Избранное» найден').not.toBeNull();
    expect(fav!.style.minHeight, '«Избранное»: minHeight 40px').toBe('40px');
    // Раскрываем «Фильтры» → теги и статусы комментариев
    expandFilters(container);
    const tag = findButtonByText(container, 'идея');
    expect(tag, 'чип тега «#идея» найден').not.toBeNull();
    expect(tag!.style.minHeight, 'тег: minHeight 40px').toBe('40px');
    const open = findButtonByText(container, 'Открытые');
    expect(open, 'чип «Открытые» найден').not.toBeNull();
    expect(open!.style.minHeight, 'статус комментария: minHeight 40px').toBe('40px');
    root.unmount();
  });
});

// ─── S5d: вкладка «Вики» за флагом llmWiki ───
// Флаг — реальный стор featureFlags (setAllFlags), как в тесте флага у WikiTab, но с другой
// стороны: проверяется вкладка на странице заметок. Утверждения — по конечному состоянию
// после полного маршрута: «Вики» → каталог раздела → «Раздел ▸» → экран «Разделы» →
// выбор раздела → обратно каталог. Мобильный режим (useIsMobile=true мокан выше) —
// он же маршрут Веры на 360.

const wikiSection = (key: string, over: Record<string, unknown> = {}) => ({
  key, name: null, orphan: false, local: false, sphereReserved: false, pageCount: 0, ...over,
});
const WIKI_TREE = {
  initialized: true,
  sections: [
    wikiSection('common', { name: 'Общее', pageCount: 12 }),
    wikiSection('personal', { name: 'Личное', pageCount: 31 }),
    wikiSection('projects/aaa11111', { name: 'AI Home', pageCount: 3 }),
  ],
};
const WIKI_CATALOG = {
  key: 'projects/aaa11111', name: 'AI Home', orphan: false,
  pages: [{
    pageKey: 'test-page', title: 'Тестовая страница вики', summary: 'Проверка связи вкладки',
    type: 'topic', status: 'active', sourcesCount: 1, updated: '2026-10-28T00:00:00Z',
  }],
};

// Заметка для десктопного теста: та же, что в notesMock (строка рельсы), деталь — ответ
// моканного getNoteForView для NoteView
const NOTE_TITLE = 'Заметка из рельса';
const NOTE_DETAIL: NoteDetail = {
  id: 'n1', title: NOTE_TITLE, source: 'personal', sourceLabel: 'Личный', path: '',
  content: 'тело открытой заметки', tags: [], links: [], backlinks: [], unlinkedMentions: [],
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
};

function hasText(container: AnyNode, needle: string): boolean {
  return textOf(container).join(' ').includes(needle);
}

// Прогнать промисы эффектов вкладки (загрузка → выбор раздела): тот же паттерн settle,
// что в WikiTab.test
async function settle() {
  for (let i = 0; i < 8; i++) await act(async () => { await Promise.resolve(); });
}

function clickNode(node: AnyNode) {
  const props = reactProps(node) as { onClick?: () => void } | null;
  if (!props?.onClick) throw new Error('у узла нет onClick');
  act(() => props.onClick());
}

describe('NotesPage: вкладка «Вики» за флагом llmWiki (S5d)', () => {
  beforeEach(() => {
    prepareEnv();
    notesMock.length = 0;
    wikiMock.tree = null;
    wikiMock.catalog = null;
  });
  afterEach(() => {
    // флаг — глобальный стор: снять его надо явно, иначе следующий тест увидел бы вкладку
    setAllFlags({});
    wikiMock.tree = null;
    wikiMock.catalog = null;
    notesMock.length = 0;
    vi.unstubAllGlobals();
    uninstallFakeDom();
  });

  it('флаг выключен: вкладки «Вики» на странице заметок нет', () => {
    setAllFlags({});
    const { root, container } = mountNotes();
    expect(findButtonByText(container, 'Вики'), 'сегмента «Вики» в переключателе нет').toBeNull();
    // остальная страница — ровно как была
    expect(findButtonByText(container, 'Заметки'), 'сегмент «Заметки»').not.toBeNull();
    expect(findButtonByText(container, 'Граф'), 'сегмент «Граф»').not.toBeNull();
    root.unmount();
  });

  it('F1: llm-wiki на 360 — две кнопки шторок, дерево во шторке, «Раздел ▸» нет', async () => {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    const { root, container } = mountNotes();
    clickNode(findButtonByText(container, 'Вики')!);
    await settle();
    expect(hasText(container, 'Тестовая страница вики'), 'в центре документ').toBe(true);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-section-switch'), 'старой кнопки «Раздел ▸» нет').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-open-panel'), 'кнопка панели в шапке').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-drawer-left'), 'шторка закрыта').toBeNull();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-open-tree')!);
    const drawer = findNodeByAttr(container, 'data-testid', 'wiki-drawer-left');
    expect(drawer, 'шторка дерева открыта').not.toBeNull();
    expect(findNodeByAttr(drawer!, 'data-testid', 'wiki-unified-tree'), 'единое дерево во шторке').not.toBeNull();
    root.unmount();
  });

  // F1-qa: на 360 шторка несёт то же единое дерево. Мутации (вывод — в отчёте задачи):
  // не передавать drawerTree в WikiTab → строк unified-row нет; не звать close() в onOpenPage →
  // «шторка закрылась» краснеет; minHeight строки 44 → 30 → «цели касания» краснеет.
  const unifiedRows = (c: AnyNode, kind: string) => {
    const out: AnyNode[] = [];
    const walk = (x: AnyNode) => {
      if (x.nodeType === 1 && x.getAttribute('data-testid') === 'unified-row' && x.getAttribute('data-kind') === kind) out.push(x);
      for (const ch of x.childNodes) walk(ch);
    };
    walk(c);
    return out;
  };

  it('F1-qa: шторка на 360 — строки заметок и страниц вики; цели касания ≥40; в центре нет tab/navigation', async () => {
    notesMock.push(note('n1', 'Заметка из шторки', []));
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    const { root, container } = mountNotes();
    clickNode(findButtonByText(container, 'Вики')!);
    await settle();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-open-tree')!);
    await settle();
    const drawer = findNodeByAttr(container, 'data-testid', 'wiki-drawer-left')!;
    expect(unifiedRows(drawer, 'note').length, 'строки заметок').toBeGreaterThan(0);
    expect(unifiedRows(drawer, 'page').length, 'строки страниц вики').toBeGreaterThan(0);
    for (const r of [...unifiedRows(drawer, 'note'), ...unifiedRows(drawer, 'page')]) {
      const st = (reactProps(r) as { style: { minHeight: number } }).style;
      expect(st.minHeight, 'цель касания строки').toBeGreaterThanOrEqual(40);
    }
    const doc = findNodeByAttr(container, 'data-testid', 'wiki-doc')!;
    for (const role of ['tab', 'tablist', 'navigation']) expect(findNodeByAttr(doc, 'role', role), `в центре нет role=${role}`).toBeNull();
    root.unmount();
  });

  it('F1-qa: выбор страницы закрывает шторку и открывает страницу; выбор заметки открывает заметку', async () => {
    notesMock.push(note('n1', 'Заметка из шторки', []));
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    noteMock.detail = { ...NOTE_DETAIL, id: 'n1', title: 'Заметка из шторки' };
    const { root, container } = mountNotes();
    clickNode(findButtonByText(container, 'Вики')!);
    await settle();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-open-tree')!);
    await settle();
    clickNode(unifiedRows(findNodeByAttr(container, 'data-testid', 'wiki-drawer-left')!, 'page')[0]);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-drawer-left'), 'шторка закрылась').toBeNull();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-open-tree')!);
    await settle();
    clickNode(unifiedRows(findNodeByAttr(container, 'data-testid', 'wiki-drawer-left')!, 'note')[0]);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-drawer-left'), 'шторки нет').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-doc'), 'вики ушла, открыта заметка').toBeNull();
    root.unmount();
  });

  // Доступность шторок на 360 (вердикт Глеба, M2). Мутации (вывод — в отчёте задачи):
  // убрать слушатель Esc / aria-modal / возврат фокуса / переключатель setDrawer(d => ...) / ariaExpanded
  async function openUnifiedMobile() {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    const { root, container } = mountNotes();
    clickNode(findButtonByText(container, 'Вики')!);
    await settle();
    return { root, container };
  }
  const pressEsc = () => {
    const fns = [...((document as unknown as { listeners: Record<string, Set<(e: unknown) => void>> }).listeners.keydown ?? [])];
    act(() => { fns.forEach(f => f({ key: 'Escape', defaultPrevented: false, preventDefault() {} })); });
  };

  it('F1/шторка: aria-modal, id для aria-controls, фокус внутри; Esc закрывает и возвращает фокус на кнопку', async () => {
    const { root, container } = await openUnifiedMobile();
    const trigger = findNodeByAttr(container, 'data-testid', 'wiki-open-tree')!;
    expect(trigger.getAttribute('aria-expanded'), 'шторка закрыта').toBe('false');
    expect(trigger.getAttribute('aria-controls')).toBe('wiki-drawer-tree');
    (trigger as unknown as { focus(): void }).focus();
    clickNode(trigger);
    const drawer = findNodeByAttr(container, 'data-testid', 'wiki-drawer-left')!;
    expect(drawer.getAttribute('aria-modal')).toBe('true');
    expect(drawer.getAttribute('id'), 'id шторки = aria-controls триггера').toBe('wiki-drawer-tree');
    expect(trigger.getAttribute('aria-expanded'), 'шторка открыта').toBe('true');
    expect((document as unknown as { activeElement: unknown }).activeElement, 'фокус ушёл в шторку').toBe(drawer);
    pressEsc();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-drawer-left'), 'Esc закрыл шторку').toBeNull();
    expect(trigger.getAttribute('aria-expanded'), 'aria-expanded снова false').toBe('false');
    expect((document as unknown as { activeElement: unknown }).activeElement, 'фокус вернулся на кнопку').toBe(trigger);
    root.unmount();
  });

  it('F1/шторка: повторное нажатие на кнопку закрывает шторку (дерево и панель)', async () => {
    const { root, container } = await openUnifiedMobile();
    for (const [btn, drawerId, aria] of [
      ['wiki-open-tree', 'wiki-drawer-left', 'wiki-drawer-tree'],
      ['wiki-open-panel', 'wiki-drawer-right', 'wiki-drawer-panel'],
    ] as const) {
      const trigger = findNodeByAttr(container, 'data-testid', btn)!;
      expect(trigger.getAttribute('aria-controls')).toBe(aria);
      clickNode(trigger);
      expect(findNodeByAttr(container, 'data-testid', drawerId), `${btn}: открыта`).not.toBeNull();
      expect(trigger.getAttribute('aria-expanded')).toBe('true');
      clickNode(trigger);
      expect(findNodeByAttr(container, 'data-testid', drawerId), `${btn}: повторное нажатие закрывает`).toBeNull();
      expect(trigger.getAttribute('aria-expanded')).toBe('false');
    }
    root.unmount();
  });

  it('F1/шторка панели: выбор по умолчанию запоминается (панель не «висит» на null)', async () => {
    const { root, container } = await openUnifiedMobile();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-open-panel')!);
    const drawer = findNodeByAttr(container, 'data-testid', 'wiki-drawer-right')!;
    // заголовок шторки — название раздела, а не общее «Панель вики»
    expect(findNodeByAttr(drawer, 'data-testid', 'wiki-panel-close'), 'шапка шторки есть').not.toBeNull();
    expect(hasText(drawer, 'Входящие'), 'заголовок — выбранный раздел').toBe(true);
    root.unmount();
  });

});

// ─── S5d-доработка: десктопный путь вкладки (major ревью Глеба) ───
// На 1280: переключатель «Заметки | Вики» — внутри панели списка (граф на десктопе
// панель, не режим), рельса со списком остаётся рядом с каталогом «Вики». Мёртвый клик
// (selectNote не снимал wikiOn) ловится здесь: после клика по строке в центре NoteView,
// а не WikiTab.

describe('NotesPage: десктоп, «Вики» и выбор заметки (S5d-доработка)', () => {
  beforeEach(() => {
    prepareEnv();
    envMock.isMobile = false;
    envMock.width = 1280;
    // PanelZone читает document.documentElement.style и ResizeObserver — стабы, как в
    // тесте воркспейса (в fake-DOM documentElement — null)
    const d = (globalThis as { document: Record<string, unknown> }).document;
    d.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
    // PanelShell в эффекте измеряет панель через requestAnimationFrame (PanelShell.tsx:139),
    // PanelZone в cleanup зовёт cancelAnimationFrame (PanelZone.tsx:184) — стабы на обе стороны
    (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (cb: () => void) => { cb(); return 1; };
    (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame = () => {};
    notesMock.length = 0;
    notesMock.push(note('n1', NOTE_TITLE, []));
    noteMock.detail = NOTE_DETAIL;
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    wikiMock.packages = [];
  });
  afterEach(() => {
    setAllFlags({});
    // раскладка зон — модульное состояние: закрываем открытое тестом, чтобы оно не текло дальше
    for (const k of ['wikiInbox', 'wikiJournal', 'wikiHealth', 'wikiSettings', 'notesGraph'] as const)
      notesPanels.ops.close(k);
    wikiMock.packages = [];
    wikiMock.tree = null;
    wikiMock.catalog = null;
    noteMock.detail = null;
    notesMock.length = 0;
    delete (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame;
    delete (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame;
    vi.unstubAllGlobals();
    uninstallFakeDom();
  });

  it('флаг выключен: десктопного переключателя «Вики» нет вовсе', () => {
    setAllFlags({});
    const { root, container } = mountNotes();
    expect(findNodeByAttr(container, 'aria-label', 'Вики'), 'сегмента «Вики» нет').toBeNull();
    root.unmount();
  });

  it('F2: unified на десктопе — одно дерево: заметки и страницы вики, переключателя режимов нет', async () => {
    setAllFlags({ 'llm-wiki': true });
    const { root, container } = mountNotes();
    await settle();
    const tree = findNodeByAttr(container, 'data-testid', 'wiki-unified-tree');
    expect(tree, 'единое дерево в левой зоне').not.toBeNull();
    expect(findNodeByAttr(container, 'aria-label', 'Вики'), 'переключателя «Заметки | Вики» нет').toBeNull();
    // в одном дереве и заметка, и страница вики
    const noteRow = findNodeByAttr(tree!, 'title', NOTE_TITLE);
    const pageRow = findNodeByAttr(tree!, 'title', 'Тестовая страница вики');
    expect(noteRow, 'строка заметки в дереве').not.toBeNull();
    expect(pageRow, 'строка страницы вики в дереве').not.toBeNull();
    // клик по странице вики — в центре документ вики, дерево на месте
    clickNode(pageRow!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-doc'), 'в центре документ вики').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-unified-tree'), 'дерево осталось').not.toBeNull();
    // клик по заметке — центр снова заметка
    clickNode(findNodeByAttr(container, 'title', NOTE_TITLE)!);
    await settle();
    expect(findButtonByText(container, 'Править'), 'заметка открыта').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-doc'), 'документа вики в центре больше нет').toBeNull();
    root.unmount();
  });

  // W3: панели вики — панели зон раздела; самодельной рельсы TL-8 в центре нет
  const rails = (c: AnyNode) => {
    const out: AnyNode[] = [];
    const walk = (x: AnyNode) => {
      if (x.nodeType === 1 && (x as unknown as { getAttribute(n: string): string | null }).getAttribute('data-testid') === 'wiki-rail') out.push(x);
      for (const ch of x.childNodes) walk(ch);
    };
    walk(c);
    return out;
  };
  // Кнопки рельсы зоны по подписи: aria-label «Журнал» или «Журнал: <подсказка бейджа>»
  const railButtons = (c: AnyNode, label: string) => {
    const out: AnyNode[] = [];
    const walk = (x: AnyNode) => {
      const al = x.nodeType === 1 ? (x as unknown as { getAttribute(n: string): string | null }).getAttribute('aria-label') : null;
      if (al === label || (al != null && al.startsWith(`${label}: `))) out.push(x);
      for (const ch of x.childNodes) walk(ch);
    };
    walk(c);
    return out;
  };
  const chipOf = (c: AnyNode) => findNodeByAttr(c as never, 'title', SECTION_CHIP_TITLE) as AnyNode | null;
  const openWikiPanel = (key: 'wikiInbox' | 'wikiJournal' | 'wikiHealth' | 'wikiSettings') =>
    act(() => { notesPanels.ops.openIn('right', key); });

  it('W3: при флаге на 1280 рельсы TL-8 нет, кнопка «Журнал» ровно одна — в рельсе зоны', async () => {
    setAllFlags({ 'llm-wiki': true });
    const { root, container } = mountNotes();
    await settle();
    expect(rails(container as unknown as AnyNode).length, 'самодельной рельсы нет').toBe(0);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-rail-pages'), 'кнопки «Страницы» нет').toBeNull();
    for (const label of ['Входящие', 'Журнал', 'Здоровье', 'Настройки'])
      expect(railButtons(container as unknown as AnyNode, label).length, `кнопка «${label}» ровно одна`).toBe(1);
    root.unmount();
  });

  it('W3: чип раздела — у «Журнала» и «Входящих» (подпись по умолчанию — AI Home), у «Настроек» чипа нет', async () => {
    setAllFlags({ 'llm-wiki': true });
    const { root, container } = mountNotes();
    await settle();
    openWikiPanel('wikiJournal');
    await settle();
    expect(hasText(chipOf(container as unknown as AnyNode)!, 'AI Home'), 'follow по умолчанию — раздел AI Home').toBe(true);
    act(() => { notesPanels.ops.close('wikiJournal'); });
    openWikiPanel('wikiSettings');
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-panel-body'), 'панель «Настройки» открыта').not.toBeNull();
    expect(chipOf(container as unknown as AnyNode), 'у «Настроек» чипа нет').toBeNull();
    root.unmount();
  });

  it('W3: выбор заметки двигает чип за её разделом, «Другие заметки» раздел не меняют; «Здоровье» получает ключ', async () => {
    setAllFlags({ 'llm-wiki': true });
    notesMock.push({ ...note('n2', 'Заметка без раздела', []), source: 'zzz-unknown', sourceLabel: 'Чужой' });
    wikiMock.healthCalls.length = 0;
    const { root, container } = mountNotes();
    await settle();
    openWikiPanel('wikiHealth');
    await settle();
    expect(wikiMock.healthCalls.at(-1), 'по умолчанию — раздел проекта').toBe('projects/aaa11111');
    // заметка личного источника → раздел «Личное»
    clickNode(findNodeByAttr(findNodeByAttr(container, 'data-testid', 'wiki-unified-tree')!, 'title', NOTE_TITLE)!);
    await settle();
    expect(hasText(chipOf(container as unknown as AnyNode)!, 'Личное'), 'чип за разделом заметки').toBe(true);
    expect(wikiMock.healthCalls.at(-1), '«Здоровье» запросило отчёт нового раздела').toBe('personal');
    // заметка из «Других заметок» раздел не меняет
    clickNode(findNodeByAttr(findNodeByAttr(container, 'data-testid', 'wiki-unified-tree')!, 'title', 'Заметка без раздела')!);
    await settle();
    expect(hasText(chipOf(container as unknown as AnyNode)!, 'Личное'), 'раздел чипа прежний').toBe(true);
    expect(wikiMock.healthCalls.at(-1)).toBe('personal');
    root.unmount();
  });

  it('W3: пустая вики — в центре «Вики пока пустая», чипа и чисел на рельсе нет', async () => {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = { initialized: false, sections: [] };
    wikiMock.packages = [{ id: 'p1' }];
    const { root, container } = mountNotes();
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-empty'), 'пустое состояние в центре').not.toBeNull();
    expect(railButtons(container as unknown as AnyNode, 'Входящие').length, 'кнопка есть, числа и подсказки нет').toBe(1);
    wikiMock.packages = [];
    expect(findButtonByText(container, 'Завести вики'), 'кнопки «Завести вики» нет').toBeNull();
    openWikiPanel('wikiInbox');
    await settle();
    expect(chipOf(container as unknown as AnyNode), 'чипа нет').toBeNull();
    root.unmount();
  });

  it('W3: число «Входящих» на рельсе — с подсказкой «N ждут вашего решения»', async () => {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.packages = [{ id: 'p1' }, { id: 'p2' }];
    const { root, container } = mountNotes();
    await settle();
    const al = (railButtons(container as unknown as AnyNode, 'Входящие')[0] as unknown as { getAttribute(n: string): string }).getAttribute('aria-label');
    expect(al).toBe('Входящие: 2 ждут вашего решения');
    root.unmount();
  });

  it('W3: открытая панель вики не закрывает граф заметок слева', async () => {
    setAllFlags({ 'llm-wiki': true });
    // граф в центре тянет useThemeColors: в node нет MutationObserver
    (globalThis as { MutationObserver?: unknown }).MutationObserver = class { observe() {} disconnect() {} };
    const { root, container } = mountNotes();
    await settle();
    act(() => { notesPanels.ops.openIn('left', 'notesGraph'); });
    openWikiPanel('wikiJournal');
    await settle();
    expect(zoneOf(notesPanels.getZones(), 'notesGraph'), 'граф на месте').toBe('left');
    expect(zoneOf(notesPanels.getZones(), 'wikiJournal'), 'журнал открыт').toBe('right');
    expect(findNodeByAttr(container, 'data-testid', 'wiki-panel-body')).not.toBeNull();
    root.unmount();
    delete (globalThis as { MutationObserver?: unknown }).MutationObserver;
  });

});

// ─── OPEN-1: запрос «Открыть хронику» из шапки чата ───
// Запрос — в настоящем сторе вики (моканы только читающие хуки). Задача страницы:
// переключить на «Вики» при монтировании с необработанным запросом — без единого клика;
// при выключенном флаге — снять запрос, не включая режим. Открытие самой страницы —
// в WikiTab.test; здесь — связь запроса с режимом страницы.

const CHRONICLE_PATH = 'Проекты/aaa11111/Хроники/chat-x.md';
const CHRONICLE_PAGE = {
  pageKey: CHRONICLE_PATH,
  content: '---\ntype: chronicle\nstatus: active\nupdated: 2026-10-30\n---\n\n# Хроника chat-x\n\n## Сводка\n- Содержание хроники chat-x.\n',
  drift: false,
};

describe('NotesPage: запрос «Открыть хронику» (OPEN-1)', () => {
  beforeEach(() => {
    prepareEnv();
    notesMock.length = 0;
    // запрос — модульное состояние стора: каждый тест начинает с чистого
    wikiStore.reset();
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    wikiMock.page = CHRONICLE_PAGE;
  });
  afterEach(() => {
    setAllFlags({});
    wikiMock.tree = null;
    wikiMock.catalog = null;
    wikiMock.page = null;
    notesMock.length = 0;
    vi.unstubAllGlobals();
    uninstallFakeDom();
  });

  it('флаг включён: страница сама переключается на «Вики» и открывает запрошенную страницу; запрос забран', async () => {
    setAllFlags({ 'llm-wiki': true });
    act(() => { requestOpenWikiPage({ section: 'projects/aaa11111', path: CHRONICLE_PATH }); });
    const { root, container } = mountNotes();
    await settle();
    // ни одного клика: режим включил запрос, страницу открыл WikiTab
    expect(hasText(container, 'Содержание хроники chat-x'), 'запрошенная страница открыта').toBe(true);
    expect(consumeOpenWikiPage()).toBeNull(); // запрос забран, не остался на следующий вход
    root.unmount();
  });

  it('флаг выключен: запрос снят, страница в «Вики» не переходит', async () => {
    setAllFlags({});
    act(() => { requestOpenWikiPage({ section: 'projects/aaa11111', path: CHRONICLE_PATH }); });
    const { root, container } = mountNotes();
    await settle();
    expect(consumeOpenWikiPage()).toBeNull(); // снят страницей с выключенным флагом — не дождётся включения
    expect(hasText(container, 'Содержание хроники chat-x')).toBe(false);
    expect(findButtonByText(container, 'Вики'), 'переключателя «Вики» нет, как и без запроса').toBeNull();
    root.unmount();
  });
});

// ─── Тач-цели кнопок создания: порог 40 px на мобиле ───
// «Новая» и «Дневниковая заметка» рисует sidebarControls NotesPage — она стоит и в
// списке заметок (мобильный сайдбар), и в мобильной шторке дерева (drawerTree WikiTab).
// Порог — TB.iconHitMobile (docs/design/guidelines.md, п.4 чек-листа «тач-цели не
// меньше 40px»). Мутации (вывод прогона — в отчёте задачи): (1) убрать minHeight у
// «Новой» → краснеет «мобильный список»; (2) убрать minHeight/minWidth у иконочной →
// краснеет та же; (3) ставить их всегда, без isMobile → краснеет десктопный тест.

const DAILY_TITLE = 'Дневниковая заметка на сегодня';
const HIT_PX = `${TB.iconHitMobile}px`;

// Кнопки блока создания: «Новая» — по подписи; «Дневниковая заметка» — по title
// (текста у неё нет, только иконка)
function createButtons(scope: AnyNode) {
  return {
    newNote: findButtonByText(scope, 'Новая'),
    daily: findNodeByAttr(scope as never, 'title', DAILY_TITLE),
  };
}

describe('NotesPage: тач-цели «Новая» / «Дневниковая заметка» ≥ 40 px на мобиле', () => {
  beforeEach(() => {
    prepareEnv();
    notesMock.length = 0;
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
  });
  afterEach(() => {
    setAllFlags({});
    wikiMock.tree = null;
    wikiMock.catalog = null;
    notesMock.length = 0;
    vi.unstubAllGlobals();
    uninstallFakeDom();
  });

  it('мобильный список: «Новая» ≥ 40 px по высоте, «Дневниковая заметка» ≥ 40×40', () => {
    const { root, container } = mountNotes();
    const { newNote, daily } = createButtons(container);
    expect(newNote, 'кнопка «Новая» есть').not.toBeNull();
    expect(daily, 'кнопка «Дневниковая заметка» есть').not.toBeNull();
    expect(newNote!.style.minHeight, '«Новая» — высота по тач-порогу').toBe(HIT_PX);
    expect(daily!.style.minHeight, 'иконочная — высота по тач-порогу').toBe(HIT_PX);
    expect(daily!.style.minWidth, 'иконочная — и ширина по тач-порогу (без подписи её держат только паддинги)').toBe(HIT_PX);
    root.unmount();
  });

  it('мобильная шторка дерева (unified, режим «Вики»): те же цели внутри шторки', async () => {
    setAllFlags({ 'llm-wiki': true });
    const { root, container } = mountNotes();
    clickNode(findButtonByText(container, 'Вики')!);
    await settle();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-open-tree')!);
    await settle();
    const drawer = findNodeByAttr(container, 'data-testid', 'wiki-drawer-left');
    expect(drawer, 'шторка дерева открыта').not.toBeNull();
    const { newNote, daily } = createButtons(drawer as unknown as AnyNode);
    expect(newNote, '«Новая» в шторке есть').not.toBeNull();
    expect(daily, '«Дневниковая заметка» в шторке есть').not.toBeNull();
    expect(newNote!.style.minHeight).toBe(HIT_PX);
    expect(daily!.style.minHeight).toBe(HIT_PX);
    expect(daily!.style.minWidth).toBe(HIT_PX);
    root.unmount();
  });

  it('десктоп 1280: размеров-исключений нет — кнопки как прежде', () => {
    envMock.isMobile = false;
    envMock.width = 1280;
    // Путь десктопа — через PanelZone/PanelShell (documentElement, ResizeObserver,
    // requestAnimationFrame): те же стабы, что в десктопном описании S5d-доработки
    const d = (globalThis as { document: Record<string, unknown> }).document;
    d.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
    (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame = (cb: () => void) => { cb(); return 1; };
    (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame = () => {};
    const { root, container } = mountNotes();
    const { newNote, daily } = createButtons(container);
    expect(newNote, 'кнопка «Новая» есть').not.toBeNull();
    expect(daily, 'кнопка «Дневниковая заметка» есть').not.toBeNull();
    expect(newNote!.style.minHeight, 'на десктопе высоты-исключения нет').toBeUndefined();
    expect(daily!.style.minHeight, 'на десктопе высоты-исключения нет').toBeUndefined();
    expect(daily!.style.minWidth, 'на десктопе ширины-исключения нет').toBeUndefined();
    root.unmount();
    delete (globalThis as { requestAnimationFrame?: unknown }).requestAnimationFrame;
    delete (globalThis as { cancelAnimationFrame?: unknown }).cancelAnimationFrame;
    delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver;
  });
});
