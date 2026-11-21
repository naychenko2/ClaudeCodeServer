import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
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
  childSubjectCapabilities,
  projectPanelKeysAllowed,
  PANEL_FEATURE,
} from '../projectCapabilities';
import type { Project, ProjectCapabilitiesView, ProjectFeatureKey, ProjectDeviceView } from '../../types';
import { ProjectFeature } from '../../types';
import { WORKSPACE_KEYS, childWorkspaceKeys } from '../../pages/workspace/panelCatalog';
import type { ChildSurface } from '../../features/account/types';

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
    files: { host: 'device', available: false, reason: 'Устройство проекта не в сети', features: [ProjectFeature.Files, ProjectFeature.Diff, ProjectFeature.Git, ProjectFeature.Terminal, ProjectFeature.DevServers, ProjectFeature.Skills, ProjectFeature.Attachments] },
    platform: { host: 'server', available: true, reason: null, features: [ProjectFeature.Chat, ProjectFeature.Tasks, ProjectFeature.Personas] },
    serverContent: { host: 'off', available: false, reason: 'У локального проекта недоступно: его файлы лежат на устройстве, а не на сервере', features: [ProjectFeature.Knowledge, ProjectFeature.CodeGraph, ProjectFeature.Dossiers, ProjectFeature.Docs, ProjectFeature.MapHygiene] },
    exec: { available: false, reason: 'Устройство проекта не в сети' },
  };
}

