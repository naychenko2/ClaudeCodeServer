// M1 (Ф2-rework4): диплинк #/project/{pid}/feature/{fid} в СВЕЖЕМ браузере (проект не
// запомнен) и с отложенным /me. Эффект диплинка App.tsx открывает проект через navPush —
// туда обязан попасть featureId из хэша, иначе инициализатор openFeatureId WorkspacePage
// берёт getNav()?.feature = null и адрес схлопывается в #/project/{pid} ещё до прихода /me.
//
// Воспроизводим реальный порядок: адрес (initialHash) → проект не запомнен (OPEN_PROJECT_KEY
// пуст, project=null) → /me позже (auth задан) → эффект диплинка api.projects.list() →
// setProject + navPush. Проверяем, что в снимке истории (getNav()) feature = fid.
//
// Окружение node (без jsdom): fakeDom + тяжёлые либы/страницы — no-op-моки; nav и
// featureFlags — реальные (toHash гейтит сегмент feature по флагу).

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { getNav, parseHash } from './lib/nav';
import { __test as flagsTest } from './lib/featureFlags';
import {
  installFakeDom, uninstallFakeDom, mount,
} from './components/chat/__tests__/fakeDom';
import type { Project } from './types';

// ── localStorage / sessionStorage + window.location (initialHash считается при импорте) ──
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
  // Диплинк: адрес задаём ДО импорта App (initialHash = parseHash() считается при загрузке)
  (globalThis as any).window = (globalThis as any).window ?? {};
  (globalThis as any).window.location = {
    hash: '#/project/p1/feature/f1', href: 'http://x/', origin: 'http://x',
    protocol: 'http:', host: 'x', pathname: '/', search: '',
  };
  // matchMedia — на уровне модуля (SidebarSplitter/shell-kit) читается при импорте
  (globalThis as any).window.matchMedia = () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} });
  // document — минимально полный: многие либы пишут documentElement.dataset/getElementById/
  // createElement на уровне модуля (themeMode, IconButton, …) ещё до installFakeDom
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

// ── api: projects.list отдаёт проект p1 (или падает, если fail); остальное — no-op ──
const mockData = vi.hoisted(() => ({ projects: [] as Project[], fail: false }));
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
      if (prop === 'projects') return ns({ list: () => mockData.fail ? Promise.reject(new Error('list')) : Promise.resolve(mockData.projects) });
      if (prop === 'auth') return ns({ me: () => Promise.resolve(null) });
      return () => Promise.resolve(null);
    },
  });
  return { api, setGitSessionContext: () => {} };
});

// ── toast: журнал тостов (тосты запасного перехода и его отказы) ──
const toastMock = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('./lib/toast', () => ({
  __esModule: true,
  showToast: (title: string, body: string) => { toastMock.calls.push(`${title}: ${body}`); },
}));

