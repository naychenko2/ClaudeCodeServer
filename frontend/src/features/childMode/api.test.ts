import { beforeEach, describe, expect, it, vi } from 'vitest';
import { childApi, ChildAttachmentRejectedError } from './api';
import type { ChildProfile } from './types';

// Клиент проверяем по его запросам: моким request (lib/offline) утверждаем URL, метод
// и тело по таблице «Ручки» ADR-041 («Контракт /api/child/*»). Путь request() строит
// '/api' + url — здесь ожидаем путь после префикса, как его передаёт клиент.
vi.mock('../../lib/offline', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../lib/offline')>();
  return { ...actual, request: vi.fn() };
});

async function req() {
  const { request } = await import('../../lib/offline');
  return request as ReturnType<typeof vi.fn>;
}

// Последний вызов: [url, options] — на нём проверяются URL/метод/тела вызовов.
async function lastCall(): Promise<{ url: string; opts: Record<string, unknown> }> {
  const calls = (await req()).mock.calls;
  const last = calls[calls.length - 1];
  return { url: last[0] as string, opts: (last[1] ?? {}) as Record<string, unknown> };
}

async function rejection(p: Promise<unknown>): Promise<unknown> {
  try {
    await p;
  } catch (e) {
    return e;
  }
  throw new Error('ожидался отказ');
}

// Ошибка как её выставляет request на !res.ok: status + тело целиком (ADR-041, «Общие правила»).
function httpErr(status: number, code: string, message: string): Error {
  const e = new Error(message) as Error & { status?: number; body?: unknown };
  e.status = status;
  e.body = { code, message };
  return e;
}

beforeEach(async () => {
  const r = await req();
  r.mockReset();
  r.mockResolvedValue({});
});

describe('GET /api/child/status', () => {
  it('без space: URL без параметра, GET, live (снимок per-owner, кэш общий по URL)', async () => {
    await childApi.status();
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/status');
    expect((opts.method ?? 'GET').toUpperCase()).toBe('GET');
    expect(opts.live).toBe(true);
  });

  it('space уходит в query-параметр', async () => {
    await childApi.status('lessons');
    const { url } = await lastCall();
    expect(url).toBe('/child/status?space=lessons');
  });

  it('signal вызывающего доходит до request (и fetch)', async () => {
    const ac = new AbortController();
    await childApi.status('lessons', { signal: ac.signal });
    const { opts } = await lastCall();
    expect(opts.signal).toBe(ac.signal);
  });
});

describe('POST /api/child/heartbeat', () => {
  it('тело { space }', async () => {
    await childApi.heartbeat('lessons');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/heartbeat');
    expect(opts.method).toBe('POST');
    expect(JSON.parse(opts.body as string)).toEqual({ space: 'lessons' });
  });
});

describe('POST /api/child/spaces/{space}/chat', () => {
  it('пустое тело на путь пространства', async () => {
    await childApi.spaceChat('lessons');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/spaces/lessons/chat');
    expect(opts.method).toBe('POST');
    expect(opts.body).toBeUndefined();
  });
});

describe('GET /api/child/spaces/{space}/chats', () => {
  it('live, GET; signal доходит до request', async () => {
    const ac = new AbortController();
    await childApi.spaceChats('lessons', { signal: ac.signal });
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/spaces/lessons/chats');
    expect((opts.method ?? 'GET').toUpperCase()).toBe('GET');
    expect(opts.live).toBe(true);
    expect(opts.signal).toBe(ac.signal);
  });
});

describe('PUT /api/child/chats/{id}/voice', () => {
  it('style отправляется, только когда задан', async () => {
    await childApi.voice('c1', true, 'talk');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/chats/c1/voice');
    expect(opts.method).toBe('PUT');
    expect(JSON.parse(opts.body as string)).toEqual({ enabled: true, style: 'talk' });

    await childApi.voice('c1', false);
    const second = await lastCall();
    expect(JSON.parse(second.opts.body as string)).toEqual({ enabled: false });
  });
});

