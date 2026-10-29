// QA-1: отзыв доступа при открытом проекте. Бэк шлёт личное событие project_access_revoked
// в группу user_{id}; эффект App слушает его: открытый проект закрывается, человек уходит
// к списку проектов (#/projects), а список получает снимок accessNotice — по нему
// ProjectListPage рисует Notice (саму плашку и текст проверяет ProjectListPage.test).
// Отзыв другого (не открытого) проекта: текущий экран не трогается, проект лишь уходит из
// общего кэша списка (dropProjectFromCache).
//
// Окружение — то же, что у App.deeplink.test.ts: node без jsdom, fakeDom + тяжёлые
// либы/страницы no-op-моками; nav — настоящий.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  installFakeDom, uninstallFakeDom, mount, findNodeByAttr,
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
    get length() { return store.size; },
  } as Storage;
  // Открытый проект: адрес #/project/p1 задаём ДО импорта App (initialHash = parseHash())
  (globalThis as any).window = (globalThis as any).window ?? {};
  (globalThis as any).window.location = {
    hash: '#/project/p1', href: 'http://x/', origin: 'http://x',
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

// ── api: projects.list отдаёт p1; остальное — no-op ──
const mockData = vi.hoisted(() => ({ projects: [] as Project[] }));
vi.mock('./lib/api', () => {
  const ns = (overrides: Record<string, () => any>): any => new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      const o = overrides[prop as string];
      return o ?? (() => Promise.resolve(null));
    },
  });
  const api: any = new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'projects') return ns({ list: () => Promise.resolve(mockData.projects) });
      if (prop === 'auth') return ns({ me: () => Promise.resolve(null) });
      return () => Promise.resolve(null);
    },
  });
  return { api, setGitSessionContext: () => {} };
});

vi.mock('./lib/toast', () => ({ __esModule: true, showToast: () => {} }));

// ── signalr: ловим подписчика project_access_revoked, чтобы «прислать» событие из теста;
//     остальные подписки и действия — no-op ──
const sig = vi.hoisted(() => ({ revokedHandlers: [] as Array<(d: { projectId: string; ownerDisplayName?: string | null }) => void> }));
vi.mock('./lib/signalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve();
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
  loadSubsystemRemotes: () => Promise.resolve(),
  getSubsystem: () => undefined, getSubsystemTab: () => undefined,
  REVEAL_PANEL_EVENT: 'cc-reveal-panel', SLOT_WORKSPACE_PANEL_DEF: { id: 'workspace' },
}));