// ── signalr / terminalSignalr / agentHub: подписки и действия no-op ──
vi.mock('./lib/signalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve();
  return {
    __esModule: true,
    onMessage: sub, onReconnected: sub, onFilesChanged: sub, onPreviewMessage: sub,
    onLocalModelMessage: sub, onGitStatusChanged: sub, onAgentHubMessage: sub,
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

// ── HubTabs: App берёт отсюда только три функции; мокируем, чтобы не тянуть цепочку
//     Toolbar→IconButton (document.getElementById на уровне модуля) и shell-kit (matchMedia) ──
vi.mock('./components/HubTabs', () => ({
  __esModule: true,
  moduleIdOf: () => null,
  subsystemKeyOf: () => null,
  subsystemTabValue: () => 'subsystem:x',
}));
// HubHeader: App берёт только константы; мок — чтобы не тянуть IconButton (document.getElementById)
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
vi.mock('./lib/offline', () => ({ __esModule: true, initConnectivity: () => {} }));
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
vi.mock('./lib/pendingProjectChat', () => ({ __esModule: true, pendingChatOnPop: () => null }));
vi.mock('./features/projects/useAllProjects', () => ({ __esModule: true, onProjectIconBackfilled: () => () => {} }));
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

// ── страницы и оверлеи: лёгкие заглушки (не тянут свои тяжёлые зависимости) ──
// vi.mock хойстится выше top-level констант, поэтому фабрику заглушки — в vi.hoisted
const { makeStub } = vi.hoisted(() => {
  const { createElement } = require('react');
  return { makeStub: (name: string) => ({ __esModule: true, [name]: () => createElement('div', { 'data-stub': name }) }) };
});
vi.mock('./pages/LoginPage', () => makeStub('LoginPage'));
vi.mock('./pages/ProjectListPage', () => makeStub('ProjectListPage'));
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

// ── App (default export) ──
import App from './App';

const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;

// Окно для обоих describe: installFakeDom + настоящий location/history/listeners
// (installFakeDom сбрасывает window/location — возвращаем диплинк и историю).
function setupTestWindow() {
  vi.useFakeTimers();
  installFakeDom();
  const win = globalThis as any;
  win.window.location = { hash: '#/project/p1/feature/f1', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
  win.window.history = { state: null, pushState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; }, replaceState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; }, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
  const winListeners: Record<string, Set<Function>> = {};
  win.window.addEventListener = (t: string, f: Function) => { (winListeners[t] ??= new Set()).add(f); };
  win.window.removeEventListener = (t: string, f: Function) => { winListeners[t]?.delete(f); };
  win.window.dispatchEvent = (e: any) => { (winListeners[e?.type] ?? new Set()).forEach(f => f(e)); return true; };
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

describe('App: диплинк #/project/{pid}/feature/{fid} — проект не запомнен, /me позже', () => {
  beforeEach(() => {
    setupTestWindow();
    store.clear(); sstore.clear();
    flagsTest.reset(); // «не загружено»: toHash не схлопывает сегмент feature до ответа /me
    // auth задан (токен в localStorage) → эффект диплинка запустится; проект НЕ запомнен
    store.set('cc_token', 't');
    store.set('cc_server_url', 'http://x');
    store.set('cc_username', 'u');
    // OPEN_PROJECT_KEY (cc_open_project) ПУСТ — свежий браузер
    mockData.projects = [project];
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('navPush эффекта диплинка несёт featureId: getNav().feature = fid (адрес не схлопывается)', async () => {
    // До эффекта: адрес — диплинк, снимок истории ещё не сидирован (null)
    expect(parseHash('#/project/p1/feature/f1')?.featureId).toBe('f1');
    mount(createElement(App));
    // Эффект диплинка: api.projects.list() → setProject + navPush (асинхронно)
    await act(async () => { vi.advanceTimersByTime(100); });
    // КЛЮЧЕВОЕ (M1): в снимке истории feature = fid. Мутация «убрать feature из navPush»
    // красит этот тест (getNav().feature = null → адрес схлопнут в #/project/p1).
    expect(getNav()?.feature, 'featureId потерян в снимке истории (M1: адрес схлопнется)').toBe('f1');
    // Проект запомнен (OPEN_PROJECT_KEY) и адрес — #/project/p1/feature/f1
    expect(JSON.parse(store.get('cc_open_project') ?? 'null')?.id).toBe('p1');
    expect((globalThis as any).window.location.hash).toContain('/feature/f1');
  });
});

// ═══ Ф-хв (ревью Ф9b-д4, зам. 1 и 4): запасный переход из раздела «Чаты» ═══════════
// Чип «← к фиче» вне воркспейса шлёт cc-open-project-feature с chatId: обработчик
// открывает проект со страницей фичи И пишет чат в канал выбора cc_pending_project_chat
// (тот же, что диплинк) — иначе WorkspacePage при монтировании нужный чат не выберет.
// Плюс два отказных тоста (проект не найден / список не загрузился) — раньше без тестов.
describe('App: cc-open-project-feature — канал выбора чата и отказные тосты', () => {
  beforeEach(() => {
    setupTestWindow();
    store.clear(); sstore.clear();
    flagsTest.reset();
    store.set('cc_token', 't');
    store.set('cc_server_url', 'http://x');
    store.set('cc_username', 'u');
    mockData.fail = false;
    mockData.projects = [project];
    toastMock.calls.length = 0;
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  const openFeature = () => {
    (globalThis as any).window.dispatchEvent(
      new CustomEvent('cc-open-project-feature', { detail: { projectId: 'p1', featureId: 'f1', chatId: 'chat-1' } }),
    );
  };

  it('событие с chatId → канал cc_pending_project_chat записан (мутация: убрать запись → red)', async () => {
    mount(createElement(App));
    openFeature();
    await act(async () => { vi.advanceTimersByTime(100); });
    // КЛЮЧЕВОЕ: чат передан в канал выбора — тот же формат, что у диплинка («pid|chatId»).
    expect(sstore.get('cc_pending_project_chat'), 'чат не попал в канал выбора').toBe('p1|chat-1');
    // Снимок истории несёт фичу и чат.
    expect(getNav()?.feature, 'featureId потерян в снимке').toBe('f1');
    expect(getNav()?.chatId).toBe('chat-1');
  });

  it('проект не найден → тост «Проект фичи недоступен», канал не записан', async () => {
    mockData.projects = [];
    mount(createElement(App));
    openFeature();
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(toastMock.calls.some(t => t.includes('Проект фичи недоступен')), 'тоста о недоступном проекте нет').toBe(true);
    expect((globalThis as any).sessionStorage.getItem('cc_pending_project_chat'), 'канал не должен быть написан').toBe(null);
  });

  it('список проектов не загрузился → тост «Не удалось открыть проект фичи»', async () => {
    mockData.fail = true;
    mount(createElement(App));
    openFeature();
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(toastMock.calls.some(t => t.includes('Не удалось открыть проект фичи')), 'тоста о сбое списка нет').toBe(true);
    expect((globalThis as any).sessionStorage.getItem('cc_pending_project_chat'), 'канал не должен быть написан').toBe(null);
  });
});
