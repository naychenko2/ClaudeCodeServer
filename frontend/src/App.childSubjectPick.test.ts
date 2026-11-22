// Г6: выбор произвольного предмета на телефоне (находка P2 аудита 21.11). Схема перехода
// целиком, на живом App + живом пикере: ребёнок без открытого проекта видит список своих
// предметов (ChildSubjectPicker, GET /api/child/subjects), клик по ЛЮБОЙ плитке открывает
// проект выбранного предмета (включая второй), «назад» из проекта (goToProjects) возвращает
// к списку, и первый проект при этом САМ не открывается — авто-выбор list[0] убран, без
// него дефект возвращался бы: «назад» тут же снова открывало бы первый предмет.
// Мутации, красящие кейсы (три, доказаны прогоном в копии; доработки Г6-д1 и Г20):
// (а) в детском эффекте App вернуть авто-выбор `const target = fresh ?? list[0] ?? null`
// — красит кейс 4: при входе с сохранённым проектом выключенного предмета открывается p2
// вместо пикера; кейсы 1–3 под ней зелёные — путь «открытый проект исчез из списка»
// достижим только в кейсе 4; (б) в ChildSubjectPicker снять фильтр enabled — красит кейс 2;
// (в) в App.openSubjectProject снять `setSubjectMiss(subject.id)` (вернуть молчание промаха)
// — красит кейс 5: текста про недоступность нет, ребёнок тапает в тишину (Г20).
// Окружение — общий каркас App.childGate.test.ts (моки api/signalr/defaultPersona/страниц).

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  installFakeDom, uninstallFakeDom, mount, findNodeByAttr, reactProps, textNodes,
} from './components/chat/__tests__/fakeDom';
import type { Project } from './types';

// ── localStorage / sessionStorage + window.location (initialHash считается при импорте App) ──
const { store, sstore } = vi.hoisted(() => {
  (globalThis as any).self = globalThis;
  const store = new Map<string, string>();
  const sstore = new Map<string, string>();
  (globalThis as any).localStorage = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => { store.set(k, String(v)); },
    removeItem: (k: string) => { store.delete(k); },
    clear: () => store.clear(),
    key: () => null,
    get length() { return store.size; },
  } as Storage;
  (globalThis as any).sessionStorage = {
    getItem: (k: string) => sstore.get(k) ?? null,
    setItem: (k: string, v: string) => { sstore.set(k, String(v)); },
    removeItem: (k: string) => { sstore.delete(k); },
    clear: () => sstore.clear(),
    key: () => null,
    get length() { return sstore.size; },
  } as Storage;
  // Ребёнок БЕЗ открытого проекта: никакого диплинка и никакого OPEN_PROJECT_KEY —
  // ровно состояние «назад из проекта» после Г6
  (globalThis as any).window = (globalThis as any).window ?? {};
  (globalThis as any).window.location = {
    hash: '', href: 'http://x/', origin: 'http://x',
    protocol: 'http:', host: 'x', pathname: '/', search: '',
  };
  (globalThis as any).window.matchMedia = () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} });
  const mkNode = () => ({
    nodeType: 1, nodeName: 'DIV', style: { setProperty: () => {}, removeProperty: () => {} },
    dataset: {}, attributes: {}, childNodes: [], appendChild: (c: any) => c,
    setAttribute: () => {}, getAttribute: () => null, addEventListener: () => {}, removeEventListener: () => {},
  });
  (globalThis as any).document = (globalThis as any).document ?? {
    documentElement: { style: { setProperty: () => {}, removeProperty: () => {} }, dataset: {} },
    body: mkNode(), head: mkNode(),
    createElement: mkNode, createElementNS: (_ns: string, t: string) => ({ ...mkNode(), nodeName: t.toUpperCase() }),
    createTextNode: (t: string) => ({ nodeType: 3, nodeValue: t }),
    getElementById: () => null, querySelector: () => null, querySelectorAll: () => [],
    addEventListener: () => {}, removeEventListener: () => {},
  };
  return { store, sstore };
});

