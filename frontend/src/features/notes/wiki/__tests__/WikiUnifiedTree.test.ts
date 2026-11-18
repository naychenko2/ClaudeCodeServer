// Единое дерево «заметки + страницы вики» (ADR-038, F2): значки происхождения, фильтр
// «кто написал», поиск, состояния и гонка загрузки каталогов. Данные — фикстуры; стор вики
// настоящий, мокан только транспорт (offline.request) — так проверка идёт по потоку
// «ответ REST → дерево».
//
// Мутации (прогнаны, вывод — в отчёте задачи):
//  (1) снять сверку ключа запроса в useSectionCatalogs → «гонка» краснеет;
//  (2) читать отсутствующее authorship не как 'ai' (wikiAuthorshipOf) → «без authorship — ИИ» краснеет;
//  (3) не фильтровать по происхождению (filterGroups возвращает всё) → «фильтр» краснеет;
//  (4) передавать ключ узла и для OTHER_KEY (без null) → «Другие заметки» краснеет;
//  (5) убрать ветку сфер в buildUnifiedGroups (строку addAll(by.sphere, …)) → «узел сферы» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import {
  WikiUnifiedTree, buildUnifiedGroups, countByOrigin, filterGroups, noteSectionKey, queryText,
  type WikiUnifiedTreeProps,
} from '../WikiUnifiedTree';
import { wikiAuthorshipOf, __test as wikiStore } from '../../../../lib/wiki';
import type { NoteSummary } from '../../../../types';

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

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

// localStorage для свёртки подгрупп: в окружении node её нет — минимальное in-memory хранилище.
// Чистится в beforeEach: тесты не наследуют свёртки друг у друга.
const foldStore = new Map<string, string>();
(globalThis as { localStorage?: unknown }).localStorage = {
  getItem: (k: string) => foldStore.get(k) ?? null,
  setItem: (k: string, v: string) => { foldStore.set(k, v); },
};

const PROJECT_ID = '11121314-1516-1718-191a-1b1c1d1e1f10';
const PROJECT_KEY = `projects/${PROJECT_ID}`;

const sec = (key: string, over: Record<string, unknown> = {}) => ({
  key, name: null, orphan: false, local: false, sphereReserved: false, pageCount: 0, ...over,
});

const SECTIONS = [
  sec('common', { name: 'Общее', pageCount: 1 }),
  sec('personal', { name: 'Личное', pageCount: 1 }),
  sec(PROJECT_KEY, { name: 'AI Home', pageCount: 3 }),
];

const page = (pageKey: string, title: string, authorship?: string, summary: string | null = null, type = 'decision') => ({
  pageKey, title, summary, type, status: 'active', sourcesCount: 1, updated: null,
  ...(authorship ? { authorship } : {}),
});

const CATALOGS: Record<string, unknown> = {
  common: { key: 'common', name: 'Общее', orphan: false, pages: [page('Общее/c1.md', 'Правила команды', 'human')] },
  personal: { key: 'personal', name: 'Личное', orphan: false, pages: [page('Личное/p1.md', 'Мои цели', 'together')] },
  [PROJECT_KEY]: {
    key: PROJECT_KEY, name: 'AI Home', orphan: false,
    pages: [
      page(`Проекты/${PROJECT_ID}/a1.md`, 'Апскейл картинок', 'ai', 'как увеличить'),
      page(`Проекты/${PROJECT_ID}/a2.md`, 'Без метки'),
      page(`Проекты/${PROJECT_ID}/a3.md`, 'Ревью фронта', 'together'),
    ],
  },
};

const note = (id: string, title: string, source: string): NoteSummary => ({
  id, title, source, sourceLabel: source, path: `${title}.md`, tags: [],
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
});

const NOTES = [note('n1', 'Идея про граф', 'personal'), note('n2', 'Заметка проекта', PROJECT_ID), note('n3', 'Чужая', 'unknown-src')];

// Раздел сферы: ключ «spheres/{id}», запись резервирована (ADR-036 §7), страниц нет
const SPHERE_ID = 'c0d1e2f3-a4b5-c6d7-e8f9-0a1b2c3d4e5f';
const SPHERE_KEY = `spheres/${SPHERE_ID}`;
const SPHERE = sec(SPHERE_KEY, { name: 'Дом и семья', sphereReserved: true });

const sectionFromUrl = (url: string) => decodeURIComponent(url.split('section=')[1] ?? '');

