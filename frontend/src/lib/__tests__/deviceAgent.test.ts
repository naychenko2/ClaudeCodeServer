import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { Project, ProjectCapabilitiesView } from '../../types';
import { ProjectFeature } from '../../types';

// Сервер (request из offline.ts) подменяем целиком: билет и серверные маршруты
const { request } = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../offline', () => ({ request }));

import {
  noteProject, projectRequest, probeDeviceAgent, getDeviceAgentStatus, agentStreamUrl,
  agentHubTicket, agentHubUrl, agentPreviewUrl, uploadAgentAttachment,
  DeviceAgentError, resetDeviceAgentForTests,
} from '../deviceAgent';

const FILE_FEATURES = [ProjectFeature.Files, ProjectFeature.Diff, ProjectFeature.Git, ProjectFeature.FileWatcher, ProjectFeature.Terminal];

function caps(host: 'server' | 'device'): ProjectCapabilitiesView {
  return {
    host, deviceId: host === 'device' ? 'd1' : null,
    files: { host, available: true, reason: null, features: FILE_FEATURES },
    platform: { host: 'server', available: true, reason: null, features: [ProjectFeature.Chat] },
    serverContent: { host: host === 'device' ? 'off' : 'server', available: host === 'server', reason: null, features: [ProjectFeature.Knowledge] },
    exec: { available: true, reason: null },
  };
}

function project(id: string, host: 'server' | 'device', extra: Partial<Project> = {}): Project {
  return { id, name: id, rootPath: '/p', createdAt: '', updatedAt: '', capabilities: caps(host), ...extra };
}

function res(status: number, body?: unknown) {
  return {
    ok: status >= 200 && status < 300, status, statusText: `HTTP ${status}`,
    json: async () => body, text: async () => (body === undefined ? '' : JSON.stringify(body)),
  };
}

const ticketResponse = (ticket = 't-1') => ({
  ticket, deviceId: 'd1', expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(), port: 47318, header: 'X-Agent-Ticket',
});

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  resetDeviceAgentForTests();
  request.mockReset();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('projectRequest — сервер или агент', () => {
  it('серверный проект идёт на сервер как раньше', async () => {
    noteProject(project('s1', 'server'));
    request.mockResolvedValue([]);
    await projectRequest('/projects/s1/files/tree?path=');
    expect(request).toHaveBeenCalledWith('/projects/s1/files/tree?path=', undefined);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('неизвестный проект — сервер (по умолчанию ничего не уезжает на localhost)', async () => {
    request.mockResolvedValue([]);
    await projectRequest('/projects/unknown/git/status');
    expect(request).toHaveBeenCalledTimes(1);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('локальный проект идёт в агента с билетом и без JWT сервера', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse());
    fetchMock.mockResolvedValue(res(200, [{ name: 'a.txt' }]));

    const r = await projectRequest<unknown[]>('/projects/l1/files/tree?path=src');

    expect(r).toEqual([{ name: 'a.txt' }]);
    expect(request).toHaveBeenCalledWith('/projects/l1/device-agent/ticket', expect.objectContaining({ method: 'POST' }));
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://127.0.0.1:47318/api/projects/l1/files/tree?path=src');
    expect(init.headers['X-Agent-Ticket']).toBe('t-1');
    expect(init.headers.Authorization).toBeUndefined();
    expect(getDeviceAgentStatus('l1').kind).toBe('ready');
  });

  it('тело мутации едет в агента как есть', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse());
    fetchMock.mockResolvedValue(res(200, { sha: 'abc' }));

    await projectRequest('/projects/l1/git/commit', { method: 'POST', body: JSON.stringify({ message: 'm' }) });

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://127.0.0.1:47318/api/projects/l1/git/commit');
    expect(init.method).toBe('POST');
    expect(init.body).toBe('{"message":"m"}');
    expect(init.headers['Content-Type']).toBe('application/json');
  });

  it('билет переиспользуется, пока свежий', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse());
    fetchMock.mockResolvedValue(res(200, {}));
    await projectRequest('/projects/l1/git/status');
    await projectRequest('/projects/l1/git/status');
    expect(request).toHaveBeenCalledTimes(1);
  });

  it('действие, которого агент не умеет, отказывает до сети понятным текстом', async () => {
    noteProject(project('l1', 'device'));
    await expect(projectRequest('/projects/l1/git/push', { method: 'POST' }))
      .rejects.toMatchObject({ kind: 'unsupported' });
    expect(request).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('showHidden проекта едет параметром: настроек проекта у агента нет', async () => {
    noteProject(project('l1', 'device', { showHiddenFiles: true }));
    request.mockResolvedValue(ticketResponse());
    fetchMock.mockResolvedValue(res(200, []));
    await projectRequest('/projects/l1/files?path=');
    expect(fetchMock.mock.calls[0][0]).toBe('http://127.0.0.1:47318/api/projects/l1/files?path=&showHidden=true');
  });

  it('401 агента — один перевыпуск билета и повтор', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValueOnce(ticketResponse('old')).mockResolvedValueOnce(ticketResponse('new'));
    fetchMock.mockResolvedValueOnce(res(401, { error: 'истёк' })).mockResolvedValueOnce(res(200, {}));

    await projectRequest('/projects/l1/git/status');

    expect(request).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][1].headers['X-Agent-Ticket']).toBe('new');
  });

  it('ошибка агента несёт status и текст, как у request()', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse());
    fetchMock.mockResolvedValue(res(409, { error: 'exists' }));
    await expect(projectRequest('/projects/l1/files/create', { method: 'POST', body: '{}' }))
      .rejects.toMatchObject({ status: 409, message: 'exists' });
  });
});