// ── данные: два предмета и их проекты-предметы; выключенный предмет для кейса 2 ──
const mockData = vi.hoisted(() => ({
  projects: [] as Project[],
  subjects: [] as unknown[],
}));
// Журнал сети: всё, что App и его хуки шлют наружу (api, request, fetch, signalr, загрузчики)
const net = vi.hoisted(() => ({
  api: [] as string[], urls: [] as string[], fetches: [] as string[], signalr: [] as string[], loaders: [] as string[],
  meResult: null as unknown, hold: false, release: null as null | ((v: unknown) => void),
  meState: { loaded: false, defaultPersonaId: null as string | null, isChild: false },
}));
vi.mock('./lib/api', () => {
  const ns = (group: string, overrides: Record<string, () => any>): any => new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      return (...a: unknown[]) => {
        net.api.push(`${group}.${String(prop)}`);
        const o = overrides[prop as string];
        return o ? o() : Promise.resolve(null);
      };
    },
  });
  const api: any = new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      const group = String(prop);
      if (group === 'projects') return ns(group, { list: () => Promise.resolve(mockData.projects) });
      if (group === 'auth') return ns(group, {
        me: () => net.hold
          ? new Promise(r => { net.release = r; })
          : Promise.resolve(net.meResult),
      });
      return ns(group, {});
    },
  });
  return { api, setGitSessionContext: () => {} };
});

// ── детские ручки: subjects пикера — управляемые; остальное моку не нужно ──
vi.mock('./features/childMode/api', () => ({
  __esModule: true,
  childApi: {
    getSubjects: () => Promise.resolve(mockData.subjects),
    createLesson: () => Promise.resolve({ sessionId: 'chatNew' }),
    spaceChats: () => Promise.resolve({ chats: [] }),
  },
  tutorAvatarUrl: () => '',
}));

vi.mock('./lib/toast', () => ({ __esModule: true, showToast: () => {} }));

// ── signalr / transport: no-op (тот же набор, что App.childGate) ──
// ── signalr: подписчик project_access_revoked (зовёт App:1037 в детском маршруте тоже) ──
const sig = vi.hoisted(() => ({ revokedHandlers: [] as Array<(d: { projectId: string; ownerDisplayName?: string | null }) => void> }));
vi.mock('./lib/signalr', () => {
  const sub = () => () => {};
  const act = () => { net.signalr.push('action'); return Promise.resolve(); };
  return {
    __esModule: true,
    onMessage: sub, onReconnected: sub, onFilesChanged: sub, onPreviewMessage: sub,
    onLocalModelMessage: sub, onGitStatusChanged: sub, onAgentHubMessage: sub,
    onProjectAccessRevoked: (h: (d: { projectId: string; ownerDisplayName?: string | null }) => void) => {
      sig.revokedHandlers.push(h);
      return () => {};
    },
    joinProject: act, leaveProject: act, joinSession: act, leaveSession: act,
    joinUser: act, leaveUser: act, sendMessage: act, respondPermission: act,
    interruptSession: act, compactSession: act, answerQuestion: act, respondPlan: act,
    respondTeamPlan: act, respondTeamEscalation: act, setMode: act, resetConnection: act,
    joinPreviewLog: act, leavePreviewLog: act,
  };
});
vi.mock('./lib/terminalSignalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve(null);
  return {
    __esModule: true,
    resetTerminalConnection: act, createTerminal: act, connectTerminal: act,
    listTerminals: act, stopTerminal: act, renameTerminal: act,
    sendTerminalInput: act, resizeTerminal: act, onTerminalMessage: sub,
  };
});
vi.mock('./lib/agentHub', () => ({ __esModule: true, resetAgentHubs: () => {} }));

// ── общий кэш списка: dropProjectFromCache — журнал вызовов ──
const cacheSpy = vi.hoisted(() => ({ dropped: [] as string[] }));
vi.mock('./features/projects/useAllProjects', () => ({
  __esModule: true,
  onProjectIconBackfilled: () => () => {},
  dropProjectFromCache: (id: string) => { cacheSpy.dropped.push(id); },
}));

