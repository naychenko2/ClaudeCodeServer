// F3 фичи «Резолвинг ссылок по worktree»: sessionId чата доезжает до FileViewer по всем
// путям открытия файла из чата. ChatPanel → WorkspacePage → (мобильная ветка | DesktopWorkspace)
// → FileViewer. ChatPanel и FileViewer — заглушки, ловящие пропсы; WorkspacePage настоящий.
//
// Мутация: убрать sessionId={openFileSessionId} в любой из трёх точек FileViewer — соответствующий
// кейс краснеет; убрать setOpenFileChat в handleOpenFileFromChat — краснеют все.

import { act } from 'react';
import { createElement } from 'react';
import { readFileSync } from 'node:fs';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { WorkspacePage } from './WorkspacePage';
import { setAllFlags } from '../lib/featureFlags';
import { __test as backNavTest } from '../lib/featureBackNav';
import { __test as indexTest } from '../lib/featuresIndex';
import type { FeatureIndexFeature, FeaturesIndex } from '../types/featureIndex';
import type { Project, Session } from '../types';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../components/chat/__tests__/fakeDom';

// ── localStorage / sessionStorage (loadWorkspaceState, git.ts) — vi.hoisted:
//     модули читают их при импорте, до module-level кода ──
const { store, sstore } = vi.hoisted(() => {
  // self — глобальный worker/браузер; часть зависимостей проверяет typeof self
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
  // Раскладка панельного стора воркспейса (cc_ws_zones) читается при ИМПОРТЕ модуля
  // (module-level singleton). Дефолт [['chats']] не рендерит TasksPanel, а M2-тест
  // (доска на десктопе) открывает доску кнопкой «Доска» в TasksPanel — поэтому панель
  // «Задачи» открыта в раскладке с первого кадра. beforeEach store.clear() не влияет:
  // стор уже прочитал состояние при импорте, дальше живёт в памяти.
  store.set('cc_ws_zones', JSON.stringify({
    left: { layout: [['chats'], ['tasks']] },
    right: { layout: [] },
  }));
  return { store, sstore };
});

// ── workspaceState: activeSession, openFile и leftTab задаются из хранилища (инициализатор WorkspacePage) ──
const wsState = vi.hoisted(() => ({ activeSession: null as unknown, openFile: null as string | null, leftTab: undefined as string | undefined }));
vi.mock('../lib/workspaceState', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/workspaceState')>();
  return {
    ...orig,
    loadWorkspaceState: () => ({ activeSession: wsState.activeSession, openFile: wsState.openFile, leftTab: wsState.leftTab }),
    saveWorkspaceState: () => {},
    loadFileFullscreenPref: () => false, saveFileFullscreenPref: () => {},
    isLeftTab: (t: string) => ['sessions', 'changes', 'tasks', 'personas', 'skills', 'tools', 'knowledge'].includes(t),
  };
});

// ── signalr: экспортируемые символы → no-op (подписки → cleanup, действия → Promise) ──
vi.mock('../lib/signalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve();
  return {
    __esModule: true,
    // подписки (возвращают функцию отписки)
    onMessage: sub, onReconnected: sub, onFilesChanged: sub, onPreviewMessage: sub,
    onLocalModelMessage: sub, onGitStatusChanged: sub, onAgentHubMessage: sub,
    // действия (Promise)
    joinProject: act, leaveProject: act, joinSession: act, leaveSession: act,
    joinUser: act, leaveUser: act, sendMessage: act, respondPermission: act,
    interruptSession: act, compactSession: act, answerQuestion: act, respondPlan: act,
    respondTeamPlan: act, respondTeamEscalation: act, setMode: act, resetConnection: act,
    joinPreviewLog: act, leavePreviewLog: act,
  };
});