describe('probeDeviceAgent — понятные состояния вместо пустой панели', () => {
  it('агент не отвечает — unreachable', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse());
    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));
    await probeDeviceAgent('l1');
    expect(getDeviceAgentStatus('l1').kind).toBe('unreachable');
  });

  it('сервер не выдал билет — refused с текстом сервера', async () => {
    noteProject(project('l1', 'device'));
    request.mockRejectedValue(Object.assign(new Error('Устройство офлайн'), { status: 409 }));
    await probeDeviceAgent('l1');
    expect(getDeviceAgentStatus('l1')).toEqual({ kind: 'refused', reason: 'Устройство офлайн' });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('агент другого устройства не принял билет — rejected', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse());
    fetchMock.mockResolvedValue(res(401, { error: 'Билет к агенту недействителен или истёк' }));
    await probeDeviceAgent('l1');
    expect(getDeviceAgentStatus('l1')).toEqual({ kind: 'rejected', reason: 'Билет к агенту недействителен или истёк' });
  });

  it('агент ответил — ready', async () => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse('tk'));
    fetchMock.mockResolvedValue(res(200, []));
    await probeDeviceAgent('l1');
    expect(getDeviceAgentStatus('l1').kind).toBe('ready');
  });

  it('DeviceAgentError различает виды отказа', () => {
    expect(new DeviceAgentError('unreachable', 'x').kind).toBe('unreachable');
  });
});

describe('agentStreamUrl — единственная точка URL потока', () => {
  const streamTicket = (st: string, ttlMs = 60_000) => res(200, { streamTicket: st, expiresAt: new Date(Date.now() + ttlMs).toISOString() });

  beforeEach(() => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse('main-ticket'));
  });
  afterEach(() => { vi.useRealTimers(); });

  it('узкий билет запрашивается у агента по основному в заголовке, в URL едет только узкий', async () => {
    fetchMock.mockResolvedValue(streamTicket('narrow-1'));
    const url = await agentStreamUrl('l1', 'img/a b.png');

    const [reqUrl, init] = fetchMock.mock.calls[0];
    expect(reqUrl).toBe('http://127.0.0.1:47318/api/projects/l1/agent/stream-ticket');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({ path: 'img/a b.png' });
    expect(init.headers['X-Agent-Ticket']).toBe('main-ticket');

    const u = new URL(url);
    expect(u.origin + u.pathname).toBe('http://127.0.0.1:47318/api/projects/l1/files/stream');
    expect(u.searchParams.get('path')).toBe('img/a b.png');
    expect(u.searchParams.get('streamTicket')).toBe('narrow-1');
    expect(url).not.toContain('main-ticket');
  });

  it('живой билет на тот же путь переиспользуется, на другой путь — свой', async () => {
    fetchMock.mockResolvedValueOnce(streamTicket('narrow-a')).mockResolvedValueOnce(streamTicket('narrow-b'));
    const a1 = await agentStreamUrl('l1', 'a.mp4');
    const a2 = await agentStreamUrl('l1', 'a.mp4');
    const b = await agentStreamUrl('l1', 'b.mp4');
    expect(a2).toBe(a1);
    expect(b).toContain('streamTicket=narrow-b');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('истёкший за 60 с билет перевыпускается', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    fetchMock.mockResolvedValueOnce(streamTicket('narrow-1')).mockResolvedValueOnce(streamTicket('narrow-2'));
    const first = await agentStreamUrl('l1', 'a.mp4');
    vi.setSystemTime(Date.now() + 61_000);
    const second = await agentStreamUrl('l1', 'a.mp4');
    expect(first).toContain('narrow-1');
    expect(second).toContain('narrow-2');
  });

  it('force перевыпускает даже живой билет', async () => {
    fetchMock.mockResolvedValueOnce(streamTicket('narrow-1')).mockResolvedValueOnce(streamTicket('narrow-2'));
    await agentStreamUrl('l1', 'a.mp4');
    expect(await agentStreamUrl('l1', 'a.mp4', true)).toContain('narrow-2');
  });

  it('путь вне корня — 403 агента доходит до вызывающего', async () => {
    fetchMock.mockResolvedValue(res(403, { error: 'Путь вне проекта' }));
    await expect(agentStreamUrl('l1', '../x')).rejects.toMatchObject({ status: 403, message: 'Путь вне проекта' });
  });
});