const OTHER = '__other__';
const roots: Root[] = [];

const baseProps = (over: Partial<WikiUnifiedTreeProps> = {}): WikiUnifiedTreeProps => ({
  sections: SECTIONS, notes: NOTES, query: '', selectedNoteId: null, selectedPage: null,
  onSelectNote: vi.fn(), onOpenPage: vi.fn(), ...over,
});

function render(props: WikiUnifiedTreeProps) {
  installFakeDom();
  const m = mount(createElement(WikiUnifiedTree, props));
  roots.push(m.root);
  return m;
}

async function settle() {
  for (let i = 0; i < 8; i++) await act(async () => { await Promise.resolve(); });
}

type C = ReturnType<typeof mount>['container'];
const has = (c: C, needle: string) => textNodes(c).map(t => t.trim()).filter(Boolean).join(' ').includes(needle);

function allByAttr(c: C, attr: string, value: string): ReturnType<typeof findNodeByAttr>[] {
  const out: ReturnType<typeof findNodeByAttr>[] = [];
  const walk = (n: C) => {
    const props = reactProps(n);
    if (props?.[attr] === value) out.push(n);
    for (const ch of (n as unknown as { children?: C[] }).children ?? []) walk(ch);
  };
  walk(c);
  return out;
}

// Чип фильтра: ближайший к подписи узел с onClick внутри группы фильтра
function chipNode(c: C, label: string) {
  const group = findNodeByAttr(c, 'data-testid', 'unified-filter')!;
  let hit: C | null = null;
  const walk = (n: C) => {
    if (hit) return;
    const p = reactProps(n) as { onClick?: () => void } | null;
    if (p?.onClick && textNodes(n).join('').includes(label)) { hit = n; return; }
    for (const ch of (n as unknown as { children?: C[] }).children ?? []) walk(ch);
  };
  walk(group);
  if (!hit) throw new Error(`нет чипа ${label}`);
  return hit;
}
const clickChip = (c: C, label: string) => {
  const node = chipNode(c, label);
  act(() => { (reactProps(node) as { onClick: () => void }).onClick(); });
};
// Заявленный minHeight кнопки чипа: на мобиле — тач-цель, на десктопе его нет
const chipMinHeight = (c: C, label: string) =>
  (reactProps(chipNode(c, label)) as { style?: { minHeight?: number } }).style?.minHeight;

// Заголовок узла по подписи (для узлов сфер)
const groupHead = (c: C, label: string) => {
  const hit = allByAttr(c, 'data-testid', 'unified-group').find(n => textNodes(n!).join('').includes(label));
  if (!hit) throw new Error(`нет узла ${label}`);
  return hit;
};
const hasGroup = (c: C, label: string) =>
  allByAttr(c, 'data-testid', 'unified-group').some(n => textNodes(n!).join('').includes(label));

const rowTitles = (c: C) => allByAttr(c, 'data-testid', 'unified-row').map(r => String(reactProps(r!)?.title));
const clickRow = (c: C, title: string) => {
  const r = allByAttr(c, 'data-testid', 'unified-row').find(x => reactProps(x!)?.title === title)!;
  act(() => { (reactProps(r) as { onClick: () => void }).onClick(); });
};

// Подгруппы: заголовок по ключу раздела и виду (data-section/data-type — по ним и сверяем)
const subHead = (c: C, sectionKey: string, typeKey: string) => {
  const hit = allByAttr(c, 'data-testid', 'unified-subgroup')
    .find(n => reactProps(n!)?.['data-section'] === sectionKey && reactProps(n!)?.['data-type'] === typeKey);
  if (!hit) throw new Error(`нет подгруппы ${sectionKey}#${typeKey}`);
  return hit;
}
const hasSub = (c: C, sectionKey: string, typeKey: string) =>
  allByAttr(c, 'data-testid', 'unified-subgroup')
    .some(n => reactProps(n!)?.['data-section'] === sectionKey && reactProps(n!)?.['data-type'] === typeKey);
// Счётчик подгруппы — число в её заголовке
const subCount = (c: C, sectionKey: string, typeKey: string) => {
  const m = textNodes(subHead(c, sectionKey, typeKey)).join('').match(/(\d+)\s*$/);
  if (!m) throw new Error(`нет счётчика у ${sectionKey}#${typeKey}`);
  return Number(m[1]);
};
const clickSub = (c: C, sectionKey: string, typeKey: string) => {
  act(() => { (reactProps(subHead(c, sectionKey, typeKey)) as { onClick: () => void }).onClick(); });
};

