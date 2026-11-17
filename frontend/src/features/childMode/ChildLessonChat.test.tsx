// KM-F8: конкурентность загрузки вложения в детском уроке.
// Проверяем ключ состояния — sessionId: ответ загрузки, ушедшей с прежнего чата, не должен
// добавлять чип в новый чат. Контрольный случай (та же загрузка без смены чата → чип есть)
// держит тест невакуумным: сломанный ключ краснит первый случай, сломанную загрузку — второй.
// Окружение — node + linkedom, как в ChildHintsPanel.test: ChatItemView, WaitingIndicator
// и сам Composer подменены стендами (визуал проверяют их собственные тесты), useSession и
// useOnline — моками, чтобы не тянуть SignalR.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  matchMedia: () => ({
    matches: false, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// childApi: uploadAttachment отдаёт управляемый promise — ответ приходит в удобный момент
let uploadResolve: ((v: { path: string; kind: 'image' | 'text'; name: string }) => void) | null = null;
let uploadReject: ((e: unknown) => void) | null = null;
const uploadCalls: { id: string }[] = [];

vi.mock('./api', () => {
  class ChildAttachmentRejectedError extends Error {
    readonly status: number;
    readonly code: string;
    constructor(status: number, code: string, message: string) {
      super(message);
      this.status = status;
      this.code = code;
    }
  }
  return {
    ChildAttachmentRejectedError,
    childApi: {
      uploadAttachment: (id: string) => {
        uploadCalls.push({ id });
        return new Promise((res, rej) => { uploadResolve = res; uploadReject = rej; });
      },
      voice: async () => ({ voiceMode: false, voiceStyle: 'talk' }),
    },
  };
});

vi.mock('../../hooks/useOnline', () => ({ useOnline: () => true }));
let mockItems: unknown[] = [];
vi.mock('../../hooks/useSession', () => ({
  useSession: () => ({ items: mockItems, isWaiting: false, isJoined: true, send: vi.fn(), interrupt: vi.fn() }),
}));
// Стенд ChatItemView печатает вид и «сырой» технический текст — утечка через него видна в ленте
vi.mock('../../components/chat/ChatItemView', () => ({
  ChatItemView: (p: { item: { kind: string; text?: string; subtype?: string; apiErrorStatus?: string } }) =>
    createElement('div', { 'data-civ': p.item.kind },
      p.item.kind === 'result' && p.item.subtype !== 'success'
        ? `ошибка API: ${p.item.apiErrorStatus} проверьте API-ключ ${p.item.subtype}`
        : (p.item.text ?? p.item.kind)),
}));
vi.mock('../../components/ui/WaitingIndicator', () => ({ WaitingIndicator: () => null }));
// Стенд композера: кнопка «прикрепить» отдаёт файл хостуChildLessonChat и печатает чипы
vi.mock('../../components/Composer', () => ({
  Composer: (p: { attachments: string[]; onAttachFiles?: (f: File[]) => void }) => createElement('div', null,
    createElement('button', { 'data-mock-attach': '', onClick: () => p.onAttachFiles?.([new win.File(['x'], 'zadacha.jpg')]) }, 'attach'),
    ...p.attachments.map(path => createElement('span', { key: path, 'data-att': path }, path))),
}));

const { ChildLessonChat, CHILD_TURN_ERROR_TEXT } = await import('./ChildLessonChat');
const { ChildAttachmentRejectedError } = await import('./api');

const STATUS = {
  state: 'ok' as const, space: 'lessons', minutesLeft: null, windowEndsAt: null, nextWindowAt: null,
  warnMinutes: 5, maxAttachmentBytes: 10 * 1024 * 1024,
  attachmentTypes: ['image/png', 'image/jpeg', 'image/webp', 'application/pdf', 'text/plain'],
  rulesAgreedAt: null,
};

function renderChat(root: ReturnType<typeof createRoot>, sessionId: string, status = STATUS) {
  act(() => {
    root.render(createElement(ChildLessonChat, {
      sessionId, subjectId: 'math', title: 'Сравнение дробей', status,
    }));
  });
}

function chips(): string[] {
  return Array.from(doc.querySelectorAll('[data-att]')).map(el => el.getAttribute('data-att') ?? '');
}

async function flush() {
  await act(async () => { await Promise.resolve(); await Promise.resolve(); });
}

