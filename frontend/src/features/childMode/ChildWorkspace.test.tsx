// KM-F10: детская оболочка — какой экран выбирается по состоянию статуса, ключ чата урока,
// отсутствие взрослых слоёв. Окружение — node + linkedom; тяжёлые дети подменены заглушками.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ChildStatus } from './types';

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
const store: Record<string, string> = {};
vi.stubGlobal('localStorage', { getItem: (k: string) => store[k] ?? null, setItem: (k: string, v: string) => { store[k] = v; }, removeItem: (k: string) => { delete store[k]; } });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const h = vi.hoisted(() => ({
  status: null as unknown,
  offline: false,
  mounts: [] as string[],
  api: {
    spaceChats: vi.fn(),
    spaceChat: vi.fn(),
    rulesStart: vi.fn(),
  },
}));

vi.mock('./useChildStatus', () => ({ useChildStatus: () => ({ status: h.status, offline: h.offline }) }));
vi.mock('./api', () => ({ childApi: h.api, ChildAttachmentRejectedError: class extends Error {} }));
vi.mock('./ChildLessonChat', async () => {
  const React = await import('react');
  return {
    ChildLessonChat: (p: { sessionId: string; initialVoiceMode?: boolean }) => {
      React.useEffect(() => { h.mounts.push(`${p.sessionId}:${p.initialVoiceMode}`); }, [p.sessionId]);
      return React.createElement('div', { 'data-testid': 'lesson-chat' }, p.sessionId);
    },
  };
});
vi.mock('./ChildHintsPanel', async () => {
  const React = await import('react');
  return { ChildHintsPanel: () => React.createElement('div', { 'data-testid': 'hints' }) };
});
vi.mock('../../components/ui/PageCanvas', async () => {
  const React = await import('react');
  return {
    PageCanvas: (p: { project?: { icon?: { color?: string } }; children: unknown }) =>
      React.createElement('div', { 'data-canvas': p.project?.icon?.color ?? 'neutral' }, p.children as never),
  };
});

const { ChildWorkspace } = await import('./ChildWorkspace');

const status = (over: Partial<ChildStatus> = {}): ChildStatus => ({
  state: 'ok', space: 'lessons', minutesLeft: 40,
  windowEndsAt: '2026-11-17T19:00:00+03:00', nextWindowAt: null,
  warnMinutes: 5, maxAttachmentBytes: 10_000_000,
  attachmentTypes: ['image/png'], rulesAgreedAt: '2026-11-10T12:00:00+03:00', ...over,
});

const auth = { serverUrl: '', token: 't', username: 'liza', displayName: 'Лиза' };

async function mount() {
  const root = createRoot(doc.getElementById('root')!);
  await act(async () => { root.render(createElement(ChildWorkspace, { auth, onLogout: () => {} })); });
  await act(async () => {});
  return root;
}
const click = async (el: Element) => { await act(async () => { el.dispatchEvent(new win.Event('click', { bubbles: true })); }); await act(async () => {}); };

beforeEach(() => {
  doc.getElementById('root')!.innerHTML = '';
  h.status = status(); h.offline = false; h.mounts.length = 0;
  h.api.spaceChats.mockReset().mockResolvedValue({ chats: [
    { sessionId: 's1', name: 'Дроби', updatedAt: '2026-11-17T10:00:00Z', voiceMode: true },
    { sessionId: 's2', name: 'Уравнения', updatedAt: '2026-11-16T10:00:00Z', voiceMode: false },
  ] });
  h.api.spaceChat.mockReset().mockResolvedValue({ sessionId: 's3' });
  h.api.rulesStart.mockReset().mockResolvedValue({ sessionId: 'r1' });
  for (const k of Object.keys(store)) delete store[k];
});

describe('ChildWorkspace', () => {
  it('time_up показывает экран «время вышло» и не грузит уроки', async () => {
    h.status = status({ state: 'time_up' });
    await mount();
    expect(doc.querySelector('[data-testid="lesson-chat"]')).toBeNull();
    expect(h.api.spaceChats).not.toHaveBeenCalled();
    expect(doc.querySelector('[data-canvas]')!.getAttribute('data-canvas')).toBe('neutral');
  });

  it('warn — плашка про оставшиеся минуты и «Хорошо» её скрывает, экран «время вышло» не открывается', async () => {
    h.status = status({ state: 'warn', minutesLeft: 5 });
    await mount();
    expect(doc.body.textContent).toContain('Осталось 5 минут');
    const ok = Array.from(doc.querySelectorAll('button')).find(b => b.textContent === 'Хорошо')!;
    await click(ok);
    expect(doc.body.textContent).not.toContain('Осталось 5 минут');
  });

  it('offline — плашка «Нет связи»', async () => {
    h.offline = true;
    await mount();
    expect(doc.body.textContent).toContain('Нет связи');
  });

  it('rules_pending: точка на «Правила», старт правил идёт через rulesStart', async () => {
    h.status = status({ state: 'rules_pending', rulesAgreedAt: null });
    await mount();
    expect(doc.querySelector('[data-testid="rules-dot"]')).not.toBeNull();
    expect(h.api.spaceChats).not.toHaveBeenCalled();
    const start = Array.from(doc.querySelectorAll('button')).find(b => /Начать вместе с папой/.test(b.textContent ?? ''))!;
    await click(start);
    expect(h.api.rulesStart).toHaveBeenCalledTimes(1);
  });

  it('урок монтируется с key=sessionId и берёт озвучку с сервера; холст красится предметом', async () => {
    store.cc_child_lesson_subjects = JSON.stringify({ s1: 'russian', s2: 'english' });
    await mount();
    const resume = Array.from(doc.querySelectorAll('button')).find(b => /Вчера|Продолжить|Дроби/.test(b.textContent ?? ''));
    expect(resume).toBeTruthy();
    await click(resume!);
    expect(h.mounts).toEqual(['s1:true']);
    expect(doc.querySelector('[data-canvas]')!.getAttribute('data-canvas')).toBe('purple');
    const row = Array.from(doc.querySelectorAll('button')).find(b => b.textContent === 'Уравнения')!;
    await click(row);
    // смена урока — новый экземпляр чата, а не переиспользование
    expect(h.mounts).toEqual(['s1:true', 's2:false']);
    expect(doc.querySelector('[data-canvas]')!.getAttribute('data-canvas')).toBe('green');
  });
});
