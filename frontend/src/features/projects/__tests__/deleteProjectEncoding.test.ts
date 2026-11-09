import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Хвост OWN-TAIL-API (minor по итогам ревью R-MEMBERS-CONTRACT): delete проекта обязан
// экранировать id в пути — как соседние методы того же домена (events, teamMemory,
// subjects, members…). Тест против заглушки fetch: без encodeURIComponent путь несёт
// сырые '/' и '?' вместо %2F/%3F — краснеет. Каркас — membersApiContract.test.ts.
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

let fetchMock: ReturnType<typeof vi.fn>;
let projectsApi: typeof import('../api').projectsApi;

beforeEach(async () => {
  vi.resetModules();
  fetchMock = vi.fn().mockResolvedValue({
    ok: true, status: 200, statusText: 'OK',
    json: async () => ({}), text: async () => '',
  });
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub());
  vi.stubGlobal('sessionStorage', storageStub());
  // Доменный api.ts напрямую, без lib/api — проверяем именно его delete
  projectsApi = (await import('../api')).projectsApi;
});

afterEach(() => { vi.unstubAllGlobals(); });

function sentRequest(i = 0): { url: string; init: RequestInit } {
  const [url, init] = fetchMock.mock.calls[i] as [string, RequestInit];
  return { url, init };
}

describe('delete проекта: экранирование id в пути', () => {
  it('обычный id — DELETE /projects/{id}, поведение не изменилось', async () => {
    await projectsApi.projects.delete('p1');
    const { url, init } = sentRequest();
    expect(url).toBe('/api/projects/p1');
    expect(init.method).toBe('DELETE');
  });

  it('id с символами, требующими экранирования: в пути %2F/%3F, а не сырые', async () => {
    await projectsApi.projects.delete('p/1?2');
    expect(sentRequest().url).toBe('/api/projects/p%2F1%3F2');
  });
});