beforeEach(() => {
  wikiStore.reset();
  foldStore.clear();
  req.mockReset();
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/section')) return Promise.resolve(CATALOGS[sectionFromUrl(url)]);
    return Promise.resolve(null);
  });
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('Чистая логика дерева', () => {
  it('без authorship страница — «ИИ»; неизвестное значение тоже', () => {
    expect(wikiAuthorshipOf({})).toBe('ai');
    expect(wikiAuthorshipOf({ authorship: 'странное' })).toBe('ai');
    expect(wikiAuthorshipOf({ authorship: 'together' })).toBe('together');
    expect(wikiAuthorshipOf({ authorship: 'human' })).toBe('human');
  });

  it('заметка идёт в раздел по источнику; чужой источник — в «Другие заметки»', () => {
    expect(noteSectionKey(NOTES[0], SECTIONS)).toBe('personal');
    expect(noteSectionKey(NOTES[1], SECTIONS)).toBe(PROJECT_KEY);
    expect(noteSectionKey(NOTES[2], SECTIONS)).toBeNull();
    const groups = buildUnifiedGroups(SECTIONS, NOTES, CATALOGS as never, '');
    expect(groups.map(g => g.label)).toEqual(['AI Home', 'Общее', 'Личное', 'Другие заметки']);
  });

  it('в узле вперемешку страницы и заметки, у каждой строки своё происхождение', () => {
    const g = buildUnifiedGroups(SECTIONS, NOTES, CATALOGS as never, '').find(x => x.key === PROJECT_KEY)!;
    expect(g.items.map(i => `${i.kind}:${i.origin}`)).toEqual(['page:ai', 'page:ai', 'page:together', 'note:note']);
  });

  it('счётчики чипов и фильтр по происхождению', () => {
    const groups = buildUnifiedGroups(SECTIONS, NOTES, CATALOGS as never, '');
    expect(countByOrigin(groups)).toEqual({ all: 8, note: 3, ai: 2, together: 2, me: 1 });
    const together = filterGroups(groups, 'together');
    expect(together.flatMap(g => g.items.map(i => i.title)).sort()).toEqual(['Мои цели', 'Ревью фронта']);
    expect(together.every(g => g.items.length > 0)).toBe(true);
  });

  it('запрос: операторы заметок не ищутся в страницах вики, текст — по заголовку и summary', () => {
    expect(queryText('tag:идея Апскейл')).toBe('апскейл');
    const groups = buildUnifiedGroups(SECTIONS, [], CATALOGS as never, 'увеличить');
    expect(groups.flatMap(g => g.items.map(i => i.title))).toEqual(['Апскейл картинок']);
  });
});

