// Тесты секции «Заметки» страницы вики (F5a): помощники над текстом, чтение и пустое состояние,
// правка тем же редактором, отказ wiki_notes_footnote, конкурентность (409, двойной клик, гонка).
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) снять проверку ключа страницы в WikiPageEditor.submit (idRef.current !== startId) →
//      «ответ на правку «Заметок» после смены страницы» краснеет;
//  (2) снять замок busyRef → «двойной клик» краснеет;
//  (3) withNotes не пишет текст в страницу → «правка «Заметок» уезжает» краснеет;
//  (4) без флага рисовать новую секцию → «без флагов прежний вид» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiPageView } from '../WikiPageView';
import { WikiPageEditor } from '../WikiPageEditor';
import { extractNotes, parseNotesBlocks, withNotes } from '../WikiNotesSection';
import { __test as wikiStore, loadWikiPage, type WikiPageEditableDto } from '../../../../lib/wiki';

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
const flags = vi.hoisted(() => ({ on: new Set<string>() }));
vi.mock('../../../../lib/featureFlags', async (orig) => ({
  ...await orig<typeof import('../../../../lib/featureFlags')>(),
  useFeature: (k: string) => flags.on.has(k),
}));

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

const SECTION = { key: 'projects/p1', name: 'AI Home', orphan: false, local: false, sphereReserved: false, pageCount: 5 };
const KEY_A = 'Проекты/p1/Глеб.md';
const KEY_B = 'Проекты/p1/Тимур.md';

const HEAD = `---
type: entity
status: active
summary: Ревьюер
---

# Глеб

## Сводка
- Ведёт ревью. [^s1]

## История
- Старое утверждение.
`;
const DEFS = '\n[^s1]: [[Источники/Чат]] — «цитата»\n';
const NO_NOTES = HEAD + DEFS;
const NOTES_BODY = 'Черновик: проверить очередь.\n\n| Размер | 5 с |\n| --- | --- |\n| 864×480 | 1,5 мин |';
const WITH_NOTES = `${HEAD}\n## Заметки\n\n${NOTES_BODY}\n${DEFS}`;

const FOOTNOTES = [{ label: 's1', card: 'Источники/Чат', origin: 'session' }];
const dtoOf = (content: string, over: Partial<WikiPageEditableDto> = {}): WikiPageEditableDto => ({
  pageKey: KEY_A, content, hash: 'h1', footnotes: FOOTNOTES, ...over,
});

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;
const roots: Root[] = [];

function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}
const texts = (c: Cont) => textNodes(c).map(t => t.trim()).filter(Boolean).join(' ');
const byId = (c: Cont, id: string) => findNodeByAttr(c, 'data-testid', id);

function click(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const p = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!p?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { p.onClick!(undefined); });
}

function typeNotes(c: Cont, value: string) {
  const box = byId(c, 'wiki-edit-notes');
  const ta = box && allNodes(box).find(n => n.nodeName === 'TEXTAREA');
  if (!ta) throw new Error('нет поля «Заметок»');
  const p = reactProps(ta) as { onChange: (e: unknown) => void };
  act(() => { p.onChange({ target: { value } }); });
}

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

type Post = (body: Record<string, unknown>) => Promise<unknown>;
const posts: Record<string, unknown>[] = [];
async function seed(dto: WikiPageEditableDto, post: Post) {
  req.mockImplementation((url: string, init?: { method?: string; body?: string }) => {
    if (url.startsWith('/wiki/pages/edit')) {
      const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
      posts.push(body);
      return post(body);
    }
    if (url.startsWith('/wiki/page')) return Promise.resolve(dto);
    return Promise.resolve(null);
  });
  await act(async () => { await loadWikiPage(SECTION.key, KEY_A); });
}

function render(): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiPageView, { section: SECTION, pageKey: KEY_A, onBack: () => {} }));
  roots.push(root);
  return container;
}

const refusal = (status: number, body: unknown) => Promise.reject(Object.assign(new Error('отказ'), { status, body }));
function unifiedOn() { flags.on.add('notes-wiki-unified'); flags.on.add('llm-wiki'); }

async function openEditor(c: Cont) {
  click(byId(c, 'wiki-edit-open'), 'Править');
  await settle();
}

