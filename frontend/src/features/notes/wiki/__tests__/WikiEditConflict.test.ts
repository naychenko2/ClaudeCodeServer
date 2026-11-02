// Тесты сравнения «Моё / ИИ / Было» при 409 (F4b): разбор секций, три действия, гонки.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) снять проверку ключа страницы в submit (idRef.current !== startId) → «ответ после смены страницы» краснеет;
//  (2) «Взять мою» шлёт base.hash вместо conflict.theirs.hash → «Взять мою версию поверх» краснеет;
//  (3) снять замок busyRef → «двойной клик по «Взять мою»» краснеет;
//  (4) при повторном 409 не обновлять conflict → «ещё пакет ИИ» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiPageEditor, rebaseEdits } from '../WikiPageEditor';
import { compareSections } from '../WikiEditConflict';
import { __test as wikiStore } from '../../../../lib/wiki';

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

const SECTION = { key: 'projects/p1', name: 'AI Home', orphan: false, local: false, sphereReserved: false, pageCount: 5 };
const KEY_A = 'Проекты/p1/Глеб.md';
const KEY_B = 'Проекты/p1/Тимур.md';

const TEXT = `---
type: entity
---

# Глеб

## Сводка
- Ведёт ревью.
- Второе утверждение.

## Контакты
- Почта.
`;
const AI1 = TEXT.replace('Ведёт ревью.', 'ИИ: ведёт ревью кода.').replace('- Почта.', '- Почта и чат.');
const AI2 = AI1.replace('Второе утверждение.', 'ИИ добавил ещё.');

type Cont = ReturnType<typeof mount>['container'];
const roots: Root[] = [];
const texts = (c: Cont) => textNodes(c).map(t => t.trim()).filter(Boolean).join(' ');
const byId = (c: Cont, id: string) => findNodeByAttr(c, 'data-testid', id);
function allNodes(root: Cont, out: Cont[] = []): Cont[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}
function click(node: Cont | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const p = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!p?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { p.onClick!(undefined); });
}
function typeInto(c: Cont, itemId: number, value: string) {
  const row = byId(c, `wiki-edit-item-${itemId}`);
  const ta = row && allNodes(row).find(n => n.nodeName === 'TEXTAREA');
  if (!ta) throw new Error(`нет поля у строки ${itemId}`);
  act(() => { (reactProps(ta) as { onChange: (e: unknown) => void }).onChange({ target: { value } }); });
}
async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}
const conflictBody = (content: string, hash: string, pageKey = KEY_A) => ({
  code: 'wiki_page_changed', message: 'changed', page: { pageKey, content, hash },
});
const refusal = (status: number, body: unknown) => Promise.reject(Object.assign(new Error('отказ'), { status, body }));

const posts: Record<string, unknown>[] = [];
function transport(post: (body: Record<string, unknown>, n: number) => Promise<unknown>) {
  req.mockImplementation((url: string, init?: { body?: string }) => {
    if (url.startsWith('/wiki/pages/edit')) {
      const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
      posts.push(body);
      return post(body, posts.length);
    }
    return Promise.resolve(null);
  });
}

const onDone = vi.fn();
function mountEditor(pageKey = KEY_A): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiPageEditor, { section: SECTION, pageKey, content: TEXT, hash: 'h1', footnotes: [], onDone }));
  roots.push(root);
  return container;
}
// Правка и первый сейв, который упирается в конфликт
async function intoConflict(c: Cont, value = 'Моя правка') {
  typeInto(c, 0, value);
  click(byId(c, 'wiki-edit-save'), 'Сохранить');
  await settle();
  expect(byId(c, 'wiki-edit-conflict')).not.toBeNull();
}
const taValue = (c: Cont, id: number) =>
  (reactProps(allNodes(byId(c, `wiki-edit-item-${id}`)!).find(n => n.nodeName === 'TEXTAREA')!) as { value: string }).value;

beforeEach(() => { posts.length = 0; onDone.mockReset(); wikiStore.reset(); req.mockReset(); });
afterEach(() => {
  for (const r of roots.splice(0)) act(() => r.unmount());
  uninstallFakeDom();
});

