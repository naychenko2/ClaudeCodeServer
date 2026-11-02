// Единое дерево «заметки + страницы вики» (ADR-038, F2): значки происхождения, фильтр
// «кто написал», поиск, состояния и гонка загрузки каталогов. Данные — фикстуры; стор вики
// настоящий, мокан только транспорт (offline.request) — так проверка идёт по потоку
// «ответ REST → дерево».
//
// Мутации (прогнаны, вывод — в отчёте задачи):
//  (1) снять сверку ключа запроса в useSectionCatalogs → «гонка» краснеет;
//  (2) читать отсутствующее authorship не как 'ai' (wikiAuthorshipOf) → «без authorship — ИИ» краснеет;
//  (3) не фильтровать по происхождению (filterGroups возвращает всё) → «фильтр» краснеет.

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

const page = (pageKey: string, title: string, authorship?: string, summary: string | null = null) => ({
  pageKey, title, summary, type: 'decision', status: 'active', sourcesCount: 1, updated: null,
  ...(authorship ? { authorship } : {}),
});

const CATALOGS: Record<string, unknown> = {
  common: { key: 'common', name: 'Общее', orphan: false, pages: [page('c1.md', 'Правила команды', 'human')] },
  personal: { key: 'personal', name: 'Личное', orphan: false, pages: [page('p1.md', 'Мои цели', 'together')] },
  [PROJECT_KEY]: {
    key: PROJECT_KEY, name: 'AI Home', orphan: false,
    pages: [
      page('a1.md', 'Апскейл картинок', 'ai', 'как увеличить'),
      page('a2.md', 'Без метки'),
      page('a3.md', 'Ревью фронта', 'together'),
    ],
  },
};

const note = (id: string, title: string, source: string): NoteSummary => ({
  id, title, source, sourceLabel: source, path: `${title}.md`, tags: [],
  createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
});

const NOTES = [note('n1', 'Идея про граф', 'personal'), note('n2', 'Заметка проекта', PROJECT_ID), note('n3', 'Чужая', 'unknown-src')];

const sectionFromUrl = (url: string) => decodeURIComponent(url.split('section=')[1] ?? '');

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
function clickChip(c: C, label: string) {
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
  act(() => { (reactProps(hit!) as { onClick: () => void }).onClick(); });
}

const rowTitles = (c: C) => allByAttr(c, 'data-testid', 'unified-row').map(r => String(reactProps(r!)?.title));

beforeEach(() => {
  wikiStore.reset();
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
    const rows = allByAttr(container, 'data-testid', 'unified-row');
    const click = (title: string) => {
      const r = rows.find(x => reactProps(x!)?.title === title)!;
      act(() => { (reactProps(r) as { onClick: () => void }).onClick(); });
    };
    click('Идея про граф');
    expect(props.onSelectNote).toHaveBeenCalledWith('n1');
    click('Апскейл картинок');
    expect(props.onOpenPage).toHaveBeenCalledWith(PROJECT_KEY, 'a1.md');
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
      releaseOld({ key: 'personal', name: 'Личное', orphan: false, pages: [page('old.md', 'Устаревшая страница')] });
      await Promise.resolve();
    });
    await settle();
    expect(rowTitles(m.container)).not.toContain('Устаревшая страница');
    expect(rowTitles(m.container)).toContain('Мои цели');
    expect(rowTitles(m.container)).toContain('Правила команды');
  });
});
