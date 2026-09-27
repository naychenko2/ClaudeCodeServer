import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Контракт-тест шва фронт ↔ бэк для локальных проектов (ADR-016 §3.1). Точный набор ключей
// тела сверяется с record'ами бэка: CreateProjectRequest и SetProjectDeviceRequest в
// backend/ClaudeHomeServer/Controllers/ProjectsController.cs. Лишний ключ бэк молча
// проглотит (так путь локального проекта уже терялся без ошибки), поэтому ловим здесь.
const CREATE_PROJECT_KEYS = [
  'name', 'rootPath', 'createDirectory', 'groupId',
  'enableGit', 'gitAutoCommit', 'gitAutoPush', 'color', 'deviceId',
];
const SET_DEVICE_KEYS = ['deviceId', 'rootPath'];

vi.mock('../idb', () => ({ idbGet: vi.fn(), idbSet: vi.fn() }));

function storageStub() {
  const m = new Map<string, string>([['cc_token', 'tok']]);
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

let fetchMock: ReturnType<typeof vi.fn>;
let api: typeof import('../api').api;

beforeEach(async () => {
  vi.resetModules();
  fetchMock = vi.fn().mockResolvedValue({
    ok: true, status: 200, statusText: 'OK',
    json: async () => ({ id: 'p1' }), text: async () => '{"id":"p1"}',
  });
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub());
  vi.stubGlobal('sessionStorage', storageStub());
  api = (await import('../api')).api;
});

afterEach(() => { vi.unstubAllGlobals(); });

function sentBody(): Record<string, unknown> {
  expect(fetchMock).toHaveBeenCalledTimes(1);
  const init = fetchMock.mock.calls[0][1] as RequestInit;
  return JSON.parse(init.body as string) as Record<string, unknown>;
}

describe('api.projects — контракт 3.1 с бэком', () => {
  it('create локального проекта шлёт путь на устройстве в rootPath', async () => {
    await api.projects.create('Demo', null, false, null, undefined, null,
      { deviceId: 'dev-1', rootPath: 'C:\\Sources\\demo' });
    const body = sentBody();
    for (const k of Object.keys(body)) expect(CREATE_PROJECT_KEYS).toContain(k);
    expect(body.rootPath).toBe('C:\\Sources\\demo');
    expect(body.deviceId).toBe('dev-1');
  });

  it('create серверного проекта не шлёт deviceId и оставляет rootPath аргумента', async () => {
    await api.projects.create('Demo', '/srv/demo', false, null,
      { enableGit: true, gitAutoCommit: false, gitAutoPush: false }, null);
    const body = sentBody();
    expect(Object.keys(body).sort()).toEqual([...CREATE_PROJECT_KEYS].filter(k => k !== 'deviceId').sort());
    expect(body.rootPath).toBe('/srv/demo');
  });

  it('setDevice шлёт ровно { deviceId, rootPath }', async () => {
    await api.projects.setDevice('p1', { deviceId: 'dev-2', rootPath: '/home/u/demo' });
    const body = sentBody();
    expect(Object.keys(body).sort()).toEqual([...SET_DEVICE_KEYS].sort());
    expect(body).toEqual({ deviceId: 'dev-2', rootPath: '/home/u/demo' });
    expect(fetchMock.mock.calls[0][0]).toContain('/projects/p1/device');
  });

  it('setDevice при отвязке шлёт только deviceId: null', async () => {
    await api.projects.setDevice('p1', { deviceId: null });
    expect(sentBody()).toEqual({ deviceId: null });
  });
});