describe('compareSections', () => {
  it('делит секции: обе стороны / только я / только ИИ / одинаково; нетронутые пропускает', () => {
    const mine = TEXT.replace('Ведёт ревью.', 'Моя правка.').replace('- Почта.', '- Почта и чат.');
    const r = compareSections(TEXT, mine, AI1);
    expect(r.map(x => [x.name, x.verdict])).toEqual([['Сводка', 'both'], ['Контакты', 'same']]);
    expect(r[0].was).toEqual(['Ведёт ревью.', 'Второе утверждение.']);
    expect(r[0].mine[0]).toBe('Моя правка.');
    expect(r[0].ai[0]).toBe('ИИ: ведёт ревью кода.');
    const only = compareSections(TEXT, TEXT.replace('Второе утверждение.', 'Я.'), TEXT.replace('- Почта.', '- ИИ.'));
    expect(only.map(x => [x.name, x.verdict])).toEqual([['Сводка', 'mine'], ['Контакты', 'ai']]);
  });
});

describe('rebaseEdits', () => {
  it('переносит правку по тексту и по месту в разделе, «убрать» без пары не переносит', () => {
    const edits = new Map([[0, { text: 'Моя' }], [1, { gone: true }]]);
    // строка 0 переписана ИИ → по месту; строка 1 на месте → по тексту
    const r = rebaseEdits(TEXT, AI1, edits);
    expect(r.get(0)).toEqual({ text: 'Моя' });
    expect(r.get(1)).toEqual({ gone: true });
    const lost = rebaseEdits(TEXT, TEXT.replace('Второе утверждение.', 'Другое.'), new Map([[1, { gone: true }]]));
    expect(lost.size).toBe(0);
  });
});