function revokedDeviceCaps(): ProjectCapabilitiesView {
  return {
    host: 'device', deviceId: 'd1',
    files: { host: 'off', available: false, reason: 'Устройство проекта не найдено или отозвано', features: [] },
    platform: { host: 'server', available: true, reason: null, features: [ProjectFeature.Chat, ProjectFeature.Tasks, ProjectFeature.Personas] },
    serverContent: { host: 'off', available: false, reason: 'У локального проекта недоступно: его файлы лежат на устройстве, а не на сервере', features: [] },
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
    expect(featureReason(p, ProjectFeature.Files)).toBe('Устройство проекта не в сети');
    expect(featureReason(p, ProjectFeature.Knowledge)).toBe('У локального проекта недоступно: его файлы лежат на устройстве, а не на сервере');
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
  it('локальный офлайн → нельзя, причина «Устройство проекта не в сети»', () => {
    const r = canRunTurn(localProject(offlineDeviceCaps()));
    expect(r.available).toBe(false);
    expect(r.reason).toBe('Устройство проекта не в сети');
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
  it('"Устройство проекта не в сети" когда device.online=false', () => {
    expect(deviceOfflineLabel(localProject(offlineDeviceCaps()))).toBe('Устройство проекта не в сети');
  });
  it('готовый текст проблемы агента устройства, когда harnessReady=false и online=true', () => {
    const cap: ProjectCapabilitiesView = {
      ...offlineDeviceCaps(),
      exec: { available: false, reason: 'Агент устройства не готов' },
    };
    const p: Project = {
      ...localProject(cap),
      device: { ...deviceView(), online: true, harnessReady: false, harnessProblem: 'обновите агента' },
    };
    expect(deviceOfflineLabel(p)).toBe('обновите агента');
  });
  it('null если device онлайн и агент готов', () => {
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
    expect(featureReason(localProject(offlineDeviceCaps()), ProjectFeature.Terminal)).toBe('Устройство проекта не в сети');
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
  const transcriptOff = 'У локального проекта недоступно: подробная история чата хранится на его устройстве';

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

// Проект-предмет ребёнка (Ф1b): профиль Б8a (бэк ddde23597 + правка b22a0aa9c — Notes
// включены, ADR-041 решения 60/76/80). Фронт получает его суженной матрицей groups.features;
// рельса фильтруется по составу features — тому же способу, что весь этот файл.
describe('проект-предмет: профиль Б8a и набор панелей (Ф1b)', () => {
  // Живой проект-предмет глазами бэка: маркер childSubjectId + суженная матрица
  function subjectProject(): Project {
    return {
      id: 'p-subj', name: 'Математика', rootPath: '/projects/liza/Предметы/s1',
      createdAt: '', updatedAt: '',
      childSubjectId: 's1',
      capabilities: childSubjectCapabilities(),
    };
  }

  // Копия замороженного снимка Б18a (ChildModeAllowList.Surface.Panels,
  // ChildModeAllowList.cs:23). К9л: копия сверяется сторожем в конце describe — он читает
  // перечень прямо из бэкового файла (прежний срез копии был без 'chats' — находка сдачи К9к).
  const CHILD_PANELS = ['files', 'knowledge', 'notes', 'chatContext', 'tasks', 'chats'];
  const surface: ChildSurface = { panels: CHILD_PANELS, composerFeatures: [], feedKinds: [] };

  it('матрица профиля: files=[Files], serverContent=[Knowledge], platform без FileWatcher/Memory', () => {
    const cap = childSubjectCapabilities();
    expect(cap.files.features).toEqual([ProjectFeature.Files]);
    expect(cap.serverContent.features).toEqual([ProjectFeature.Knowledge]);
    expect(cap.platform.features).toEqual([
      ProjectFeature.Chat, ProjectFeature.History, ProjectFeature.Tasks, ProjectFeature.Personas,
      ProjectFeature.Notes, ProjectFeature.Costs, ProjectFeature.Tts,
    ]);
    expect(cap.transcript?.features ?? []).toEqual([]);
  });

  it('что можно проекту-предмету: files/knowledge/notes/tasks/personas; git/терминал/сервисы/граф/доки/досье/навыки — нет', () => {
    const p = subjectProject();
    // Personas включён: репетитор — проектная персона-руководитель, куратор правит её (Б16a–c)
    for (const f of [ProjectFeature.Files, ProjectFeature.Knowledge, ProjectFeature.Notes, ProjectFeature.Tasks, ProjectFeature.Personas]) {
      expect(isFeatureAvailable(p, f), `ключ ${f} в предмете включён`).toBe(true);
    }
    for (const f of [
      ProjectFeature.Git, ProjectFeature.Terminal, ProjectFeature.DevServers, ProjectFeature.CodeGraph,
      ProjectFeature.Docs, ProjectFeature.Dossiers, ProjectFeature.Skills,
      ProjectFeature.FileWatcher, ProjectFeature.Memory,
    ]) {
      expect(isFeatureAvailable(p, f), `ключ ${f} в предмете выключен`).toBe(false);
    }
  });

  it('c2: взрослый куратор в проекте-предмете — в наборе нет changes/terminal/preview/docs/graph/skills/dossiers', () => {
    const out = projectPanelKeysAllowed(subjectProject(), [...WORKSPACE_KEYS]);
    for (const k of ['changes', 'terminal', 'preview', 'docs', 'graph', 'skills', 'dossiers']) {
      expect(out, `панель ${k} в проекте-предмете off`).not.toContain(k);
    }
    for (const k of ['files', 'knowledge', 'notes', 'tasks', 'chats', 'team']) {
      expect(out, `панель ${k} — то, что есть в самом проекте`).toContain(k);
    }
  });

  it('c1: ребёнок в проекте-предмете — ровно [chats, files, tasks, knowledge, notes, chatContext]', () => {
    const out = childWorkspaceKeys(projectPanelKeysAllowed(subjectProject(), [...WORKSPACE_KEYS]), surface);
    // порядок рельсы: в WORKSPACE_KEYS chats стоит первым, tasks — раньше knowledge/notes
    expect(out).toEqual(['chats', 'files', 'tasks', 'knowledge', 'notes', 'chatContext']);
    expect([...out].sort()).toEqual([...CHILD_PANELS].sort()); // состав — ровно снимок, без лишнего
  });

  it('обычный серверный проект — набор не меняется', () => {
    expect(projectPanelKeysAllowed(emptyProject(), [...WORKSPACE_KEYS])).toEqual([...WORKSPACE_KEYS]);
  });

  it('набор предмета = ровно ожидаемый список (зафиксированный результат, не сторож объявления)', () => {
    // Это ровно зафиксированный набор: он ловит новую панель реестра, которая ОСТАЛАСЬ
    // в результате — без пары (её тут нет) или с парой к ВКЛЮЧЕННОЙ в предмете
    // возможности. Новая панель с парой к ВЫКЛЮЧЕННОЙ возможности отфильтруется и
    // пройдёт этот тест зелёным: её ловит сторож объявления PANEL_FEATURE ниже.
    const out = projectPanelKeysAllowed(subjectProject(), [...WORKSPACE_KEYS]);
    expect(out).toEqual([
      // порядок — рельса WORKSPACE_KEYS: в ней arch стоит раньше team
      'chats', 'files', 'tasks', 'knowledge', 'notes', 'arch', 'team',
      'plan', 'agents', 'context', 'toc', 'video', 'characters', 'localModel', 'voices',
      'chatContext', 'pluginPanels',
    ]);
  });

  it('К9л: CHILD_PANELS — ровно ChildModeAllowList.Surface.Panels (сверка с бэком)', () => {
    // Копия CHILD_PANELS выше — фикстура c1 и одновременно снимок бэка: без сверки она
    // разъезжается с бэком молча (так и вышло с 'chats' — К9к). Сторож читает перечень
    // прямо из бэкового файла: изменение бэка без ответа фронта красит этот тест
    // (тот же приём, что сторож в WorkspacePage.child.test.tsx и wikiLabels.test.ts:219).
    const path = resolve(dirname(fileURLToPath(import.meta.url)),
      '../../../../backend/ClaudeHomeServer.Core/Services/ChildMode/ChildModeAllowList.cs');
    const src = readFileSync(path, 'utf8');
    // Panels: ["files", …] — строка снимка ChildModeAllowList.Surface (.:23)
    const m = src.match(/Panels:\s*\[([^\]]*)\]/);
    expect(m).not.toBeNull();
    const backend = [...m![1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
    // сравнение по множеству, не по порядку: снимок для фронта — множество
    // разрешённого, порядок рельсы собирает сам фронт (panelCatalog)
    expect([...CHILD_PANELS].sort(), 'копия снимка разъехалась с бэком').toEqual(backend.sort());
  });
});

// Панели без матричного гейта: явный allow-list с обоснованием на строке. Здесь
// ЕДИНЫЙ перечень панелей, чью видимость решает не состав проекта (их нет в
// PANEL_FEATURE); комментарий над PANEL_FEATURE в projectCapabilities.ts только
// ссылается на него — по образцу PANELS_WITHOUT_GATE в panelSubsystems.test.ts.
const PANELS_WITHOUT_MATRIX_GATE: Record<string, string> = {
  // Список чатов проекта: видимость не зависит от состава проекта.
  chats: 'список чатов проекта: видимость не зависит от состава проекта',
  // Видимость по childSurface ребёнка (ответ Софьи 19.11), не по матрице проекта.
  tasks: 'видимость по childSurface ребёнка, не по матрице проекта',
  chatContext: 'видимость по childSurface ребёнка, не по матрице проекта',
  // Пары нет: в матрице предмета Notes включены (b22a0aa9c, решения 60/76/80).
  notes: 'пары не нужна: в матрице предмета Notes включены (b22a0aa9c, решения 60/76/80)',
  // Сессионные панели: видимость по артефактам хода.
  plan: 'сессионная панель: видимость по артефакту хода',
  agents: 'сессионная панель: видимость по артефакту хода',
  context: 'сессионная панель: видимость по артефакту хода',
  // Центральная панель: существует, пока в центре открыт источник.
  toc: 'центральная панель: живёт, пока открыт источник',
  // Свои вертикали и подсистемные гейты, не ProjectFeatures: не состав проекта.
  arch: 'своя подсистема (architecture), не ProjectFeatures',
  video: 'своя подсистема (video), не ProjectFeatures',
  characters: 'панель слота: скрыта реестром вкладов, не ProjectFeatures',
  voices: 'модуль audio-editor: свой гейт, не ProjectFeatures',
  // Core-фича без бэкового тумблера: гейтится своим isAvailable (local-model-stats).
  localModel: 'фича: свой isAvailable, бэкового тумблера нет',
  // Маркер семейства плагинов: отдельная возможность Plugins, свой гейт.
  pluginPanels: 'маркер семейства плагинов: свой гейт (флаг + возможности проекта)',
};

describe('Сторож объявления PANEL_FEATURE: карта и реестр сверены в обе стороны', () => {
  it('каждый ключ реестра — в карте PANEL_FEATURE или в allow-list', () => {
    // Новая панель WORKSPACE_KEYS без решения (ни пары, ни пропуска) — красный:
    // именно её не ловил тест зафиксированного набора выше.
    for (const k of WORKSPACE_KEYS) {
      const declared = k in PANEL_FEATURE || k in PANELS_WITHOUT_MATRIX_GATE;
      expect(declared, `ключ ${k}: ни в PANEL_FEATURE, ни в allow-list`).toBe(true);
    }
  });

  it('каждая позиция карты и allow-list — живой ключ реестра', () => {
    // Мёртвая позиция — решение без свежего решения: при возврате панели в реестр
    // она получила бы автоматическую амнистию (или фильтр по несуществующей паре).
    for (const k of Object.keys(PANEL_FEATURE)) {
      expect(WORKSPACE_KEYS, `ключ ${k}: в PANEL_FEATURE, но нет в реестре`).toContain(k);
    }
    for (const k of Object.keys(PANELS_WITHOUT_MATRIX_GATE)) {
      expect(WORKSPACE_KEYS, `ключ ${k}: в allow-list, но нет в реестре`).toContain(k);
    }
  });

  it('панель заявлена ровно в одном списке: не в карте и в allow-list сразу', () => {
    for (const k of Object.keys(PANEL_FEATURE)) {
      expect(PANELS_WITHOUT_MATRIX_GATE, `ключ ${k}: и в PANEL_FEATURE, и в allow-list`).not.toHaveProperty(k);
    }
  });

  it('у каждой позиции allow-list есть обоснование', () => {
    for (const [k, why] of Object.entries(PANELS_WITHOUT_MATRIX_GATE)) {
      expect(why.trim().length, `ключ ${k}: пустое обоснование`).toBeGreaterThan(0);
    }
  });
});
