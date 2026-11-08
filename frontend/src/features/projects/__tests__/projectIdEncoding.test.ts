import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Хвост OWN-TAIL-API-2 (minor по итогам ревью R-OWN-TAIL-API): id проекта в путях доменного
// api обязан экранироваться encodeURIComponent — как соседние методы того же домена
// (events, teamMemory, subjects, members, delete). Без экранирования путь в заглушке
// fetch несёт сырые '/' и '?' вместо %2F/%3F — тест краснеет. Каркас —
// deleteProjectEncoding.test.ts.
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
  // Доменный api.ts напрямую, без lib/api — проверяем именно его пути
  projectsApi = (await import('../api')).projectsApi;
});

afterEach(() => { vi.unstubAllGlobals(); });

function sentUrl(i = 0): string {
  const [url] = fetchMock.mock.calls[i] as [string, RequestInit];
  return url;
}

// id с символами, которые меняют сегмент пути: '/' дробит путь, '?' — начало query
const ID_RAW = 'p/1?2';
const ID_ENC = 'p%2F1%3F2';

describe('id проекта в путях доменного api: экранирование', () => {
  it('update: PUT /projects/{encodeURIComponent(id)}', async () => {
    await projectsApi.projects.update(ID_RAW, { name: 'x' });
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(sentUrl()).toBe(`/api/projects/${ID_ENC}`);
    expect(init.method).toBe('PUT');
  });

  it('getEffectivePrompt: GET /projects/{id}/effective-prompt с экранированным id', async () => {
    await projectsApi.projects.getEffectivePrompt(ID_RAW);
    expect(sentUrl()).toBe(`/api/projects/${ID_ENC}/effective-prompt`);
  });

  it('updateBoardColumns: PUT /projects/{id}/board-columns с экранированным id', async () => {
    await projectsApi.projects.updateBoardColumns(ID_RAW, []);
    expect(sentUrl()).toBe(`/api/projects/${ID_ENC}/board-columns`);
  });

  it('пути projectRequest (services/preview/launch-config): экранированный id', async () => {
    await projectsApi.projects.services(ID_RAW);
    expect(sentUrl(0)).toBe(`/api/projects/${ID_ENC}/services`);
    await projectsApi.projects.previewStart(ID_RAW, { serviceId: 's', name: 'n', command: 'c', args: [] });
    expect(sentUrl(1)).toBe(`/api/projects/${ID_ENC}/preview/start`);
    await projectsApi.projects.previewStop(ID_RAW, 's');
    expect(sentUrl(2)).toBe(`/api/projects/${ID_ENC}/preview/stop`);
    await projectsApi.projects.previewStatus(ID_RAW);
    expect(sentUrl(3)).toBe(`/api/projects/${ID_ENC}/preview/status`);
    await projectsApi.projects.previewActive(ID_RAW, 's');
    expect(sentUrl(4)).toBe(`/api/projects/${ID_ENC}/preview/active`);
    await projectsApi.projects.previewActiveExternal(ID_RAW, 's');
    expect(sentUrl(5)).toBe(`/api/projects/${ID_ENC}/preview/active-external`);
    await projectsApi.projects.previewStopExternal(ID_RAW, 's');
    expect(sentUrl(6)).toBe(`/api/projects/${ID_ENC}/preview/stop-external`);
    await projectsApi.projects.previewExternalLink(ID_RAW, 's');
    expect(sentUrl(7)).toBe(`/api/projects/${ID_ENC}/preview/external-link`);
    await projectsApi.projects.getLaunchConfig(ID_RAW);
    expect(sentUrl(8)).toBe(`/api/projects/${ID_ENC}/launch-config`);
    await projectsApi.projects.putLaunchConfig(ID_RAW, []);
    expect(sentUrl(9)).toBe(`/api/projects/${ID_ENC}/launch-config`);
  });

  it('обычный id: путь не изменился (экранирование GUID — identity)', async () => {
    await projectsApi.projects.update('p1', { name: 'x' });
    expect(sentUrl(0)).toBe('/api/projects/p1');
    await projectsApi.projects.getEffectivePrompt('p1');
    expect(sentUrl(1)).toBe('/api/projects/p1/effective-prompt');
    await projectsApi.projects.services('p1');
    expect(sentUrl(2)).toBe('/api/projects/p1/services');
  });
});