describe('Дубли и служебные карточки (формат данных бэка)', () => {
  const withPages = (pages: unknown[]) => ({
    ...CATALOGS, [PROJECT_KEY]: { key: PROJECT_KEY, name: 'AI Home', orphan: false, pages },
  });
  const pk = (rel: string) => `Проекты/${PROJECT_ID}/${rel}`;
  // Вики-страница в /api/notes: источник 'wiki', путь = полный pageKey (NotesService.cs:179)
  const wikiNote = (id: string, title: string, rel: string): NoteSummary => ({ ...note(id, title, 'wiki'), path: pk(rel) });

  // Мутация: убрать `if (n.source === 'wiki') continue` в buildUnifiedGroups → тест краснеет
  it('вики-заметка (source=wiki) не дублирует страницу из каталога; счётчики по рядам', () => {
    const cats = withPages([page(pk('Решения/Граф.md'), 'Граф'), page(pk('Перенесённая.md'), 'Перенесённая')]);
    const notes = [wikiNote('w1', 'Граф', 'Решения/Граф.md'), wikiNote('w2', 'Перенесённая', 'Перенесённая.md'), NOTES[1]];
    const groups = buildUnifiedGroups(SECTIONS, notes, cats as never, '');
    const g = groups.find(x => x.key === PROJECT_KEY)!;
    expect(g.items.map(i => `${i.kind}:${i.title}`)).toEqual(['page:Граф', 'page:Перенесённая', 'note:Заметка проекта']);
    expect(groups.some(x => x.key === OTHER)).toBe(false);
    expect(countByOrigin(groups).all).toBe(2 + 1 + 1 + 1);
  });

  it('заметка пользователя с заголовком как у страницы — остаётся (сверки по заголовку нет)', () => {
    const cats = withPages([page(pk('Граф.md'), 'Граф')]);
    const own = { ...note('u1', 'Граф', PROJECT_ID), path: 'Граф.md' };
    const g = buildUnifiedGroups(SECTIONS, [own], cats as never, '').find(x => x.key === PROJECT_KEY)!;
    expect(g.items.map(i => i.kind)).toEqual(['page', 'note']);
  });

  it('вики-заметка не возвращается при поиске', () => {
    const cats = withPages([page(pk('Граф.md'), 'Граф')]);
    const groups = buildUnifiedGroups(SECTIONS, [wikiNote('w1', 'Граф', 'Граф.md')], cats as never, 'граф');
    expect(groups.flatMap(x => x.items.map(i => i.kind))).toEqual(['page']);
  });

  // Мутация: убрать `if (isSourceCard(p)) continue` → тест краснеет
  it('карточки источников скрыты по type=source и по сегменту «Источники»; хроника и обычные остаются', () => {
    const cats = withPages([
      page(pk('Источники/Чат-1-abc12345.md'), 'Чат-1', undefined, null, 'source'),
      page(pk('Источники/Без-типа-def67890.md'), 'Без типа', undefined, null, 'decision'),
      page(pk('Прочее/Типовая.md'), 'Типовая', undefined, null, 'source'),
      page(pk('Хроники/s1.md'), 'Хроника чата', undefined, null, 'chronicle'),
      page(pk('a1.md'), 'Обычная'),
    ]);
    const groups = buildUnifiedGroups(SECTIONS, [], cats as never, '');
    const titles = groups.flatMap(x => x.items.map(i => i.title));
    expect(titles).not.toContain('Чат-1');
    expect(titles).not.toContain('Без типа');
    expect(titles).not.toContain('Типовая');
    expect(titles).toContain('Хроника чата');
    expect(titles).toContain('Обычная');
    expect(countByOrigin(groups).all).toBe(2 + 2);
  });

  it('заголовок группы на мобильной ширине — цель касания не ниже 40 px', async () => {
    installFakeDom();
    (globalThis as unknown as { window: { matchMedia: unknown } }).window.matchMedia =
      () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    const m = mount(createElement(WikiUnifiedTree, baseProps()));
    roots.push(m.root);
    await settle();
    const heads = allByAttr(m.container, 'data-testid', 'unified-group');
    expect(heads.length).toBeGreaterThan(0);
    for (const h of heads) {
      expect((reactProps(h!)?.style as { minHeight: number }).minHeight).toBeGreaterThanOrEqual(40);
    }
  });
});

