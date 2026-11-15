import { describe, it, expect, vi, beforeEach } from 'vitest';

// Регресс (ADR-040 Ф2-9): гейт устройства считал файлы «Личного» устройственными и панель
// личного чата вечно показывала «Ищем агент AI Home на этом компьютере».
// Мок lib/offline — как в personalFiles.test: цепочка projectRequest → request настоящая.
const { request } = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request };
});

import { noteProject, projectRequest, routeWaitsDeviceAgent } from '../../../lib/deviceAgent';
import { projectFilesRoute } from '../../../lib/projectCapabilities';
import { personalFilesProject } from '../personalFiles';

beforeEach(() => {
  request.mockReset();
  request.mockResolvedValue([]);
  noteProject(personalFilesProject('chat-1'));
});

describe('устройственный гейт для файлов «Личного»', () => {
  it('маршрут personal агента устройства не ждёт', () => {
    expect(routeWaitsDeviceAgent('personal')).toBe(false);
  });

  it('псевдо-проект личных файлов даёт маршрут personal — и тот тоже не ждёт агента', () => {
    const route = projectFilesRoute(personalFilesProject('chat-1'));
    expect(route).toBe('personal');
    expect(routeWaitsDeviceAgent(route)).toBe(false);
  });

  it('локальный проект и ретранслятор агента ждут, серверный — нет', () => {
    expect(routeWaitsDeviceAgent('agent')).toBe(true);
    expect(routeWaitsDeviceAgent('relay')).toBe(true);
    expect(routeWaitsDeviceAgent('server')).toBe(false);
  });
});

describe('запрос личных файлов не уходит к агенту устройства', () => {
  it('адрес проекта с личным маршрутом обслуживает сервер, билет агента не берётся', async () => {
    await projectRequest('/projects/personal-files%3Achat-1/files?path=').catch(() => undefined);
    expect(request.mock.calls.map(c => c[0])).toEqual(['/projects/personal-files%3Achat-1/files?path=']);
    expect(request.mock.calls.map(c => String(c[0]))).not.toContain(expect.stringContaining('device-agent/ticket'));
  });
});