// ── api: конкретные ответы для методов, которые читают сторы (featuresIndex/getFeature/
//     chats.get), остальное — no-op. mockData — hoisted, задаётся в beforeEach ──
const mockData = vi.hoisted(() => ({ index: null as any, featurePage: null as any, chat: null as any, chats: [] as Session[] }));
vi.mock('../lib/api', () => {
  const deep = (val: any): any => {
    const fn: any = () => Promise.resolve(val ?? null);
    return new Proxy(fn, {
      get: (t, prop) => {
        if (prop === 'then' || prop === Symbol.toPrimitive || prop === Symbol.iterator) return undefined;
        if (prop === 'catch' || prop === 'finally') return () => Promise.resolve(val ?? null);
        return deep(val);
      },
      apply: () => Promise.resolve(val ?? null),
    });
  };
  // namespace-Proxy: любой метод → no-op Promise, кроме явных overrides
  const ns = (overrides: Record<string, () => any>): any => new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      const o = overrides[prop as string];
      return o ?? (() => Promise.resolve(null));
    },
  });
  const api: any = new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'tasks') return ns({
        featuresIndex: () => Promise.resolve(mockData.index),
        getFeature: () => Promise.resolve(mockData.featurePage),
        listAll: () => Promise.resolve([]),
      });
      if (prop === 'chats') return ns({ get: () => Promise.resolve(mockData.chat) });
      // list отдаёт активный чат: эффект «чат-призрак» (WorkspacePage) сбрасывает
      // activeSession в null, если его id нет в списке — пустой список молча терял бы чат.
      if (prop === 'sessions') return ns({ list: () => Promise.resolve(mockData.chats.length ? mockData.chats : (mockData.chat ? [mockData.chat] : [])) });
      if (prop === 'projects') return ns({ list: () => Promise.resolve([]) });
      // personas.list → []: иначе deep(null) отдаёт null → setCtxPersonas(null) →
      // проп personas Composer'а null (не []) → «personas is not iterable» при полном рендере.
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
      return deep(null);
    },
  });
  return { api, setGitSessionContext: () => {} };
});

// ── terminalSignalr (WorkspacePage импортирует * as terminalApi) ──
vi.mock('../lib/terminalSignalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve(null);
  return {
    __esModule: true,
    resetTerminalConnection: act, createTerminal: act, connectTerminal: act,
    listTerminals: act, stopTerminal: act, renameTerminal: act,
    sendTerminalInput: act, resizeTerminal: act, onTerminalMessage: sub,
  };
});

// ── subsystems / registry: слоты и подсистемы no-op ──
vi.mock('../lib/subsystems', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/subsystems')>();
  return { ...orig, SUBSYSTEMS: {}, useSubsystem: () => ({ id: 'x', enabled: false }) };
});
vi.mock('../lib/subsystems/registry', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/subsystems/registry')>();
  return {
    ...orig,
    useSlot: () => [], useSlotItem: () => null,
    REVEAL_PANEL_EVENT: 'cc-reveal-panel', SLOT_WORKSPACE_PANEL_DEF: { id: 'workspace' },
  };
});

// ── projectCapabilities / personas / git: no-op ──
vi.mock('../lib/projectCapabilities', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/projectCapabilities')>();
  return { ...orig, useProjectFeature: () => false, isLocalProject: () => false, featureReason: () => null };
});
vi.mock('../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/personas')>();
  return {
    ...orig,
    usePersonasVersion: () => 0, getPersonaById: () => null,
    getPersonasSnapshot: () => [], ensurePersonasLoaded: () => Promise.resolve(), personaLabel: () => '',
  };
});
vi.mock('../lib/git', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/git')>();
  return {
    ...orig,
    useGitState: () => ({ status: null, unpushed: [], changedBy: new Map() }),
    ensureGit: () => Promise.resolve(),
    loadUnpushedLog: () => Promise.resolve([]), setActiveSessionForChangedBy: () => {},
  };
});