describe('Дерево в интерфейсе', () => {
  it('строки: страницы вики и заметки со значками происхождения', async () => {
    const { container } = render(baseProps());
    await settle();
    expect(rowTitles(container)).toContain('Апскейл картинок');
    expect(rowTitles(container)).toContain('Идея про граф');
    const origins = allByAttr(container, 'data-testid', 'unified-row').map(r => reactProps(r!)?.['data-origin']);
    expect(new Set(origins)).toEqual(new Set(['ai', 'together', 'me', 'note']));
    // значок несёт подпись для скринридера
    expect(findNodeByAttr(container, 'aria-label', 'Ведёт ИИ')).not.toBeNull();
    expect(findNodeByAttr(container, 'aria-label', 'Заметка')).not.toBeNull();
  });

  it('клик по строке: заметка и страница вики зовут свои колбэки', async () => {
    const props = baseProps();
    const { container } = render(props);
    await settle();
    clickRow(container, 'Идея про граф');
    expect(props.onSelectNote).toHaveBeenCalledWith('n1', 'personal');
    clickRow(container, 'Апскейл картинок');
    expect(props.onOpenPage).toHaveBeenCalledWith(PROJECT_KEY, `Проекты/${PROJECT_ID}/a1.md`);
  });

  // Мутация: передавать item.sectionKey и для OTHER_KEY (без null) → «Другие заметки» краснеет
  it('клик по заметке в узле раздела зовёт onSelectNote с ключом этого раздела', async () => {
    const props = baseProps();
    const { container } = render(props);
    await settle();
    clickRow(container, 'Идея про граф');
    expect(props.onSelectNote).toHaveBeenCalledWith('n1', 'personal');
    clickRow(container, 'Заметка проекта');
    expect(props.onSelectNote).toHaveBeenCalledWith('n2', PROJECT_KEY);
  });

  it('клик по заметке в «Других заметках» зовёт onSelectNote с null (чип не меняется)', async () => {
    const props = baseProps();
    const { container } = render(props);
    await settle();
    clickRow(container, 'Чужая');
    expect(props.onSelectNote).toHaveBeenCalledWith('n3', null);
  });

  it('клик по заголовку узла onSelectNote не зовёт', async () => {
    const props = baseProps();
    const { container } = render(props);
    await settle();
    const heads = allByAttr(container, 'data-testid', 'unified-group');
    expect(heads.length).toBeGreaterThan(0);
    for (const h of heads) act(() => { (reactProps(h!) as { onClick: () => void }).onClick(); });
    expect(props.onSelectNote).not.toHaveBeenCalled();
  });

  it('чипы фильтра на мобильной ширине — тач-цель не ниже 40 px', async () => {
    installFakeDom();
    (globalThis as unknown as { window: { matchMedia: unknown } }).window.matchMedia =
      () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    const m = mount(createElement(WikiUnifiedTree, baseProps()));
    roots.push(m.root);
    await settle();
    for (const label of ['Все', 'Заметки', 'ИИ', 'Вместе', 'Мои']) {
      expect(chipMinHeight(m.container, label)).toBeGreaterThanOrEqual(40);
    }
  });

  it('чипы фильтра на десктопе — без повышенной высоты (как прежде)', async () => {
    const { container } = render(baseProps());
    await settle();
    for (const label of ['Все', 'Заметки', 'ИИ', 'Вместе', 'Мои']) {
      expect(chipMinHeight(container, label)).toBeUndefined();
    }
  });

  it('фильтр «кто написал»: «Вместе» оставляет только совместные страницы', async () => {
    const { container } = render(baseProps());
    await settle();
    clickChip(container, 'Вместе');
    await settle();
    expect(rowTitles(container).sort()).toEqual(['Мои цели', 'Ревью фронта']);
  });

  it('пустой фильтр: «Ничего не нашлось» и сброс', async () => {
    const { container } = render(baseProps({ notes: [], sections: [sec('personal', { name: 'Личное', pageCount: 1 })] }));
    await settle();
    clickChip(container, 'Мои');
    await settle();
    expect(has(container, 'Ничего не нашлось')).toBe(true);
    const reset = findNodeByAttr(container, 'data-testid', 'unified-reset-filter')!;
    act(() => { (reactProps(reset) as { onClick: () => void }).onClick(); });
    await settle();
    expect(rowTitles(container)).toContain('Мои цели');
  });

  it('пусто совсем: подсказка вместо дерева', async () => {
    const { container } = render(baseProps({ notes: [], sections: [] }));
    await settle();
    expect(has(container, 'Здесь будут ваши заметки и страницы, которые ведёт ИИ.')).toBe(true);
  });

  it('пока каталоги грузятся — скелетон', async () => {
    req.mockImplementation(() => new Promise(() => {}));
    const { container } = render(baseProps({ notes: [] }));
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'unified-skeleton')).not.toBeNull();
  });
});

