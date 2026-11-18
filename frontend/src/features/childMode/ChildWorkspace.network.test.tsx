// KM-F10: сеть оболочки — за первую загрузку уходят только запросы из allow-list ребёнка (ADR-041),
// и ответ прежнего запроса предметов не перетирает новый (Т3c).
// Настоящие childApi и useChildStatus, подменён транспорт request; чат урока — заглушка
// (его запросы проверяет ChildLessonChat.test).

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></html>');
Object.assign(win, {
  innerWidth: 1280,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {} }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const urls = vi.hoisted(() => [] as string[]);
// Ответы subjects/chats — управляемые: тест сам решает, в каком порядке они приходят
const subjectsQ = vi.hoisted(() => [] as Array<(v: { subjects: unknown[] }) => void>);
const chatsQ = vi.hoisted(() => [] as Array<(v: { chats: unknown[] }) => void>);

vi.mock('../../lib/offline', () => ({
  request: (url: string) => {
    urls.push(url);
    if (url.startsWith('/child/status')) {
      return Promise.resolve({
        state: 'ok', space: 'lessons', minutesLeft: 40, windowEndsAt: null, nextWindowAt: null,
        warnMinutes: 5, maxAttachmentBytes: 1, attachmentTypes: [], rulesAgreedAt: '2026-11-10T12:00:00+03:00',
      });
    }
    if (url === '/child/subjects') return new Promise(r => subjectsQ.push(r));
    if (url.endsWith('/chats')) return new Promise(r => chatsQ.push(r));
    if (url.endsWith('/chat')) return Promise.resolve({ sessionId: 'new-1' });
    return Promise.resolve({});
  },
}));
vi.mock('./ChildLessonChat', () => ({ ChildLessonChat: () => null }));
vi.mock('../../components/ui/PageCanvas', async () => {
  const React = await import('react');
  return { PageCanvas: (p: { children: unknown }) => React.createElement('div', null, p.children as never) };
});

const { ChildWorkspace } = await import('./ChildWorkspace');

async function mount() {
  const root = createRoot(doc.getElementById('root')!);
  await act(async () => { root.render(createElement(ChildWorkspace, { auth: { serverUrl: '', token: 't', username: 'liza' }, onLogout: () => {} })); });
  await act(async () => {});
  return root;
}
const click = async (el: Element) => { await act(async () => { el.dispatchEvent(new win.Event('click', { bubbles: true })); }); await act(async () => {}); };
const byText = (re: RegExp) => Array.from(doc.querySelectorAll('button')).find(b => re.test(b.textContent ?? ''))!;

describe('ChildWorkspace: сеть', () => {
  beforeEach(() => {
    // управляемые очереди и журнал url'ов — свои на каждый тест, иначе предыдущий
    // несбросленный промис считается новым запросом
    urls.length = 0; subjectsQ.length = 0; chatsQ.length = 0;
    doc.getElementById('root')!.innerHTML = '';
  });

  it('первая загрузка ходит только на /child/*', async () => {
    const root = await mount();
    expect(urls.length).toBeGreaterThan(0);
    expect(urls.filter(u => !u.startsWith('/child/'))).toEqual([]);
    expect(urls).toContain('/child/spaces/lessons/chats');
    root.unmount();
  });

  it('ответ getSubjects прежнего запроса не перетирает новый: на экране данные последнего', async () => {
    const root = await mount();
    expect(subjectsQ.length).toBe(1); // первый запрос — при монтировании

    // Моста волны 1 (Т3e) больше нет: пока первый ответ не пришёл, плиток нет.
    expect(doc.body.textContent ?? '').not.toContain('Математика');

    // Плитки — только из ответа сервера. Первый «Начать урок» зовёт loadLists: новый запрос
    // предметов (subjectsQ[1]) идёт вместе со списком чатов.
    await act(async () => { subjectsQ[0]({ subjects: [
      { id: 'math', label: 'Математика', glyph: '×÷', color: 'info', tutor: null },
      { id: 'english', label: 'Английский', glyph: 'Ab', color: 'success', tutor: null },
    ] }); });
    await click(byText(/Математика/));
    await click(byText(/^Начать урок$/));
    expect(subjectsQ.length).toBe(2);

    // Не дождавшись ответа первого запроса — назад на главную и второй урок: ещё запрос
    // (subjectsQ[2]); его ответ придёт раньше ответа первого.
    await click(doc.querySelector('button[aria-label="Уроки"]')!);
    await click(byText(/Английский/));
    await click(byText(/^Начать урок$/));
    expect(subjectsQ.length).toBe(3);

    await act(async () => { subjectsQ[2]({ subjects: [{ id: 'history', label: 'История', glyph: 'И', color: 'warning', tutor: null }] }); });
    await act(async () => { subjectsQ[1]({ subjects: [{ id: 'english', label: 'Английский', glyph: 'Ab', color: 'success', tutor: null }] }); });

    // На главную — плитки того, что принёс последний запрос: старый ответ отброшен счётчиком.
    await click(doc.querySelector('button[aria-label="Уроки"]')!);
    const text = doc.body.textContent ?? '';
    expect(text).toContain('История');
    expect(text).not.toContain('Английский');
    root.unmount();
  });
});
