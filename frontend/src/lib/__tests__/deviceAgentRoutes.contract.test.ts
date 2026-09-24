import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DEVICE_AGENT_SHARED, DEVICE_AGENT_UNSUPPORTED, agentServesPath } from '../deviceAgentRoutes';

// Контракт фронт/бэк: списки маршрутов агента на фронте — ровно DeviceAgentRoutes.Shared и
// .Unsupported из ProjectFilesApiContract.cs. Бэк сам сторожит, что каждый серверный маршрут
// стоит ровно в одном списке; этот тест держит фронт в шаге за ним.

const here = dirname(fileURLToPath(import.meta.url));
const csFile = resolve(here, '../../../../backend/ClaudeHomeServer.Core/Protocol/ProjectFilesApiContract.cs');

function readBackendList(name: 'Shared' | 'Unsupported'): string[] {
  const src = readFileSync(csFile, 'utf-8');
  const start = src.indexOf(`ProjectApiRoute> ${name} =`);
  expect(start, `список ${name} не найден в ProjectFilesApiContract.cs`).toBeGreaterThan(-1);
  const end = src.indexOf('];', start);
  expect(end, `конец списка ${name} не найден`).toBeGreaterThan(start);
  return [...src.slice(start, end).matchAll(/new\("([A-Z]+)",\s*"([^"]+)"\)/g)].map(m => `${m[1]} ${m[2]}`);
}

describe('контракт маршрутов агента устройства фронт/бэк', () => {
  it('регэксп разбора не сломан: списки C# непустые', () => {
    expect(readBackendList('Shared').length).toBeGreaterThan(0);
    expect(readBackendList('Unsupported').length).toBeGreaterThan(0);
  });

  it('DEVICE_AGENT_SHARED совпадает с DeviceAgentRoutes.Shared', () => {
    expect([...DEVICE_AGENT_SHARED].sort()).toEqual(readBackendList('Shared').sort());
  });

  it('DEVICE_AGENT_UNSUPPORTED совпадает с DeviceAgentRoutes.Unsupported', () => {
    expect([...DEVICE_AGENT_UNSUPPORTED].sort()).toEqual(readBackendList('Unsupported').sort());
  });
});

describe('agentServesPath', () => {
  it('узнаёт общие маршруты по методу и пути', () => {
    expect(agentServesPath('GET', 'files/tree')).toBe(true);
    expect(agentServesPath('delete', 'files')).toBe(true);
    expect(agentServesPath('POST', 'git/commit')).toBe(true);
  });

  it('метод входит в маршрут: GET и POST одного пути различаются', () => {
    expect(agentServesPath('GET', 'git/branches')).toBe(true);
    expect(agentServesPath('POST', 'git/branches')).toBe(false);
  });

  it('неподдержанные и незнакомые маршруты — нет', () => {
    expect(agentServesPath('POST', 'files/upload')).toBe(false);
    expect(agentServesPath('GET', 'git/commits/abc123/diff')).toBe(false);
    expect(agentServesPath('GET', 'git/whatever')).toBe(false);
  });
});