describe('Узлы сфер (решение Андрея 04.11)', () => {
  // Мутация: убрать addAll(by.sphere, …) в buildUnifiedGroups → «узел сферы» и «пустое дерево» краснеют
  it('сфера — узел-раздел после проектов, с именем; заметки в неё не раскладываются', () => {
    const notes = [...NOTES, note('n4', 'Заметка о доме', SPHERE_ID)];
    const groups = buildUnifiedGroups([...SECTIONS, SPHERE], notes, CATALOGS as never, '');
    expect(groups.map(g => g.label)).toEqual(['AI Home', 'Дом и семья', 'Общее', 'Личное', 'Другие заметки']);
    const sphere = groups.find(g => g.kind === 'sphere')!;
    expect(sphere.key).toBe(SPHERE_KEY);
    expect(sphere.items.length).toBe(0);
    // источник-сфера для заметки — не раздел вики: заметка остаётся в «Других заметках»
    expect(groups.find(g => g.key === OTHER)!.items.map(i => i.title)).toContain('Заметка о доме');
    expect(countByOrigin(groups).note).toBe(4);
  });

  it('без разделов сферы — дерева с узлом «сфера» нет, порядок прежний', () => {
    const groups = buildUnifiedGroups(SECTIONS, NOTES, CATALOGS as never, '');
    expect(groups.some(g => g.kind === 'sphere')).toBe(false);
    expect(groups.map(g => g.key)).toEqual([PROJECT_KEY, 'common', 'personal', OTHER]);
  });

  it('фильтр по происхождению выбрасывает пустой узел сферы; «Все» — оставляет', () => {
    const groups = buildUnifiedGroups([...SECTIONS, SPHERE], [], CATALOGS as never, '');
    expect(filterGroups(groups, 'all').some(g => g.kind === 'sphere')).toBe(true);
    expect(filterGroups(groups, 'note').some(g => g.kind === 'sphere')).toBe(false);
  });

  it('узел сферы: имя и пояснение внутри; клик по заголовку onSelectNote не зовёт', async () => {
    const props = baseProps({ sections: [...SECTIONS, SPHERE] });
    const { container } = render(props);
    await settle();
    expect(hasGroup(container, 'Дом и семья')).toBe(true);
    expect(has(container, 'Страниц пока нет — раздел сферы наполняется отдельной фичей')).toBe(true);
    const head = groupHead(container, 'Дом и семья');
    act(() => { (reactProps(head) as { onClick: () => void }).onClick(); });
    expect(props.onSelectNote).not.toHaveBeenCalled();
    expect(props.onOpenPage).not.toHaveBeenCalled();
  });

  it('фильтр «Заметки»: узел сферы и его пояснение скрыты', async () => {
    const { container } = render(baseProps({ sections: [...SECTIONS, SPHERE] }));
    await settle();
    clickChip(container, 'Заметки');
    await settle();
    expect(hasGroup(container, 'Дом и семья')).toBe(false);
    expect(has(container, 'Страниц пока нет')).toBe(false);
  });

  it('дерево только с узлом сферы: узел с пояснением, а не общая подсказка', async () => {
    const { container } = render(baseProps({ notes: [], sections: [SPHERE] }));
    await settle();
    expect(hasGroup(container, 'Дом и семья')).toBe(true);
    expect(has(container, 'Страниц пока нет — раздел сферы наполняется отдельной фичей')).toBe(true);
    expect(has(container, 'Здесь будут ваши заметки')).toBe(false);
  });
});