// ── HubTabs / HubHeader: только константы (не тянем IconButton/Toolbar) ──
vi.mock('./components/HubTabs', () => ({
  __esModule: true,
  moduleIdOf: () => null,
  subsystemKeyOf: () => null,
  subsystemTabValue: () => 'subsystem:x',
}));
vi.mock('./components/HubHeader', () => ({
  __esModule: true,
  PRODUCT_HISTORY_EVENT: 'open-product-history',
  productHistorySeenKey: (userId?: string | null) => `cc_ph_seen${userId ? '_' + userId : ''}`,
}));

// ── подсистемы / реестр: no-op ──
vi.mock('./lib/subsystems', async (importOriginal) => {
  const orig = await importOriginal<typeof import('./lib/subsystems')>();
  return { ...orig, useSubsystem: () => false, isSubsystemEnabled: () => false, setAllSubsystems: () => {} };
});
vi.mock('./lib/subsystems/registry', () => ({
  __esModule: true,
  useSlot: () => [], useSlotItem: () => null,
  loadSubsystemRemotes: () => { net.loaders.push('remotes'); return Promise.resolve(); },
  getSubsystem: () => undefined, getSubsystemTab: () => undefined,
  REVEAL_PANEL_EVENT: 'cc-reveal-panel', SLOT_WORKSPACE_PANEL_DEF: { id: 'workspace' },
}));

// ── defaultPersona / wall / video / breakpoints / online / theme / inspector: no-op ──
vi.mock('./lib/defaultPersona', async (importOriginal) => {
  const orig = await importOriginal<typeof import('./lib/defaultPersona')>();
  return { ...orig, useMe: () => net.meState, setMeFromServer: (m: { isChild?: boolean }) => { net.meState = { loaded: true, defaultPersonaId: null, isChild: !!m.isChild }; }, clearMe: () => {} };
});
vi.mock('./features/wall/wallStore', async (importOriginal) => {
  const orig = await importOriginal<typeof import('./features/wall/wallStore')>();
  return { ...orig, useWallFocusProject: () => null, resetWallOwner: () => {} };
});
vi.mock('./lib/videoStage', () => ({ __esModule: true, useVideoStage: () => null }));
vi.mock('./lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('./lib/breakpoints')>();
  return { ...orig, useIsMobile: () => false };
});
vi.mock('./hooks/useOnline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('./hooks/useOnline')>();
  return { ...orig, useOnline: () => true };
});
vi.mock('./hooks/useThemeColor', () => ({ __esModule: true, useThemeColor: () => {} }));
vi.mock('./lib/uiInspector', () => ({
  __esModule: true, useUiInspector: () => false, setUiInspectorAdmin: () => {}, wireUiInspectorHotkey: () => {},
}));

