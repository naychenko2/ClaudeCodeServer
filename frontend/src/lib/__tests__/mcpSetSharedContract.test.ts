// Контракт вызова api.mcp.setShared (S2a-F6): форма HTTP-запроса пометки «для участников»
// — РОВНО PUT /mcp/servers/{id}/shared с телом { sharedWithMembers: bool }. Бэкенд
// (McpServersController) читает ту же форму; здесь — фронт (образец — pluginCallContract.test.ts).
//
// Мутация «метод POST» или «тело {shared}» → красный: не тот метод или не то имя поля.

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
  m.fetch.mockImplementation(async () => new Response(JSON.stringify({ ok: true, result: { id: 'srv-1' } }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('api.mcp.setShared: контракт HTTP-запроса (S2a-F6)', () => {
  it('URL /mcp/servers/{id}/shared, метод PUT, тело ровно { sharedWithMembers: true }', async () => {
    await api.mcp.setShared('srv-1', true);
    expect(m.fetch).toHaveBeenCalledTimes(1);
    const [url, init] = m.fetch.mock.calls[0] as [string, RequestInit];
    expect(url).toContain('/mcp/servers/srv-1/shared');
    expect(init.method).toBe('PUT');
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body).toEqual({ sharedWithMembers: true });
    // Ровно один ключ — других полей нет
    expect(Object.keys(body)).toEqual(['sharedWithMembers']);
  });

  it('false — тот же PUT, тело { sharedWithMembers: false }; id кодируется в URL', async () => {
    await api.mcp.setShared('srv 2', false);
    const [url, init] = m.fetch.mock.calls[0] as [string, RequestInit];
    // id идёт через encodeURIComponent: пробел → %20
    expect(url).toContain('/mcp/servers/srv%202/shared');
    expect(init.method).toBe('PUT');
    const body = JSON.parse(init.body as string) as Record<string, unknown>;
    expect(body).toEqual({ sharedWithMembers: false });
  });
});