describe('Подгруппы по видам (3А)', () => {
  const pk = (rel: string) => `Проекты/${PROJECT_ID}/${rel}`;
  // Проектный каталог со страницами всех видов + без типа; свои отметки — отдельной группой
  const MIXED: Record<string, unknown> = {
    ...CATALOGS,
    [PROJECT_KEY]: {
      key: PROJECT_KEY, name: 'AI Home', orphan: false,
      pages: [
        page(pk('d1.md'), 'Решение 1', 'ai', null, 'decision'),
        page(pk('d2.md'), 'Решение 2', 'ai', null, 'decision'),
        page(pk('r1.md'), 'Правило 1', 'human', null, 'rule'),
        page(pk('t1.md'), 'Термин 1', 'ai', null, 'term'),
        page(pk('s1.md'), 'Состояние 1', 'together', null, 'state'),
        page(pk('c1.md'), 'Хроника 1', 'ai', null, 'chronicle'),
        page(pk('o1.md'), 'Обзор 1', 'ai', null, 'overview'),
        page(pk('e1.md'), 'Сущность 1', 'human', null, 'entity'),
        page(pk('n0.md'), 'Без типа', 'ai', null, null),
      ],
    },
  };
  const MY_NOTE = note('nm1', 'Моя заметка', PROJECT_ID);
  const serve = (catalogs: Record<string, unknown>) =>
    req.mockImplementation((url: string) => Promise.resolve(url.startsWith('/wiki/section') ? catalogs[sectionFromUrl(url)] : null));

  it('узел раскладывается по подгруппам в фиксированном порядке; заметки — отдельная группа', () => {
    const groups = buildUnifiedGroups(SECTIONS, [MY_NOTE], MIXED as never, '');
    const g = groups.find(x => x.key === PROJECT_KEY)!;
    expect(g.subgroups.map(s => s.typeKey)).toEqual(['decision', 'rule', 'term', 'state', 'chronicle', 'other', 'note']);
    expect(g.subgroups.map(s => s.label)).toEqual(['Решение', 'Правило', 'Термин', 'Состояние', 'Хроника', 'Прочее', 'Мои заметки']);
    expect(g.subgroups.map(s => s.items.length)).toEqual([2, 1, 1, 1, 1, 3, 1]);
    // «Прочее» — типы вне фиксированного списка (overview, entity) и страница без типа;
    // ключ свёртки — раздел + вид
    expect(g.subgroups[5].items.map(i => i.title)).toEqual(['Обзор 1', 'Сущность 1', 'Без типа']);
    expect(g.subgroups.map(s => s.key)).toEqual([
      `${PROJECT_KEY}#decision`, `${PROJECT_KEY}#rule`, `${PROJECT_KEY}#term`, `${PROJECT_KEY}#state`,
      `${PROJECT_KEY}#chronicle`, `${PROJECT_KEY}#other`, `${PROJECT_KEY}#note`,
    ]);
    // общий вид узла — строки в порядке подгрупп
    expect(g.items.map(i => i.kind)).toEqual(Array(9).fill('page').concat(['note']));
  });

  // Мутация: заметки не отдельная группа (NOTE_SUB убран из отображения → 'other') → краснеет
  it('заметка не смешивается со страницами без типа: своя подгруппа', () => {
    const cats = {
      ...CATALOGS,
      [PROJECT_KEY]: { key: PROJECT_KEY, name: 'AI Home', orphan: false, pages: [page(pk('n0.md'), 'Без типа', 'ai', null, null)] },
    };
    const g = buildUnifiedGroups(SECTIONS, [MY_NOTE], cats as never, '').find(x => x.key === PROJECT_KEY)!;
    expect(g.subgroups.map(s => `${s.typeKey}:${s.items.map(i => i.title).join(',')}`)).toEqual([
      'other:Без типа', 'note:Моя заметка',
    ]);
  });

  it('пустые подгруппы в раскладку не попадают', () => {
    const cats = {
      ...CATALOGS,
      [PROJECT_KEY]: { key: PROJECT_KEY, name: 'AI Home', orphan: false, pages: [page(pk('d.md'), 'Только решение', 'ai', null, 'decision')] },
    };
    const g = buildUnifiedGroups(SECTIONS, [], cats as never, '').find(x => x.key === PROJECT_KEY)!;
    expect(g.subgroups.map(s => s.typeKey)).toEqual(['decision']);
  });

  it('дерево: узел содержит подгруппы по порядку и со счётчиками', async () => {
    serve(MIXED);
    const { container } = render(baseProps({ notes: [MY_NOTE] }));
    await settle();
    const heads = allByAttr(container, 'data-testid', 'unified-subgroup')
      .filter(n => reactProps(n!)?.['data-section'] === PROJECT_KEY);
    expect(heads.map(n => reactProps(n!)?.['data-type'])).toEqual(['decision', 'rule', 'term', 'state', 'chronicle', 'other', 'note']);
    expect(subCount(container, PROJECT_KEY, 'decision')).toBe(2);
    expect(subCount(container, PROJECT_KEY, 'other')).toBe(3);
    expect(subCount(container, PROJECT_KEY, 'note')).toBe(1);
  });

  it('фильтр «кто написал»: счётчики подгрупп равны видимым строкам; ставшая пустой — скрыта', async () => {
    serve(MIXED);
    const { container } = render(baseProps({ notes: [], sections: [SECTIONS[2]] }));
    await settle();
    expect(subCount(container, PROJECT_KEY, 'decision')).toBe(2);
    clickChip(container, 'Мои');
    await settle();
    const visible = rowTitles(container);
    expect(visible.sort()).toEqual(['Правило 1', 'Сущность 1']);
    // видны ровно столько строк, сколько показывают счётчики оставшихся подгрупп
    const counts = allByAttr(container, 'data-testid', 'unified-subgroup')
      .filter(n => reactProps(n!)?.['data-section'] === PROJECT_KEY)
      .map(n => subCount(container, String(reactProps(n!)?.['data-section']), String(reactProps(n!)?.['data-type'])));
    expect(counts.reduce((a, b) => a + b, 0)).toBe(visible.length);
    // подгруппы без строк исчезли, с ними — их заголовки
    expect(hasSub(container, PROJECT_KEY, 'decision')).toBe(false);
    expect(hasSub(container, PROJECT_KEY, 'term')).toBe(false);
    expect(subCount(container, PROJECT_KEY, 'rule')).toBe(1);
    expect(subCount(container, PROJECT_KEY, 'other')).toBe(1);
  });

  it('поиск: подгруппы по найденному, счётчик — число после поиска', async () => {
    serve(MIXED);
    const { container } = render(baseProps({ notes: [], sections: [SECTIONS[2]], query: 'термин' }));
    await settle();
    expect(subCount(container, PROJECT_KEY, 'term')).toBe(1);
    expect(rowTitles(container)).toEqual(['Термин 1']);
    expect(hasSub(container, PROJECT_KEY, 'decision')).toBe(false);
  });

  it('свёртка подгруппы сохраняется в localStorage под ключом cc_ и возвращается при повторном рендере', async () => {
    serve(MIXED);
    const m = render(baseProps({ notes: [], sections: [SECTIONS[2]] }));
    await settle();
    expect(reactProps(subHead(m.container, PROJECT_KEY, 'decision'))?.['aria-expanded']).toBe(true);
    clickSub(m.container, PROJECT_KEY, 'decision');
    await settle();
    expect(reactProps(subHead(m.container, PROJECT_KEY, 'decision'))?.['aria-expanded']).toBe(false);
    const raw = (globalThis as { localStorage: { getItem: (k: string) => string | null } }).localStorage.getItem('cc_wiki_tree_folds');
    expect(raw).toBeTruthy();
    expect(JSON.parse(String(raw))).toContain(`${PROJECT_KEY}#decision`);
    // перечитка: новый экземпляр читает хранилище — подгруппа снова свёрнута, строки скрыты
    const m2 = render(baseProps({ notes: [], sections: [SECTIONS[2]] }));
    roots.push(m2.root);
    await settle();
    expect(reactProps(subHead(m2.container, PROJECT_KEY, 'decision'))?.['aria-expanded']).toBe(false);
    expect(rowTitles(m2.container)).not.toContain('Решение 1');
    // раскрытие — тоже пишется: после клика строки видны
    clickSub(m2.container, PROJECT_KEY, 'decision');
    await settle();
    expect(rowTitles(m2.container)).toContain('Решение 1');
  });

  it('заголовок подгруппы на мобильной ширине — цель касания не ниже 40 px', async () => {
    installFakeDom();
    (globalThis as unknown as { window: { matchMedia: unknown } }).window.matchMedia =
      () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    serve(MIXED);
    const m = mount(createElement(WikiUnifiedTree, baseProps({ notes: [MY_NOTE] })));
    roots.push(m.root);
    await settle();
    const subs = allByAttr(m.container, 'data-testid', 'unified-subgroup');
    expect(subs.length).toBeGreaterThan(0);
    for (const s of subs) {
      expect((reactProps(s!)?.style as { minHeight: number }).minHeight).toBeGreaterThanOrEqual(40);
    }
  });
});