describe('сервисы, превью и навыки локального проекта (4.4б)', () => {
  beforeEach(() => {
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse());
  });

  it('сервисы и навыки идут в агента, серверный проект — на сервер', async () => {
    fetchMock.mockResolvedValue(res(200, { services: [], activeServiceId: null }));
    await projectRequest('/projects/l1/services');
    await projectRequest('/projects/l1/preview/start', { method: 'POST', body: '{}' });
    await projectRequest('/projects/l1/skills');
    await projectRequest('/projects/l1/agents/reviewer', { method: 'PUT', body: '{}' });
    expect(fetchMock.mock.calls.map(c => c[0])).toEqual([
      'http://127.0.0.1:47318/api/projects/l1/services',
      'http://127.0.0.1:47318/api/projects/l1/preview/start',
      'http://127.0.0.1:47318/api/projects/l1/skills',
      'http://127.0.0.1:47318/api/projects/l1/agents/reviewer',
    ]);

    noteProject(project('s1', 'server'));
    request.mockResolvedValue({ services: [] });
    await projectRequest('/projects/s1/services');
    expect(request).toHaveBeenLastCalledWith('/projects/s1/services', undefined);
  });

  it('внешний доступ к сервису у локального проекта отказывает до сети', async () => {
    await expect(projectRequest('/projects/l1/preview/external-link', { method: 'POST', body: '{}' }))
      .rejects.toMatchObject({ kind: 'unsupported' });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('билет хаба выдаёт агент по основному в заголовке; адрес хаба — на порту агента', async () => {
    fetchMock.mockResolvedValue(res(200, { hubTicket: 'hub-1', expiresAt: new Date(Date.now() + 60_000).toISOString() }));
    expect(await agentHubTicket('l1')).toBe('hub-1');
    // negotiate и WebSocket берут билет подряд — второй раз агент не спрашиваем
    expect(await agentHubTicket('l1')).toBe('hub-1');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://127.0.0.1:47318/api/projects/l1/agent/hub-ticket');
    expect(init.method).toBe('POST');
    expect(init.headers['X-Agent-Ticket']).toBe('t-1');
    expect(await agentHubUrl('l1')).toBe('http://127.0.0.1:47318/hubs/agent');
  });

  it('истекающий билет хаба перевыпускается', async () => {
    fetchMock
      .mockResolvedValueOnce(res(200, { hubTicket: 'hub-old', expiresAt: new Date(Date.now() + 5_000).toISOString() }))
      .mockResolvedValueOnce(res(200, { hubTicket: 'hub-new', expiresAt: new Date(Date.now() + 60_000).toISOString() }));
    expect(await agentHubTicket('l1')).toBe('hub-old');
    expect(await agentHubTicket('l1')).toBe('hub-new');
  });

  it('адрес превью — готовый адрес агента, переиспользуется до конца срока', async () => {
    const url = 'http://127.0.0.1:47319/preview/l1/?previewTicket=p-1';
    fetchMock.mockResolvedValue(res(200, { previewTicket: 'p-1', expiresAt: new Date(Date.now() + 8 * 3600_000).toISOString(), url }));
    expect(await agentPreviewUrl('l1')).toBe(url);
    expect(await agentPreviewUrl('l1')).toBe(url);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('http://127.0.0.1:47318/api/projects/l1/agent/preview-ticket');
    await agentPreviewUrl('l1', true);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('вложение уходит формой в агента, без JSON-заголовка', async () => {
    fetchMock.mockResolvedValue(res(200, { path: '.cc-attachments/g/a.png' }));
    const file = new File(['x'], 'a.png', { type: 'image/png' });
    expect(await uploadAgentAttachment('l1', file)).toEqual({ path: '.cc-attachments/g/a.png' });
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://127.0.0.1:47318/api/projects/l1/agent/attachments');
    expect(init.body).toBeInstanceOf(FormData);
    expect((init.body as FormData).get('file')).toBeInstanceOf(File);
    expect(init.headers['Content-Type']).toBeUndefined();
    expect(init.headers['X-Agent-Ticket']).toBe('t-1');
  });
});

describe('api.files.fileUrl у локального проекта', () => {
  it('не отдаёт URL с основным билетом проекта', async () => {
    const { api } = await import('../api');
    noteProject(project('l1', 'device'));
    request.mockResolvedValue(ticketResponse('main-ticket'));
    fetchMock.mockResolvedValue(res(200, []));
    await probeDeviceAgent('l1');
    const url = api.files.fileUrl('l1', 'img/a.png');
    expect(url).not.toContain('main-ticket');
    expect(url).not.toContain('ticket=');
  });
});
