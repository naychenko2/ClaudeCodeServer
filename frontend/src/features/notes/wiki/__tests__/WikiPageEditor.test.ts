// Тесты редактора страницы вики (F4a): разбор и сборка текста, путь «Править → Сохранить»
// через настоящий стор (мокан только транспорт), отказы 400/409 с сохранением правки,
// двойной клик, гонка «ответ после смены страницы».
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) снять проверку ключа страницы (idRef.current !== startId) → «ответ после смены страницы» краснеет;
//  (2) снять замок busyRef → «двойной клик» краснеет;
//  (3) buildContent не выкидывает убранную строку → «Убрать не пишет в запрос…» краснеет;
//  (4) при 409 wiki_page_changed закрывать редактор как успех → «409 wiki_page_changed» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiPageView } from '../WikiPageView';
import { WikiPageEditor, buildContent, scanEditable } from '../WikiPageEditor';
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

const TEXT = `---
type: entity
status: active
summary: Ревьюер
---

# Глеб

## Сводка
- Ведёт ревью. [^s1]
- Второе утверждение. [^s2]

## История
- Старое утверждение.

[^s1]: [[Источники/Чат]] — «цитата»
[^s2]: [[Источники/Чат2]] — «цитата два»
`;

const FOOTNOTES = [
  { label: 's1', card: 'Источники/Чат', origin: 'session' },
  { label: 's2', card: 'Источники/Чат2', origin: 'human', author: 'Андрей', captured: '2026-11-02T10:00:00Z' },
];

const dtoOf = (over: Partial<WikiPageEditableDto> = {}): WikiPageEditableDto => ({
  pageKey: KEY_A, content: TEXT, hash: 'h1', footnotes: FOOTNOTES, ...over,
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
  const p = reactProps(node) as { onClick?: (e: unknown) => void; disabled?: boolean } | null;
  if (!p?.onClick) throw new Error(`у «${what}» нет onClick`);
  if (p.disabled) return; // отключённая кнопка клик не принимает
  act(() => { p.onClick!(undefined); });
}

function typeInto(c: Cont, itemId: number, value: string) {
  const row = byId(c, `wiki-edit-item-${itemId}`);
  const ta = row && allNodes(row).find(n => n.nodeName === 'TEXTAREA');
  if (!ta) throw new Error(`нет поля у строки ${itemId}`);
  const p = reactProps(ta) as { onChange: (e: unknown) => void };
  act(() => { p.onChange({ target: { value } }); });
}

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

// Транспорт: страница отдаётся GET-ом, POST правки — функцией теста
type Post = (body: Record<string, unknown>) => Promise<unknown>;
const posts: Record<string, unknown>[] = [];
async function seed(dto: WikiPageEditableDto, post: Post, pageKey = KEY_A) {
  req.mockImplementation((url: string, init?: { method?: string; body?: string }) => {
    if (url.startsWith('/wiki/pages/edit')) {
      const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
      posts.push(body);
      return post(body);
    }
    if (url.startsWith('/wiki/page')) return Promise.resolve(dto);
    return Promise.resolve(null);
  });
  await act(async () => { await loadWikiPage(SECTION.key, pageKey); });
}

function render(pageKey = KEY_A): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiPageView, { section: SECTION, pageKey, onBack: () => {} }));
  roots.push(root);
  return container;
}

const refusal = (status: number, body: unknown) => Promise.reject(Object.assign(new Error('отказ'), { status, body }));
function unifiedOn() { flags.on.add('llm-wiki'); }

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

describe('Разбор и сборка текста правки', () => {
  it('утверждения секций — строки правки, «История» не правится, сноски срезаются из поля', () => {
    const { items } = scanEditable(TEXT);
    expect(items.map(i => [i.section, i.text, i.editable, i.refs])).toEqual([
      ['Сводка', 'Ведёт ревью.', true, ['s1']],
      ['Сводка', 'Второе утверждение.', true, ['s2']],
      ['История', 'Старое утверждение.', false, []],
    ]);
  });

  it('без правок текст уезжает как был, изменённая строка — без старой сноски, убранная выпадает', () => {
    expect(buildContent(TEXT, new Map())).toBe(TEXT);
    const edited = buildContent(TEXT, new Map([[0, { text: 'Ведёт ревью фронта.' }], [1, { gone: true }]]));
    expect(edited).toContain('- Ведёт ревью фронта.\n');
    expect(edited).not.toContain('Второе утверждение');
    expect(edited).toContain('- Старое утверждение.');
    expect(edited).toContain('[^s2]: [[Источники/Чат2]]');
    // «История» убрать нельзя, даже если попросили
    expect(buildContent(TEXT, new Map([[2, { gone: true }]]))).toBe(TEXT);
  });
});

