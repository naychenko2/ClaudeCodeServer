// KM-F10: сеть оболочки — за первую загрузку уходят только запросы из allow-list ребёнка (ADR-041).
// Настоящие childApi и useChildStatus, подменён транспорт request; чат урока — заглушка
// (его запросы проверяет ChildLessonChat.test).

import { describe, it, expect, vi } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
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

vi.mock('../../lib/offline', () => ({
  request: (url: string) => {
    urls.push(url);
    if (url.startsWith('/child/status')) {
      return Promise.resolve({
        state: 'ok', space: 'lessons', minutesLeft: 40, windowEndsAt: null, nextWindowAt: null,
        warnMinutes: 5, maxAttachmentBytes: 1, attachmentTypes: [], rulesAgreedAt: '2026-11-10T12:00:00+03:00',
      });
    }
    return Promise.resolve({ chats: [] });
  },
}));
vi.mock('./ChildLessonChat', () => ({ ChildLessonChat: () => null }));
vi.mock('../../components/ui/PageCanvas', async () => {
  const React = await import('react');
  return { PageCanvas: (p: { children: unknown }) => React.createElement('div', null, p.children as never) };
});

const { ChildWorkspace } = await import('./ChildWorkspace');

describe('ChildWorkspace: сеть', () => {
  it('первая загрузка ходит только на /child/*', async () => {
    const root = createRoot(doc.getElementById('root')!);
    await act(async () => { root.render(createElement(ChildWorkspace, { auth: { serverUrl: '', token: 't', username: 'liza' }, onLogout: () => {} })); });
    await act(async () => {});
    expect(urls.length).toBeGreaterThan(0);
    expect(urls.filter(u => !u.startsWith('/child/'))).toEqual([]);
    expect(urls).toContain('/child/spaces/lessons/chats');
    root.unmount();
  });
});