// ── SessionList (сайдбар): заглушка — display:none, но в DOM. N1: «загружает» список
//     (mockSessionList) и прокидывает его владельцу onSessionsListChanged (useEffect, как
//     в реальном SessionList:393) — иначе на десктопе chatNames/chatPersonas пусты. ──
const mockSessionList = vi.hoisted(() => [] as Session[]);
// onSessionUpdated (handleSessionUpdated) — ловим из мок'а: в fake-DOM на нодах нет
// __reactFiber$ (fiber-walk не работает), а SessionList получает обработчик как пропс.
const sessionListMock = vi.hoisted(() => ({
  onSessionUpdated: null as ((s: Session) => void) | null,
  onSelect: null as ((s: Session, first?: string, autoSelect?: boolean) => void) | null,
}));
vi.mock('../components/SessionList', async () => {
  const { createElement, useEffect } = await import('react');
  return {
    SessionList: ({ onSessionsListChanged, onSessionUpdated, onSelect }: { onSessionsListChanged?: (s: Session[]) => void; onSessionUpdated?: (s: Session) => void; onSelect?: (s: Session, first?: string, autoSelect?: boolean) => void }) => {
      sessionListMock.onSessionUpdated = onSessionUpdated ?? null;
      sessionListMock.onSelect = onSelect ?? null;
      useEffect(() => {
        if (onSessionsListChanged && mockSessionList.length) onSessionsListChanged(mockSessionList);
      }, [onSessionsListChanged, mockSessionList.length]);
      return createElement('div', { 'data-testid': 'sessionlist-stub' });
    },
  };
});

// ── TaskBoard: тяжёлая доска (select/options) не живёт в fake-DOM — заглушка. Для проверки
//     «доска занимает центр» хватает самого факта режима «Задачи» + включённой доски. ──
vi.mock('../features/tasks/board/TaskBoard', async () => {
  const { createElement } = await import('react');
  return { TaskBoard: () => createElement('div', { 'data-testid': 'task-board-stub' }) };
});
// ── BoardToolbar: тулбар доски в сайдбаре (select группировки) не живёт в fake-DOM —
//     заглушка. Рендерится в TasksPanel при boardMode=true (M2, десктоп: клик «Доска»). ──
vi.mock('../features/tasks/board/BoardToolbar', async () => {
  const { createElement } = await import('react');
  return { BoardToolbar: () => createElement('div', { 'data-testid': 'board-toolbar-stub' }) };
});

// ── ProjectPersonasPanel (сайдбар): onShowTeam → handleShowTeam (setTeamCenterOpen(true),
//     minor-4: командный центр — окно центра). Ловим onShowTeam, чтобы триггернуть командный
//     центр в тесте (в fake-DOM на кнопках нет __reactFiber$; проп — доступен). ──
const personasPanelMock = vi.hoisted(() => ({ onShowTeam: null as (() => void) | null }));
vi.mock('../features/personas/ProjectPersonasPanel', async (importOriginal) => {
  const { createElement } = await import('react');
  const orig = await importOriginal<typeof import('../features/personas/ProjectPersonasPanel')>();
  return {
    ...orig,
    ProjectPersonasPanel: ({ onShowTeam }: { onShowTeam?: () => void }) => {
      personasPanelMock.onShowTeam = onShowTeam ?? null;
      return createElement('div', { 'data-testid': 'personas-panel-stub' });
    },
  };
});

// ── useFeaturePage: состояние страницы (run.epic.title) — no-op api отдаёт null ──
// page — mockData.featurePage (динамически): тесты «Планы и макеты» задают plans/mockups.
vi.mock('../lib/featurePage', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/featurePage')>();
  return {
    ...orig,
    useFeaturePage: () => ({ status: 'ok', page: mockData.featurePage ?? { run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0 } }),
  };
});

// ── useProjectTerminals: terminals (re-render после события) ──
vi.mock('../hooks/useProjectTerminals', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../hooks/useProjectTerminals')>();
  return {
    ...orig,
    useProjectTerminals: () => ({
      terminals: [], activeTerminalId: null, setActiveTerminalId: () => {},
      refresh: () => {}, create: () => {}, stop: () => {}, rename: () => {},
    }),
  };
});