describe('Добавление утверждения (F4c)', () => {
  const addBtn = (c: Cont, name = 'Сводка') => byId(c, `wiki-edit-add-${name}`);
  const addedArea = (c: Cont, key: number) => {
    const row = byId(c, `wiki-edit-added-${key}`);
    const ta = row && allNodes(row).find(n => n.nodeName === 'TEXTAREA');
    if (!ta) throw new Error(`нет поля добавленного ${key}`);
    return ta;
  };
  const typeAdded = (c: Cont, key: number, value: string) => {
    const p = reactProps(addedArea(c, key)) as { onChange: (e: unknown) => void };
    act(() => { p.onChange({ target: { value } }); });
  };

  it('buildContent: новое утверждение — в конец секции до «Истории», пустое не пишется', () => {
    const out = buildContent(TEXT, new Map(), undefined, [
      { key: 0, section: 'Сводка', text: ' Новое. ' },
      { key: 1, section: 'Сводка', text: '   ' },
    ]);
    expect(out).toContain('- Второе утверждение. [^s2]\n- Новое.\n');
    expect(out.indexOf('- Новое.')).toBeLessThan(out.indexOf('## История'));
    expect(out.match(/- Новое\./g)).toHaveLength(1);
  });

  it('кнопка «Добавить утверждение» есть в каждой правимой секции, в «Истории» нет ни её, ни полей', async () => {
    unifiedOn();
    await seed(dtoOf(), async () => ({}));
    const c = render();
    await settle();
    await openEditor(c);
    expect(addBtn(c)).not.toBeNull();
    expect(texts(addBtn(c)!)).toContain('Добавить утверждение');
    expect(addBtn(c, 'История')).toBeNull();
    const hist = allNodes(c).find(n => n.getAttribute('data-section-name') === 'История')!;
    expect(hist).toBeTruthy();
    expect(allNodes(hist).filter(n => n.nodeName === 'TEXTAREA')).toHaveLength(0);
    expect(texts(hist)).not.toContain('Добавить');
    expect(allNodes(hist).filter(n => n.getAttribute('aria-label') === 'Убрать утверждение')).toHaveLength(0);
  });

  it('пустое поле блокирует «Сохранить» с подсказкой; поле можно убрать до сохранения', async () => {
    unifiedOn();
    await seed(dtoOf(), async () => ({}));
    const c = render();
    await settle();
    await openEditor(c);
    click(addBtn(c), 'Добавить');
    expect((reactProps(byId(c, 'wiki-edit-save')!) as { disabled?: boolean }).disabled).toBe(true);
    expect(texts(c)).toContain('Пустое утверждение не сохранить');
    click(allNodes(byId(c, 'wiki-edit-added-0')!).find(n => n.getAttribute('aria-label') === 'Убрать новое утверждение') ?? null, 'Убрать новое');
    expect(byId(c, 'wiki-edit-added-0')).toBeNull();
    expect(texts(c)).not.toContain('Пустое утверждение не сохранить');
  });

  it('добавил → «Сохранить»: новая строка секции в содержимом запроса', async () => {
    unifiedOn();
    await seed(dtoOf(), async () => ({ status: 'applied', hash: 'h2', content: TEXT }));
    const c = render();
    await settle();
    await openEditor(c);
    click(addBtn(c), 'Добавить');
    typeAdded(c, 0, 'Дописанное человеком.');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(posts).toHaveLength(1);
    expect(String(posts[0].content)).toContain('- Второе утверждение. [^s2]\n- Дописанное человеком.\n');
  });

  it('добавил → 409 → «Оставить моё»: новое утверждение уезжает в payload поверх свежей версии', async () => {
    unifiedOn();
    const fresh = TEXT.replace('Ведёт ревью.', 'ИИ поправил.');
    let n = 0;
    await seed(dtoOf(), async () => {
      if (n++ === 0) return refusal(409, { code: 'wiki_page_changed', message: 'c', page: { pageKey: KEY_A, content: fresh, hash: 'h9' } });
      return { status: 'applied', hash: 'h10', content: fresh };
    });
    const c = render();
    await settle();
    await openEditor(c);
    click(addBtn(c), 'Добавить');
    typeAdded(c, 0, 'Моё новое.');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(byId(c, 'wiki-edit-conflict')).not.toBeNull();
    click(byId(c, 'wiki-conflict-take-mine'), 'Оставить моё');
    await settle();
    expect(posts).toHaveLength(2);
    expect(posts[1].baseHash).toBe('h9');
    expect(String(posts[1].content)).toContain('ИИ поправил.');
    expect(String(posts[1].content)).toContain('- Моё новое.\n');
  });

  const count = (s: string, sub: string) => s.split(sub).length - 1;

  it('дубль: добавил «X» → 409, свежая версия уже содержит «X» → «Оставить моё» → в payload одна «X»', async () => {
    unifiedOn();
    const fresh = TEXT.replace('- Второе утверждение. [^s2]', '- Второе утверждение. [^s2]\n- Общая формулировка.');
    let n = 0;
    await seed(dtoOf(), async () => {
      if (n++ === 0) return refusal(409, { code: 'wiki_page_changed', message: 'c', page: { pageKey: KEY_A, content: fresh, hash: 'h9' } });
      return { status: 'applied', hash: 'h10', content: fresh };
    });
    const c = render();
    await settle();
    await openEditor(c);
    click(addBtn(c), 'Добавить');
    typeAdded(c, 0, 'Общая формулировка.');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    click(byId(c, 'wiki-conflict-take-mine'), 'Оставить моё');
    await settle();
    expect(posts).toHaveLength(2);
    expect(count(String(posts[1].content), 'Общая формулировка.')).toBe(1);
  });

  it('дубль без конфликта: база уже содержит «X» → «Сохранить» → в payload одна «X»', async () => {
    unifiedOn();
    await seed(dtoOf(), async () => ({ status: 'applied', hash: 'h2', content: TEXT }));
    const c = render();
    await settle();
    await openEditor(c);
    click(addBtn(c), 'Добавить');
    typeAdded(c, 0, 'Второе утверждение.');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(posts).toHaveLength(1);
    expect(count(String(posts[0].content), 'Второе утверждение.')).toBe(1);
  });

  it('buildContent: отличие только маркером, сноской, пробелами — тот же текст; регистр и другой текст — разные', () => {
    const out = buildContent(TEXT, new Map(), undefined, [
      { key: 0, section: 'Сводка', text: '*  Второе   утверждение. [^s7]' },
      { key: 1, section: 'Сводка', text: 'второе утверждение.' },
      { key: 2, section: 'Сводка', text: 'Совсем другое.' },
      { key: 3, section: 'Сводка', text: 'Совсем другое.' },
    ]);
    expect(count(out, 'Второе утверждение.')).toBe(1);
    expect(out).toContain('- второе утверждение.');
    expect(count(out, 'Совсем другое.')).toBe(1);
  });
});