beforeEach(() => {
  flags.on.clear();
  posts.length = 0;
  wikiStore.reset();
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('Текст «Заметок» в странице', () => {
  it('extractNotes: нет секции — null, есть — текст без определений сносок', () => {
    expect(extractNotes(NO_NOTES)).toBeNull();
    expect(extractNotes(WITH_NOTES)).toBe(NOTES_BODY);
  });

  it('withNotes: правит существующую секцию и не трогает остальное; пустой текст оставляет заголовок', () => {
    const next = withNotes(WITH_NOTES, 'Новое');
    expect(extractNotes(next)).toBe('Новое');
    expect(next).toContain('- Ведёт ревью. [^s1]');
    expect(next).toContain('[^s1]: [[Источники/Чат]]');
    expect(extractNotes(withNotes(WITH_NOTES, '  '))).toBe('');
  });

  it('withNotes: нет секции — дописывает последней перед определениями сносок; пустой текст её не заводит', () => {
    const next = withNotes(NO_NOTES, 'Моя заметка');
    expect(next.indexOf('## История')).toBeLessThan(next.indexOf('## Заметки'));
    expect(next.indexOf('## Заметки')).toBeLessThan(next.indexOf('[^s1]:'));
    expect(extractNotes(next)).toBe('Моя заметка');
    expect(withNotes(NO_NOTES, '')).toBe(NO_NOTES);
  });

  it('parseNotesBlocks: абзац и таблица', () => {
    const b = parseNotesBlocks(NOTES_BODY);
    expect(b.map(x => x.kind)).toEqual(['p', 'table']);
    expect(b[1]).toEqual({ kind: 'table', rows: [['Размер', '5 с'], ['864×480', '1,5 мин']] });
  });
});

describe('Секция «Заметки»: чтение', () => {
  it('без флагов прежний вид: обычная секция, ни новой секции, ни empty-state', async () => {
    await seed(dtoOf(WITH_NOTES), async () => ({}));
    const c = render();
    await settle();
    expect(byId(c, 'wiki-notes')).toBeNull();
    const sec = allNodes(c).filter(n => n.getAttribute?.('data-testid') === 'wiki-section' && n.getAttribute('data-section-name') === 'Заметки');
    expect(sec).toHaveLength(1);
  });

  it('с флагами: последняя секция, подпись дословно, текст и таблица', async () => {
    unifiedOn();
    await seed(dtoOf(WITH_NOTES), async () => ({}));
    const c = render();
    await settle();
    const names = allNodes(c).filter(n => /^wiki-(section|notes)$/.test(n.getAttribute?.('data-testid') ?? '')).map(n => n.getAttribute('data-section-name'));
    expect(names).toEqual(['Сводка', 'История', 'Заметки']);
    const t = texts(byId(c, 'wiki-notes')!);
    expect(t).toContain('свободный текст · ИИ его не меняет, но читает');
    expect(t).toContain('Черновик: проверить очередь.');
    expect(t).toContain('1,5 мин');
    expect(allNodes(byId(c, 'wiki-notes')!).some(n => n.nodeName === 'TABLE')).toBe(true);
  });

  it('пустая секция: приглашение, кнопка открывает редактор; без права править — секции нет', async () => {
    unifiedOn();
    await seed(dtoOf(NO_NOTES), async () => ({}));
    const c = render();
    await settle();
    expect(texts(byId(c, 'wiki-notes-empty')!)).toContain('заметку для себя и ИИ');
    click(byId(c, 'wiki-notes-write'), 'Записать заметку');
    await settle();
    expect(byId(c, 'wiki-editor')).not.toBeNull();
    expect(byId(c, 'wiki-edit-notes')).not.toBeNull();
    // нет hash → править нельзя → приглашать некуда
    wikiStore.reset();
    await seed(dtoOf(NO_NOTES, { hash: undefined }), async () => ({}));
    await settle();
    expect(byId(c, 'wiki-notes')).toBeNull();
  });
});

describe('Секция «Заметки»: правка', () => {
  it('правка «Заметок» уезжает в запросе полным текстом, без «Убрать», утверждения не затронуты', async () => {
    unifiedOn();
    await seed(dtoOf(WITH_NOTES), async () => ({ status: 'applied', hash: 'h2', content: WITH_NOTES }));
    const c = render();
    await settle();
    await openEditor(c);
    // «Заметки» не строка-утверждение: в списке строк редактора её нет
    expect(allNodes(c).filter(n => n.getAttribute?.('data-testid') === 'wiki-edit-section').map(n => n.getAttribute('data-section-name'))).toEqual(['Сводка', 'История']);
    expect(texts(byId(c, 'wiki-edit-notes')!)).toContain('свободный текст, без сносок');
    expect(reactProps(byId(c, 'wiki-edit-save')!) as { disabled?: boolean }).toMatchObject({ disabled: true });
    typeNotes(c, 'Другая заметка');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(posts).toHaveLength(1);
    expect(posts[0].baseHash).toBe('h1');
    const sent = String(posts[0].content);
    expect(extractNotes(sent)).toBe('Другая заметка');
    expect(sent).toContain('- Ведёт ревью. [^s1]');
    expect(byId(c, 'wiki-editor')).toBeNull();
  });

  it('первая заметка у страницы без секции: «Заметки» дописаны последней', async () => {
    unifiedOn();
    await seed(dtoOf(NO_NOTES), async () => ({ status: 'applied', hash: 'h2', content: NO_NOTES }));
    const c = render();
    await settle();
    click(byId(c, 'wiki-notes-write'), 'Записать заметку');
    await settle();
    typeNotes(c, 'Первая');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(String(posts[0].content)).toMatch(/## История\n- Старое утверждение\.\n\n## Заметки\n\nПервая\n/);
  });

  it('wiki_notes_footnote: русский заголовок, правка остаётся в поле', async () => {
    unifiedOn();
    await seed(dtoOf(WITH_NOTES), () => refusal(400, {
      code: 'wiki_edit_invalid', message: 'x', violations: [],
      issues: [{ code: 'wiki_notes_footnote', page: KEY_A, message: '«Глеб.md»: в секции «Заметки» нет сносок: см. [^s9]' }],
    }));
    const c = render();
    await settle();
    await openEditor(c);
    typeNotes(c, 'см. [^s9]');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    const t = texts(byId(c, 'wiki-edit-error')!);
    expect(t).toContain('В секции «Заметки» не бывает сносок');
    expect(t).not.toContain('«Глеб.md»');
    const ta = allNodes(byId(c, 'wiki-edit-notes')!).find(n => n.nodeName === 'TEXTAREA')!;
    expect((reactProps(ta) as { value: string }).value).toBe('см. [^s9]');
  });
});

describe('Секция «Заметки»: конкурентность', () => {
  it('пакет ИИ пришёл раньше: 409 → сравнение, моя заметка в поле, повторной отправки нет', async () => {
    unifiedOn();
    await seed(dtoOf(WITH_NOTES), () => refusal(409, {
      code: 'wiki_page_changed', message: 'changed',
      page: { pageKey: KEY_A, content: WITH_NOTES.replace('Ведёт ревью.', 'ИИ поправил.'), hash: 'h9' },
    }));
    const c = render();
    await settle();
    await openEditor(c);
    typeNotes(c, 'Моя заметка');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(byId(c, 'wiki-edit-conflict')).not.toBeNull();
    expect(posts).toHaveLength(1);
    expect((reactProps(byId(c, 'wiki-edit-save')!) as { disabled?: boolean }).disabled).toBe(true);
    const ta = allNodes(byId(c, 'wiki-edit-notes')!).find(n => n.nodeName === 'TEXTAREA')!;
    expect((reactProps(ta) as { value: string }).value).toBe('Моя заметка');
  });

  it('двойной клик «Сохранить» — один запрос', async () => {
    unifiedOn();
    let resolve!: (v: unknown) => void;
    await seed(dtoOf(WITH_NOTES), () => new Promise(r => { resolve = r; }));
    const c = render();
    await settle();
    await openEditor(c);
    typeNotes(c, 'Двойной');
    const p = reactProps(byId(c, 'wiki-edit-save')!) as { onClick: (e: unknown) => void };
    act(() => { p.onClick(undefined); p.onClick(undefined); });
    expect(posts).toHaveLength(1);
    await act(async () => { resolve({ status: 'unchanged', hash: 'h1' }); });
    await settle();
    expect(posts).toHaveLength(1);
  });

  it('ответ на правку «Заметок» после смены страницы отбрасывается по ключу: ни onDone, ни состояния', async () => {
    let resolve!: (v: unknown) => void;
    req.mockImplementation((url: string) => (
      url.startsWith('/wiki/pages/edit') ? new Promise(r => { resolve = r; }) : Promise.resolve(dtoOf(WITH_NOTES))
    ));
    installFakeDom();
    const onDone = vi.fn();
    const props = (pageKey: string) => ({ section: SECTION, pageKey, content: WITH_NOTES, hash: 'h1', footnotes: FOOTNOTES, onDone });
    const { root, container } = mount(createElement(WikiPageEditor, props(KEY_A)));
    roots.push(root);
    typeNotes(container, 'Уйдёт в никуда');
    click(byId(container, 'wiki-edit-save'), 'Сохранить');
    act(() => { root.render(createElement(WikiPageEditor, props(KEY_B))); });
    await act(async () => { resolve({ status: 'applied', hash: 'h2', content: WITH_NOTES }); });
    await settle();
    expect(onDone).not.toHaveBeenCalled();
  });

  it('отказ после смены страницы не просачивается наружу', async () => {
    let reject!: (e: unknown) => void;
    req.mockImplementation(() => new Promise((_, rj) => { reject = rj; }));
    installFakeDom();
    const onDone = vi.fn();
    const props = (pageKey: string) => ({ section: SECTION, pageKey, content: WITH_NOTES, hash: 'h1', onDone });
    const { root, container } = mount(createElement(WikiPageEditor, props(KEY_A)));
    roots.push(root);
    typeNotes(container, 'Другое');
    click(byId(container, 'wiki-edit-save'), 'Сохранить');
    act(() => { root.render(createElement(WikiPageEditor, props(KEY_B))); });
    await act(async () => { reject(Object.assign(new Error('x'), { status: 409, body: { code: 'wiki_unified_off', message: 'off' } })); });
    await settle();
    expect(onDone).not.toHaveBeenCalled();
    expect(byId(container, 'wiki-edit-error')).toBeNull();
  });
});
