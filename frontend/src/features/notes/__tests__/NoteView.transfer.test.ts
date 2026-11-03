// «Перенести в вики» на экране заметки (F5b, ADR-038 «Контракт для фронта»).
// Кнопка — только при обоих флагах (llm-wiki И notes-wiki-unified): без любого — ни кнопки,
// ни запросов. Клик — подтверждение текстом записки notes-wiki-unified-v1 (§«е. Заметка»,
// дословно, кроме «✎ вы» вместо имени макета); до подтверждения — ни одного запроса.
// Подтверждение → POST /api/wiki/ingest/note {noteId, transfer:true}; 200 → Notice «Перенесено»
// + запрос открытия страницы в стор вики (section/page из ответа, OPEN-1) + перечитка заметок;
// отказ → Notice русским текстом (по коду; у wiki_refused — причина из message бэка), кнопка
// на месте, страницы никто не открывает.
// Конкурентность: двойной клик по «Перенести» — один запрос (busy-гард); ответ после смены
// заметки отбрасывается по noteId — ни плашки, ни открытой страницы, и чужой ответ не гасит
// состояние новой заметки.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) снять проверку noteId в успехе doTransfer (убрать `if (forNoteId !== openNoteIdRef.current)
//     return;`) → «ответ после смены заметки отбрасывается» краснеет;
// (2) снять busy-гард doTransfer → «двойной клик — один запрос» краснеет;
// (3) рендерить кнопку при одном llm-wiki (убрать unifiedOn из условия) → «без флага
//     notes-wiki-unified кнопки нет» краснеет;
// (4) не звать requestOpenWikiPage после 200 → «200 … и открытие страницы» краснеет;
// (5) показать сырой код вместо текста → «отказ — русским текстом» краснеет.
//
// Стор вики и флаги — настоящие; мокан только транспорт (offline.request), чтение заметки
// (notesOffline.getNoteForView) и leaf-вызовы api — тот же приём, что в NotesPage.test и
// WikiChatActions.test. ConfirmDialog — портал в document.body.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NoteView } from '../NoteView';
import type { NoteDetail } from '../../../types';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../components/chat/__tests__/fakeDom';
import { setAllFlags } from '../../../lib/featureFlags';
// стор вики — настоящий (тот же экземпляр, что у компонента через кит): запрос открытия
// страницы проверяется в самом сторе (тот же шов, что в WikiChatActions.test)
import { __test as wikiStore, consumeOpenWikiPage } from '../../../lib/wiki';

vi.mock('../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
  joinProject: vi.fn(() => Promise.resolve()),
  onLocalModelMessage: vi.fn(() => () => {}),
}));
vi.mock('../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../lib/offline')>(),
  // по делу в этих тестах работает только request: транспорт переноса
  request: vi.fn(),
  readStoredUserId: () => 'u1',
}));
// список заметок не нужен экрану (перенос не трогает его содержимое), но NoteView зовёт
// useNotes для диалога переноса — пустой снимок; bumpNotes/useNotesVersion остаются настоящими
const { notesEmpty } = vi.hoisted(() => ({ notesEmpty: [] as never[] }));
vi.mock('../../../lib/notes', async (orig) => ({
  ...await orig<typeof import('../../../lib/notes')>(),
  useNotes: () => notesEmpty,
  ensureNotesLoaded: async () => {},
}));
// NoteView читает заметку через офлайн-слой — фиксированной деталью (тот же приём, что в
// NotesPage.test); смена детали между тестами — через noteMock
const { noteMock } = vi.hoisted(() => ({ noteMock: { detail: null as unknown } }));
vi.mock('../../../lib/notesOffline', async (orig) => ({
  ...await orig<typeof import('../../../lib/notesOffline')>(),
  getNoteForView: async () => noteMock.detail,
}));
// DocComments при монтировании зовёт ensurePersonasLoaded: в node стор уходит в offline — no-op
vi.mock('../../../lib/personas', async (orig) => ({
  ...await orig<typeof import('../../../lib/personas')>(),
  ensurePersonasLoaded: () => Promise.resolve(),
}));
// NoteTasksSection зовёт api.notes.tasks, useAllProjects — api.projects.list: вне дела
// тестов, пустые ответы чтобы effects не шумели
vi.mock('../../../lib/api', async (orig) => {
  const o = await orig<typeof import('../../../lib/api')>();
  return {
    ...o,
    api: {
      ...o.api,
      projects: { ...o.api.projects, list: async () => [] },
      notes: { ...o.api.notes, tasks: async () => [], annotations: async () => [] },
    },
  };
});
vi.mock('../../../hooks/useOnline', async (orig) => ({
  ...await orig<typeof import('../../../hooks/useOnline')>(),
  useOnline: () => true,
}));

