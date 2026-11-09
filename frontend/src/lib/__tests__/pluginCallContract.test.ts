// Контракт вызова моста (ADR-031 §3.4, дефект 64191abe): форма HTTP-запроса
// plugins.call РОВНО {capability, args, projectId} — ключами и типами. Бэкенд
// проверяет ту же форму в PluginCallContractTests; здесь — фронт.
//
// Мутация «вернуть op/context в тело» → красный: лишнее поле или отсутствие capability.
// projectId — null, если панель вне проекта.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// request (offline.ts) использует globalThis.fetch; readStoredToken — токен (null — без auth)
const m = vi.hoisted(() => ({
  fetch: vi.fn<(input: string, init?: RequestInit) => Promise<Response>>(),
}));

vi.mock('../offline', () => ({
  readStoredToken: () => null,
  request: (url: string, options?: RequestInit) =>
    m.fetch(url, options).then((r: Response) => r.json()),
}));

const { api } = await import('../api');

beforeEach(() => {
  vi.stubGlobal('fetch', m.fetch);
  m.fetch.mockReset();
  m.fetch.mockImplementation(async () => new Response(JSON.stringify({ ok: true, result: { hello: 'world' } }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('plugins.call: контракт тела HTTP-запроса (PluginCallContractTests)', () => {
  it('тело = {capability, args, projectId}: capability строка, args объект, projectId строка', async () => {
    await api.plugins.call('acme/notes', {
      capability: 'notes.search',
      args: { query: 'a', limit: 3 },
      projectId: 'proj-1',
    });
    expect(m.fetch).toHaveBeenCalledTimes(1);
    const [url, init] = m.fetch.mock.calls[0] as [string, RequestInit];
    // URL: /plugins/{id}/call (pluginUrlId кодирует / как ~)
    expect(url).toContain('/plugins/acme~notes/call');
    // Тело: РОВНО {capability, args, projectId} — без op, без context
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body).toEqual({
      capability: 'notes.search',
      args: { query: 'a', limit: 3 },
      projectId: 'proj-1',
    });
    // Лишних полей нет
    expect(body).not.toHaveProperty('op');
    expect(body).not.toHaveProperty('context');
  });

  it('projectId = null (вне проекта) — поле присутствует, значение null', async () => {
    await api.plugins.call('acme/notes', {
      capability: 'notes.search',
      args: {},
      projectId: null,
    });
    const [, init] = m.fetch.mock.calls[0] as [string, RequestInit];
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body).toEqual({ capability: 'notes.search', args: {}, projectId: null });
    // projectId — ключ с null (не отсутствующий)
    expect(body).toHaveProperty('projectId', null);
  });

  it('args = {} (пустой объект) — проходит, не превращается в null', async () => {
    await api.plugins.call('acme/notes', {
      capability: 'notes.read',
      args: {},
      projectId: 'proj-2',
    });
    const [, init] = m.fetch.mock.calls[0] as [string, RequestInit];
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body.args).toEqual({});
  });
});