// ── videoStage: useVideoCenter — управляемый (MAJOR-1: видео-центр в предикате
//     «центр занят»). useVideoCenter — НАСТОЯЩИЙ useSyncExternalStore (подписка на
//     listeners+emit): смена center триггерит re-render, как в продукте. ──
const videoStageMock = vi.hoisted(() => {
  let center: 'player' | 'picker' | null = null;
  const listeners = new Set<() => void>();
  const emit = () => { for (const cb of listeners) cb(); };
  return {
    get center() { return center; },
    set center(v: 'player' | 'picker' | null) { center = v; emit(); },
    subscribe: (cb: () => void) => { listeners.add(cb); return () => { listeners.delete(cb); }; },
  };
});
vi.mock('../lib/videoStage', async (importOriginal) => {
  const { useSyncExternalStore } = await import('react');
  const orig = await importOriginal<typeof import('../lib/videoStage')>();
  return {
    ...orig,
    useVideoCenter: () => useSyncExternalStore(videoStageMock.subscribe, () => videoStageMock.center, () => videoStageMock.center),
    setVideoStage: (c: any, mode: any) => { videoStageMock.center = c ? mode : null; },
    setVideoPicker: (v: boolean) => { videoStageMock.center = v ? 'picker' : null; },
    closeVideoCenter: () => { videoStageMock.center = null; },
  };
});

// ── useHandsFree / useVoiceInput: textarea композера рендерится, когда не talkActive
//     и не isListening; в fake-окружении аудио-состояние может их включать ──
vi.mock('../hooks/useHandsFree', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../hooks/useHandsFree')>();
  return { ...orig, useHandsFree: () => ({ phase: 'off', active: false, buffer: '', barren: 0, warned: false, seq: 0 }) };
});
vi.mock('../hooks/useVoiceInput', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../hooks/useVoiceInput')>();
  return {
    ...orig,
    useVoiceInput: () => ({ hasSpeech: false, isListening: false, recSeconds: 0, startMic: () => {}, stopMic: () => {} }),
  };
});
vi.mock('../hooks/useMicLevel', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../hooks/useMicLevel')>();
  return {
    ...orig,
    useMicLevel: () => ({ reportMicDead: () => {}, reportCycleEnd: () => {}, reportEngineHeard: () => {}, reportCycleStart: () => {} }),
  };
});
// online=true: в node-env navigator.onLine === undefined → _online falsy → Composer
// рисует офлайн-заглушку без поля ввода. Для проверки черновика композеру нужен полный
// рендер (textarea), поэтому связь фиксируем в online.
vi.mock('../hooks/useOnline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../hooks/useOnline')>();
  return { ...orig, useOnline: () => true };
});

// ── Данные ──
const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;
const session: Session = {
  id: 'chatA', name: 'Чат Дениса', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '',
} as Session;

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', attention: null,
    rail: [
      { key: 'cut', state: 'done', tone: null },
      { key: 'plan', state: 'done', tone: null },
      { key: 'prod', state: 'active', tone: 'run' },
      { key: 'review', state: 'pending', tone: null },
      { key: 'qa', state: 'pending', tone: null },
      { key: 'final', state: 'pending', tone: null },
      { key: 'merge', state: 'pending', tone: null },
    ],
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chatA', keyArchived: false, keyProjectId: 'p1', chatCount: 1,
    ...over,
  };
}

// textarea композера: обёртка data-composer-input → textarea внутри
function composerTextarea(container: any): any {
  const wrap = findNodeByAttr(container, 'data-composer-input', '');
  if (!wrap) return null;
  let hit: any = null;
  const walk = (x: any) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'TEXTAREA') { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(wrap);
  return hit;
}

// Есть ли у узла (или его предка) display:none: файл/ридер под открытой страницей прячутся
// так (без размонтирования). Ходит вверх по parentNode до корня.
function hasHiddenAncestor(node: any): boolean {
  let cur = node;
  while (cur) {
    if (cur.nodeType === 1 && cur.style && cur.style.display === 'none') return true;
    cur = cur.parentNode;
  }
  return false;
}