// ── defaultPersona / wall / video / breakpoints / online / theme / inspector: no-op ──
vi.mock('./lib/defaultPersona', async (importOriginal) => {
  const orig = await importOriginal<typeof import('./lib/defaultPersona')>();
  return { ...orig, useMe: () => ({ loaded: false, defaultPersonaId: null }), setMeFromServer: () => {}, clearMe: () => {} };
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
vi.mock('./lib/notifications', () => ({ __esModule: true, ensureNotificationsSubscribed: () => {} }));
vi.mock('./lib/contextPrefs', () => ({ __esModule: true, setCtxThresholdsFromServer: () => {} }));
vi.mock('./lib/models', () => ({ __esModule: true, loadModels: () => Promise.resolve([]) }));
vi.mock('./lib/modules', () => ({ __esModule: true, loadModules: () => Promise.resolve([]) }));
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

// ── страницы и оверлеи: заглушки; ProjectListPage пишет полученные props в журнал ──
const { makeStub, makeSpy } = vi.hoisted(() => {
  const { createElement } = require('react');
  const makeStub = (name: string) => ({ __esModule: true, [name]: () => createElement('div', { 'data-stub': name }) });
  const makeSpy = (name: string, reg: Array<Record<string, unknown>>) => ({
    __esModule: true,
    [name]: (p: Record<string, unknown>) => { reg.push(p); return createElement('div', { 'data-stub': name }); },
  });
  return { makeStub, makeSpy };
});
const { plpPropsReg } = vi.hoisted(() => ({ plpPropsReg: [] as Array<Record<string, unknown>> }));
vi.mock('./pages/ProjectListPage', () => makeSpy('ProjectListPage', plpPropsReg));
vi.mock('./pages/LoginPage', () => makeStub('LoginPage'));
vi.mock('./pages/ChatsPage', () => makeStub('ChatsPage'));
vi.mock('./pages/WorkspacePage', () => makeStub('WorkspacePage'));
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

const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;

// Окно: installFakeDom + настоящий location/history (хэш #/project/p1 — открытый проект)
function setupTestWindow() {
  vi.useFakeTimers();
  installFakeDom();
  const win = globalThis as any;
  win.window.location = { hash: '#/project/p1', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
  win.window.history = { state: null, pushState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; }, replaceState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; }, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
  win.window.setTimeout = win.setTimeout; win.window.clearTimeout = win.clearTimeout;
  win.window.setInterval = win.setInterval; win.window.clearInterval = win.clearInterval;
  win.window.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
  win.window.getSelection = () => null;
  win.window.requestAnimationFrame = (cb: any) => { cb(0); return 1; };
  win.window.cancelAnimationFrame = () => {};
  win.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
  win.getComputedStyle = () => ({ paddingLeft: '0px', paddingRight: '0px' });
  win.MutationObserver = class { observe() {} disconnect() {} takeRecords() { return []; } };
  win.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
  win.Image = class { set src(_v: string) {} onload: any; onerror: any; width = 0; height = 0; };
}

// Открытый проект p1: токен + снимок открытого проекта + список с p1
function beforeEachOpenProject() {
  setupTestWindow();
  store.clear(); sstore.clear();
  sig.revokedHandlers.length = 0;
  cacheSpy.dropped.length = 0;
  plpPropsReg.length = 0;
  store.set('cc_token', 't');
  store.set('cc_server_url', 'http://x');
  store.set('cc_username', 'u');
  store.set('cc_open_project', JSON.stringify(project));
  mockData.projects = [project];
}

const fireRevoked = (d: { projectId: string; ownerDisplayName?: string | null }) =>
  act(() => { sig.revokedHandlers.forEach(h => h(d)); });

describe('App: project_access_revoked при открытом проекте (QA-1)', () => {
  beforeEach(beforeEachOpenProject);
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('открытый проект закрыт, переход к списку, список получил снимок для Notice', async () => {
    const { container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Исходное состояние: воркспейс открытого проекта на экране
    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'воркспейс не открылся').not.toBeNull();

    await fireRevoked({ projectId: 'p1', ownerDisplayName: 'admin' });

    // Ушли к списку проектов: адрес — #/projects, открытый проект закрыт
    expect((globalThis as any).window.location.hash, 'нет перехода к списку проектов').toBe('#/projects');
    expect(store.get('cc_open_project'), 'снимок открытого проекта не снят').toBeUndefined();
    // Список получил снимок: по нему ProjectListPage рисует Notice (текст — в её тесте)
    const last = plpPropsReg.at(-1);
    expect(last, 'ProjectListPage не отрисован').toBeTruthy();
    expect(last!.accessNotice, 'снимок для Notice не дошёл').toEqual({ projectId: 'p1', ownerDisplayName: 'admin' });
  });

  it('отзыв другого (не открытого) проекта: экран и открытый проект не тронуты, кэш списка очищен', async () => {
    const { container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'воркспейс не открылся').not.toBeNull();

    await fireRevoked({ projectId: 'p2', ownerDisplayName: 'admin' });

    // Текущий экран остался: тот же адрес, тот же открытый проект, без сообщения
    expect((globalThis as any).window.location.hash, 'ушёл с текущего экрана').toBe('#/project/p1');
    expect(store.get('cc_open_project'), 'закрыл не свой проект').toBeTruthy();
    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'воркспейс пропал').not.toBeNull();
    expect(plpPropsReg.filter(p => p.accessNotice), 'Notice про чужой проект').toHaveLength(0);
    // Из общего кэша списка проект убран (зона шапки и палитра без него)
    expect(cacheSpy.dropped, 'проект не убран из общего кэша').toContain('p2');
  });
});
