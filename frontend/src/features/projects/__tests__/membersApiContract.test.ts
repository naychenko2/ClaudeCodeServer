import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Контракт-тест доменного блока members в features/projects/api.ts (находка R-K6, 08.11).
// Тесты members/__tests__ подменяют lib/api целиком и доменный api.ts не держат: без этого
// файла существование блока projectsApi.projects.members доказывает только tsc. Здесь —
// импорт доменного api.ts БЕЗ мока lib/api, против заглушки fetch: блок удалён или путь
// испорчен — тест краснеет. Маршруты сверяются с ProjectMembersController (GET/POST
// /projects/{id}/members, PUT/DELETE /projects/{id}/members/{userId}, DELETE …/members/me).
vi.mock('../../../lib/idb', () => ({
  idbCountRange: vi.fn(), idbDelete: vi.fn(), idbGet: vi.fn(),
  idbKeys: vi.fn(), idbRangeStamps: vi.fn(), idbSet: vi.fn(),
}));

function storageStub() {
  const m = new Map<string, string>([['cc_token', 'tok'], ['cc_user_id', 'u0']]);
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

// Ответ сервера по всем пяти операциям — одно и то же тело ProjectMembersView
const VIEW = {
  members: [{ userId: 'u2', role: 'reader', username: 'guest' }],
  owner: { userId: 'u0', username: 'owner' },
  myRole: 'owner',
};

let fetchMock: ReturnType<typeof vi.fn>;
let projectsApi: typeof import('../api').projectsApi;
let idbGet: ReturnType<typeof vi.fn>;
let OfflineError: typeof import('../../../lib/offline').OfflineError;

beforeEach(async () => {
  vi.resetModules();
  fetchMock = vi.fn().mockResolvedValue({
    ok: true, status: 200, statusText: 'OK',
    json: async () => VIEW, text: async () => JSON.stringify(VIEW),
  });
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub());
  vi.stubGlobal('sessionStorage', storageStub());
  // Доменный api.ts напрямую, без lib/api — именно его блок и проверяем
  projectsApi = (await import('../api')).projectsApi;
  idbGet = (await import('../../../lib/idb')).idbGet as ReturnType<typeof vi.fn>;
  OfflineError = (await import('../../../lib/offline')).OfflineError;
});

afterEach(() => { vi.unstubAllGlobals(); });

function sentRequest(i = 0): { url: string; init: RequestInit } {
  const [url, init] = fetchMock.mock.calls[i] as [string, RequestInit];
  return { url, init };
}

function sentBody(i = 0): Record<string, unknown> {
  return JSON.parse(sentRequest(i).init.body as string) as Record<string, unknown>;
}

describe('блок members домена projects', () => {
  it('существует и отдаёт все пять операций', () => {
    const m = projectsApi.projects.members;
    expect(m).toBeTypeOf('object');
    for (const k of ['list', 'add', 'changeRole', 'remove', 'leave']) {
      expect(m[k]).toBeTypeOf('function');
    }
  });

  it('list: GET /projects/{id}/members, без тела, с токеном', async () => {
    const view = await projectsApi.projects.members.list('p1');
    expect(view).toEqual(VIEW);
    const { url, init } = sentRequest();
    expect(url).toBe('/api/projects/p1/members');
    expect(init.method ?? 'GET').toBe('GET');
    expect(init.body).toBeUndefined();
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer tok');
  });

  it('list — живой запрос: при сетевом отказе офлайн-кэш не подставляется', async () => {
    // Живой список нужен именно фактом ответа сервера: доставленный из кэша показал бы
    // уже убранного человека. live:true — request не идёт в passiveRead даже при отказе.
    fetchMock.mockRejectedValue(new TypeError('network down'));
    await expect(projectsApi.projects.members.list('p1')).rejects.toBeInstanceOf(OfflineError);
    expect(idbGet).not.toHaveBeenCalled();
  });

  it('add: POST /projects/{id}/members телом { username, role }', async () => {
    await projectsApi.projects.members.add('p1', 'guest', 'reader');
    const { url, init } = sentRequest();
    expect(url).toBe('/api/projects/p1/members');
    expect(init.method).toBe('POST');
    expect(Object.keys(sentBody()).sort()).toEqual(['role', 'username']);
    expect(sentBody()).toEqual({ username: 'guest', role: 'reader' });
  });

  it('changeRole: PUT /projects/{id}/members/{userId} телом { role }', async () => {
    await projectsApi.projects.members.changeRole('p1', 'u2', 'editor');
    const { url, init } = sentRequest();
    expect(url).toBe('/api/projects/p1/members/u2');
    expect(init.method).toBe('PUT');
    expect(sentBody()).toEqual({ role: 'editor' });
  });

  it('remove: DELETE /projects/{id}/members/{userId} без тела', async () => {
    await projectsApi.projects.members.remove('p1', 'u2');
    const { url, init } = sentRequest();
    expect(url).toBe('/api/projects/p1/members/u2');
    expect(init.method).toBe('DELETE');
    expect(init.body).toBeUndefined();
  });

  it('leave: DELETE /projects/{id}/members/me — «me» маркер, не id', async () => {
    await projectsApi.projects.members.leave('p1');
    const { url, init } = sentRequest();
    expect(url).toBe('/api/projects/p1/members/me');
    expect(init.method).toBe('DELETE');
  });

  it('id проекта и id участника в пути — через encodeURIComponent', async () => {
    await projectsApi.projects.members.list('p/1 2');
    expect(sentRequest().url).toBe('/api/projects/p%2F1%202/members');
    await projectsApi.projects.members.changeRole('p/1 2', 'u 2/3', 'reader');
    expect(sentRequest(1).url).toBe('/api/projects/p%2F1%202/members/u%202%2F3');
  });
});