// ChatPanel и FileViewer — заглушки: фиксируем пропсы, которые им отдаёт WorkspacePage.
const cap = vi.hoisted(() => ({
  chatOnOpenFile: null as ((path: string, sessionId?: string) => void) | null,
  viewer: null as { filePath: string; sessionId?: string } | null,
}));
vi.mock('../components/ChatPanel', async () => {
  const { createElement } = await import('react');
  return {
    ChatPanel: (props: { onOpenFile?: (path: string, sessionId?: string) => void }) => {
      cap.chatOnOpenFile = props.onOpenFile ?? null;
      return createElement('div', { 'data-testid': 'chat-panel-stub' });
    },
  };
});
// GitChangesRail — один из путей «открытие из дерева» (onOpenFile = handleOpenFileFromTree):
// заглушка отдаёт тесту этот обработчик
const treeCap = vi.hoisted(() => ({ onOpenFile: null as ((path: string) => void) | null }));
vi.mock('../components/GitChangesRail', async () => {
  const { createElement } = await import('react');
  return {
    GitChangesRail: (props: { onOpenFile?: (path: string) => void }) => {
      treeCap.onOpenFile = props.onOpenFile ?? null;
      return createElement('div', { 'data-testid': 'git-changes-stub' });
    },
  };
});
vi.mock('../components/FileViewer', async () => {
  const { createElement } = await import('react');
  return {
    FileViewer: (props: { filePath: string; sessionId?: string }) => {
      cap.viewer = { filePath: props.filePath, sessionId: props.sessionId };
      return createElement('div', { 'data-testid': 'file-viewer-stub' });
    },
  };
});