import { request } from '../../../lib/offline';
type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const NOTE_A: NoteDetail = {
  id: 'n-aaaaaaaa-0000-0000-0000-00000000000a', title: 'Заметка А', source: 'personal',
  sourceLabel: 'Личный', path: 'Ideas/Заметка А.md', content: 'Текст заметки А.',
  tags: [], links: [], backlinks: [], unlinkedMentions: [],
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
};
const NOTE_B: NoteDetail = {
  ...NOTE_A, id: 'n-bbbbbbbb-0000-0000-0000-00000000000b', title: 'Заметка Б',
  path: 'Ideas/Заметка Б.md', content: 'Текст заметки Б.',
};
// форма ответа 200 — как у бэка (WikiController.TransferNote): outcome + ключи созданной
// страницы (section — ключ раздела, page — путь под pages/)
const TRANSFER_OK = {
  outcome: 'transferred', section: 'personal', page: 'Личное/Заметка А.md', packageId: 'pkg-1',
};

const roots: Root[] = [];

function mountNote(noteId: string): { root: Root; container: Cont } {
  const { root, container } = mount(createElement(NoteView, {
    noteId,
    existingTitles: new Set<string>(),
    onWikilink: () => {},
    onSelectNote: () => {},
    onDeleted: () => {},
    isMobile: true,
  }));
  roots.push(root);
  return { root, container };
}

function renderNote(noteId: string): { root: Root; container: Cont } {
  noteMock.detail = noteId === NOTE_A.id ? NOTE_A : NOTE_B;
  return mountNote(noteId);
}

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(container: AnyNode): string {
  return textNodes(container).map(t => t.trim()).filter(Boolean).join(' ');
}

function docBody(): AnyNode {
  return (globalThis as { document: { body: AnyNode } }).document.body;
}

function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}

function buttonByExactText(root: AnyNode, text: string): AnyNode | null {
  return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === text) ?? null;
}

function click(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick!(undefined); });
}

// транспорт переноса: POST /wiki/ingest/note — отложенным промисом, чтобы тест держал
// запрос открытым (гонка); прочие пути — пустое тело
function deferTransfer() {
  const calls: { body: unknown }[] = [];
  // держател, а не локальная переменная: объект returned иначе захватил бы undefined
  const ctl: { resolve?: (v: unknown) => void; reject?: (e: unknown) => void } = {};
  req.mockImplementation((url: string, init?: { method?: string; body?: string }) => {
    if (String(url).endsWith('/wiki/ingest/note') && (init?.method ?? 'GET') === 'POST') {
      calls.push({ body: JSON.parse(String(init!.body)) });
      return new Promise((r, j) => { ctl.resolve = r; ctl.reject = j; });
    }
    return Promise.resolve(null);
  });
  return {
    calls,
    resolve: (v: unknown) => ctl.resolve!(v),
    reject: (e: unknown) => { ctl.reject!(e); },
  };
}