describe('POST /api/child/rules/*', () => {
  it('start — пустое тело', async () => {
    await childApi.rulesStart();
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/rules/start');
    expect(opts.method).toBe('POST');
    expect(opts.body).toBeUndefined();
  });

  it('agree — { sessionId, summary }', async () => {
    await childApi.rulesAgree('c1', 'Правила: сначала уроки, потом игры');
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/rules/agree');
    expect(opts.method).toBe('POST');
    expect(JSON.parse(opts.body as string)).toEqual({ sessionId: 'c1', summary: 'Правила: сначала уроки, потом игры' });
  });
});

describe('POST /api/child/chats/{id}/attachments', () => {
  it('multipart, поле file', async () => {
    const file = new Blob(['\x89PNG-данные'], { type: 'image/png' });
    await childApi.uploadAttachment('c1', file);
    const { url, opts } = await lastCall();
    expect(url).toBe('/child/chats/c1/attachments');
    expect(opts.method).toBe('POST');
    const form = opts.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    expect((form.get('file') as Blob).size).toBe(file.size);
  });

  it('413 too_large → типизированная ошибка с code и message сервера', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(413, 'too_large', 'Файл слишком большой'));
    const e = (await rejection(childApi.uploadAttachment('c1', new Blob(['x'])))) as ChildAttachmentRejectedError;
    expect(e).toBeInstanceOf(ChildAttachmentRejectedError);
    expect(e.status).toBe(413);
    expect(e.code).toBe('too_large');
    expect(e.message).toBe('Файл слишком большой');
  });

  it('415 unsupported_type и 422 pdf_no_text — те же поля', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(415, 'unsupported_type', 'Такие файлы нельзя'));
    const e415 = (await rejection(childApi.uploadAttachment('c1', new Blob(['x'])))) as ChildAttachmentRejectedError;
    expect(e415.code).toBe('unsupported_type');
    r.mockRejectedValueOnce(httpErr(422, 'pdf_no_text', 'В этом PDF нет текста'));
    const e422 = (await rejection(childApi.uploadAttachment('c1', new Blob(['x'])))) as ChildAttachmentRejectedError;
    expect(e422.code).toBe('pdf_no_text');
    expect(e422.message).toBe('В этом PDF нет текста');
  });

  it('прочий отказ (500) — как есть, не в обёртке', async () => {
    const r = await req();
    r.mockRejectedValueOnce(httpErr(500, 'oops', 'Сбой сервера'));
    const e = await rejection(childApi.uploadAttachment('c1', new Blob(['x'])));
    expect(e).not.toBeInstanceOf(ChildAttachmentRejectedError);
    expect((e as { status?: number }).status).toBe(500);
  });
});

describe('POST /api/child/stop-signal', () => {
  it('без sessionId — пустой объект; с sessionId — { sessionId }', async () => {
    await childApi.stopSignal();
    const first = await lastCall();
    expect(first.url).toBe('/child/stop-signal');
    expect(first.opts.method).toBe('POST');
    expect(JSON.parse(first.opts.body as string)).toEqual({});

    await childApi.stopSignal('c1');
    const second = await lastCall();
    expect(JSON.parse(second.opts.body as string)).toEqual({ sessionId: 'c1' });
  });
});

describe('PUT /api/users/{id}/child-profile', () => {
  const profile: ChildProfile = {
    enabled: true,
    guardianUserIds: ['u2', 'u3'],
    providerKey: 'pk-org',
    model: 'claude-sonnet-4-5',
    spaces: { lessons: { windows: [{ from: '16:00', to: '19:00', weekdays: [1, 2, 3] }], dailyMinutes: 30 } },
    dailyBudgetUsd: 2,
    monthlyBudgetUsd: 40,
    warnMinutes: 5,
  };

  it('тело — ChildProfile целиком', async () => {
    await childApi.putProfile('u1', profile);
    const { url, opts } = await lastCall();
    expect(url).toBe('/users/u1/child-profile');
    expect(opts.method).toBe('PUT');
    expect(JSON.parse(opts.body as string)).toEqual(profile);
  });

  it('выключение — { enabled: false }', async () => {
    await childApi.putProfile('u1', { enabled: false });
    const { opts } = await lastCall();
    expect(JSON.parse(opts.body as string)).toEqual({ enabled: false });
  });
});