describe('WorkspacePage: sessionId чата доезжает до FileViewer', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    // Мобильная ширина (≤ MOBILE_MAX=600): одноколоночная раскладка, где страница фичи
    // — СОСЕД центра (ChatPanel в DOM, display:none). На десктопе (DesktopWorkspace)
    // страницы фичи в центре нет — она рендерится только в мобильной ветке.
    (globalThis as any).window.innerWidth = 360;
    // PanelZone читает document.documentElement.style — в fake-DOM documentElement null
    (globalThis as any).document.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    // FeatureTimelineTab (якорь этапа, M4) читает document.querySelector — FDoc его не
    // имеет: заглушка (тот же приём, что в FeaturePage.test.ts).
    const doc0 = (globalThis as any).document;
    if (!doc0.querySelector) doc0.querySelector = () => null;
    (globalThis as any).window.innerHeight = 800;
    (globalThis as any).window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
    (globalThis as any).window.history = { state: null, pushState: () => {}, replaceState: () => {}, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
    // Настоящая event-система окна: fakeDom'ов dispatchEvent — no-op, а WorkspacePage
    // вешает слушателей (cc-open-feature, popstate) через addEventListener
    const winListeners: Record<string, Set<Function>> = {};
    (globalThis as any).window.addEventListener = (t: string, f: Function) => { (winListeners[t] ??= new Set()).add(f); };
    (globalThis as any).window.removeEventListener = (t: string, f: Function) => { winListeners[t]?.delete(f); };
    (globalThis as any).window.dispatchEvent = (e: any) => { (winListeners[e?.type] ?? new Set()).forEach(f => f(e)); return true; };
    (globalThis as any).window.setTimeout = (globalThis as any).setTimeout;
    (globalThis as any).window.clearTimeout = (globalThis as any).clearTimeout;
    (globalThis as any).window.setInterval = (globalThis as any).setInterval;
    (globalThis as any).window.clearInterval = (globalThis as any).clearInterval;
    (globalThis as any).window.fetch = () => Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
    (globalThis as any).window.getSelection = () => null;
    (globalThis as any).window.requestAnimationFrame = (cb: any) => { cb(0); return 1; };
    (globalThis as any).window.cancelAnimationFrame = () => {};
    (globalThis as any).requestAnimationFrame = (globalThis as any).window.requestAnimationFrame;
    (globalThis as any).cancelAnimationFrame = (globalThis as any).window.cancelAnimationFrame;
    (globalThis as any).window.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
    (globalThis as any).window.getComputedStyle = () => ({ paddingLeft: '0px', paddingRight: '0px' });
    (globalThis as any).ResizeObserver = (globalThis as any).window.ResizeObserver;
    (globalThis as any).getComputedStyle = (globalThis as any).window.getComputedStyle;
    (globalThis as any).Image = class { set src(_v: string) {} onload: any; onerror: any; width = 0; height = 0; };
    (globalThis as any).MutationObserver = class { observe() {} disconnect() {} takeRecords() { return []; } };
    (globalThis as any).IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
    store.clear(); sstore.clear();
    mockSessionList.length = 0;
    backNavTest.reset();
    indexTest.reset();
    videoStageMock.center = null;
    sessionListMock.onSessionUpdated = null;
    sessionListMock.onSelect = null;
    personasPanelMock.onShowTeam = null;
    setAllFlags({ 'features-overview': true });
    const idx = { features: [feat()], chats: {} } as FeaturesIndex;
    indexTest.setIndex('p1', idx);
    mockData.index = idx;
    mockData.featurePage = { run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0 };
    mockData.chat = session;
    mockData.chats.length = 0;
    wsState.activeSession = session;
    wsState.openFile = null;
    wsState.leftTab = undefined;
    cap.chatOnOpenFile = null;
    cap.viewer = null;
    treeCap.onOpenFile = null;
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

// ── Заглушки: ловим пропсы ChatPanel и FileViewer ──


  const mountPage = () => mount(createElement(WorkspacePage, {
    project,
    auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
    onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
  } as any));

  it('мобильная ветка: файл из ленты чата открывается с sessionId этого чата', async () => {
    (globalThis as any).window.innerWidth = 360;
    mountPage();
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(cap.chatOnOpenFile, 'ChatPanel не получил onOpenFile').not.toBeNull();
    act(() => { cap.chatOnOpenFile!('docs/a.md', 'chatA'); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(cap.viewer).toEqual({ filePath: 'docs/a.md', sessionId: 'chatA' });
  });

  it('десктоп: файл из ленты чата открывается с sessionId этого чата (десктопная раскладка; режим сплит/экран — по предпочтению, тут не проверяется)', async () => {
    (globalThis as any).window.innerWidth = 1600;
    mountPage();
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(cap.chatOnOpenFile, 'ChatPanel не получил onOpenFile').not.toBeNull();
    act(() => { cap.chatOnOpenFile!('docs/a.md', 'chatA'); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(cap.viewer).toEqual({ filePath: 'docs/a.md', sessionId: 'chatA' });
  });

  it('без sessionId (открытие не из чата) FileViewer читает корень проекта', async () => {
    (globalThis as any).window.innerWidth = 360;
    mountPage();
    await act(async () => { vi.advanceTimersByTime(100); });
    act(() => { cap.chatOnOpenFile!('docs/a.md'); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(cap.viewer).toEqual({ filePath: 'docs/a.md', sessionId: undefined });
  });

  // Хвост п.22 (FINAL, шаг П): открытие из дерева — единственный путь, где правка файла
  // разрешена (FileViewer прячет её для файла из неосновного дерева), поэтому «пара»
  // путь+чат обязана сбрасываться. Мутация: убрать setOpenFileChat(null) в
  // handleOpenFileFromTree → viewer остаётся с sessionId прошлого открытия из чата → краснеет.
  it('открытие из дерева сбрасывает пару: тот же файл, открытый из чата, затем из дерева — без sessionId', async () => {
    (globalThis as any).window.innerWidth = 360;
    wsState.leftTab = 'changes';
    mountPage();
    await act(async () => { vi.advanceTimersByTime(100); });
    act(() => { cap.chatOnOpenFile!('docs/a.md', 'chatA'); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(cap.viewer, 'контроль: из чата — с sessionId').toEqual({ filePath: 'docs/a.md', sessionId: 'chatA' });
    expect(treeCap.onOpenFile, 'дерево отдало обработчик открытия').not.toBeNull();
    act(() => { treeCap.onOpenFile!('docs/a.md'); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(cap.viewer, 'из дерева — корень проекта, пара сброшена').toEqual({ filePath: 'docs/a.md', sessionId: undefined });
  });

  // ChatPanel и стена в fake-DOM целиком не монтируются — их звенья проверяем по исходнику.
  it('ChatPanel: все пути открытия файла из ленты несут session.id', () => {
    const src = readFileSync(new URL('../components/ChatPanel.tsx', import.meta.url), 'utf8');
    expect(src).toContain('onOpenFileProp(path, session.id)');
  });

  it('стена: sessionId колонки уходит в оверлей и в FileViewer', () => {
    const src = readFileSync(new URL('./WallPage.tsx', import.meta.url), 'utf8');
    expect(src).toContain("setOverlay({ kind: 'file', path, sessionId })");
    expect(src).toContain('sessionId={overlay.sessionId}');
  });
});