const transferBtn = (c: Cont) => findNodeByAttr(c, 'title', 'Перенести в вики');
// атрибуты узла: disabled у выключенной кнопки — '' (React), у активной — атрибута нет
const attrs = (n: AnyNode | null): Record<string, string> =>
  ((n as { attributes?: Record<string, string> } | null)?.attributes) ?? {};

beforeEach(() => {
  installFakeDom();
  // DocComments читает localStorage в useState (ключ показанной панели) — в node его нет,
  // stub как в NotesPage.test
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {} });
  vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {} });
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
  wikiStore.reset();
  setAllFlags({ 'llm-wiki': true, 'notes-wiki-unified': true });
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  vi.unstubAllGlobals();
  uninstallFakeDom();
});

describe('«Перенести в вики» на экране заметки (F5b)', () => {
  it('без флагов кнопки нет: ни при выключенном llm-wiki, ни при одном llm-wiki', async () => {
    setAllFlags({});
    const { container } = renderNote(NOTE_A.id);
    await settle();
    expect(transferBtn(container), 'при выключенных флагах').toBeNull();
    // один llm-wiki — тоже нет: перенос относится к «Заметкам и Вики»
    setAllFlags({ 'llm-wiki': true });
    await settle();
    expect(transferBtn(container), 'при одном llm-wiki').toBeNull();
    expect(req.mock.calls.filter(([u]) => String(u).endsWith('/wiki/ingest/note'))).toHaveLength(0);
  });

  it('клик — подтверждение текстом записки; до подтверждения ни одного запроса', async () => {
    const { container } = renderNote(NOTE_A.id);
    await settle();
    const btn = transferBtn(container);
    expect(btn, 'при обоих флагах кнопка есть').not.toBeNull();
    click(btn, 'Перенести в вики');
    const body = texts(docBody());
    expect(body).toContain('Перенести заметку в вики?');
    expect(body).toContain('Заметка станет страницей вики: ваш текст — вашими утверждениями «✎ вы», а ИИ сможет дописывать страницу рядом с ними. Сама заметка заменится ссылкой на страницу — копий не будет.');
    expect(buttonByExactText(docBody(), 'Перенести'), 'кнопка подтверждения').not.toBeNull();
    expect(req.mock.calls.filter(([u]) => String(u).endsWith('/wiki/ingest/note'))).toHaveLength(0);
  });

  it('200 → POST {noteId, transfer:true}, Notice «Перенесено» и открытие страницы из ответа', async () => {
    const t = deferTransfer();
    const { container } = renderNote(NOTE_A.id);
    await settle();
    click(transferBtn(container), 'Перенести в вики');
    click(buttonByExactText(docBody(), 'Перенести'), 'подтверждение');
    t.resolve(TRANSFER_OK);
    await settle();
    // один запрос переноса, телом — id открытой заметки и transfer (target не шлём:
    // дом страницы задаёт бэк)
    expect(t.calls).toHaveLength(1);
    expect(t.calls[0].body).toEqual({ noteId: NOTE_A.id, transfer: true });
    expect(texts(container)).toContain('Перенесено');
    // созданная страница открылась запросом в стор (ключи — из ответа бэка, как есть)
    expect(consumeOpenWikiPage()).toEqual({ section: 'personal', path: 'Личное/Заметка А.md' });
    // подтверждение закрыто исходом
    expect(texts(docBody())).not.toContain('Перенести заметку в вики?');
    // кнопка на месте и снова активна (перенос другой заметки возможен)
    const btn = transferBtn(container);
    expect(btn).not.toBeNull();
    expect(attrs(btn).disabled, 'после успеха').toBeUndefined();
  });

  it('отказ — Notice русским текстом по коду, кнопка остаётся, страницу никто не открывает', async () => {
    const t = deferTransfer();
    const { container } = renderNote(NOTE_A.id);
    await settle();
    click(transferBtn(container), 'Перенести в вики');
    click(buttonByExactText(docBody(), 'Перенести'), 'подтверждение');
    // имя занято чужой страницей (409 wiki_page_changed без page — ADR-038, минор 7)
    t.reject({ status: 409, body: { code: 'wiki_page_changed', message: 'страница «Заметка А» в разделе уже есть: переименуйте заметку и повторите перенос' } });
    await settle();
    const view = texts(container);
    expect(view).toContain('Страница с таким именем уже есть: переименуйте заметку и повторите перенос.');
    expect(view).not.toContain('wiki_page_changed');
    expect(view).not.toContain('Перенесено');
    expect(consumeOpenWikiPage(), 'отказ — никакой страницы не открыли').toBeNull();
    expect(transferBtn(container), 'после отказа кнопка на месте').not.toBeNull();
    expect(texts(docBody())).not.toContain('Перенести заметку в вики?');
  });

  it('wiki_refused — причина бэка доходит текстом («повтор доведёт перенос»)', async () => {
    const t = deferTransfer();
    const { container } = renderNote(NOTE_A.id);
    await settle();
    click(transferBtn(container), 'Перенести в вики');
    click(buttonByExactText(docBody(), 'Перенести'), 'подтверждение');
    t.reject({ status: 409, body: { code: 'wiki_refused', message: 'страница создана, но заметку заменить не удалось (диск полон); повторите перенос — он доведёт замену' } });
    await settle();
    // message бэка показан как есть, с большой буквы (точки у строки бэка нет — не добавляем)
    expect(texts(container)).toContain('Страница создана, но заметку заменить не удалось (диск полон); повторите перенос — он доведёт замену');
    expect(consumeOpenWikiPage()).toBeNull();
  });

  it('двойной клик по подтверждению — один запрос', async () => {
    const t = deferTransfer();
    const { container } = renderNote(NOTE_A.id);
    await settle();
    click(transferBtn(container), 'Перенести в вики');
    const confirm = buttonByExactText(docBody(), 'Перенести');
    click(confirm, 'подтверждение, первый клик');
    // пока запрос идёт — кнопка выключена (React ставит атрибут disabled=""; .disabled
    // в fakeDom не читается — проверена пробой: только attributes)
    expect(attrs(transferBtn(container)).disabled, 'на время запроса').not.toBeUndefined();
    click(confirm, 'подтверждение, второй клик');
    expect(t.calls, 'пока запрос идёт — второй ничего не шлёт').toHaveLength(1);
    t.resolve(TRANSFER_OK);
    await settle();
    expect(t.calls).toHaveLength(1);
  });

  it('ответ после смены заметки отбрасывается по noteId: ни плашки, ни открытой страницы', async () => {
    const t = deferTransfer();
    const { root, container } = renderNote(NOTE_A.id);
    await settle();
    click(transferBtn(container), 'Перенести в вики');
    click(buttonByExactText(docBody(), 'Перенести'), 'подтверждение');
    // пока запрос шёл, пользователь открыл другую заметку: та же инстанция, другой noteId
    noteMock.detail = NOTE_B;
    act(() => {
      root.render(createElement(NoteView, {
        noteId: NOTE_B.id,
        existingTitles: new Set<string>(),
        onWikilink: () => {},
        onSelectNote: () => {},
        onDeleted: () => {},
        isMobile: true,
      }));
    });
    await settle();
    // подтверждение прежней заметки не перешло в новую
    expect(texts(docBody())).not.toContain('Перенести заметку в вики?');
    t.resolve(TRANSFER_OK);
    await settle();
    const view = texts(container);
    expect(view).toContain('Заметка Б');
    expect(view, 'ответ чужой заметки не показал плашку').not.toContain('Перенесено');
    expect(consumeOpenWikiPage(), 'ответ чужой заметки не открыл страницу').toBeNull();
    // состояние новой заметки чистое: её собственный перенос доступен
    const btn = transferBtn(container);
    expect(btn).not.toBeNull();
    expect(attrs(btn).disabled, 'для новой заметки').toBeUndefined();
  });
});