// ── lib-модули со side-эффектами: no-op ──
vi.mock('./lib/offline', () => ({
  __esModule: true, initConnectivity: () => {}, invalidateTokenCache: () => {},
  request: (url: string) => { net.urls.push(url); return Promise.resolve(null); },
  readStoredToken: () => (globalThis as any).localStorage.getItem('cc_token'),
  readStoredUserId: () => (globalThis as any).localStorage.getItem('cc_user_id'),
}));
vi.mock('./lib/selectionScope', () => ({ __esModule: true, installSelectionScopes: () => {} }));
vi.mock('./lib/sync', () => ({
  __esModule: true, runOfflineSnapshot: () => Promise.resolve(), syncProjectFiles: () => Promise.resolve(), drainOfflineQueues: () => Promise.resolve(),
}));
vi.mock('./lib/pinnedProjects', () => ({ __esModule: true, recordRecentProject: () => {} }));
vi.mock('./lib/idb', () => ({ __esModule: true, idbClear: () => {} }));
vi.mock('./lib/chatContext/forget', () => ({ __esModule: true, resetChatContextMemory: () => {} }));
vi.mock('./lib/ai/awaiting', () => ({ __esModule: true, resetAiAwaiting: () => {} }));
vi.mock('./lib/modelProvidersNav', () => ({ __esModule: true, requestOpenModelsSpend: () => {} }));
vi.mock('./lib/notifications', () => ({ __esModule: true, ensureNotificationsSubscribed: () => { net.loaders.push('notifications'); } }));
vi.mock('./lib/contextPrefs', () => ({ __esModule: true, setCtxThresholdsFromServer: () => {} }));
vi.mock('./lib/models', () => ({ __esModule: true, loadModels: () => { net.loaders.push('models'); return Promise.resolve([]); } }));
vi.mock('./lib/modules', () => ({ __esModule: true, loadModules: () => { net.loaders.push('modules'); return Promise.resolve([]); } }));
vi.mock('./lib/pendingProjectChat', async (importOriginal) => ({ ...(await importOriginal<typeof import('./lib/pendingProjectChat')>()), pendingChatOnPop: () => null }));
// eslint-disable-next-line design/no-raw-color -- тестовый мок цвета проекта, не дизайн-токен
vi.mock('./features/projects/projectUtil', () => ({ __esModule: true, projectMainColor: () => '#000' }));
vi.mock('./features/telemetry/incidentLink', () => ({ __esModule: true, setPendingIncident: () => {}, INCIDENT_OPEN_EVENT: 'cc-open-incident' }));
vi.mock('./features/onboarding/OnboardingPage', () => ({
  __esModule: true, IntroChatPage: () => null, ProjectIntroChatPage: () => null, OPEN_INTRO_EVENT: 'cc-open-intro',
}));
vi.mock('./lib/wallMode', () => ({
  __esModule: true, getWallReturn: () => null, isWallActive: () => false, setWallActive: () => {}, setWallReturn: () => {},
}));
vi.mock('./lib/workspaceState', () => ({
  __esModule: true, loadWorkspaceState: () => null, saveWorkspaceState: () => {},
  loadFileFullscreenPref: () => false, saveFileFullscreenPref: () => {}, isLeftTab: () => false,
}));

// ── страницы и оверлеи: заглушки ──
const { makeStub } = vi.hoisted(() => {
  const { createElement } = require('react');
  const makeStub = (name: string) => ({ __esModule: true, [name]: () => createElement('div', { 'data-stub': name }) });
  return { makeStub };
});
// WorkspacePage — шпион с кнопкой «назад»: детская цепочка «выбор → проект → назад»
// должна пройти целиком, поэтому заглушка отдаёт наружу реальный onGoToProjects.
const { wpPropsReg, wsBackClicks } = vi.hoisted(() => ({
  wpPropsReg: [] as Array<Record<string, unknown>>,
  wsBackClicks: [] as string[],
}));
vi.mock('./pages/WorkspacePage', () => {
  const { createElement } = require('react');
  return {
    __esModule: true,
    WorkspacePage: (p: any) => {
      wpPropsReg.push(p);
      wsBackClicks.push(String(p.project?.id ?? ''));
      return createElement('div', { 'data-stub': 'WorkspacePage', 'data-project': p.project?.id ?? '' }, [
        createElement('button', { 'data-testid': 'ws-back', onClick: () => p.onGoToProjects?.() }, 'назад'),
      ]);
    },
  };
});
vi.mock('./pages/ProjectListPage', () => makeStub('ProjectListPage'));
vi.mock('./pages/LoginPage', () => makeStub('LoginPage'));
vi.mock('./pages/ChatsPage', () => makeStub('ChatsPage'));
vi.mock('./pages/HomePage', () => makeStub('HomePage'));
vi.mock('./pages/WallPage', () => makeStub('WallPage'));
vi.mock('./features/tasks/CalendarPage', () => makeStub('CalendarPage'));
vi.mock('./features/personas/PersonasPage', () => makeStub('PersonasPage'));
vi.mock('./features/personas/SpecialtiesPage', () => makeStub('SpecialtiesPage'));
vi.mock('./features/knowledge/KnowledgePage', () => makeStub('KnowledgePage'));
vi.mock('./features/telemetry/TelemetryPage', () => makeStub('TelemetryPage'));
vi.mock('./features/notifications/NotificationsPage', () => makeStub('NotificationsPage'));
vi.mock('./components/modules/ModuleScreen', () => makeStub('ModuleScreen'));
vi.mock('./features/video/VideoFloat', () => makeStub('VideoFloat'));
vi.mock('./features/video/VideoStageFrame', () => makeStub('VideoStageFrame'));
vi.mock('./components/UpdatePrompt', () => makeStub('UpdatePrompt'));
vi.mock('./components/NotificationToasts', () => makeStub('NotificationToasts'));
vi.mock('./components/DeployHost', () => makeStub('DeployHost'));
vi.mock('./components/ProductHistory', () => makeStub('ProductHistory'));
vi.mock('./components/GlobalSearch', () => makeStub('GlobalSearch'));
vi.mock('./components/ai/AiLauncher', () => makeStub('AiLauncher'));
vi.mock('./features/inspector/UiInspectorOverlay', () => makeStub('UiInspectorOverlay'));