describe('ChildLessonChat: загрузка вложения', () => {
  beforeEach(() => { uploadResolve = null; uploadReject = null; uploadCalls.length = 0; });

  it('upload response after chat switch is dropped', async () => {
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-1');
    act(() => { (doc.querySelector('[data-mock-attach]') as HTMLElement).click(); });
    expect(uploadCalls).toEqual([{ id: 'chat-1' }]);

    // Чат сменился, пока загрузка ещё в полёте
    renderChat(root, 'chat-2');
    act(() => { uploadResolve?.({ path: '.cc-attachments/chat-1/zadacha.jpg', kind: 'image', name: 'zadacha.jpg' }); });
    await flush();

    expect(chips()).toEqual([]);
    root.unmount();
  });

  it('под чатом урока постоянно видна строка «Помощник — компьютерная программа»', async () => {
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-d');
    await flush();
    expect(doc.querySelector('[data-child-disclosure]')?.textContent).toBe('Помощник — компьютерная программа');
    root.unmount();
  });

  it('upload response in the same chat adds a chip', async () => {
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-7');
    act(() => { (doc.querySelector('[data-mock-attach]') as HTMLElement).click(); });
    act(() => { uploadResolve?.({ path: '.cc-attachments/chat-7/zadacha.jpg', kind: 'image', name: 'zadacha.jpg' }); });
    await flush();

    expect(chips()).toEqual(['.cc-attachments/chat-7/zadacha.jpg']);
    root.unmount();
  });

  it.each([[10 * 1024 * 1024, '10'], [5 * 1024 * 1024, '5']])('413 too_large: «до N МБ» берётся из status.maxAttachmentBytes (%i → %s)', async (bytes, mb) => {
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-9', { ...STATUS, maxAttachmentBytes: bytes });
    act(() => { (doc.querySelector('[data-mock-attach]') as HTMLElement).click(); });
    await act(async () => { uploadReject?.(new ChildAttachmentRejectedError(413, 'too_large', 'серверный текст')); await Promise.resolve(); });
    await flush();

    const alert = doc.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain(`можно до ${mb} МБ`);
    expect(chips()).toEqual([]);
    root.unmount();
  });
});

describe('ChildLessonChat: сбой хода в ленте', () => {
  beforeEach(() => { mockItems = []; });

  it('сырой текст ошибки провайдера не попадает в ленту — вместо него спокойный текст на «ты»', () => {
    mockItems = [
      { kind: 'error', text: 'Invalid API key · Fix external API key', canRetry: true },
      { kind: 'error_group', date: 0, items: [{ kind: 'error', text: 'ошибка авторизации — проверьте API-ключ' }] },
    ];
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-err');
    const feed = doc.querySelector('[data-child-feed]')!;
    expect(feed.textContent).not.toMatch(/API|ключ/i);
    const blocks = feed.querySelectorAll('[data-child-turn-error]');
    expect(blocks.length).toBe(1);
    expect(blocks[0].textContent).toContain(CHILD_TURN_ERROR_TEXT);
    expect(CHILD_TURN_ERROR_TEXT).toContain('Попробуй');
    root.unmount();
  });

  it('result с ошибкой API (401) — без «API/ключ/error_», вместо него плашка', () => {
    mockItems = [{ kind: 'result', subtype: 'error_during_execution', apiErrorStatus: '401', durationMs: 1, numTurns: 1 }];
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-res');
    const feed = doc.querySelector('[data-child-feed]')!;
    expect(feed.textContent).not.toMatch(/API|ключ|error_/i);
    expect(feed.querySelectorAll('[data-child-turn-error]').length).toBe(1);
    expect(feed.querySelector('[data-civ="result"]')).toBeNull();
    root.unmount();
  });

  it('успешный result показывается как раньше', () => {
    mockItems = [{ kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1 }];
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-ok');
    const feed = doc.querySelector('[data-child-feed]')!;
    expect(feed.querySelector('[data-civ="result"]')).not.toBeNull();
    expect(feed.querySelectorAll('[data-child-turn-error]').length).toBe(0);
    root.unmount();
  });

  it('неизвестный вид не показывается, реплики ребёнка и тьютора — да', () => {
    mockItems = [
      { kind: 'user_message', text: 'привет' },
      { kind: 'brand_new_kind', text: 'секрет' },
      { kind: 'session_started', model: 'm', mode: 'default' },
      { kind: 'text', text: 'здравствуй' },
    ];
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-unk');
    const kinds = Array.from(doc.querySelectorAll('[data-civ]')).map(e => e.getAttribute('data-civ'));
    expect(kinds).toEqual(['user_message', 'text']);
    expect(doc.querySelector('[data-child-feed]')!.textContent).not.toContain('секрет');
    root.unmount();
  });

  it.each([
    { kind: 'rate_limit', limitType: 'x' },
    { kind: 'provider_limit', providers: [] },
    { kind: 'model_switched', model: 'a', previousModel: 'b' },
    { kind: 'work_loop_stopped', reason: 'r', text: 'technical' },
    { kind: 'hands_notice', text: 'technical', tone: 'warning' },
    { kind: 'tool_use', id: '1', name: 'Bash', input: {}, isError: true, result: 'technical' },
  ])('технический вид $kind — спокойная плашка, подряд идущие сворачиваются в одну', item => {
    mockItems = [item, { kind: 'error', text: 'raw' }];
    const root = createRoot(doc.getElementById('root')!);
    renderChat(root, 'chat-tech');
    const feed = doc.querySelector('[data-child-feed]')!;
    expect(feed.querySelectorAll('[data-civ]').length).toBe(0);
    expect(feed.querySelectorAll('[data-child-turn-error]').length).toBe(1);
    root.unmount();
  });
});
