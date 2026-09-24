import { describe, it, expect } from 'vitest';
import {
  getProjectCapabilities,
  isFeatureAvailable,
  featureReason,
  hostFor,
  canRunTurn,
  isLocalProject,
  deviceOfflineLabel,
  projectFilesRoute,
  projectSupportsRoute,
  RELAY_ROUTE_REASON,
  DEVICE_ROUTE_REASON,
  projectRouteReason,
} from '../projectCapabilities';
import type { Project, ProjectCapabilitiesView, ProjectFeatureKey, ProjectDeviceView } from '../../types';
import { ProjectFeature } from '../../types';

// Шаблонный серверный проект без capabilities (старый бэк) — для проверки дефолта
function emptyProject(): Project {
  return {
    id: 'p1', name: 'Серверный', rootPath: '/srv/p',
    createdAt: '', updatedAt: '',
  };
}

// Шаблонные capabilities для трёх сценариев
function serverCaps(): ProjectCapabilitiesView {
  return {
    host: 'server', deviceId: null,
    files: { host: 'server', available: true, reason: null, features: [ProjectFeature.Files, ProjectFeature.Diff, ProjectFeature.Git, ProjectFeature.Terminal, ProjectFeature.DevServers, ProjectFeature.Skills, ProjectFeature.Attachments] },
    platform: { host: 'server', available: true, reason: null, features: [ProjectFeature.Chat, ProjectFeature.Tasks, ProjectFeature.Personas] },
    serverContent: { host: 'server', available: true, reason: null, features: [ProjectFeature.Knowledge, ProjectFeature.CodeGraph, ProjectFeature.Dossiers, ProjectFeature.Docs, ProjectFeature.MapHygiene] },
    exec: { available: true, reason: null },
  };
}

function offlineDeviceCaps(): ProjectCapabilitiesView {
  return {
    host: 'device', deviceId: 'd1',
    files: { host: 'device', available: false, reason: 'Устройство офлайн', features: [ProjectFeature.Files, ProjectFeature.Diff, ProjectFeature.Git, ProjectFeature.Terminal, ProjectFeature.DevServers, ProjectFeature.Skills, ProjectFeature.Attachments] },
    platform: { host: 'server', available: true, reason: null, features: [ProjectFeature.Chat, ProjectFeature.Tasks, ProjectFeature.Personas] },
    serverContent: { host: 'off', available: false, reason: 'Нужен контент проекта на сервере', features: [ProjectFeature.Knowledge, ProjectFeature.CodeGraph, ProjectFeature.Dossiers, ProjectFeature.Docs, ProjectFeature.MapHygiene] },
    exec: { available: false, reason: 'Устройство офлайн' },
  };
}

function revokedDeviceCaps(): ProjectCapabilitiesView {
  return {
    host: 'device', deviceId: 'd1',
    files: { host: 'off', available: false, reason: 'Устройство проекта не найдено или отозвано', features: [] },
    platform: { host: 'server', available: true, reason: null, features: [ProjectFeature.Chat, ProjectFeature.Tasks, ProjectFeature.Personas] },
    serverContent: { host: 'off', available: false, reason: 'Нужен контент проекта на сервере', features: [] },
    exec: { available: false, reason: 'Устройство проекта не найдено или отозвано' },
  };
}

function deviceView(): ProjectDeviceView {
  return { id: 'd1', name: 'Workstation', online: false, platform: 'linux', agentVersion: '1.2.3', harnessReady: false, harnessProblem: 'обновите агента' };
}

function localProject(caps: ProjectCapabilitiesView, device: ProjectDeviceView | null = deviceView()): Project {
  return {
    id: 'p1', name: 'Локальный', rootPath: '/srv/p',
    createdAt: '', updatedAt: '',
    deviceId: caps.deviceId,
    device,
    capabilities: caps,
  };
}

describe('getProjectCapabilities — дефолт для серверного проекта без матрицы', () => {
  it('без capabilities возвращает полный серверный набор', () => {
    const cap = getProjectCapabilities(emptyProject());
    expect(cap.host).toBe('server');
    expect(cap.files.host).toBe('server');
    expect(cap.files.available).toBe(true);
    expect(isFeatureAvailable(emptyProject(), ProjectFeature.Files)).toBe(true);
    expect(isFeatureAvailable(emptyProject(), ProjectFeature.Knowledge)).toBe(true);
    expect(isFeatureAvailable(emptyProject(), ProjectFeature.Tasks)).toBe(true);
    expect(canRunTurn(emptyProject()).available).toBe(true);
  });

  it('null/undefined проект не падает и возвращает серверный дефолт', () => {
    expect(getProjectCapabilities(null).host).toBe('server');
    expect(getProjectCapabilities(undefined).host).toBe('server');
    expect(canRunTurn(null).available).toBe(true);
  });
});

describe('isLocalProject — единственная функция проверки локальности', () => {
  it('null/undefined → false', () => {
    expect(isLocalProject(null)).toBe(false);
    expect(isLocalProject(undefined)).toBe(false);
  });
  it('серверный проект (deviceId отсутствует) → false', () => {
    expect(isLocalProject(emptyProject())).toBe(false);
  });
  it('локальный проект с deviceId → true', () => {
    expect(isLocalProject(localProject(offlineDeviceCaps()))).toBe(true);
  });
});

