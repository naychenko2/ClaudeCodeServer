// KM-F10 (доработка): гейт детского режима в App стоит ДО побочных эффектов. Перехватываем всё,
// что App шлёт наружу — api, request, fetch, signalr, загрузчики (модели, модули, подписка на
// уведомления, MF-remote). Для ребёнка наружу уходит только allow-list ADR-041, даже если в
// localStorage лежит OPEN_PROJECT_KEY и адрес — диплинк на проект; взрослый каркас — как раньше.
// Окружение — то же, что у App.accessRevoked.test.ts.

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

vi.mock('./lib/toast', () => ({ __esModule: true, showToast: () => {} }));

// ── signalr: ловим подписчика project_access_revoked, чтобы «прислать» событие из теста;
//     остальные подписки и действия — no-op ──
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
vi.mock('./features/childMode/ChildWorkspace', () => makeStub('ChildWorkspace'));

import App from './App';

const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;

function setupTestWindow() {
  vi.useFakeTimers();
  installFakeDom();
  const win = globalThis as any;
  win.window.location = { hash: '#/project/p1', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
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

// Токен + «открытый проект» в localStorage + диплинк #/project/p1: всё, что тянет взрослые запросы
function arrange(me: { isChild: boolean } | 'hold') {
  setupTestWindow();
  store.clear(); sstore.clear();
  for (const k of ['api', 'urls', 'fetches', 'signalr', 'loaders'] as const) net[k].length = 0;
  net.meState = { loaded: false, defaultPersonaId: null, isChild: false };
  net.hold = me === 'hold'; net.release = null;
  net.meResult = me === 'hold' ? null : { ...me, subsystems: {}, featureFlags: {} };
  plpPropsReg.length = 0;
  store.set('cc_token', 't');
  store.set('cc_server_url', 'http://x');
  store.set('cc_username', 'kid');
  store.set('cc_open_project', JSON.stringify(project));
  mockData.projects = [project];
}

// allow-list ADR-041: логин, /me, TTS, /child/*, GET chats/{id} и его /history
const ALLOWED_URL = /^\/(auth\/login|auth\/me|tts|child\/.*|chats\/[^/]+(\/history)?)(\?.*)?$/;

describe('App: гейт isChild до побочных эффектов (KM-F10)', () => {
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('isChild=true: наружу только /me, остальное из allow-list; взрослого каркаса нет, хотя в storage лежит OPEN_PROJECT_KEY', async () => {
    arrange({ isChild: true });
    const { container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });

    expect(findNodeByAttr(container, 'data-stub', 'ChildWorkspace'), 'детская оболочка не открылась').not.toBeNull();
    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'взрослый воркспейс смонтирован').toBeNull();
    expect(net.api, 'вызовы api, кроме /me').toEqual(['auth.me']);
    expect(net.urls.filter(u => !ALLOWED_URL.test(u)), 'URL вне allow-list').toEqual([]);
    expect(net.fetches.filter(u => !ALLOWED_URL.test(u.replace(/^https?:\/\/[^/]+/, '').replace(/^\/api/, ''))), 'fetch вне allow-list').toEqual([]);
    expect(net.signalr, 'signalr-действия у ребёнка').toEqual([]);
    expect(net.loaders, 'взрослые загрузчики (модели, модули, уведомления, remote)').toEqual([]);
  });

  it('пока /me не получен: ни взрослого экрана, ни взрослых запросов — даже через 3 с', async () => {
    arrange('hold');
    const { container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });

    for (const stub of ['HomePage', 'WorkspacePage', 'ChatsPage', 'ChildWorkspace', 'LoginPage']) {
      expect(findNodeByAttr(container, 'data-stub', stub), `до /me показан ${stub}`).toBeNull();
    }
    expect(net.api, 'до /me уходит только сам /me').toEqual(['auth.me']);
    expect(net.loaders).toEqual([]);

    await act(async () => { net.release?.({ isChild: true, subsystems: {}, featureFlags: {} }); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-stub', 'ChildWorkspace'), 'после /me нет детской оболочки').not.toBeNull();
    expect(net.api).toEqual(['auth.me']);
  });

  it('isChild=false: взрослый каркас как раньше — воркспейс, список проектов, модели и подписка на уведомления', async () => {
    arrange({ isChild: false });
    const { container } = mount(createElement(App));
    await act(async () => { vi.advanceTimersByTime(5_000); });

    expect(findNodeByAttr(container, 'data-stub', 'WorkspacePage'), 'взрослый воркспейс не открылся').not.toBeNull();
    expect(findNodeByAttr(container, 'data-stub', 'ChildWorkspace')).toBeNull();
    expect(net.api).toContain('projects.list');
    expect(net.loaders).toEqual(expect.arrayContaining(['models', 'modules', 'notifications', 'remotes']));
  });
});