describe('Конкурентность загрузки', () => {
  // Набор разделов поменялся, пока первый запрос в пути: его поздний ответ (старый заголовок)
  // не должен затереть свежий. Мутация «снять сверку ключа» делает тест красным.
  it('гонка: устаревший ответ после смены набора разделов отбрасывается', async () => {
    let releaseOld: (v: unknown) => void = () => {};
    let personalCalls = 0;
    req.mockImplementation((url: string) => {
      if (!url.startsWith('/wiki/section')) return Promise.resolve(null);
      const key = sectionFromUrl(url);
      if (key === 'personal' && ++personalCalls === 1) {
        return new Promise(res => { releaseOld = res; });
      }
      return Promise.resolve(CATALOGS[key]);
    });
    installFakeDom();
    const m = mount(createElement(WikiUnifiedTree, baseProps({ notes: [], sections: [SECTIONS[1]] })));
    roots.push(m.root);
    await settle();
    // набор поменялся: к «Личному» добавился «Общее» — второй запрос уходит и отвечает сразу
    act(() => { m.root.render(createElement(WikiUnifiedTree, baseProps({ notes: [], sections: [SECTIONS[0], SECTIONS[1]] }))); });
    await settle();
    expect(rowTitles(m.container)).toContain('Мои цели');
    // поздний ответ первого запроса — с другим содержимым
    await act(async () => {
      releaseOld({ key: 'personal', name: 'Личное', orphan: false, pages: [page('Личное/old.md', 'Устаревшая страница')] });
      await Promise.resolve();
    });
    await settle();
    expect(rowTitles(m.container)).not.toContain('Устаревшая страница');
    expect(rowTitles(m.container)).toContain('Мои цели');
    expect(rowTitles(m.container)).toContain('Правила команды');
  });
});