describe('isFeatureAvailable — матрица capabilities', () => {
  it('локальный с офлайн-устройством: файлы и серверный контент недоступны, платформа доступна', () => {
    const p = localProject(offlineDeviceCaps());
    expect(isFeatureAvailable(p, ProjectFeature.Files)).toBe(false);
    expect(isFeatureAvailable(p, ProjectFeature.Git)).toBe(false);
    expect(isFeatureAvailable(p, ProjectFeature.Terminal)).toBe(false);
    expect(isFeatureAvailable(p, ProjectFeature.DevServers)).toBe(false);
    expect(isFeatureAvailable(p, ProjectFeature.Knowledge)).toBe(false);
    expect(isFeatureAvailable(p, ProjectFeature.CodeGraph)).toBe(false);
    expect(isFeatureAvailable(p, ProjectFeature.Tasks)).toBe(true);
    expect(isFeatureAvailable(p, ProjectFeature.Personas)).toBe(true);
  });

  it('локальный с отозванным устройством: вся группа files/serverContent выключена', () => {
    const p = localProject(revokedDeviceCaps(), null);
    expect(isFeatureAvailable(p, ProjectFeature.Files)).toBe(false);
    expect(isFeatureAvailable(p, ProjectFeature.Knowledge)).toBe(false);
  });

  it('незнакомый ключ возможности → false (защита от тихих регрессий)', () => {
    const p = localProject(offlineDeviceCaps());
    expect(isFeatureAvailable(p, 'unknown' as ProjectFeatureKey)).toBe(false);
  });
});

describe('featureReason — текст причины для UI', () => {
  it('null если доступно', () => {
    expect(featureReason(emptyProject(), ProjectFeature.Files)).toBeNull();
  });
  it('готовый текст причины когда недоступно', () => {
    const p = localProject(offlineDeviceCaps());
    expect(featureReason(p, ProjectFeature.Files)).toBe('Устройство офлайн');
    expect(featureReason(p, ProjectFeature.Knowledge)).toBe('Нужен контент проекта на сервере');
  });
  it('ключ вне групп — явный текст «не в этой версии» (защита от тихих регрессий)', () => {
    // Не null: не показывать человеку «нет причины», когда мы реально не знаем ключа.
    // Иначе новая фича с незнакомым ключом в DTO будет показывать «доступно» молча.
    expect(featureReason(emptyProject(), 'unknown' as ProjectFeatureKey)).toBe('Возможность недоступна в этой версии');
  });
});

describe('hostFor — где работает ключ', () => {
  it('локальный с офлайн: files → device', () => {
    expect(hostFor(localProject(offlineDeviceCaps()), ProjectFeature.Files)).toBe('device');
  });
  it('локальный: serverContent → off', () => {
    expect(hostFor(localProject(offlineDeviceCaps()), ProjectFeature.Knowledge)).toBe('off');
  });
  it('локальный: platform всегда → server', () => {
    expect(hostFor(localProject(offlineDeviceCaps()), ProjectFeature.Tasks)).toBe('server');
  });
  it('серверный: всё → server', () => {
    expect(hostFor(emptyProject(), ProjectFeature.Knowledge)).toBe('server');
  });
});

describe('canRunTurn — можно ли отправить ход', () => {
  it('серверный проект → можно', () => {
    expect(canRunTurn(emptyProject()).available).toBe(true);
    expect(canRunTurn(emptyProject()).reason).toBeNull();
  });
  it('локальный офлайн → нельзя, причина «Устройство офлайн»', () => {
    const r = canRunTurn(localProject(offlineDeviceCaps()));
    expect(r.available).toBe(false);
    expect(r.reason).toBe('Устройство офлайн');
  });
  it('локальный отозван → нельзя, причина «не найдено»', () => {
    const r = canRunTurn(localProject(revokedDeviceCaps(), null));
    expect(r.available).toBe(false);
    expect(r.reason).toBe('Устройство проекта не найдено или отозвано');
  });
});

describe('deviceOfflineLabel — баннер в композере', () => {
  it('null если нет device', () => {
    expect(deviceOfflineLabel(emptyProject())).toBeNull();
  });
  it('"Устройство офлайн" когда device.online=false', () => {
    expect(deviceOfflineLabel(localProject(offlineDeviceCaps()))).toBe('Устройство офлайн');
  });
  it('готовый текст проблемы харнеса когда harnessReady=false и online=true', () => {
    const cap: ProjectCapabilitiesView = {
      ...offlineDeviceCaps(),
      exec: { available: false, reason: 'Харнес устройства не готов' },
    };
    const p: Project = {
      ...localProject(cap),
      device: { ...deviceView(), online: true, harnessReady: false, harnessProblem: 'обновите агента' },
    };
    expect(deviceOfflineLabel(p)).toBe('обновите агента');
  });
  it('null если device онлайн и харнес готов', () => {
    const cap: ProjectCapabilitiesView = {
      ...offlineDeviceCaps(),
      exec: { available: true, reason: null },
    };
    const p: Project = {
      ...localProject(cap),
      device: { ...deviceView(), online: true, harnessReady: true, harnessProblem: null },
    };
    expect(deviceOfflineLabel(p)).toBeNull();
  });
});