describe('Сравнение «Моё / ИИ / Было»', () => {
  it('409: колонки раздела, три действия, «Было» — версия открытия', async () => {
    transport(() => refusal(409, conflictBody(AI1, 'h9')));
    const c = mountEditor();
    await intoConflict(c);
    const t = texts(byId(c, 'wiki-edit-conflict')!);
    expect(t).toContain('Страница изменилась, пока вы правили');
    for (const s of ['Моё', 'ИИ', 'Было, когда вы начали', 'Оставить моё', 'Взять их', 'Слить вручную']) expect(t).toContain(s);
    expect(texts(byId(c, 'wiki-conflict-col-was')!)).toContain('Ведёт ревью.');
    expect(texts(byId(c, 'wiki-conflict-col-mine')!)).toContain('Моя правка');
    expect(texts(byId(c, 'wiki-conflict-col-ai')!)).toContain('ИИ: ведёт ревью кода.');
    expect(posts).toHaveLength(1);
  });

  it('«Взять мою версию поверх»: новый запрос с baseHash из theirs.hash и текстом «Моё»', async () => {
    transport((_b, n) => (n === 1 ? refusal(409, conflictBody(AI1, 'h9')) : Promise.resolve({ status: 'applied', hash: 'h10', content: 'x' })));
    const c = mountEditor();
    await intoConflict(c);
    click(byId(c, 'wiki-conflict-take-mine'), 'Взять мою');
    await settle();
    expect(posts).toHaveLength(2);
    expect(posts[1].baseHash).toBe('h9');
    expect(String(posts[1].content)).toContain('Моя правка');
    // правки ИИ в разделе, где человек не правил, не откатываются снимком на старой базе
    expect(String(posts[1].content)).toContain('Почта и чат.');
    expect(onDone).toHaveBeenCalledWith({ kind: 'saved' });
  });

  it('правка во время сравнения: поля заблокированы, «Моё» и payload берут актуальные правки', async () => {
    transport((_b, n) => (n === 1 ? refusal(409, conflictBody(AI1, 'h9')) : Promise.resolve({ status: 'applied', hash: 'h10', content: 'x' })));
    const c = mountEditor();
    await intoConflict(c);
    const ta = allNodes(byId(c, 'wiki-edit-item-0')!).find(n => n.nodeName === 'TEXTAREA')!;
    expect((reactProps(ta) as { disabled?: boolean }).disabled).toBe(true);
    typeInto(c, 0, 'Правка во время сравнения');
    expect(texts(byId(c, 'wiki-conflict-col-mine')!)).toContain('Правка во время сравнения');
    click(byId(c, 'wiki-conflict-take-mine'), 'Взять мою');
    await settle();
    expect(String(posts[1].content)).toContain('Правка во время сравнения');
    expect(String(posts[1].content)).not.toContain('Моя правка');
  });

  it('пустая колонка «Моё»: «вы убрали это утверждение»', async () => {
    transport(() => refusal(409, conflictBody(TEXT.replace('- Почта.', '- ИИ.'), 'h9')));
    const c = mountEditor();
    const row = byId(c, 'wiki-edit-item-2')!;
    const trash = findNodeByAttr(row, 'aria-label', 'Убрать утверждение');
    click(trash, 'Убрать');
    click(byId(c, 'wiki-edit-remove-ok-2'), 'Убрать ок');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(texts(byId(c, 'wiki-conflict-col-mine')!)).toContain('вы убрали это утверждение');
  });

  it('пустая колонка ИИ: раздела у ИИ нет — «раздела здесь нет»', async () => {
    transport(() => refusal(409, conflictBody(TEXT.replace('\n## Контакты\n- Почта.\n', ''), 'h9')));
    const c = mountEditor();
    typeInto(c, 2, 'Моя почта.');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(texts(byId(c, 'wiki-conflict-col-ai')!)).toContain('раздела здесь нет');
  });

  it('«Взять версию ИИ»: выход из правки без записи', async () => {
    transport(() => refusal(409, conflictBody(AI1, 'h9')));
    const c = mountEditor();
    await intoConflict(c);
    click(byId(c, 'wiki-conflict-take-ai'), 'Взять ИИ');
    await settle();
    expect(onDone).toHaveBeenCalledWith({ kind: 'cancel' });
    expect(posts).toHaveLength(1);
  });

  it('«Продолжить правку»: текст остался, baseHash обновлён, запись — только по «Сохранить»', async () => {
    transport((_b, n) => (n === 1 ? refusal(409, conflictBody(AI1, 'h9')) : Promise.resolve({ status: 'applied', hash: 'h10', content: 'x' })));
    const c = mountEditor();
    await intoConflict(c);
    click(byId(c, 'wiki-conflict-keep'), 'Продолжить');
    await settle();
    expect(byId(c, 'wiki-edit-conflict')).toBeNull();
    expect(posts).toHaveLength(1);
    expect(taValue(c, 0)).toBe('Моя правка');
    click(byId(c, 'wiki-edit-save'), 'Сохранить');
    await settle();
    expect(posts).toHaveLength(2);
    expect(posts[1].baseHash).toBe('h9');
    // чужие правки ИИ в других разделах не затёрты старой базой
    expect(String(posts[1].content)).toContain('Почта и чат.');
  });

  it('ещё пакет ИИ во время сравнения: повторный сейв даёт снова 409 — новое сравнение, перезаписи нет', async () => {
    transport((_b, n) => (n === 1 ? refusal(409, conflictBody(AI1, 'h9')) : refusal(409, conflictBody(AI2, 'h12'))));
    const c = mountEditor();
    await intoConflict(c);
    click(byId(c, 'wiki-conflict-take-mine'), 'Взять мою');
    await settle();
    expect(posts).toHaveLength(2);
    expect(posts[1].baseHash).toBe('h9');
    expect(onDone).not.toHaveBeenCalled();
    expect(texts(byId(c, 'wiki-conflict-col-ai')!)).toContain('ИИ добавил ещё.');
    click(byId(c, 'wiki-conflict-take-mine'), 'Взять мою');
    await settle();
    expect(posts[2].baseHash).toBe('h12');
  });

  it('двойной клик по «Взять мою» — один запрос', async () => {
    let resolve!: (v: unknown) => void;
    transport((_b, n) => (n === 1 ? refusal(409, conflictBody(AI1, 'h9')) : new Promise(r => { resolve = r; })));
    const c = mountEditor();
    await intoConflict(c);
    const p = reactProps(byId(c, 'wiki-conflict-take-mine')!) as { onClick: (e: unknown) => void };
    act(() => { p.onClick(undefined); p.onClick(undefined); });
    expect(posts).toHaveLength(2);
    await act(async () => { resolve({ status: 'applied', hash: 'h10', content: 'x' }); });
    await settle();
    expect(posts).toHaveLength(2);
  });

  it('ответ на «Взять мою» после смены страницы отбрасывается по ключу «раздел+путь»', async () => {
    let resolve!: (v: unknown) => void;
    transport((_b, n) => (n === 1 ? refusal(409, conflictBody(AI1, 'h9')) : new Promise(r => { resolve = r; })));
    const c = mountEditor();
    await intoConflict(c);
    click(byId(c, 'wiki-conflict-take-mine'), 'Взять мою');
    // человек ушёл на другую страницу — тот же экземпляр получил другой ключ
    act(() => {
      roots[0].render(createElement(WikiPageEditor, { section: SECTION, pageKey: KEY_B, content: TEXT, hash: 'h1', footnotes: [], onDone }));
    });
    await act(async () => { resolve({ status: 'applied', hash: 'h10', content: 'x' }); });
    await settle();
    expect(onDone).not.toHaveBeenCalled();
  });
});