describe('Редактор страницы (F4a)', () => {
  it('без флага кнопки «Править» нет; без хэша в ответе — тоже', async () => {
    await seed(dtoOf(), async () => ({}));
    const c = render();
    await settle();
    expect(byId(c, 'wiki-edit-open')).toBeNull();
    expect(texts(c)).toContain('Только чтение');
  });

  it('с флагами — кнопка есть; нет hash в ответе — нет кнопки', async () => {
    unifiedOn();
    await seed(dtoOf({ hash: undefined }), async () => ({}));
    const c = render();
    await settle();
    expect(byId(c, 'wiki-edit-open')).toBeNull();
    wikiStore.reset();
    await seed(dtoOf(), async () => ({}));
    await settle();
    expect(byId(c, 'wiki-edit-open')).not.toBeNull();
  });

  it('«Править» открывает редактор на той же странице; «Отмена» возвращает чтение без запросов', async () => {
    unifiedOn();
    await seed(dtoOf(), async () => ({}));
    const c = render();
    await settle();
    await openEditor(c);
    expect(byId(c, 'wiki-editor')).not.toBeNull();
    expect(byId(c, 'wiki-page')).not.toBeNull();
    click(byId(c, 'wiki-edit-cancel'), 'Отмена');
    expect(byId(c, 'wiki-editor')).toBeNull();
    expect(posts).toHaveLength(0);
  });

  it('«Убрать» спрашивает про «Историю» и не пишет строку в запрос; 200 → выход, «Сохранено», страница перечитана', async () => {
    unifiedOn();
    await seed(dtoOf(), async () => ({ status: 'applied', hash: 'h2', content: TEXT }));
    const c = render();
    await settle();
    await openEditor(c);
    click(allNodes(byId(c, 'wiki-edit-item-1')!).find(n => n.getAttribute('aria-label') === 'Убрать утверждение') ?? null, 'Убрать');
    expect(texts(byId(c, 'wiki-edit-remove-ask-1')!)).toContain('уйдёт в «Историю»');
    click(byId(c, 'wiki-edit-remove-ok-1'), 'Убрать ok');
    expect(byId(c, 'wiki-edit-item-1')!.getAttribute('data-gone')).toBe('true');
    const getsBefore = req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/page')).length;
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(posts).toHaveLength(1);
    expect(posts[0].baseHash).toBe('h1');
    expect(posts[0].section).toBe(SECTION.key);
    expect(posts[0].page).toBe(KEY_A);
    expect(String(posts[0].content)).not.toContain('Второе утверждение');
    expect(String(posts[0].content)).toContain('Ведёт ревью. [^s1]');
    expect(byId(c, 'wiki-editor')).toBeNull();
    expect(texts(byId(c, 'wiki-edit-flash')!)).toContain('Сохранено');
    expect(req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/page')).length).toBeGreaterThan(getsBefore);
  });

  it('правка утверждения ИИ предупреждает об «Истории», правка утверждения человека — нет', async () => {
    unifiedOn();
    await seed(dtoOf(), async () => ({}));
    const c = render();
    await settle();
    await openEditor(c);
    expect(byId(c, 'wiki-edit-ai-warn-0')).toBeNull();
    typeInto(c, 0, 'Ведёт ревью фронта.');
    expect(texts(byId(c, 'wiki-edit-ai-warn-0')!)).toContain('уйдёт в «Историю»');
    typeInto(c, 1, 'Второе утверждение, правка.');
    expect(byId(c, 'wiki-edit-ai-warn-1')).toBeNull();
  });

  it('400 wiki_edit_invalid: русский текст по кодам issues, правка остаётся', async () => {
    unifiedOn();
    await seed(dtoOf(), () => refusal(400, {
      code: 'wiki_edit_invalid', message: 'x', violations: ['«Глеб.md»: ссылка'],
      issues: [{ code: 'wiki_link_sideways', page: KEY_A, link: 'X', message: '«Глеб.md»: ссылка [[X]] ведёт вбок' }],
    }));
    const c = render();
    await settle();
    await openEditor(c);
    typeInto(c, 0, 'Новый текст [[X]]');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    const t = texts(byId(c, 'wiki-edit-error')!);
    expect(t).toContain('Ссылка ведёт в соседний раздел');
    expect(t).toContain('ссылка [[X]] ведёт вбок');
    expect(t).not.toContain('«Глеб.md»');
    expect(byId(c, 'wiki-editor')).not.toBeNull();
    const row = byId(c, 'wiki-edit-item-0')!;
    expect((reactProps(allNodes(row).find(n => n.nodeName === 'TEXTAREA')!) as { value: string }).value).toBe('Новый текст [[X]]');
  });

  it('400 без issues: показываем violations без префикса пути', async () => {
    unifiedOn();
    await seed(dtoOf(), () => refusal(400, { code: 'wiki_edit_invalid', message: 'm', violations: ['«Глеб.md»: заголовок страницы не правится'] }));
    const c = render();
    await settle();
    await openEditor(c);
    typeInto(c, 0, 'Другое');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(texts(byId(c, 'wiki-edit-error')!)).toContain('заголовок страницы не правится');
    expect(texts(byId(c, 'wiki-edit-error')!)).not.toContain('«Глеб.md»');
  });

  it('сетевой сбой: сообщение, правка остаётся, rejection наружу не выходит', async () => {
    unifiedOn();
    await seed(dtoOf(), () => Promise.reject(new Error('offline')));
    const c = render();
    await settle();
    await openEditor(c);
    typeInto(c, 0, 'Другое');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(texts(byId(c, 'wiki-edit-error')!)).toContain('Не удалось сохранить');
    expect(byId(c, 'wiki-editor')).not.toBeNull();
  });

  it('409 wiki_unified_off: режим правки выключен, кнопка пропадает', async () => {
    unifiedOn();
    await seed(dtoOf(), () => refusal(409, { code: 'wiki_unified_off', message: 'off' }));
    const c = render();
    await settle();
    await openEditor(c);
    typeInto(c, 0, 'Другое');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(byId(c, 'wiki-editor')).toBeNull();
    expect(byId(c, 'wiki-edit-open')).toBeNull();
    expect(texts(byId(c, 'wiki-edit-off')!)).toContain('Правка страниц выключена');
  });

  it('409 wiki_page_changed: не перезаписываем, плашка конфликта, повторного запроса нет', async () => {
    unifiedOn();
    await seed(dtoOf(), () => refusal(409, {
      code: 'wiki_page_changed', message: 'changed',
      page: { pageKey: KEY_A, content: TEXT.replace('Ведёт ревью.', 'ИИ поправил.'), hash: 'h9' },
    }));
    const c = render();
    await settle();
    await openEditor(c);
    typeInto(c, 0, 'Моя правка');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(texts(byId(c, 'wiki-edit-conflict')!)).toContain('Страница изменилась, пока вы правили');
    expect(byId(c, 'wiki-editor')).not.toBeNull();
    const save = reactProps(byId(c, 'wiki-edit-save')!) as { disabled?: boolean };
    expect(save.disabled).toBe(true);
    expect(posts).toHaveLength(1);
    const row = byId(c, 'wiki-edit-item-0')!;
    expect((reactProps(allNodes(row).find(n => n.nodeName === 'TEXTAREA')!) as { value: string }).value).toBe('Моя правка');
  });

  it('двойной клик «Сохранить» — один запрос', async () => {
    unifiedOn();
    let resolve!: (v: unknown) => void;
    await seed(dtoOf(), () => new Promise(r => { resolve = r; }));
    const c = render();
    await settle();
    await openEditor(c);
    typeInto(c, 0, 'Другое');
    const save = byId(c, 'wiki-edit-save');
    const p = reactProps(save!) as { onClick: (e: unknown) => void };
    act(() => { p.onClick(undefined); p.onClick(undefined); });
    expect(posts).toHaveLength(1);
    await act(async () => { resolve({ status: 'unchanged', hash: 'h1' }); });
    await settle();
    expect(posts).toHaveLength(1);
  });
});

describe('Редактор: гонка со сменой страницы', () => {
  it('ответ после смены страницы отбрасывается по ключу: ни onDone, ни состояния', async () => {
    let resolve!: (v: unknown) => void;
    // POST правки висит до resolve; перечитка страницы после applied отвечает сразу
    req.mockImplementation((url: string) => (
      url.startsWith('/wiki/pages/edit') ? new Promise(r => { resolve = r; }) : Promise.resolve(dtoOf())
    ));
    installFakeDom();
    const onDone = vi.fn();
    const props = (pageKey: string) => ({
      section: SECTION, pageKey, content: TEXT, hash: 'h1', footnotes: FOOTNOTES, onDone,
    });
    const { root, container } = mount(createElement(WikiPageEditor, props(KEY_A)));
    roots.push(root);
    typeInto(container, 0, 'Другое');
    click(byId(container, 'wiki-edit-save'), 'Сохранить');
    // человек ушёл на другую страницу — тот же экземпляр редактора получил другой ключ
    act(() => { root.render(createElement(WikiPageEditor, props(KEY_B))); });
    await act(async () => { resolve({ status: 'applied', hash: 'h2', content: TEXT }); });
    await settle();
    expect(onDone).not.toHaveBeenCalled();
  });

  it('отказ после смены страницы тоже отбрасывается и не просачивается наружу', async () => {
    let reject!: (e: unknown) => void;
    req.mockImplementation(() => new Promise((_, rj) => { reject = rj; }));
    installFakeDom();
    const onDone = vi.fn();
    const props = (pageKey: string) => ({ section: SECTION, pageKey, content: TEXT, hash: 'h1', onDone });
    const { root, container } = mount(createElement(WikiPageEditor, props(KEY_A)));
    roots.push(root);
    typeInto(container, 0, 'Другое');
    click(byId(container, 'wiki-edit-save'), 'Сохранить');
    act(() => { root.render(createElement(WikiPageEditor, props(KEY_B))); });
    await act(async () => { reject(Object.assign(new Error('x'), { status: 409, body: { code: 'wiki_unified_off', message: 'off' } })); });
    await settle();
    expect(onDone).not.toHaveBeenCalled();
    expect(byId(container, 'wiki-edit-error')).toBeNull();
  });
});