describe('локальный проект с живым устройством — что умеет браузер через агента (4.4)', () => {
  function onlineDeviceCaps(): ProjectCapabilitiesView {
    const c = offlineDeviceCaps();
    return { ...c, files: { ...c.files, available: true, reason: null } };
  }

  it('вся группа файлов доступна через агента: терминал, сервисы, навыки и вложения тоже (4.4б)', () => {
    const p = localProject(onlineDeviceCaps());
    for (const f of [ProjectFeature.Files, ProjectFeature.Git, ProjectFeature.Terminal, ProjectFeature.DevServers,
      ProjectFeature.Skills, ProjectFeature.Attachments]) {
      expect(isFeatureAvailable(p, f)).toBe(true);
      expect(featureReason(p, f)).toBeNull();
    }
  });

  it('внешний доступ к сервису у локального проекта скрыт с причиной, у серверного есть', () => {
    const local = localProject(onlineDeviceCaps());
    expect(projectSupportsRoute(local, 'POST preview/external-link')).toBe(false);
    expect(projectRouteReason(local, 'POST preview/external-link')).toMatch(/поддомен сервера/);
    expect(projectRouteReason(local, 'POST git/push')).toBe(DEVICE_ROUTE_REASON);
    expect(projectRouteReason(local, 'POST preview/start')).toBeNull();
    expect(projectRouteReason(emptyProject(), 'POST preview/external-link')).toBeNull();
  });

  it('офлайн-устройство: терминал недоступен с причиной группы', () => {
    expect(featureReason(localProject(offlineDeviceCaps()), ProjectFeature.Terminal)).toBe('Устройство офлайн');
  });

  it('маршрут файлов: локальный — агент, серверный — сервер', () => {
    expect(projectFilesRoute(localProject(onlineDeviceCaps()))).toBe('agent');
    expect(projectFilesRoute(emptyProject())).toBe('server');
    expect(projectFilesRoute(null)).toBe('server');
  });

  it('маршрут файлов с другого устройства — ретранслятор; у серверного присутствие ничего не меняет (5.2)', () => {
    const local = localProject(onlineDeviceCaps());
    expect(projectFilesRoute(local, 'unknown')).toBe('agent');
    expect(projectFilesRoute(local, 'here')).toBe('agent');
    expect(projectFilesRoute(local, 'elsewhere')).toBe('relay');
    expect(projectFilesRoute(emptyProject(), 'elsewhere')).toBe('server');
  });

  it('с другого устройства запись скрыта с причиной «только просмотр», чтение есть (5.2)', () => {
    const local = localProject(onlineDeviceCaps());
    expect(projectSupportsRoute(local, 'GET files/tree', 'elsewhere')).toBe(true);
    expect(projectSupportsRoute(local, 'GET git/commits/{sha}/diff', 'elsewhere')).toBe(true);
    expect(projectSupportsRoute(local, 'PUT files/content', 'elsewhere')).toBe(false);
    expect(projectSupportsRoute(local, 'POST git/commit', 'elsewhere')).toBe(false);
    expect(projectRouteReason(local, 'DELETE files', 'elsewhere')).toBe(RELAY_ROUTE_REASON);
    expect(projectRouteReason(local, 'GET files', 'elsewhere')).toBeNull();
  });

  it('действие без маршрута у агента скрыто только у локального проекта', () => {
    expect(projectSupportsRoute(localProject(onlineDeviceCaps()), 'POST git/push')).toBe(false);
    expect(projectSupportsRoute(localProject(onlineDeviceCaps()), 'POST git/commit')).toBe(true);
    expect(projectSupportsRoute(emptyProject(), 'POST git/push')).toBe(true);
  });
});

describe('группа транскрипта CLI (ADR-016 3.7)', () => {
  const transcriptOff = 'Транскрипт разговора локального проекта живёт на устройстве';

  it('бэк без группы транскрипта — механики доступны, как у серверного проекта', () => {
    const project = localProject(serverCaps(), null);
    expect(isFeatureAvailable(project, ProjectFeature.ChatBranch)).toBe(true);
    expect(featureReason(project, ProjectFeature.WorkflowView)).toBeNull();
  });

  it('у локального проекта ветка, workflow и живые субагенты выключены с причиной', () => {
    const project = localProject({
      ...offlineDeviceCaps(),
      transcript: {
        host: 'off', available: false, reason: transcriptOff,
        features: [ProjectFeature.LiveSubagents, ProjectFeature.WorkflowView, ProjectFeature.ChatBranch],
      },
    });
    for (const f of [ProjectFeature.LiveSubagents, ProjectFeature.WorkflowView, ProjectFeature.ChatBranch]) {
      expect(isFeatureAvailable(project, f)).toBe(false);
      expect(featureReason(project, f)).toBe(transcriptOff);
      expect(hostFor(project, f)).toBe('off');
    }
  });
});