import App from './App';

// Проекты-предметы ребёнка: p1 — первый предмет, p2 — второй (то, что раньше выбрать
// было нельзя). childSubjectId — ключ связи «предмет → проект» (openSubjectProject).
const proj = (id: string, subjectId: string, name: string): Project => ({
  id, name, rootPath: `/projects/liza/Предметы/${id}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  childSubjectId: subjectId,
} as Project);
const p1 = proj('p1', 's1', 'Математика');
const p2 = proj('p2', 's2', 'История');
// Форма GET /api/child/subjects: только включённые, порядок профиля (И5).
const subj = (id: string, label: string, enabled?: boolean) => ({ id, label, glyph: '×÷', color: 'info', tutor: null, ...(enabled !== undefined ? { enabled } : {}) });

function setupTestWindow() {
  vi.useFakeTimers();
  installFakeDom();
  const win = globalThis as any;
  win.window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
  win.window.history = { state: null, pushState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; }, replaceState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; }, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
  win.window.setTimeout = win.setTimeout; win.window.clearTimeout = win.clearTimeout;
  win.window.setInterval = win.setInterval; win.window.clearInterval = win.clearInterval;
  win.window.fetch = (u: string) => { net.fetches.push(String(u)); return Promise.resolve({ ok: true, json: () => Promise.resolve({}) }); };
  win.fetch = win.window.fetch;
  win.window.getSelection = () => null;
  win.window.requestAnimationFrame = (cb: any) => { cb(0); return 1; };
  win.window.cancelAnimationFrame = () => {};
  win.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
  win.getComputedStyle = () => ({ paddingLeft: '0px', paddingRight: '0px' });
  win.MutationObserver = class { observe() {} disconnect() {} takeRecords() { return []; } };
  win.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
  win.Image = class { set src(_v: string) {} onload: any; onerror: any; width = 0; height = 0; };
}

// Ребёнок БЕЗ открытого проекта: токен есть, OPEN_PROJECT_KEY не ставим, диплинка нет —
// ровно состояние после «назад из проекта» (Г6).
function arrange(opts: { projects?: Project[]; subjects?: unknown[] } = {}) {
  setupTestWindow();
  store.clear(); sstore.clear();
  for (const k of ['api', 'urls', 'fetches', 'signalr', 'loaders'] as const) net[k].length = 0;
  net.meState = { loaded: false, defaultPersonaId: null, isChild: false };
  net.hold = false; net.release = null;
  net.meResult = { isChild: true, subsystems: {}, featureFlags: {} };
  wpPropsReg.length = 0;
  wsBackClicks.length = 0;
  store.set('cc_token', 't');
  store.set('cc_server_url', 'http://x');
  store.set('cc_username', 'kid');
  mockData.projects = opts.projects ?? [p1, p2];
  mockData.subjects = opts.subjects ?? [
    subj('s1', 'Математика'),
    subj('s2', 'История'),
  ];
}

const pickClick = (container: any, tile: string): (() => void) => {
  const node = findNodeByAttr(container, 'data-testid', tile);
  expect(node, `плитки ${tile} нет на экране`).not.toBeNull();
  const props = reactProps(node!) as { onClick?: () => void } | null;
  expect(typeof props?.onClick, 'у плитки нет onClick').toBe('function');
  return props!.onClick!;
};

describe('Г6: ребёнок выбирает предмет сам — выбор → проект → назад → выбор', () => {
  beforeEach(() => {
    arrange();
  });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('кейс 1: вход — список предметов, клик по второму открывает его проект, «назад» — снова список, первый проект сам не открывается', async () => {
    const { root, container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });

    // Вход: пикер на экране, воркспейса нет; projects.list не зовётся вовсе — проект не выбран,
    // и никакой «первый предмет» не подставляется
    expect(findNodeByAttr(container, 'data-testid', 'child-subject-picker'), 'пикера предметов на входе нет').not.toBeNull();
    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'первый проект сам открылся при входе').toBeNull();
    expect(net.api, 'вход ребёнка: только /me — выбор предмета не тянет список проектов').toEqual(['auth.me']);

    // Выбор ВТОРОГО предмета: пикер → onPick → openSubjectProject ищет его проект в списке
    await act(async () => { pickClick(container, 'child-subject-tile-s2')(); });
    await act(async () => { vi.advanceTimersByTime(200); });

    expect(findNodeByAttr(container, 'data-testid', 'child-subject-picker'), 'пикер остался на экране после выбора').toBeNull();
    expect(wpPropsReg.length, 'WorkspacePage не поднялся').toBe(1);
    expect(String(wpPropsReg[0].project && (wpPropsReg[0].project as Project).id), 'открылся не проект второго предмета').toBe('p2');

    // «Назад» (мобильная шапка): onGoToProjects → goToProjects → пикер, не первый проект
    await act(async () => {
      const back = reactProps(findNodeByAttr(container, 'data-testid', 'ws-back')!) as { onClick?: () => void };
      back.onClick!();
    });
    await act(async () => { vi.advanceTimersByTime(200); });

    expect(findNodeByAttr(container, 'data-testid', 'child-subject-picker'), 'после «назад» пикера нет').not.toBeNull();
    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'после «назад» воркспейс на экране').toBeNull();
    // Даже с таймерами «вперёд» первый проект сам не подставляется — ровно дефект Г6
    await act(async () => { vi.advanceTimersByTime(5_000); });
    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'первый проект сам открылся после «назад»').toBeNull();
    // После «назад» новых запросов списка нет: проект не открыт — сверять нечего
    // (два projects.list выше — легитимные: поиск проекта предмета и сверка открытого)
    expect(net.api, 'после «назад» ушёл лишний запрос списка проектов').toEqual(['auth.me', 'projects.list', 'projects.list']);
  });

  it('кейс 2: выключенный предмет в списке не показан (даже если сервер вдруг его прислал)', async () => {
    arrange({
      subjects: [subj('s1', 'Математика'), { ...subj('s2', 'История'), enabled: false }],
    });
    const { root, container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });
    try {
      // Плитка включённого — на экране; выключенного — нет вовсе: кликнуть её нельзя,
      // и «не выводит на список уроков выключенного предмета» закрыт построением
      expect(findNodeByAttr(container, 'data-testid', 'child-subject-tile-s1'), 'включённого предмета нет в списке').not.toBeNull();
      expect(findNodeByAttr(container, 'data-testid', 'child-subject-tile-s2'), 'выключенный предмет попал в список').toBeNull();
      const all = textNodes(container as any).join(' ');
      expect(all.includes('История'), 'имя выключенного предмета видно на экране').toBe(false);
    } finally { root.unmount(); }
  });

  it('кейс 3: предметов нет — «Предметов пока нет», а не вечно идущая загрузка', async () => {
    arrange({ subjects: [] });
    const { root, container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });
    try {
      const all = textNodes(container as any).join(' ');
      expect(all.includes('Предметов пока нет'), 'текста «Предметов пока нет» нет').toBe(true);
      expect(all.includes('Попроси маму или папу настроить твои предметы'), 'подсказки нет').toBe(true);
      expect(findNodeByAttr(container, 'data-testid', 'child-subject-picker'), 'пустой пикер вместо named empty-state').toBeNull();
    } finally { root.unmount(); }
  });

  it('кейс 4: выключили предмет у открытого проекта — возврат к выбору, первый из оставшихся сам не открывается', async () => {
    // Открытый проект p1 восстановился из localStorage при монтировании, а сервер его
    // больше не отдаёт: предмет s1 выключили, в списке остался только p2
    arrange({ projects: [p2] });
    store.set('cc_open_project', JSON.stringify(p1));
    const { root, container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });
    try {
      // Возврат к выбору: пикер на экране, воркспейса выключенного предмета нет
      expect(findNodeByAttr(container, 'data-testid', 'child-subject-picker'), 'пикера нет после снятия предмета').not.toBeNull();
      expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'воркспейс выключенного предмета остался на экране').toBeNull();
      // Инвариант Г6: первый из оставшихся проектов (p2) САМ не подставляется — ровно
      // против этой мутации `fresh ?? list[0]`; кейс 1 этот путь не достигает
      await act(async () => { vi.advanceTimersByTime(5_000); });
      expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'первый оставшийся проект сам открылся').toBeNull();
      // Сверка открытого проекта — единственный запрос списка: возврат к выбору не тянет лишнего
      expect(net.api, 'лишние запросы при возврате к выбору').toEqual(['auth.me', 'projects.list']);
    } finally { root.unmount(); }
  });

  it('кейс 5 (Г20): тап по предмету без проекта — видимый текст, а не тишина; проект не открывается', async () => {
    // У предмета s2 нет проекта-предмета (его удалили из воркспейса вручную). Раньше такой
    // тап молчал: ни перехода, ни текста — ребёнок не понимал, что делать (Г20).
    arrange({ projects: [p1] });
    const { root, container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });
    try {
      // Плитка НЕ заблокирована заранее: наличия проекта до тапа никто не знает (п.3
      // постановки) — плашки про недоступность на экране до тапа нет
      expect(findNodeByAttr(container, 'data-testid', 'child-subject-tile-s2'), 'плитки предмета без проекта нет').not.toBeNull();
      expect(findNodeByAttr(container, 'data-testid', 'child-subject-miss'), 'плашки про недоступность нет до тапа').toBeNull();

      await act(async () => { pickClick(container, 'child-subject-tile-s2')(); });
      await act(async () => { vi.advanceTimersByTime(200); });

      // Исход — текст в существующем детском тоне: «…пока не открывается» + «Попроси
      // взрослого помочь.» (те же формулировки, что у «Правила пока не открываются» в
      // воркспейсе предмета); тон нового сообщения не заводим
      const all = textNodes(container as any).join(' ');
      expect(all.includes('«История» пока не открывается'), 'текста про недоступность предмета нет').toBe(true);
      expect(all.includes('Попроси взрослого помочь.'), 'просьбы позвать взрослого нет').toBe(true);
      // Исход — не переход: воркспейса нет, пикер на экране, другой выбрать можно
      expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'проект всё-таки открылся').toBeNull();
      expect(wpPropsReg.length, 'WorkspacePage поднялся').toBe(0);
      expect(findNodeByAttr(container, 'data-testid', 'child-subject-picker'), 'после промаха пикера нет').not.toBeNull();
      // Один запрос списка — сам поиск проекта; «сверки открытого» нет: проект не открыт
      expect(net.api, 'лишних запросов при промахе').toEqual(['auth.me', 'projects.list']);
    } finally { root.unmount(); }
  });
});
