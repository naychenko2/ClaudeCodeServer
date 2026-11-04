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
const { wikiMock } = vi.hoisted(() => ({ wikiMock: { tree: null as unknown, catalog: null as unknown, page: null as unknown } }));
vi.mock('../../lib/wiki', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/wiki')>();
  return {
    ...orig,
    ensureWikiLoaded: async () => {},
    loadWikiCatalog: async () => wikiMock.catalog,
    loadWikiPage: async () => wikiMock.page,
    // S7a: «Входящие» — свои, экран списка зовёт listWikiPackages; здесь он пустой:
    // тест проверяет смену подвкладки, а не список пакетов
    listWikiPackages: async () => ({ packages: [], invalidCount: 0 }),
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

  it('F1: notes-wiki-unified + llm-wiki на 360 — две кнопки шторок, дерево во шторке, «Раздел ▸» нет', async () => {
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
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
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
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
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
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

  it('F1: без notes-wiki-unified раскладка прежняя — кнопки шторок нет', async () => {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    const { root, container } = mountNotes();
    clickNode(findButtonByText(container, 'Вики')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-open-tree')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-section-switch')).not.toBeNull();
    root.unmount();
  });

  // Доступность шторок на 360 (вердикт Глеба, M2). Мутации (вывод — в отчёте задачи):
  // убрать слушатель Esc / aria-modal / возврат фокуса / переключатель setDrawer(d => ...) / ariaExpanded
  async function openUnifiedMobile() {
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
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

  it('флаг включён: «Вики» → каталог; «Раздел ▸» → экран «Разделы»; раздел → обратно каталог', async () => {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    const { root, container } = mountNotes();
    const tab = findButtonByText(container, 'Вики');
    expect(tab, 'сегмент «Вики» в мобильном переключателе').not.toBeNull();
    clickNode(tab!);
    await settle();
    // Центр — вкладка «Вики»: каталог выбранного раздела. Мобильного дерева над каталогом
    // нет (оно и схлопывало каталог в нулевую высоту — хвост ревью S5c) — вместо него кнопка
    expect(hasText(container, 'Тестовая страница вики'), 'каталог раздела открыт').toBe(true);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'дерева над каталогом нет').toBeNull();
    const switchBtn = findNodeByAttr(container, 'data-testid', 'wiki-section-switch');
    expect(switchBtn, 'кнопка «Раздел ▸»').not.toBeNull();
    expect(hasText(container, 'Проекты / AI Home'), 'кнопка показывает префикс выбранного раздела').toBe(true);
    clickNode(switchBtn!);
    // Экран «Разделы»: полное дерево, без ограничения высоты
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'список разделов открыт').not.toBeNull();
    const row = findNodeByAttr(container, 'data-testid', 'wiki-section-row');
    expect(row, 'строка раздела в списке').not.toBeNull();
    clickNode(row!);
    await settle();
    // Выбор раздела возвращает к каталогу этого раздела
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'экран «Разделы» закрыт').toBeNull();
    expect(hasText(container, 'Тестовая страница вики'), 'каталог снова открыт').toBe(true);
    root.unmount();
  });

  // Минор 3 ревью S5d: экран «Разделы» был тупиком (возврат только выбором раздела).
  // Кнопка «Назад» (ui/Button, aria-label) возвращает к каталогу выбранного раздела
  it('мобильный «Разделы»: кнопка «Назад» возвращает к каталогу выбранного раздела', async () => {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    const { root, container } = mountNotes();
    clickNode(findNodeByAttr(container, 'aria-label', 'Вики')!);
    await settle();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-section-switch')!);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'экран «Разделы» открыт').not.toBeNull();
    const back = findNodeByAttr(container, 'data-testid', 'wiki-sections-back');
    expect(back, 'кнопка «Назад» на экране «Разделы»').not.toBeNull();
    clickNode(back!);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'экран «Разделы» закрыт').toBeNull();
    expect(hasText(container, 'Тестовая страница вики'), 'каталог выбранного раздела снова открыт').toBe(true);
    root.unmount();
  });

  // Минор 4 ревью S5d: showSections не сбрасывался при смене подвкладки — возврат на
  // «Страницы» открывал «Разделы» снова. Смена подвкладки теперь закрывает этот экран
  it('смена подвкладки закрывает «Разделы»: возврат на «Страницы» — каталог, не список разделов', async () => {
    setAllFlags({ 'llm-wiki': true });
    wikiMock.tree = WIKI_TREE;
    wikiMock.catalog = WIKI_CATALOG;
    const { root, container } = mountNotes();
    clickNode(findNodeByAttr(container, 'aria-label', 'Вики')!);
    await settle();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-section-switch')!);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'экран «Разделы» открыт').not.toBeNull();
    // Подвкладки на мобиле icons-only, имена — в aria-label (PillSwitch.tsx:273).
    // С S7a «Входящие» — не заглушка: признак смены — пустое состояние своего экрана
    clickNode(findNodeByAttr(container, 'aria-label', 'Входящие')!);
    await settle();
    expect(hasText(container, 'Спорного нет'), 'экран «Входящие» открыт').toBe(true);
    clickNode(findNodeByAttr(container, 'aria-label', 'Страницы')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), '«Разделы» не открылся снова').toBeNull();
    expect(hasText(container, 'Тестовая страница вики'), 'открыт каталог').toBe(true);
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
  });
  afterEach(() => {
    setAllFlags({});
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

  it('«Вики» → клик по заметке в рельсе открывает заметку, а не каталог «Вики»', async () => {
    setAllFlags({ 'llm-wiki': true });
    const { root, container } = mountNotes();
    await settle();
    const tab = findNodeByAttr(container, 'aria-label', 'Вики');
    expect(tab, 'десктопный переключатель «Заметки | Вики»').not.toBeNull();
    clickNode(tab!);
    await settle();
    expect(hasText(container, 'Тестовая страница вики'), 'центр — каталог «Вики»').toBe(true);
    // Строка заметки в панели списка — div с title=имени (NotesList.tsx:327)
    const row = findNodeByAttr(container, 'title', NOTE_TITLE);
    expect(row, 'строка заметки в рельсе').not.toBeNull();
    clickNode(row!);
    await settle();
    // Центр — NoteView: кнопка «Править» в тулбаре есть только у загруженной заметки
    expect(findButtonByText(container, 'Править'), 'заметка открыта').not.toBeNull();
    expect(hasText(container, 'Тестовая страница вики'), 'каталога «Вики» в центре больше нет').toBe(false);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'WikiTab размонтирован').toBeNull();
    root.unmount();
  });

  it('F2: unified на десктопе — одно дерево: заметки и страницы вики, переключателя режимов нет', async () => {
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
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

  // Мутации (вывод прогона — в отчёте задачи): (1) не рисовать WikiRail в центре unified →
  // «в режиме заметок рельса есть» краснеет; (2) не передавать pages в WikiRail → «вход в вики
  // из пустой вики» краснеет; (3) вернуть рельсу в WikiTab → «ровно одна рельса» краснеет.
  const rails = (c: AnyNode) => {
    const out: AnyNode[] = [];
    const walk = (x: AnyNode) => {
      if (x.nodeType === 1 && (x as unknown as { getAttribute(n: string): string | null }).getAttribute('data-testid') === 'wiki-rail') out.push(x);
      for (const ch of x.childNodes) walk(ch);
    };
    walk(c);
    return out;
  };

  it('TL-8: unified — в режиме заметок рельса вики есть (четыре раздела + «Страницы»), панель закрыта', async () => {
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
    const { root, container } = mountNotes();
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-doc'), 'режим заметок: центр не вики').toBeNull();
    expect(rails(container as unknown as AnyNode).length, 'рельса одна').toBe(1);
    for (const id of ['pages', 'inbox', 'journal', 'health', 'settings'])
      expect(findNodeByAttr(container, 'data-testid', `wiki-rail-${id}`), `кнопка ${id}`).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-panel'), 'панель закрыта').toBeNull();
    root.unmount();
  });

  it('TL-8: пустая вики — «Страницы» на рельсе ведут к «Вики пока пустая» без кнопки; панель вики открывается без заведённой вики', async () => {
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
    wikiMock.tree = { initialized: false, sections: [] };
    const { root, container } = mountNotes();
    await settle();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-rail-settings')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-panel'), 'панель «Настройки» открыта при пустой вики').not.toBeNull();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-rail-pages')!);
    await settle();
    // LI-3: вики заводится сама — пустое состояние без кнопки «Завести вики»
    expect(findNodeByAttr(container, 'data-testid', 'wiki-empty'), 'пустое состояние вики достижимо').not.toBeNull();
    expect(findButtonByText(container, 'Завести вики'), 'кнопки «Завести вики» нет').toBeNull();
    expect(rails(container as unknown as AnyNode).length, 'в режиме «Вики» рельса ровно одна').toBe(1);
    // повторное нажатие «Страниц» возвращает к заметкам
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-rail-pages')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-empty')).toBeNull();
    root.unmount();
  });

  it('TL-8: кнопка рельсы открывает панель, повторное нажатие закрывает; в режиме «Вики» рельса одна', async () => {
    setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
    const { root, container } = mountNotes();
    await settle();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-rail-journal')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-panel'), 'панель открыта').not.toBeNull();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-rail-pages')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-doc'), 'центр — вики').not.toBeNull();
    expect(rails(container as unknown as AnyNode).length, 'ровно одна рельса').toBe(1);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-panel'), 'панель пережила смену режима').not.toBeNull();
    clickNode(findNodeByAttr(container, 'data-testid', 'wiki-rail-journal')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-panel'), 'повторное нажатие закрывает').toBeNull();
    root.unmount();
  });

  it('TL-8: без notes-wiki-unified рельсы на десктопе нет', async () => {
    setAllFlags({ 'llm-wiki': true });
    const { root, container } = mountNotes();
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-rail'), 'рельсы нет').toBeNull();
    clickNode(findNodeByAttr(container, 'aria-label', 'Вики')!);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-rail'), 'и в режиме «Вики» прежней раскладки нет').toBeNull();
    root.unmount();
  });

  it('переключение «Заметки ↔ Вики» работает в обе стороны', async () => {
    setAllFlags({ 'llm-wiki': true });
    const { root, container } = mountNotes();
    await settle();
    clickNode(findNodeByAttr(container, 'aria-label', 'Вики')!);
    await settle();
    expect(hasText(container, 'Тестовая страница вики'), 'каталог «Вики» открыт').toBe(true);
    clickNode(findNodeByAttr(container, 'aria-label', 'Заметки')!);
    expect(hasText(container, 'Тестовая страница вики'), '«Вики» закрыта').toBe(false);
    expect(findNodeByAttr(container, 'data-testid', 'wiki-tree'), 'WikiTab размонтирован').toBeNull();
    clickNode(findNodeByAttr(container, 'aria-label', 'Вики')!);
    await settle();
    expect(hasText(container, 'Тестовая страница вики'), '«Вики» открылась снова').toBe(true);
    root.unmount();
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
