// Центр WorkspacePage (волна 2 «Обзор фич», Ф2.3): страница фичи — СОСЕД центра,
// а ChatPanel прячется display:none (не размонтируется) — иначе рвётся стрим хода
// и теряется черновик композера. Тест: при открытой странице textarea композера
// остаётся в DOM, его значение (черновик) переживает переход и возврат; «Закрыть»
// «← К чату…» закрывает страницу и возвращает центр; системное «назад» (popstate)
// делает то же (проверка по исходнику).
//
// Окружение node (без jsdom): fakeDom (createRoot) + window.innerWidth=360
// (isMobile) + тяжёлые либы (signalr/api/subsystems/personas/git) — no-op-моки,
// SessionList — заглушка. ChatPanel и FeaturePage — реальные.

import { act } from 'react';
import { createElement } from 'react';
import { readFileSync } from 'node:fs';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { WorkspacePage } from './WorkspacePage';
import { getNav } from '../lib/nav';
import { encodePendingChat } from '../lib/pendingProjectChat';
import { centerWinner } from './workspace/DesktopWorkspace';
import { setAllFlags, __test as flagsTest } from '../lib/featureFlags';
import { setMeFromServer } from '../lib/defaultPersona';
import { __test as backNavTest, recordBackNav, recordFeatureReturn, featureBackNavFrom, backNavFrom, getBackNav } from '../lib/featureBackNav';
import { centerOccupiedByOther, centerFingerprint, CENTER_OCCUPANCY_KEYS, type CenterOccupancy } from '../lib/centerOccupancy';
import { __test as indexTest } from '../lib/featuresIndex';
import { setFeaturePageTab, __test as pageTest } from '../lib/featurePage';
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

describe('WorkspacePage: центр — страница фичи соседом, ChatPanel в DOM', () => {
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
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('открытая страница: ChatPanel в DOM (data-testid), черновик композера переживает возврат, «Закрыть» закрывает страницу', async () => {
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Маркер «ChatPanel в DOM»: data-testid="chat-panel" — есть ТОЛЬКО в ChatPanel, а не
    // в шапке WorkspacePage (там тоже рендерится имя сессии — старый маркер не ловил
    // размонтирование). Условный рендер вместо display:none убирает testid — тест краснеет.
    const chatPanelInDom = () => findNodeByAttr(container, 'data-testid', 'chat-panel') !== null;

    // 1. ChatPanel смонтирован (activeSession задан).
    expect(chatPanelInDom(), 'ChatPanel не смонтирован (data-testid="chat-panel" не в DOM)').toBe(true);

    // 2. Черновик: пишем в textarea композера. Композер ОБЯЗАТЕЛЕН (MAJOR-3):
    //    online=true + personas=[] дают полный рендер карточки, textarea гарантированно
    //    в DOM. КЛЮЧЕВОЙ маркер «ChatPanel в DOM» — data-testid="chat-panel".
    const ta = composerTextarea(container);
    expect(ta, 'textarea композера не в DOM (композер не отрендерился)').not.toBeNull();
    const draft = 'черновик хода';
    act(() => { (reactProps(ta!) as any).onChange({ target: { value: draft, selectionStart: 0 } }); });

    // 3. Открываем страницу фичи из ЧАТА (fromChat: true): обработчик cc-open-feature
    //    сам пишет указатель возврата (chatA → фича) — «Закрыть» «← К чату «…»».
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // 4. Страница фичи показана. Майя M2: видимая подпись КОРОТКАЯ («Чат»),
    //    полный «К чату «…»» — только в title (на 360 длинная ломала шапку).
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(back, '«Закрыть» страницы фичи не найдена').not.toBeNull();
    expect(textNodes(back!).join(''), 'видимая подпись не короткая «Чат»').toBe('Чат');

    // 5. КЛЮЧЕВОЕ (MAJOR-3): ChatPanel НЕ размонтирован при открытой странице
    //    (display:none, а не unmount) — testid в DOM. Мутация «display → условный
    //    рендер» краснит этот тест (testid пропадает из DOM).
    expect(chatPanelInDom(), 'ChatPanel размонтирован (data-testid="chat-panel" пропал из DOM)').toBe(true);
    // 6. Композер (обязательно): textarea в DOM и черновик переживает открытие страницы.
    const taAfterOpen = composerTextarea(container);
    expect(taAfterOpen, 'textarea композера пропал из DOM (ChatPanel размонтирован)').not.toBeNull();
    expect((reactProps(taAfterOpen!) as any).value, 'черновик композера потерян при открытой странице').toBe(draft);

    // 7. Возврат: клик по «Закрыть» → страница фичи закрывается (центр возвращается).
    act(() => { (reactProps(back!.childNodes[0]) as any).onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), '«Закрыть» не скрыта после возврата').toBeNull();
    // ChatPanel/черновик переживают переход: страница закрыта, центр снова чат.
    expect(chatPanelInDom(), 'центр не вернулся к чату (ChatPanel пропал) после возврата').toBe(true);
    // Композер (обязательно): textarea в DOM и черновик пережил возврат.
    const taAfterReturn = composerTextarea(container);
    expect(taAfterReturn, 'textarea композера пропал после возврата').not.toBeNull();
    expect((reactProps(taAfterReturn!) as any).value, 'черновик композера не пережил возврат').toBe(draft);
  });

  // M4: страница открыта на «Чатах» → клик по точке (cc-open-feature с stage) → вкладка
  // ЯВНО «Хронология» (setPage сбрасывает _tab только при смене fid — повторный клик по
  // точке на уже открытой фиче вкладку бы не сменил) + якорь этапа; вкладка «Хронология»
  // читает якорь и ГАСИТ его (clearFeatureStageFocus) — «Фича →» без stage старый этап
  // не раскроет. Мутации: убрать setFeaturePageTab('timeline') → вкладка «chats» → red;
  // убрать clearFeatureStageFocus → stageFocus() не null → red.
  it('M4: страница на «Чатах» → точка (stage) → «Хронология» + якорь гашен; «Фича →» без stage → якоря нет', async () => {
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // 1. Открыть страницу фичи из чата (без stage): вкладка — дефолт «Хронология».
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(pageTest.tab(), 'дефолтная вкладка — «Хронология»').toBe('timeline');
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница фичи не открыта').not.toBeNull();

    // 2. Пользователь на вкладке «Чаты».
    setFeaturePageTab('chats');
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(pageTest.tab()).toBe('chats');

    // 3. Клик по точке: cc-open-feature с stage.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true, stage: 'prod' },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // 4. Вкладка ЯВНО «Хронология» (не «Чаты» — старый дефект).
    expect(pageTest.tab(), 'клик по точке не переключил на «Хронологию»').toBe('timeline');
    // 5. «Хронология» смонтирована и ГАСИТ якорь (clearFeatureStageFocus после прочтения).
    expect(pageTest.stageFocus(), 'якорь этапа не гашен вкладкой «Хронология»').toBeNull();

    // 6. «Фича →» без stage: якорь не ставится заново (старый этап не раскроется).
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(pageTest.stageFocus(), '«Фича →» без stage не должна ставить якорь').toBeNull();
    expect(pageTest.tab(), 'вкладка осталась «Хронология»').toBe('timeline');
  });

  it('системное «назад» (popstate): setOpenFeatureId по снимку — в исходнике центра', () => {
    // Страница слишком велика для полного прогона popstate без DOM-истории; связку
    // «назад → закрытие страницы» проверяем по исходнику (паттерн workspaceLeaveActiveChat).
    const src = readFileSync(new URL('./WorkspacePage.tsx', import.meta.url), 'utf8');
    // Обработчик popstate выставляет openFeatureId из снимка навигации.
    expect(src, 'popstate не выставляет openFeatureId').toContain('setOpenFeatureId(s.feature ?? null)');
    // Обёртка центра (FeatureCenter) прячет ChatPanel display:none (не размонтирует) при
    // открытой странице: общий паттерн мобильной и десктопной раскладки.
    // centerHidden = open && !yielded: страница открыта и не уступила файл → центр скрыт.
    const centerSrc = readFileSync(new URL('../components/feature/FeatureCenter.tsx', import.meta.url), 'utf8');
    expect(centerSrc, 'центр не прячет ChatPanel display:none').toContain("display: centerHidden ? 'none' : 'flex'");
  });

  it('диплинк #/project/p1/feature/f1: страница открывается при первом монтировании (чат в DOM, «Закрыть»)', async () => {
    // Свежая загрузка/перезагрузка: history.state ещё не сидирован (null), адрес — диплинк.
    // openFeatureId инициализируется из хэша (getNav() null → parseHash).
    (globalThis as any).window.location.hash = '#/project/p1/feature/f1';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница фичи открыта: «Закрыть» есть (указателя нет → «← К ключевому чату»).
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(back, '«Закрыть» страницы фичи не найдена (диплинк не открыл страницу)').not.toBeNull();
    // ChatPanel НЕ размонтирован (display:none): testid в DOM.
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'), 'ChatPanel не в DOM (диплинк)').not.toBeNull();
  });

  it('диплинк при выключенном флаге: сегмент feature игнорируется (страница не открывается)', async () => {
    setAllFlags({ 'features-overview': false });
    (globalThis as any).window.location.hash = '#/project/p1/feature/f1';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница не открыта: ни «Закрыть», ни testid feature-page (сегмент игнорируется).
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), '«Закрыть» есть при выключенном флаге').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница фичи открыта при выключенном флаге').toBeNull();
  });

  it('«Закрыть»: navReplace синхронизирует URL (feature исчезает из хэша), «назад» ведёт туда же', async () => {
    // Фейк-история, которая реально записывает hash: navReplace/navPush пишут toHash,
    // и window.location.hash отражает снимок (в beforeEach replaceState — no-op).
    const win = globalThis as any;
    win.window.history = {
      state: null,
      pushState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      replaceState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      back: () => {}, go: () => {}, forward: () => {}, length: 1,
    };
    win.window.location.hash = '';
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Открываем страницу (указатель чат → фича): navPush пишет feature в hash.
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    act(() => {
      win.window.dispatchEvent(new CustomEvent('cc-open-feature', { detail: { featureId: 'f1', fromChatId: 'chatA' } }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(win.window.location.hash, 'после открытия feature не в hash').toContain('/feature/f1');

    // Клик по «Закрыть» (kind 'chat', chatA = текущий чат): navReplace на снимок БЕЗ feature.
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(back, '«Закрыть» не найдена').not.toBeNull();
    act(() => { (reactProps(back!.childNodes[0]) as any).onClick(); });
    // Хэш больше не содержит feature («Закрыть» и история — в одно место).
    expect(win.window.location.hash, 'feature остался в hash после возврата').not.toContain('/feature/');
    // Снимок истории (history.state) тоже без feature: «назад» ведёт туда же, что «Закрыть».
    expect(win.window.history.state?.feature ?? null, 'feature остался в снимке истории').toBeNull();
  });

  // === C1: десктоп (1280) — страница фичи в DesktopWorkspace, не только мобильная ветка ===
  it('C1: десктоп 1280 — страница фичи в DOM, ChatPanel в DOM (скрыт), возврат восстанавливает чат', async () => {
    // Десктопная раскладка (DesktopWorkspace, не мобильная ветка): страница фичи — СОСЕД
    // центра, ChatPanel прячется display:none (не размонтируется).
    (globalThis as any).window.innerWidth = 1280;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    const chatPanelInDom = () => findNodeByAttr(container, 'data-testid', 'chat-panel') !== null;
    // 1. ChatPanel смонтирован (activeSession задан).
    expect(chatPanelInDom(), 'ChatPanel не смонтирован (десктоп)').toBe(true);

    // 2. Открываем страницу фичи из чата (fromChat: true).
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // 3. КЛЮЧЕВОЕ (C1): страница фичи в DOM на ДЕСКТОПЕ (было: только мобильная ветка).
    //    Мутация «убрать вставку страницы в десктоп» (FeatureCenter из DesktopWorkspace)
    //    красит этот тест: feature-page/feature-page-close пропадают из DOM.
    const page = findNodeByAttr(container, 'data-testid', 'feature-page');
    expect(page, 'страница фичи не в DOM на десктопе (C1)').not.toBeNull();
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(back, '«Закрыть» страницы фичи не найдена (C1)').not.toBeNull();

    // 4. ChatPanel НЕ размонтирован (display:none): testid в DOM.
    expect(chatPanelInDom(), 'ChatPanel размонтирован на десктопе (C1)').toBe(true);

    // 5. Возврат: клик по «Закрыть» → страница закрывается, центр (чат) возвращается.
    act(() => { (reactProps(back!.childNodes[0]) as any).onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница не скрыта после возврата (C1)').toBeNull();
    expect(chatPanelInDom(), 'чат не восстановлен после возврата (C1)').toBe(true);
  });

  // === Сквозной тест гейта владельца (хвост вердикта bb0a70c7) ===
  // Цепочка project.ownerId → WorkspacePage.tsx:2446 → FeatureCenter.tsx:42 →
  // FeaturePage.tsx:127 (canClose) раньше не проверялась: в FeaturePage.test.ts проп
  // подётся напрямую. Мутация «убрать projectOwnerId из вызова FeatureCenter» красит
  // первое утверждение (кнопка не появляется ни у кого).
  it('гейт владельца через цепочку: «Закрыть фичу» только владельцу проекта', async () => {
    (globalThis as any).window.innerWidth = 1280;
    const ownedProject = { ...project, ownerId: 'u-own' } as Project;
    act(() => { setMeFromServer({ userId: 'u-own', username: 'andrey', role: 'user' }); });
    const { container } = mount(createElement(WorkspacePage, {
      project: ownedProject,
      auth: { serverUrl: 'http://x', token: 't', username: 'andrey', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close-btn'), 'владельцу «Закрыть фичу» есть').not.toBeNull();
    // Смена me на не-владельца: тот же рендер, кнопка исчезает (гейт живой, не разовый).
    act(() => { setMeFromServer({ userId: 'u-other', username: 'other', role: 'user' }); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close-btn'), 'не владельцу кнопки нет').toBeNull();
  });

  // === N1: десктоп 1280 — названия/персоны чатов фичи приходят через SessionList-колбэк ===
  // Шов: DesktopWorkspace монтирует SessionList БЕЗ onSessionsListChanged → sessions в
  // WorkspacePage пусты → chatNames/chatPersonas пусты → у всех чатов фичи «Чат» + круг.
  // Тест ловит именно эту цепочку (SessionList → колбэк → WorkspacePage → FeatureCenter →
  // страница), а не проп напрямую. Мутация «убрать проп из DesktopWorkspace» красит.
  // Заморожен (быстрый трек «страница как файл»): блок «Чаты фичи» убран из рендера —
  // строки чатов (feature-chat-*) больше не на странице. Вернём вместе с блоком.
  it.skip('N1: десктоп 1280 — название чата фичи из SessionList-колбэка (не «Чат»)', async () => {
    (globalThis as any).window.innerWidth = 1280;
    // SessionList «загрузил» список с именованным чатом (session.name = 'Чат Дениса').
    mockSessionList.length = 0;
    mockSessionList.push(session);
    // Чат фичи = тот же session (id chatA = ключевой чат feat()); связь в индексе.
    const idx = {
      features: [feat()],
      chats: { [session.id]: [{ featureId: 'f1', kind: 'curator', taskRole: null, stepRef: null, lane: null }] },
    } as FeaturesIndex;
    indexTest.setIndex('p1', idx);
    mockData.index = idx;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Открываем страницу фичи (из чата).
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // КЛЮЧЕВОЕ (N1): строка чата фичи показывает НАЗВАНИЕ (не «Чат»):
    // SessionList → onSessionsListChanged → WorkspacePage.sessions → chatNames → FeaturePage.
    const row = findNodeByAttr(container, 'data-testid', `feature-chat-${session.id}`);
    expect(row, 'строка чата фичи не найдена (N1)').not.toBeNull();
    expect(
      textNodes(row!).join(''),
      'название чата не пришло через SessionList-колбэк (N1: «Чат» вместо имени)',
    ).toContain(session.name);
  });

  // === M3 + m4: вход из списка (fromChat: false) — «Закрыть» «К списку», не в активный чат ===
  it('M3/m4: вход из списка — «Закрыть» «К списку» (мобила), не в активный (не открытый) чат', async () => {
    // Активный чат (chatA) ≠ ключевой чат фичи (chat-key). m4: «Закрыть» и системное «назад»
    // ведут в одно место — в список (не в ключевой чат и не в активный chatA, который
    // пользователь не открывал: на экране был список).
    const idx = { features: [feat({ keySessionId: 'chat-key' })], chats: {} } as FeaturesIndex;
    indexTest.setIndex('p1', idx);
    mockData.index = idx;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Вход из СПИСКА: fromChat: false, fromChatId = активный чат (chatA).
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: false },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(back, '«Закрыть» страницы фичи не найдена (M3/m4)').not.toBeNull();
    // m4: «Закрыть» «К списку» (мобила), НЕ «К ключевому чату» и НЕ «К чату».
    const text = textNodes(back!).join('');
    expect(text, '«Закрыть» не «К списку» (m4: из списка — в список, не в ключевой/активный чат)').toContain('К списку');
    expect(text, '«Закрыть» ведёт в ключевой чат (m4: должна вести в список)').not.toContain('К ключевому чату');
    expect(text, '«Закрыть» ведёт в активный (не открытый) чат (M3)').not.toContain('К чату');
  });

  // === M1: диплинк — флаги приходят ПОЗЖЕ разбора адреса ===
  // (а) адрес с feature разбирается при НЕЗАГРУЖЕННЫХ флагах (featureId сохранён),
  //     затем флаги приходят ВКЛЮЧЕННЫМИ → страница открывается.
  it('M1(а): диплинк при незагруженных флагах → флаги пришли включёнными → страница открывается', async () => {
    // Флаги ещё НЕ загружены (до ответа /me): parseHash сохраняет featureId, toHash не
    // схлопывает адрес. Инициализатор openFeatureId берёт featureId из хэша (без гейта).
    flagsTest.reset();
    (globalThis as any).window.location.hash = '#/project/p1/feature/f1';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Пока флаги не загружены — страница ещё не показана (гейт по флагу), но адрес цел.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница открыта до прихода флагов').toBeNull();

    // Флаги «пришли» включёнными (ответ /me): страница открывается, «Закрыть» есть.
    act(() => { setAllFlags({ 'features-overview': true }); });
    await act(async () => { vi.advanceTimersByTime(100); });
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(back, '«Закрыть» не появилась после прихода флагов (M1: страница не открылась)').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'), 'ChatPanel не в DOM (M1)').not.toBeNull();
  });

  // (б) флаги пришли ВЫКЛЮЧЕННЫМИ → страница не открывается, адрес приводится к проекту.
  it('M1(б): диплинк, флаги пришли выключенными → страница не открывается, адрес = проект', async () => {
    flagsTest.reset();
    (globalThis as any).window.location.hash = '#/project/p1/feature/f1';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Флаги «пришли» выключенными: страница не открывается, адрес приводится к проекту.
    act(() => { setAllFlags({ 'features-overview': false }); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), '«Закрыть» есть при выключенном флаге (M1б)').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница открыта при выключенном флаге (M1б)').toBeNull();
  });

  // === M1-360: диплинк/перезагрузка на 360 — страница (вид chat) видна, а не список ===
  it('M1-360: диплинк на 360 — контейнер центра (страница) виден display:flex, не display:none', async () => {
    (globalThis as any).window.location.hash = '#/project/p1/feature/f1';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница открыта («Закрыть» + testid feature-page)
    const page = findNodeByAttr(container, 'data-testid', 'feature-page');
    expect(page, 'страница фичи не открыта (M1-360)').not.toBeNull();
    // M1-360: на мобиле страница живёт в виде chat. При featureId в адресе mobileView='chat',
    // поэтому контейнер центра (родитель feature-page) виден (display:flex), а не скрыт
    // (display:none = вид sidebar = список). Мутация «mobileView всегда sidebar» красит:
    // контейнер display:none, на экране список, хотя адрес /feature/{fid}.
    const centerContainer = (page as any).parentNode;
    expect(centerContainer?.style?.display, 'центр (страница) не виден на 360 (M1-360: на экране список)').toBe('flex');
  });

  // === m5: вход из списка → «Закрыть» «К списку» → history.back() (не navReplace) ===
  it('m5: вход из списка — «Закрыть» «К списку» делает history.back() (индекс 0), не navReplace (дубль)', async () => {
    const win = globalThis as any;
    // Фейк-история со стеком и индексом: navPush=pushState, «Закрыть»=history.back()/navReplace.
    // Сценарий: [список] (index 0) → открытие страницы (navPush → index 1) → «Закрыть».
    const listRecord = { screen: 'project', project, view: 'sidebar', file: null, task: null, feature: null };
    const stack: any[] = [listRecord];
    let index = 0;
    win.window.history = {
      get state() { return stack[index]; },
      set state(v: any) { stack[index] = v; },
      pushState: (s: any, _t: string, h: string) => { stack.splice(index + 1); stack.push(s); index++; win.window.location.hash = h; },
      replaceState: (s: any, _t: string, h: string) => { stack[index] = s; win.window.location.hash = h; },
      back: () => { if (index > 0) index--; win.window.location.hash = toHashOf(stack[index]); },
      go: () => {}, forward: () => {},
      get length() { return stack.length; },
    };
    win.window.location.hash = '#/project/p1';

    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Вход из СПИСКА (fromChat: false): navPush страницы поверх списка (index 1)
    act(() => {
      win.window.dispatchEvent(new CustomEvent('cc-open-feature', { detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: false } }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(index, 'страница не открыта (navPush)').toBe(1);

    // Крошка «К списку»: m5 — history.back() (index 0), НЕ navReplace (index остался бы 1)
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(back, '«Закрыть» не найдена (m5)').not.toBeNull();
    act(() => { (reactProps(back!.childNodes[0]) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // КЛЮЧЕВОЕ (m5): index=0 (history.back() снял запись страницы). Мутация «navReplace»
    // красит: index остался бы 1 (дубль [список, список]), первое «назад» — пустой шаг.
    expect(index, '«Закрыть» не сняла запись страницы (m5: navReplace дал дубль, index=1)').toBe(0);
  });

  // === m7: 360, флаг выключен, тёплый браузер (mobileView=sidebar) — адрес не обещает /chat/{id} ===
  it('m7: 360, тёплый браузер, флаг выключен, вид sidebar — адрес = #/project/p1 (без /chat/{id})', async () => {
    const win = globalThis as any;
    // Фейк-история: navReplace пишет hash (toHash), чтобы проверить, что /chat/{id} не появился
    win.window.history = {
      state: null,
      pushState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      replaceState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      back: () => {}, go: () => {}, forward: () => {}, length: 1,
    };
    // Тёплый браузер: пользователь в СПИСКЕ (mobileView='sidebar', без featureId в адресе),
    // но openFeatureId задан из снимка (feature в history.state). Адрес — проект (без feature).
    win.window.location.hash = '#/project/p1';
    win.window.history.state = { screen: 'project', project, view: 'sidebar', feature: 'f1' };
    // Флаги ещё НЕ загружены (до ответа /me)
    flagsTest.reset();
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Флаги «пришли» выключенными → эффект «один раз» приводит адрес к проекту
    act(() => { setAllFlags({ 'features-overview': false }); });
    await act(async () => { vi.advanceTimersByTime(100); });

    // m7: mobileView='sidebar' (список) — адрес НЕ должен обещать /chat/{id} (экран — список).
    // Мутация «всегда chatId» красит: адрес /chat/chatA, а на экране список.
    expect(win.window.location.hash, 'адрес обещает /chat/{id}, а на экране список (m7)').toBe('#/project/p1');
    void container;
  });

  // === m7b: 360, ХОЛОДНЫЙ браузер (feature в адресе → mobileView='chat'), флаг выключен →
  // mobileView сбрасывается в 'sidebar' (адрес #/project/p1 = список, центр скрыт) ===
  it('m7b: 360, холодный браузер (feature в адресе), флаг выключен → mobileView=sidebar (центр скрыт)', async () => {
    const win = globalThis as any;
    // Фейк-история: navReplace пишет hash (toHash), чтобы проверить, что /chat/{id} не появился.
    win.window.history = {
      state: null,
      pushState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      replaceState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      back: () => {}, go: () => {}, forward: () => {}, length: 1,
    };
    // Холодный браузер: feature В АДРЕСЕ → mobileView инициализирован 'chat' (линия 613-615).
    win.window.location.hash = '#/project/p1/feature/f1';
    win.window.history.state = null;
    // Флаги ещё НЕ загружены (до ответа /me)
    flagsTest.reset();
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Флаги «пришли» выключенными → эффект «один раз» приводит адрес к проекту
    act(() => { setAllFlags({ 'features-overview': false }); });
    await act(async () => { vi.advanceTimersByTime(100); });

    // m7b: mobileView сброслен в 'sidebar' → центр (страница/чат) скрыт (display:none),
    // на экране список. Мутация «не сбрасывать mobileView» красит: центр display:flex,
    // хотя адрес #/project/p1 (список).
    const center = findNodeByAttr(container, 'data-testid', 'workspace-center-container');
    expect(center, 'контейнер центра не найден (m7b)').not.toBeNull();
    expect((center as any).style?.display, 'центр виден (display:flex), а mobileView должен быть sidebar (m7b)').toBe('none');
    // Адрес — проект (без feature, без chat): список.
    expect(win.window.location.hash, 'адрес не #/project/p1 (m7b)').toBe('#/project/p1');
    void container;
  });

  // === «Последнее действие выигрывает» — сквозная защита на НАСТОЯЩЕЙ десктопной
  // раскладке (DesktopWorkspace + FeatureCenter с реальными children, не заглушкой).
  // Центр показывает то, что открыто ПОСЛЕДНИМ: страницу фичи или файл. Открыли страницу
  // (вкл. смена фичи A→B) — она поверх ранее открытого файла; открыли файл (любой источник)
  // при открытой странице — файл поверх, страница смонтирована и скрыта; закрыли файл —
  // страница вернулась, закрыли страницу — файл/чат как до. Мутации: «yielded={false}»
  // (страница никогда не уступает) краснеет в (б); «yielded={!!p.openFile}» (уступает
  // ЛЮБОЙ файл — исходный баг) краснеет в (а); «lastOpened сбрасывается только при
  // закрытии страницы» (частный сброс, не на смене) краснеет в (г).

  // Клик по узлу по data-testid (+ опц. title): reactProps.onClick в act().
  function clickNode(container: any, testId: string, title?: string): void {
    let node: any = null;
    const walk = (x: any): void => {
      if (node) return;
      if (x.nodeType === 1 && x.getAttribute?.('data-testid') === testId && (title === undefined || x.getAttribute?.('title') === title)) { node = x; return; }
      for (const c of x.childNodes ?? []) walk(c);
    };
    walk(container);
    expect(node, `узел ${testId}${title ? ` (title=${title})` : ''} не найден`).not.toBeNull();
    const props = reactProps(node);
    expect(props?.onClick, `onClick у ${testId}`).toBeTruthy();
    act(() => { (props!.onClick as () => void)(); });
  }

  // Закрыть файл в центре: крестик FileViewer — это кнопка CenterViewShell (title «Закрыть»,
  // родитель SPAN — обёртка closeTestId). Ищем ВНУТРИ feature-center-center (страница фичи —
  // в feature-center-page). Другая кнопка «Закрыть» в FileViewer (parent DIV) — не та.
  function clickFileClose(container: any): void {
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect(center, 'центр (feature-center-center) не найден').not.toBeNull();
    let node: any = null;
    const walk = (x: any): void => {
      if (node) return;
      if (x.nodeType === 1 && x.getAttribute?.('title') === 'Закрыть' && x.parentNode?.nodeType === 1 && x.parentNode?.nodeName === 'SPAN') { node = x; return; }
      for (const c of x.childNodes ?? []) walk(c);
    };
    walk(center);
    expect(node, 'крестик закрытия файла (CenterViewShell, parent SPAN) не найден в центре').not.toBeNull();
    const props = reactProps(node);
    expect(props?.onClick, 'onClick у крестика закрытия файла').toBeTruthy();
    act(() => { (props!.onClick as () => void)(); });
  }

  // (а) файл открыт ДО страницы (из дерева) → «Фича →» → страница ВИДНА (центр скрыт):
  // страница открыта ПОСЛЕ файла → последнее действие (страница) выигрывает.
  it('major(а): файл открыт (из дерева) → «Фича →» → страница видна, центр (файл) скрыт', async () => {
    (globalThis as any).window.innerWidth = 1280;
    // Файл открыт ДО страницы (из дерева): wsState.openFile задаёт инициализатор openFile.
    wsState.openFile = 'docs/mockups/foo.html';
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Открываем страницу фичи из чата.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница фичи смонтирована.
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect(page, 'обёртка страницы фичи не смонтирована').not.toBeNull();
    // КЛЮЧЕВОЕ (major а): страница ВИДНА (display:flex) — «чужой» файл её не вытеснил.
    expect((page as any).style?.display, 'страница скрыта «чужим» (файл из дерева) (major а)').toBe('flex');
    // Центр (файл) скрыт (display:none): страница показана поверх.
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'центр (файл) не скрыт (major а)').toBe('none');
  });

  // (б) со страницы «Открыть макет» → файл ВИДИМ, страница СКРЫТА; закрыл файл → страница вернулась.
  it('major(б): «Открыть макет» → файл виден, страница скрыта; закрыл файл → страница вернулась', async () => {
    (globalThis as any).window.innerWidth = 1280;
    // Страница фичи с макетом (existing html) + вкладка «Планы и макеты».
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [{
        path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved',
        files: [{ path: 'docs/mockups/foo.html', exists: true }],
        source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null,
      }],
    };
    setFeaturePageTab('plans');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Открываем страницу фичи.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // Выбрать макет (строка) → карточка деталей.
    clickNode(container, 'mockup-row', 'docs/mockups/foo.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    // Клик «Открыть макет» → onOpenFile (onOpenFileFromFeature) → файл открыт.
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });

    // КЛЮЧЕВОЕ (major б): файл ВИДИМ (центр display:flex), страница СКРЫТА (display:none).
    // Мутация «yielded={false}» (буквальный возврат исходного бага) краснеет: страница
    // осталась бы видной (display:flex), а файл — в скрытом центре (display:none).
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'центр (файл) не виден (major б)').toBe('flex');
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((page as any).style?.display, 'страница не скрыта (major б)').toBe('none');

    // Закрыл файл (крестик) → страница вернулась (display:flex), центр (чат) скрыт:
    // файл не сбрасывался при открытии страницы, а закрытие файла возвращает страницу.
    clickFileClose(container);
    await act(async () => { vi.advanceTimersByTime(100); });
    const pageAfterClose = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((pageAfterClose as any).style?.display, 'страница не вернулась после закрытия файла (major б)').toBe('flex');
    const centerAfterClose = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((centerAfterClose as any).style?.display, 'центр не скрыт после закрытия файла (major б)').toBe('none');
  });

  // (в) страница открыта → открыли файл (любой источник; правило «последнее действие
  // выигрывает» не различает «свой макет» и «чужой файл» — старый критерий «только свой
  // файл» убран) → файл ВИДИМ, страница скрыта. Два макета: первый открыт, затем второй
  // (другой путь) — последний (чужой) выигрывает.
  it('(в): страница открыта → открыли файл (любой источник) → файл виден, страница скрыта', async () => {
    (globalThis as any).window.innerWidth = 1280;
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [
        { path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved', files: [{ path: 'docs/mockups/foo.html', exists: true }], source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null },
        { path: 'docs/mockups/bar.html', title: 'Bar', status: 'approved', files: [{ path: 'docs/mockups/bar.html', exists: true }], source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null },
      ],
    };
    setFeaturePageTab('plans');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Открываем страницу фичи.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // Открываем первый макет (foo.html) — файл после страницы.
    clickNode(container, 'mockup-row', 'docs/mockups/foo.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });

    // Возврат к сетке (5В: деталь на всю ширину, список закрыт) — открываем ВТОРОЙ,
    // другой макет (bar.html) — «чужой» файл, тоже после страницы.
    clickNode(container, 'plans-back-to-list');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-row', 'docs/mockups/bar.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });

    // КЛЮЧЕВОЕ (в): файл (последний открытый) ВИДИМ (центр display:flex), страница СКРЫТА.
    // Мутация «yielded={false}» (страница никогда не уступает) краснеет: страница осталась
    // бы видной, а файл — в скрытом центре.
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'центр (файл) не виден (в)').toBe('flex');
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((page as any).style?.display, 'страница не скрыта (в)').toBe('none');
  });

  // Ф-ah (мобила): страница открыта → открыли файл ПОСЛЕ неё → файл вытесняет страницу
  // (как на десктопе); повторное «Фича →» возвращает страницу поверх файла.
  it('Ф-ah (мобила): страница → файл после неё → файл виден, страница скрыта; «Фича →» снова — страница поверх', async () => {
    (globalThis as any).window.innerWidth = 360;
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [
        { path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved', files: [{ path: 'docs/mockups/foo.html', exists: true }], source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null },
      ],
    };
    setFeaturePageTab('plans');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    clickNode(container, 'mockup-row', 'docs/mockups/foo.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });

    // Файл — последний открытый: контейнер центра скрыт, страница скрыта, файл виден.
    // Мутация «pageYieldsToFile = false» краснит все три утверждения.
    expect((findNodeByAttr(container, 'data-testid', 'workspace-center-container') as any).style?.display,
      'контейнер центра не уступил файлу (мобила)').toBe('none');
    expect((findNodeByAttr(container, 'data-testid', 'feature-center-page') as any).style?.display,
      'страница не скрыта при открытом позже файле (мобила)').toBe('none');
    const fileName = findNodeByAttr(container, 'data-testid', 'file-name');
    expect(fileName, 'файл не в DOM (мобила)').not.toBeNull();
    // Не только в DOM, но и ВИДЕН: мутации «обёртка файла display:none» и
    // «featurePageOpen ? none : block» (поведение до дельты) краснят это утверждение.
    expect(hasHiddenAncestor(fileName!), 'файл СКРЫТ при уступке страницы (мобила)').toBe(false);

    // Обратный переход: повторное открытие страницы — страница поверх файла.
    await openFeature(container);
    expect((findNodeByAttr(container, 'data-testid', 'feature-center-page') as any).style?.display,
      'страница не вернулась поверх файла (мобила)').toBe('flex');
    expect((findNodeByAttr(container, 'data-testid', 'workspace-center-container') as any).style?.display,
      'контейнер страницы не виден (мобила)').toBe('flex');
  });

  // (г) зонд A Глеба: страница A → «Открыть макет» (файл открыт, страница уступила) →
  // «Фича →» на ДРУГУЮ фичу B → страница B ВИДНА (смена фичи = новое открытие → страница
  // снова поверх файла). Старый баг: признак «свой файл» сбрасывался только при ЗАКРЫТИИ
  // страницы, а не при СМЕНЕ → страница B монтировалась, но оставалась display:none.
  it('(г): страница A → «Открыть макет» → «Фича →» на B → страница B видна (зонд A)', async () => {
    (globalThis as any).window.innerWidth = 1280;
    // Две фичи в индексе: A (f1) и B (f2).
    const idx = {
      features: [feat(), feat({ featureId: 'f2', title: 'Вторая фича' })],
      chats: {},
    } as FeaturesIndex;
    indexTest.setIndex('p1', idx);
    mockData.index = idx;
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [{
        path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved',
        files: [{ path: 'docs/mockups/foo.html', exists: true }],
        source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null,
      }],
    };
    setFeaturePageTab('plans');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Страница A (f1).
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // «Открыть макет» → файл открыт, страница A уступила (скрыта).
    clickNode(container, 'mockup-row', 'docs/mockups/foo.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });
    const centerBefore = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((centerBefore as any).style?.display, 'файл не виден после «Открыть макет» (г)').toBe('flex');

    // «Фича →» на ДРУГУЮ фичу B (f2): смена фичи = новое открытие страницы.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f2', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // КЛЮЧЕВОЕ (г, зонд A): страница B ВИДНА (display:flex), центр (файл A) скрыт.
    // Мутация «lastOpened сбрасывается только при закрытии страницы» (частный сброс)
    // краснеет: страница B смонтирована, но осталась бы display:none (файл A виден).
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect(page, 'страница B не смонтирована (г)').not.toBeNull();
    expect((page as any).style?.display, 'страница B скрыта (утечка: файл A поверх) (г)').toBe('flex');
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'центр (файл A) не скрыт при открытой странице B (г)').toBe('none');
  });

  // (д) три под-сценария: (1) два файла подряд — последний выигрывает; (2) быстрый
  // повторный клик «Открыть макет» — файл остаётся видим (нет сброса/мигания); (3)
  // закрыли страницу при открытом файле — файл виден (страница не удерживает центр).
  it('(д): два файла подряд → последний; быстрый повторный клик → файл; закрыли страницу при открытом файле → файл', async () => {
    (globalThis as any).window.innerWidth = 1280;
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [
        { path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved', files: [{ path: 'docs/mockups/foo.html', exists: true }], source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null },
        { path: 'docs/mockups/bar.html', title: 'Bar', status: 'approved', files: [{ path: 'docs/mockups/bar.html', exists: true }], source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null },
      ],
    };
    setFeaturePageTab('plans');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Страница.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // (1) Два файла подряд: foo.html, возврат к сетке (5В), затем bar.html → последний
    // (bar) выигрывает.
    clickNode(container, 'mockup-row', 'docs/mockups/foo.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'plans-back-to-list');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-row', 'docs/mockups/bar.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });
    let center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'последний (второй) файл не виден (д.1)').toBe('flex');

    // (2) Быстрый повторный клик «Открыть макет» (тот же bar.html): файл остаётся
    // победителем — файл виден, страница скрыта (не размонтирована). Подшаг краснеет при
    // мутации yielded={false}: тогда страница осталась бы видимой (flex), а файл — в
    // скрытом центре (none).
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });
    center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не виден после повторного клика (д.2)').toBe('flex');
    const pageAfterRepeat = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((pageAfterRepeat as any).style?.display, 'страница не скрыта после повторного клика (д.2: файл должен остаться победителем)').toBe('none');

    // (3) Закрыли страницу (крестик «Закрыть» страницы) при открытом файле → файл виден.
    const pageClose = findNodeByAttr(container, 'data-testid', 'feature-page-close');
    expect(pageClose, '«Закрыть» страницы не найдено (д.3)').not.toBeNull();
    act(() => { (reactProps(pageClose!.childNodes[0]) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница закрыта (не смонтирована), центр (файл) виден.
    expect(findNodeByAttr(container, 'data-testid', 'feature-center-page'), 'страница не закрыта (д.3)').toBeNull();
    center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не виден после закрытия страницы (д.3)').toBe('flex');
  });

  // === П1: файл открыт ДО страницы (диплинк = действие, не «восстановление») → страница →
  // повторное открытие ТОГО ЖЕ файла → файл виден. Повторное открытие того же файла —
  // событие (seq), а не вывод из сравнения значений: файл снова «позже» страницы и выигрывает.
  // Старая логика (prevFileRef): openFile не изменился → события нет → страница осталась бы
  // видимой → тест краснеет.
  it('П1: файл (диплинк) → страница → повторное открытие ТОГО ЖЕ файла → файл виден', async () => {
    (globalThis as any).window.innerWidth = 1280;
    // Макет на странице — для «Открыть макет» (тот же файл, что в диплинке).
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [{
        path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved',
        files: [{ path: 'docs/mockups/foo.html', exists: true }],
        source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null,
      }],
    };
    setFeaturePageTab('plans');
    // Диплинк файла (cc_pending_file): файл открывается действием при монтировании.
    sstore.set('cc_pending_file', 'p1|docs/mockups/foo.html');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Файл открыт (центр виден).
    let center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не виден после диплинка (П1)').toBe('flex');

    // Страница: открыта ПОСЛЕ файла → страница видна, файл скрыт.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    let page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((page as any).style?.display, 'страница не видна после открытия (П1)').toBe('flex');
    center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не скрыт под страницей (П1)').toBe('none');

    // Повторное открытие ТОГО ЖЕ файла («Открыть макет», тот же путь): событие →
    // файл снова «позже» страницы → файл виден, страница скрыта.
    clickNode(container, 'mockup-row', 'docs/mockups/foo.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });
    center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не виден после повторного открытия того же файла (П1)').toBe('flex');
    page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((page as any).style?.display, 'страница не скрыта после повторного открытия того же файла (П1)').toBe('none');
  });

  // === П2: страница → «Открыть макет» → «Фича →» на ТУ ЖЕ фичу → страница видна.
  // Повторное «Фича →» на ту же фичу — событие (seq), а не вывод из сравнения id:
  // страница снова «позже» файла и выигрывает. Старая логика (prevFeatureRef):
  // openFeatureId не изменился → события нет → файл остался бы победителем → тест краснеет.
  it('П2: страница → «Открыть макет» → «Фича →» на ТУ ЖЕ фичу → страница видна', async () => {
    (globalThis as any).window.innerWidth = 1280;
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [{
        path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved',
        files: [{ path: 'docs/mockups/foo.html', exists: true }],
        source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null,
      }],
    };
    setFeaturePageTab('plans');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Страница.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // «Открыть макет» → файл победитель (страница скрыта).
    clickNode(container, 'mockup-row', 'docs/mockups/foo.html');
    await act(async () => { vi.advanceTimersByTime(100); });
    clickNode(container, 'mockup-open-area');
    await act(async () => { vi.advanceTimersByTime(100); });
    let center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не виден после «Открыть макет» (П2)').toBe('flex');

    // «Фича →» на ТУ ЖЕ фичу (f1): повторное открытие = событие → страница победитель.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // КЛЮЧЕВОЕ (П2): страница ВИДНА (display:flex), центр (файл) скрыт.
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((page as any).style?.display, 'страница не видна после повторного «Фича →» на ту же фичу (П2)').toBe('flex');
    center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'центр (файл) не скрыт при повторном «Фича →» (П2)').toBe('none');
  });

  // === П3: два события «открыли» в ОДНОМ коммите (popstate восстанавливает файл и страницу
  // одним снимком) — порядок по seq, не по совпадению в одном эффекте.
  // (а) файл, затем страница → страница; (б) страница, затем файл (в одном коммите) → файл.
  it('П3(а): файл (диплинк), затем страница → страница выигрывает', async () => {
    (globalThis as any).window.innerWidth = 1280;
    sstore.set('cc_pending_file', 'p1|docs/mockups/foo.html');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница открыта ПОСЛЕ файла → страница выигрывает.
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((page as any).style?.display, 'страница не выиграла (П3а)').toBe('flex');
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не скрыт (П3а)').toBe('none');
  });

  it('П3(б): страница, затем файл в ОДНОМ коммите (popstate) → файл выигрывает', async () => {
    (globalThis as any).window.innerWidth = 1280;
    mockData.featurePage = {
      run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0,
      plans: [],
      mockups: [{
        path: 'docs/mockups/foo.html', title: 'Foo', status: 'approved',
        files: [{ path: 'docs/mockups/foo.html', exists: true }],
        source: 'header', taskId: null, authorPersonaId: null, modifiedAt: null,
      }],
    };
    setFeaturePageTab('plans');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Страница открыта (seq 1).
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // popstate: снимок несёт И файл, И страницу — оба «открытия» в одном коммите.
    // Обработчик ставит seq страницы, затем seq файла → файл «позже» → файл выигрывает.
    (globalThis as any).window.dispatchEvent({
      type: 'popstate',
      state: { screen: 'project', project, view: 'sidebar', file: 'docs/mockups/foo.html', feature: 'f1' },
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // КЛЮЧЕВОЕ (П3б): файл (последнее событие) ВИДИМ, страница скрыта.
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не выиграл при popstate с обоими (П3б)').toBe('flex');
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect((page as any).style?.display, 'страница не скрыта при popstate с обоими (П3б)').toBe('none');
  });

  // === П4: холодный старт — хэш фичи + сохранённый openFile → страница.
  // Страница открыта при монтировании из хэша (seq ставится при монтировании);
  // сохранённый файл — «восстановлен», а не «открыт» (fileOpenedAt = 0) → страница
  // выигрывает.
  it('П4: холодный старт: хэш фичи + сохранённый файл → страница', async () => {
    (globalThis as any).window.innerWidth = 1280;
    (globalThis as any).window.location.hash = '#/project/p1/feature/f1';
    (globalThis as any).window.history.state = null;
    // Сохранённый файл (workspaceState): «восстановлен», а не «открыт» — fileOpenedAt = 0.
    wsState.openFile = 'docs/mockups/foo.html';
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница смонтирована и ВИДНА (победитель холодного старта).
    const page = findNodeByAttr(container, 'data-testid', 'feature-center-page');
    expect(page, 'страница не смонтирована (П4)').not.toBeNull();
    expect((page as any).style?.display, 'страница не видна при холодном старте (П4)').toBe('flex');
    // Восстановленный файл не вытесняет страницу (он «не позже» по seq).
    const center = findNodeByAttr(container, 'data-testid', 'feature-center-center');
    expect((center as any).style?.display, 'файл не скрыт при холодном старте (П4)').toBe('none');
  });
  // ═══════════════════════════════════════════════════════════════════════════
  // Автоскрытие (дефект 20.10): страница фичи уступает центр любому другому окну
  // (файл/задача/граф/…) и при явном выборе чата. Единый источник — otherCenterOpen
  // (вычисляемый признак) + closeFeaturePage (явный выбор чата).
  // ═══════════════════════════════════════════════════════════════════════════

  // Открыть страницу фичи (cc-open-feature, fromChat: true) и дождаться рендера.
  const openFeature = async (container: any) => {
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });
    void container;
  };

  // История, которая реально записывает hash (navPush/navReplace пишут toHash): в beforeEach
  // pushState/replaceState — no-op, и window.location.hash не отражает снимок. Для проверок
  // «адрес /feature/» (M2) и «адрес без /feature/» (M1) подменяем history на запись hash.
  const recordHistory = () => {
    const win = globalThis as any;
    win.window.history = {
      state: null,
      pushState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      replaceState: (s: any, _t: string, h: string) => { win.window.history.state = s; win.window.location.hash = h; },
      back: () => {}, go: () => {}, forward: () => {}, length: 1,
    };
    win.window.location.hash = '';
  };

  // M2 (мобила): файл открыт, явное открытие страницы (badge) рисует её ПОВЕРХ файла
  // (центр display:none, файл в DOM); «Закрыть»/Esc возвращают к файлу под ней.
  it('M2: файл в центре (мобила) → страница ПОВЕРХ (адрес /feature/), «Закрыть» → файл виден', async () => {
    recordHistory();
    (globalThis as any).sessionStorage.setItem('cc_pending_file', 'p1|src/foo.ts');
    (globalThis as any).window.location.hash = '';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);

    // Страница ПОВЕРХ открытого файла (M2): «Закрыть» есть, адрес /feature/.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не ПОВЕРХ открытого файла (M2, мобила)').not.toBeNull();
    expect((globalThis as any).window.location.hash, 'адрес без /feature/ (M2, мобила)')
      .toContain('/feature/');
    // «Закрыть»/Esc → файл под ней снова виден (центр не display:none).
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close')!;
    act(() => { (reactProps(back.childNodes[0]) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница не закрылась по «Закрыть» (M2, мобила)').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'file-name'),
      'файл не вернулся под страницу (M2, мобила)').not.toBeNull();
  });

  // M2 (десктоп): файл открыт, явное открытие страницы рисует её ПОВЕРХ; «Закрыть» → файл.
  it('M2: файл в центре (десктоп) → страница ПОВЕРХ (адрес /feature/), «Закрыть» → файл виден', async () => {
    recordHistory();
    (globalThis as any).window.innerWidth = 1280;
    (globalThis as any).sessionStorage.setItem('cc_pending_file', 'p1|src/foo.ts');
    (globalThis as any).window.location.hash = '';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);

    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи не ПОВЕРХ открытого файла (M2, десктоп)').not.toBeNull();
    expect((globalThis as any).window.location.hash, 'адрес без /feature/ (M2, десктоп)')
      .toContain('/feature/');
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close')!;
    act(() => { (reactProps(back.childNodes[0]) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница не закрылась по «Закрыть» (M2, десктоп)').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'file-name'),
      'файл не вернулся под страницу (M2, десктоп)').not.toBeNull();
  });

  it('автоскрытие: выбор другого чата (мобила) — страница фичи скрыта, адрес без /feature/', async () => {
    const win = globalThis as any;
    // Второй чат (chatB) — в списке (listProjectSessions вернёт его).
    const chatB: Session = { id: 'chatB', name: 'Чат Б', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chat = chatB;
    win.window.location.hash = '';
    win.window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    // Страница открыта.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не открыта перед выбором чата').not.toBeNull();

    // Явный выбор chatB (cc-pending-project-chat → handleSelectSession → closeFeaturePage).
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatB');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница фичи скрыта (чат выбран): «Закрыть» нет.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не скрыта при выборе чата (мобила)').toBeNull();
    // Чат в DOM.
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'),
      'чат не в DOM после выбора (мобила)').not.toBeNull();
    // Адрес не содержит /feature/ (navPush в handleSelectSession записал chatId).
    expect(win.window.location.hash, 'адрес содержит /feature/ после выбора чата (мобила)')
      .not.toContain('/feature/');
  });

  // Ф9b-д3: возврат «страница фичи → чат» записывается ПЕРЕД выбором чата (шторка/попап);
  // closeFeaturePage страницу закрывает, но указатель не гасит — иначе чипа «← к фиче» нет.
  // Мутация (вернуть clearBackNav() в closeFeaturePage) красит тест.
  it('выбор чата из фичи: страница закрыта, указатель возврата в фичу жив (чип)', async () => {
    const win = globalThis as any;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chat = chatB;
    win.window.location.hash = '';
    win.window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    recordFeatureReturn('f1', { kind: 'chat', id: 'chatB' }, 'Моя фича', undefined, false);

    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatB');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });

    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница не закрыта').toBeNull();
    expect(featureBackNavFrom('chatB'), 'указатель возврата в фичу погашен выбором чата')
      .toMatchObject({ featureId: 'f1', title: 'Моя фича' });
  });

  // Ф9b-д4: ОБЫЧНЫЙ выбор другого чата гасит указатель возврата, записанный для чата chatB
  // (иначе позже в chatB всплывёт протухший чип «← к фиче»). Мутация «снять сброс в
  // handleSelectSession» краснит тест; парный выше (цель = выбираемый чат) — не краснит.
  it('выбор ДРУГОГО чата гасит указатель возврата (фича → chatB)', async () => {
    const win = globalThis as any;
    const chatC: Session = { id: 'chatC', name: 'Чат Ц', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatC);
    mockData.chat = chatC;
    win.window.location.hash = '';
    win.window.history.state = null;
    mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    recordFeatureReturn('f1', { kind: 'chat', id: 'chatB' }, 'Моя фича', undefined, false);
    expect(featureBackNavFrom('chatB'), 'указатель не записан (предусловие)').not.toBeNull();

    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatC');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });

    expect(featureBackNavFrom('chatB'), 'указатель «фича → chatB» пережил обычный выбор другого чата').toBeNull();
  });

  // Ф-ал (шов Александра 2д-2): эффект монтирования воркспейса гасит ТОЛЬКО указатели
  // fromKind='chat' («чат → чат» локальны для проекта). Возврат из фичи остаётся: переход
  // «фича → чат другого проекта» настоящий, чип должен появиться. Мутация: вернуть
  // безусловный clearBackNav() в эффекте — первый тест красный; снять fromKind-проверку
  // совсем (никогда не гасить) — красный второй тест.
  it('монтирование другого воркспейса: возврат «фича → чат» не гасится, чип виден (2д-2)', async () => {
    const win = globalThis as any;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chat = chatB;
    win.window.location.hash = '';
    win.window.history.state = null;
    // Переход с страницы фичи записан ДО монтирования нового воркспейса (другое
    // пространство: фича в одном проекте, её ключевой чат — в другом).
    recordFeatureReturn('f1', { kind: 'chat', id: 'chatB' }, 'Моя фича', undefined, false);
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Указатель пережил монтирование нового воркспейса.
    expect(featureBackNavFrom('chatB'), 'указатель «фича → chatB» погашен монтированием воркспейса').not.toBeNull();
    // Выбор chatB — чип «← к фиче» в шапке чата.
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatB');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-back-chip'),
      'чип «← к фиче» не показан после перехода «фича → чат другого проекта»').not.toBeNull();
  });

  it('монтирование другого воркспейса: указатель «чат → чат» гасится (2д-2, парный)', async () => {
    const win = globalThis as any;
    win.window.location.hash = '';
    win.window.history.state = null;
    // «Чат → чат» из предыдущего проекта: локальный указатель, должен быть снят.
    recordBackNav('chatOld', 'chatB');
    expect(backNavFrom('chatB'), 'предусловие: указатель «чат → чат» записан').not.toBeNull();
    mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(getBackNav(), 'указатель «чат → чат» пережил монтирование другого воркспейса').toBeNull();
  });

  it('автоскрытие: выбор другого чата (десктоп) — страница фичи скрыта', async () => {
    const win = globalThis as any;
    win.window.innerWidth = 1280;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chat = chatB;
    win.window.location.hash = '';
    win.window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи не открыта перед выбором чата (десктоп)').not.toBeNull();

    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatB');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });

    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи не скрыта при выборе чата (десктоп)').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'),
      'чат не в DOM после выбора (десктоп)').not.toBeNull();
    expect(win.window.location.hash, 'адрес содержит /feature/ после выбора чата (десктоп)')
      .not.toContain('/feature/');
  });

  // Ф-хв-д (возврат ревью Ф-хв, п.1): запасный переход «чип ← к фиче» из раздела «Чаты».
  // App.tsx пишет снимок с feature + chatId и канал cc_pending_project_chat; WorkspacePage
  // открывает страницу фичи из снимка, а чат выбирается холодным автовыбором — страница
  // НЕ закрывается (регрессия c84677f3c: closeFeaturePage в handleSelectSession закрывала
  // её сразу за выбором). Мутации: вернуть false третьим аргументом в consumePendingProjectChat
  // → красный; снять feature из снимка в App.tsx → красный (тест в App.deeplink.test.ts);
  // снять запись канала → красный (тот же тест).
  const runFallbackTransition = async (mobile: boolean, restored = false) => {
    const win = globalThis as any;
    if (!mobile) win.window.innerWidth = 1280;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chats.push(chatB);
    // холодный старт: активного чата нет; restored — активный чат восстановлен из ws:{projectId}
    if (!restored) wsState.activeSession = null;
    if (restored) { mockSessionList.push(session); mockData.chats.push(session); } // восстановленный чат есть в списке
    mockData.chat = chatB;
    // Ровно то, что делает App.tsx на запасном переходе: navPush-снимок (toHash: сегмент
    // feature приоритетнее chatId) + pending-канал.
    win.window.location.hash = '#/project/p1/feature/f1';
    win.window.history.state = { screen: 'project', project, file: null, chatId: 'chatB', feature: 'f1' };
    win.sessionStorage.setItem('cc_pending_project_chat', encodePendingChat('p1', 'chatB', undefined, true));
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // ChatPanel подгружается асинхронно: ждём появления панели, а не фиксированное время
    for (let i = 0; i < 20 && !findNodeByAttr(container, 'data-testid', 'chat-panel'); i++) {
      await act(async () => { vi.advanceTimersByTime(100); });
    }

    // КЛЮЧЕВОЕ: страница фичи открыта И чат выбран — одновременно.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи не открылась из снимка (запасный переход)').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'на странице фичи нет «Закрыть» (запасный переход)').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'),
      'чат не выбран (запасный переход)').not.toBeNull();
    // Pending-канал потреблён эффектом.
    expect(win.sessionStorage.getItem('cc_pending_project_chat'),
      'pending-канал не очищен (запасный переход)').toBeNull();
    if (restored) {
      expect(win.window.location.hash, 'адрес потерял /feature/ (восстановленный активный чат)')
        .toContain('/feature/');
    }
  };
  it('запасный переход: снимок + канал — страница фичи остаётся, чат выбран (мобила 360)', () => runFallbackTransition(true));
  it('запасный переход: снимок + канал — страница фичи остаётся, чат выбран (десктоп 1280)', () => runFallbackTransition(false));

  // M1 финального ревью: «оставить страницу» несёт сам канал (метка запасного перехода), а не
  // запись истории. Другие писатели (AiLauncher «ждёт ответа», уведомление, инцидент) метку не
  // ставят: событие для чата, из которого открыта страница, закрывает её и выбирает чат.
  // Мутации: вернуть вывод признака из getNav() → красный; снять запись метки в App.tsx →
  // красный тест «запасный переход».
  const runNoMarkSameChat = async (mobile: boolean) => {
    const win = globalThis as any;
    if (!mobile) win.window.innerWidth = 1280;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chats.push(chatB);
    mockData.chat = chatB;
    wsState.activeSession = null;
    // страница открыта из снимка «фича + чат B», канала при монтировании нет
    win.window.location.hash = '#/project/p1/feature/f1';
    win.window.history.state = { screen: 'project', project, file: null, chatId: 'chatB', feature: 'f1' };
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница не открыта (предусловие)').not.toBeNull();
    // «ждёт ответа» из чата B: канал БЕЗ метки
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatB');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    for (let i = 0; i < 20 && !findNodeByAttr(container, 'data-testid', 'chat-panel'); i++) {
      await act(async () => { vi.advanceTimersByTime(100); });
    }
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'переход к чату без метки не закрыл страницу фичи').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'), 'чат не выбран').not.toBeNull();
  };
  it('M1: событие для чата под страницей БЕЗ метки — страница закрыта, чат выбран (десктоп 1280)', () => runNoMarkSameChat(false));
  it('M1: событие для чата под страницей БЕЗ метки — страница закрыта, чат выбран (мобила 360)', () => runNoMarkSameChat(true));

  // Дефект Ф-хв (второй возврат): активный чат восстановлен из ws:{projectId}, затем pending-канал
  // на чат B со снимком feature — страница остаётся, адрес с /feature/. Мутация: вернуть
  // проверку activeSessionRef.current == null вместо признака из снимка → красный.
  it('запасный переход при восстановленном активном чате — страница остаётся (мобила 360)', () => runFallbackTransition(true, true));
  it('запасный переход при восстановленном активном чате — страница остаётся (десктоп 1280)', () => runFallbackTransition(false, true));

  // Находка MINOR (ревью 0fe2753c): строка feature в записи навигации при запасном переходе
  // не была покрыта — в beforeEach pushState/replaceState no-op, и проверки hash проходили
  // по нему, а не по реальному снимку. Здесь история пишет state и hash (как recordHistory),
  // поэтому проверяется и запись, и адрес. Мутация: feature = null в WorkspacePage.tsx
  // (строка 1471) → красный.
  it('запасный переход: запись истории несёт feature, адрес — с /feature/ (десктоп 1280)', async () => {
    const win = globalThis as any;
    recordHistory();
    // счётчик записей: запись навигации обязана быть реально сделана, а не унаследована от входного снимка
    const writes = { push: 0, replace: 0 };
    const hist = win.window.history;
    const push0 = hist.pushState, replace0 = hist.replaceState;
    hist.pushState = (...a: any[]) => { writes.push++; return push0(...a); };
    hist.replaceState = (...a: any[]) => { writes.replace++; return replace0(...a); };
    win.window.innerWidth = 1280;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chats.push(chatB);
    wsState.activeSession = null;
    // Снимок, который кладёт App.tsx на запасном переходе: feature + chatId.
    const inputSnap = { screen: 'project', project, file: null, chatId: 'chatB', feature: 'f1' };
    win.window.history.state = inputSnap;
    win.window.location.hash = '#/project/p1/feature/f1';
    win.sessionStorage.setItem('cc_pending_project_chat', encodePendingChat('p1', 'chatB', undefined, true));
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    for (let i = 0; i < 20 && !findNodeByAttr(container, 'data-testid', 'chat-panel'); i++) {
      await act(async () => { vi.advanceTimersByTime(100); });
    }
    // Выбор чата под открытой страницей: новая запись истории сохраняет feature страницы.
    expect(writes.push + writes.replace, 'запись навигации не сделана (запасный переход)').toBeGreaterThan(0);
    expect(getNav(), 'запись навигации совпала с входным снимком по ссылке').not.toBe(inputSnap);
    expect(getNav()?.feature, 'запись навигации потеряла feature (запасный переход)').toBe('f1');
    expect(getNav()?.chatId, 'запись навигации потеряла chatId (запасный переход)').toBe('chatB');
    expect(win.window.location.hash, 'адрес потерял /feature/ (запасный переход)').toContain('/feature/');
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи закрылась при запасном переходе').not.toBeNull();
  });

  // Парный: тот же холодный переход, затем ЯВНЫЙ выбор ДРУГОГО чата (тот же канал, что в
  // тестах автоскрытия выше) — автоскрытие работает, страница закрыта. Мутация «вернуть
  // false третьим аргументом» (то есть закрыть и холодный выбор) краснит предыдущий тест.
  it('парный: после запасного перехода явный выбор другого чата закрывает страницу', async () => {
    const win = globalThis as any;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    const chatC: Session = { id: 'chatC', name: 'Чат Ц', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB, chatC);
    mockData.chats.push(chatB, chatC);
    wsState.activeSession = null;
    win.window.location.hash = '#/project/p1/feature/f1';
    win.window.history.state = { screen: 'project', project, file: null, chatId: 'chatB', feature: 'f1' };
    win.sessionStorage.setItem('cc_pending_project_chat', encodePendingChat('p1', 'chatB', undefined, true));
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Предусловие: холодный выбор chatB — страница открыта.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница не открыта перед явным выбором (предусловие)').not.toBeNull();
    // Явный выбор другого чата — тот же путь, что в тестах автоскрытия: канал + событие.
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatC');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // Автоскрытие: страница закрыта, выбранный чат в DOM.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'явный выбор другого чата не закрыл страницу').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'),
      'выбранный чат не в DOM').not.toBeNull();
  });

  // Дефект Ф-хв (третий возврат): запасный переход не должен подавлять навигацию и мобильный
  // показ чата — keepFeaturePage гасит только closeFeaturePage. Мутация: вернуть третьим
  // аргументом handleSelectSession `activeSessionRef.current == null || keepFeaturePage` →
  // красные тесты холодного чата (адрес, display:flex) и явного выбора без активного чата.
  const mountPending = async (mobile: boolean, withFeature: boolean) => {
    const win = globalThis as any;
    if (!mobile) win.window.innerWidth = 1280;
    const chatA: Session = { id: 'chatA', name: 'Чат А', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatA, chatB);
    mockData.chats.push(chatA, chatB);
    mockData.chat = chatA;
    wsState.activeSession = null;
    // фейк-история пишет снимок и адрес (в beforeEach pushState/replaceState — no-op)
    win.window.history = {
      state: null, back: () => {}, go: () => {}, forward: () => {}, length: 1,
      pushState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
      replaceState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
    };
    win.window.location.hash = withFeature ? '#/project/p1/feature/f1' : '#/project/p1';
    win.window.history.state = { screen: 'project', project, file: null, chatId: withFeature ? 'chatB' : undefined, feature: withFeature ? 'f1' : undefined };
    win.sessionStorage.setItem('cc_pending_project_chat', withFeature ? encodePendingChat('p1', 'chatB', undefined, true) : 'p1|chatA');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    for (let i = 0; i < 20 && !findNodeByAttr(container, 'data-testid', 'chat-panel'); i++) {
      await act(async () => { vi.advanceTimersByTime(100); });
    }
    return { win, container };
  };
  const runColdPendingNoFeature = async (mobile: boolean) => {
    const { win, container } = await mountPending(mobile, false);
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'), 'чат не виден (холодный pending-чат)').not.toBeNull();
    expect(getNav()?.chatId, 'запись навигации без chatId (холодный pending-чат)').toBe('chatA');
    if (mobile) {
      expect((findNodeByAttr(container, 'data-testid', 'workspace-center-container') as any).style?.display,
        'центр скрыт на мобиле (холодный pending-чат)').toBe('flex');
    }
  };
  it('холодный pending-чат без страницы фичи: чат виден, адрес /chat/{id} (десктоп 1280)', () => runColdPendingNoFeature(false));
  it('холодный pending-чат без страницы фичи: чат виден, адрес /chat/{id}, центр display:flex (мобила 360)', () => runColdPendingNoFeature(true));

  // W4-6: открытие чата фичи (уведомление, «ждёт ответа», старт чата) монтирует воркспейс,
  // пока снимок/хэш ещё несут feature прошлой страницы. Канал БЕЗ метки keepFeature —
  // страница не всплывает ни на первом кадре, ни после выбора чата; адрес — /chat/{id}.
  // Мутация: убрать проверку канала в инициализаторе openFeatureId (WorkspacePage) → красный.
  const runColdChatNoMarkStaleFeature = async (mobile: boolean) => {
    const win = globalThis as any;
    if (!mobile) win.window.innerWidth = 1280;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    mockData.chats.push(chatB);
    mockData.chat = chatB;
    wsState.activeSession = null;
    win.window.history = {
      state: null, back: () => {}, go: () => {}, forward: () => {}, length: 1,
      pushState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
      replaceState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
    };
    win.window.location.hash = '#/project/p1/feature/f1';
    win.window.history.state = { screen: 'project', project, file: null, chatId: 'chatB', feature: 'f1' };
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatB');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи всплыла на первом кадре (канал без метки)').toBeNull();
    await act(async () => { vi.advanceTimersByTime(100); });
    for (let i = 0; i < 20 && !findNodeByAttr(container, 'data-testid', 'chat-panel'); i++) {
      await act(async () => { vi.advanceTimersByTime(100); });
    }
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи открыта при выборе чата без метки').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'), 'чат не выбран').not.toBeNull();
    expect(getNav()?.feature ?? null, 'снимок перенёс feature старой страницы').toBeNull();
    expect(win.window.location.hash, 'адрес остался на /feature/').not.toContain('/feature/');
  };
  it('W4-6: чат фичи по каналу без метки при старом снимке с feature — страница не всплывает (десктоп 1280)', () => runColdChatNoMarkStaleFeature(false));
  it('W4-6: чат фичи по каналу без метки при старом снимке с feature — страница не всплывает (мобила 360)', () => runColdChatNoMarkStaleFeature(true));

  // M1′: системное «назад» на снимок страницы фичи из чата A в чат B — восстановление идёт
  // через pending-канал, и писатель popstate обязан нести метку keepFeature из снимка.
  // Мутация: убрать `!!s.feature` в писателе popstate (WorkspacePage) → страница закрывается.
  it('popstate на снимок со страницей фичи и другим чатом: страница остаётся (десктоп 1280)', async () => {
    const { win, container } = await mountPending(false, false);
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'), 'чат А не активен (предусловие)').not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница открыта до popstate (предусловие)').toBeNull();
    const snap = { screen: 'project', project, file: null, chatId: 'chatB', feature: 'f1' };
    win.window.history.state = snap;
    win.window.location.hash = '#/project/p1/feature/f1';
    win.window.dispatchEvent({ type: 'popstate', state: snap });
    await act(async () => { vi.advanceTimersByTime(100); });
    for (let i = 0; i < 20 && !findNodeByAttr(container, 'data-testid', 'feature-page'); i++) {
      await act(async () => { vi.advanceTimersByTime(100); });
    }
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи закрылась при popstate на снимок с feature (M1′)').not.toBeNull();
    expect(win.window.location.hash, 'адрес потерял /feature/ при popstate (M1′)').toContain('/feature/');
  });

  // Страница фичи открыта, активного чата нет, явный выбор ДРУГОГО чата — страница закрыта.
  const runExplicitOtherChat = async (mobile: boolean) => {
    const { win, container } = await mountPending(mobile, true);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница не открыта (предусловие)').not.toBeNull();
    // снимок указывал на chatB, но активный чат так и не выбран; явно выбираем chatA
    wsState.activeSession = null;
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatA');
    act(() => { win.window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'явный выбор другого чата без активного не закрыл страницу').toBeNull();
  };
  it('страница фичи, активного чата нет, явный выбор другого чата — страница закрыта (десктоп 1280)', () => runExplicitOtherChat(false));
  it('страница фичи, активного чата нет, явный выбор другого чата — страница закрыта (мобила 360)', () => runExplicitOtherChat(true));

  // M2 (мобила): задача открыта, явное открытие страницы рисует её ПОВЕРХ; «Закрыть» → задача.
  it('M2: задача в центре (мобила) → страница ПОВЕРХ (адрес /feature/), «Закрыть» → задача видна', async () => {
    recordHistory();
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task1');
    (globalThis as any).window.location.hash = '';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);

    // Страница ПОВЕРХ открытой задачи (M2): «Закрыть» есть, адрес /feature/.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не ПОВЕРХ открытой задачи (M2, мобила)').not.toBeNull();
    expect((globalThis as any).window.location.hash, 'адрес без /feature/ (M2, мобила)')
      .toContain('/feature/');
    // «Закрыть»/Esc → страница закрывается, задача под ней снова в центре.
    // (Возврат из чата идёт через history.back() — no-op в fake-DOM, поэтому hash здесь
    //  не проверяем; в браузере back() снимает запись страницы и адрес без /feature/.)
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close')!;
    act(() => { (reactProps(back.childNodes[0]) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница не закрылась по «Закрыть» (M2, мобила)').toBeNull();
  });

  // (6) Единый источник «центр занят»: centerOccupiedByOther — чистая функция. Предшественник
  // был grep'ом по исходнику (наполовину вакуумным: 'openFile' встречается и вне otherCenterOpen).
  // Поведенческий эквивалент: предикат — единственная точка, и он покрывает каждое окно центра.
  it('единый источник: centerOccupiedByOther — true при любом окне, false при пустом', () => {
    const empty: CenterOccupancy = {
      file: false, commit: false, task: false, graph: false, reader: false,
      persona: false, teamCenter: false, board: false, preview: false,
      terminalMobile: false, videoCenter: false,
    };
    expect(centerOccupiedByOther(empty), 'пустой центр — не занят').toBe(false);
    for (const key of Object.keys(empty) as (keyof CenterOccupancy)[]) {
      expect(centerOccupiedByOther({ ...empty, [key]: true }),
        `окно «${key}» не занимает центр (единый источник)`)
        .toBe(true);
    }
  });

  // (MAJOR-1) Видео-центр в предикате: кадр в центральном острове — окно центра,
  // страница фичи уступает (десктоп). До фикса centerFree о странице не знал,
  // и «Развернуть в центре» была активна → кадр уезжал в невидимый центр.
  it('MAJOR-1: видео-центр (десктоп) — страница фичи скрыта, кадр в центре', async () => {
    (globalThis as any).window.innerWidth = 1280;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи не открыта (MAJOR-1)').not.toBeNull();
    // Разворачиваем видео в центр (кнопка «Развернуть в центре»).
    act(() => { videoStageMock.center = 'player'; });
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница скрыта (видео-центр занял центр).
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'),
      'страница фичи не скрыта при видео-центре (MAJOR-1)').toBeNull();
  });

  // (MAJOR-2) Доска на мобиле: включена (persist) но НЕ в режиме «Задачи» → центр
  // свободен, «Фича →» открывает страницу. До фикса otherCenterOpen смотрел на сырой
  // projectBoard (persist) → страница не открывалась вообще (регрессия).
  it('MAJOR-2: доска включена (persist) на мобиле, не в режиме «Задачи» — «Фича →» открывает страницу', async () => {
    (globalThis as any).localStorage.setItem('cc_proj_board_p1', '1');
    wsState.leftTab = 'sessions';
    (globalThis as any).window.location.hash = '';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    // Страница ОТКРЫТА (доска не в центре: leftTab='sessions', не 'tasks').
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не открыта при включённой (но не в центре) доске (MAJOR-2)')
      .not.toBeNull();
  });

  // (MAJOR-3) Архивация активного чата при открытой странице: leaveActiveChat зовёт
  // handleSelectSession(neighbor, autoSelect=true) → closeFeaturePage вне if (!autoSelect).
  // Тестируем ядро фикса: выбор чата с autoSelect=true (как при авто-выборе соседа после
  // архивации) закрывает страницу. handleSelectSession ловим из мок'а SessionList (onSelect)
  // — в fake-DOM на нодах нет __reactFiber$ (fiber-walk не работает), а пропс — доступен.
  it('MAJOR-3: выбор чата с autoSelect=true (архивация активного) — страница скрыта, сосед виден', async () => {
    const win = globalThis as any;
    const chatB: Session = { id: 'chatB', name: 'Чат Б', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
    mockSessionList.push(chatB);
    win.window.location.hash = '';
    win.window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не открыта (MAJOR-3)').not.toBeNull();

    // Выбор chatB с autoSelect=true (путь leaveActiveChat после архивации активного чата):
    // handleSelectSession(chatB, undefined, true) → closeFeaturePage вне if (!autoSelect).
    expect(sessionListMock.onSelect, 'onSelect (handleSelectSession) не зафиксирован в SessionList-моке (MAJOR-3)').toBeTruthy();
    act(() => { (sessionListMock.onSelect as (s: Session, first?: string, autoSelect?: boolean) => void)(chatB, undefined, true); });
    await act(async () => { vi.advanceTimersByTime(100); });

    // Страница скрыта, соседний чат (chatB) выбран и виден.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не скрыта при выборе чата с autoSelect (MAJOR-3)').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'),
      'соседний чат не в DOM после выбора (MAJOR-3)').not.toBeNull();
  });

  // (M1, регрессия c3ae97728) Холодный диплинк #/project/{pid}/feature/{fid}: страница
  // открывается, а СТАРТОВЫЙ автовыбор первого чата (activeSession ещё null) её НЕ закрывает.
  // До фикса closeFeaturePage был вне if (!autoSelect) (ради MAJOR-3), и стартовый
  // автовыбор SessionList закрывал только что открытую по диплинку страницу: виден первый
  // чат, адрес остаётся /feature/, после перезагрузки страница открывается. Разведение:
  // coldAutoSelect = autoSelect && activeSession==null (холодный старт) не закрывает;
  // leaveActiveChat (activeSession ≠ null) закрывает (MAJOR-3, тест выше).
  // Мутация (снова closeFeaturePage на стартовом автовыборе) красит оба теста.
  const runColdDeepLink = async (mobile: boolean) => {
    const win = globalThis as any;
    if (!mobile) win.window.innerWidth = 1280;
    // Холодный вход: активного чата ещё нет (wsState.activeSession = null).
    wsState.activeSession = null;
    win.window.location.hash = '#/project/p1/feature/f1';
    win.window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница открыта по диплинку (до автовыбора).
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не открыта по диплинку (M1)').not.toBeNull();
    // Стартовый автовыбор первого чата (SessionList: onSelect(live[0], undefined, true)).
    expect(sessionListMock.onSelect, 'onSelect (handleSelectSession) не зафиксирован (M1)').toBeTruthy();
    act(() => { (sessionListMock.onSelect as (s: Session, first?: string, autoSelect?: boolean) => void)(session, undefined, true); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // КЛЮЧЕВОЕ (M1): страница НЕ закрыта стартовым автовыбором — «Закрыть» на месте.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи закрыта стартовым автовыбором (M1, регрессия)').not.toBeNull();
    // Адрес сохраняет /feature/ (стартовый автовыбор не пишет nav при autoSelect).
    expect(win.window.location.hash, 'адрес потерял /feature/ после стартового автовыбора (M1)')
      .toContain('/feature/');
    // Чат выбран и в DOM (под страницей).
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel'),
      'чат не в DOM после стартового автовыбора (M1)').not.toBeNull();
  };
  it('M1: холодный диплинк (мобила 360) — страница открыта, адрес /feature/', () => runColdDeepLink(true));
  it('M1: холодный диплинк (десктоп 1280) — страница открыта, адрес /feature/', () => runColdDeepLink(false));

  // (M2, десктоп) Доска задач на десктопе закрывает открытую страницу. Корень: доска на
  // десктопе рисуется по projectBoard (DesktopWorkspace) независимо от вкладки слева, а панель
  // «Задачи» открывает панельный стор, НЕ трогая leftTab — поэтому старый признак
  // showProjectBoard (требует tasksMode = leftTab==='tasks') на десктопе был всегда false,
  // и страница не уступала доске. Фикс: board = isMobile ? showProjectBoard : (projectBoard &&
  // !selectedTask) — видимое состояние на обеих раскладках. Мутация (board: showProjectBoard)
  // красит: на десктопе leftTab='sessions' → showProjectBoard=false → страница остаётся.
  // Кнопка «Доска» — в TasksPanel (раскладка cc_ws_zones включает 'tasks', см. hoisted-блок).
  const findBoardButton = (container: any): any => {
    // IconSegmented (шапка) — title="Доска"; PillViewSwitcher (без шапки) — текст «Доска».
    const byTitle = findNodeByAttr(container, 'title', 'Доска');
    if (byTitle) return byTitle;
    let hit: any = null;
    const walk = (x: any) => {
      if (hit) return;
      if (x.nodeType === 1 && String(x.nodeName).toUpperCase() === 'BUTTON' && textNodes(x).join('').includes('Доска')) { hit = x; return; }
      for (const c of x.childNodes) walk(c);
    };
    walk(container);
    return hit;
  };
  it('M2: доска (десктоп 1280) при открытой странице — страница уходит, адрес без /feature/', async () => {
    const win = globalThis as any;
    win.window.innerWidth = 1280;
    // Вкладка слева НЕ «Задачи»: на десктопе панель «Задачи» открывается стором, leftTab не
    // меняется. Старый признак (showProjectBoard требует tasksMode) здесь false — мутация красит.
    wsState.leftTab = 'sessions';
    recordHistory();
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не открыта (M2, десктоп)').not.toBeNull();
    // Кнопка «Доска» в TasksPanel → handleProjectBoard(true) → projectBoard=true.
    const boardBtn = findBoardButton(container);
    expect(boardBtn, 'кнопка «Доска» не найдена в TasksPanel (M2, десктоп)').not.toBeNull();
    act(() => { (reactProps(boardBtn) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница закрыта (доска заняла центр), адрес без /feature/.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница фичи не уступила доске (M2, десктоп)').toBeNull();
    expect(win.window.location.hash, 'адрес содержит /feature/ после доски (M2, десктоп)')
      .not.toContain('/feature/');
  }, 20000);

  // (7) Параметризованный: каждое окно центра — одно «верхнее» (единый источник otherCenterOpen).
  // Побеждает ПОСЛЕДНЕЕ открытое:
  //   · окно ДО страницы (file/task, beforeMount) → явное открытие страницы рисует её ПОВЕРХ (M2);
  //   · страница ДО окна (videoCenter, afterMount) → переход otherCenterOpen false→true закрывает
  //     страницу (M1, настоящее закрытие).
  // beforeMount/afterMount — когда окно открывается (file/task потребляются на mount, videoCenter
  // по стору). Список окон сверяется с полями CenterOccupancy — пропуск красит проверку полноты.
  const centerWindows: { key: keyof CenterOccupancy; mode: 'onTop' | 'closed'; beforeMount: () => void; afterMount: () => void }[] = [
    { key: 'file', mode: 'onTop', beforeMount: () => { (globalThis as any).sessionStorage.setItem('cc_pending_file', 'p1|src/foo.ts'); }, afterMount: () => {} },
    { key: 'task', mode: 'onTop', beforeMount: () => { (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task1'); }, afterMount: () => {} },
    { key: 'videoCenter', mode: 'closed', beforeMount: () => {}, afterMount: () => { videoStageMock.center = 'player'; } },
  ];
  for (const w of centerWindows) {
    it(`окно центра «${w.key}» — ${w.mode === 'onTop' ? 'страница ПОВЕРХ (M2)' : 'страница закрыта (M1, переход)'} (параметризовано)`, async () => {
      recordHistory();
      (globalThis as any).window.location.hash = '';
      (globalThis as any).window.history.state = null;
      w.beforeMount();
      const { container } = mount(createElement(WorkspacePage, {
        project,
        auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
        onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
      } as any));
      await act(async () => { vi.advanceTimersByTime(100); });
      await openFeature(container);
      w.afterMount();
      await act(async () => { vi.advanceTimersByTime(100); });
      const close = findNodeByAttr(container, 'data-testid', 'feature-page-close');
      if (w.mode === 'onTop') {
        // M2: окно открыто ДО страницы → явное открытие рисует страницу ПОВЕРХ.
        expect(close, `страница фичи не ПОВЕРХ окна «${w.key}» (M2)`).not.toBeNull();
        expect((globalThis as any).window.location.hash, `адрес без /feature/ (M2, «${w.key}»)`).toContain('/feature/');
      } else {
        // M1: страница открыта, потом окно (переход otherCenterOpen false→true) → настоящее закрытие.
        expect(close, `страница фичи не закрыта при окне «${w.key}» (M1, переход)`).toBeNull();
        expect((globalThis as any).window.location.hash, `адрес содержит /feature/ (M1, «${w.key}»)`).not.toContain('/feature/');
      }
      expect(findNodeByAttr(container, 'data-testid', 'chat-panel'),
        `чат не в DOM при окне «${w.key}»`).not.toBeNull();
    });
  }
  // Полнота: ключи берутся из единого источника (CENTER_OCCUPANCY_KEYS, tsc требует каждое
  // поле интерфейса). Каждое поле обязано быть отнесено либо к поведенческим тестам этого
  // файла, либо к «только предикат»; новое окно без классификации красит тест.
  // «behavioral» = поле ИЗОЛИРОВАНО в поведенческом тесте (оно — единственное занятое окно):
  //   file/task/videoCenter — параметризованный M1/M2; board — MAJOR-2 (пустой id задачи,
  //   task=false); teamCenter — minor-4. Остальные — «только предикат» (centerOccupiedByOther).
  it('полнота: каждое поле CenterOccupancy классифицировано и занимает центр в предикате', () => {
    const behavioral = ['file', 'task', 'videoCenter', 'board', 'teamCenter'];
    const predicateOnly = ['commit', 'graph', 'reader', 'persona', 'preview', 'terminalMobile'];
    expect(CENTER_OCCUPANCY_KEYS.length, 'список полей пуст').toBeGreaterThan(0);
    expect([...behavioral, ...predicateOnly].sort(), 'поле CenterOccupancy не классифицировано в тестах')
      .toEqual([...CENTER_OCCUPANCY_KEYS].sort());
    const empty = Object.fromEntries(CENTER_OCCUPANCY_KEYS.map(k => [k, false])) as unknown as CenterOccupancy;
    expect(centerOccupiedByOther(empty)).toBe(false);
    for (const key of CENTER_OCCUPANCY_KEYS) {
      expect(centerOccupiedByOther({ ...empty, [key]: true }), `поле «${key}» не занимает центр`).toBe(true);
    }
  });

  // (MAJOR-1, корень) Отпечаток занятости = «тип:идентичность» каждого занятого окна.
  // Смена окна ТОГО ЖЕ типа (файл→файл, задача→задача, коммит→коммит) меняет идентичность →
  // отпечаток → штамп окна → автоскрытие. Старый отпечаток (набор типов) при такой смене не
  // менялся → страница оставалась (проба Глеба). Окна без пер-экземплярной идентичности
  // (teamCenter/board) — пустая строка: отпечаток меняется только при open/close.
  it('MAJOR-1 (корень): centerFingerprint отличает смену окна того же типа от «окно открыто»', () => {
    const base = (o: Partial<CenterOccupancy>): CenterOccupancy =>
      ({ file: false, commit: false, task: false, graph: false, reader: false, persona: false, teamCenter: false, board: false, preview: false, terminalMobile: false, videoCenter: false, ...o }) as CenterOccupancy;
    const ident = (o: Partial<CenterOccupancy>): Record<keyof CenterOccupancy, string> =>
      ({ file: '', commit: '', task: '', graph: '', reader: '', persona: '', teamCenter: '', board: '', preview: '', terminalMobile: '', videoCenter: '', ...o }) as Record<keyof CenterOccupancy, string>;

    // Задача → другая задача: id меняется → отпечаток меняется.
    const taskA = centerFingerprint(base({ task: true }), ident({ task: 'task1' }));
    const taskB = centerFingerprint(base({ task: true }), ident({ task: 'task2' }));
    expect(taskA, 'задача: отпечаток пуст').not.toBe('');
    expect(taskB, 'задача→другая: отпечаток не изменился (MAJOR-1)').not.toBe(taskA);

    // Файл → другой файл: путь меняется → отпечаток меняется.
    const fileA = centerFingerprint(base({ file: true }), ident({ file: 'src/foo.ts' }));
    const fileB = centerFingerprint(base({ file: true }), ident({ file: 'src/bar.ts' }));
    expect(fileB, 'файл→другой файл: отпечаток не изменился (MAJOR-1)').not.toBe(fileA);

    // Коммит → другой коммит: sha меняется → отпечаток меняется.
    const commitA = centerFingerprint(base({ commit: true }), ident({ commit: 'abc123' }));
    const commitB = centerFingerprint(base({ commit: true }), ident({ commit: 'def456' }));
    expect(commitB, 'коммит→другой коммит: отпечаток не изменился (MAJOR-1)').not.toBe(commitA);

    // То же окно (идентичность не изменилась) → отпечаток стабильен (лишний штамп не выдаётся).
    expect(centerFingerprint(base({ task: true }), ident({ task: 'task1' })),
      'стабильность: повторный отпечаток того же окна отличается').toBe(taskA);

    // Окно без пер-экземплярной идентичности (board): open → отпечаток, close → пуст.
    expect(centerFingerprint(base({ board: true }), ident({})), 'board open: отпечаток пуст').not.toBe('');
    expect(centerFingerprint(base({}), ident({})), 'board close: отпечаток не пуст').toBe('');

    // Пустой центр (никто не занят) → пустой отпечаток.
    expect(centerFingerprint(base({}), ident({})), 'пустой центр: отпечаток не пуст').toBe('');
  });

  // (MAJOR-2, поведение) Доска на мобиле: включена (persist), но не в режиме «Задачи» — центр
  // НЕ занят: окно (штамп) не возникает, страница открывается и остаётся. В режиме «Задачи»
  // доска занимает центр — открытая ДО неё страница закрывается (окно позже страницы).
  // Изоляция board (MAJOR-3, проба Глеба): переключение в «Задачи» идёт с ПУСТЫМ id задачи
  // (cc_pending_task 'p1|' → selectedTaskId='' falsy → task=false, а showProjectBoard=true →
  // board=true). Старый тест шёл через 'p1|nonexistent' → selectedTaskId='nonexistent' → task=true
  // маскировал board: мутация board: showProjectBoard → projectBoard была эквивалентна и не краснела.
  // Теперь board изолирован: мутация board: showProjectBoard → projectBoard краснит (в части 1
  // projectBoard=true даёт board=true ещё на mount → штамп окна до страницы; в части 2 отпечаток
  // не меняется → новый штамп не выдаётся → страница остаётся).
  it('MAJOR-2 (поведение): доска включена без режима «Задачи» — страница остаётся; в «Задачи» доска закрывает страницу', async () => {
    recordHistory();
    (globalThis as any).localStorage.setItem('cc_proj_board_p1', '1');
    wsState.leftTab = 'sessions';
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'доска вне режима «Задачи» закрыла страницу').not.toBeNull();
    // Режим «Задачи» БЕЗ выбора задачи: cc_pending_task 'p1|' → leftTab='tasks', selectedTaskId=''
    // (falsy → task=false), showProjectBoard=true (board=true) — доска изолирована.
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|');
    act(() => { (globalThis as any).window.dispatchEvent(new Event('cc-pending-task')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'доска в режиме «Задачи» не закрыла страницу').toBeNull();
  });

  // (minor-4) Мобильный командный центр: onShowTeam → handleShowTeam → setTeamCenterOpen(true).
  // Удаление строки красит тест: центр не занят, страница остаётся.
  it('minor-4: командный центр (мобила) закрывает открытую страницу', async () => {
    recordHistory();
    wsState.leftTab = 'personas';
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница не открыта').not.toBeNull();
    expect(personasPanelMock.onShowTeam, 'onShowTeam не пойман').toBeTruthy();
    act(() => { personasPanelMock.onShowTeam!(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'командный центр не закрыл страницу (minor-4)').toBeNull();
  });

  // (Minor) Мобила: уход в список/сайдбар (mobileView='sidebar') при открытой странице
  // закрывает её ПО-НАСТОЯЩЕМУ (setOpenFeatureId(null) + navReplace без /feature/), а не
  // гасит молча. Поведенческий тест требует re-triggerable перехода в 'sidebar' при открытой
  // странице, но все штатные пути (handleSelectSession/handleFeatureBack/handleTabSwitch) либо
  // сами закрывают страницу, либо недоступны в fake-DOM (PillSwitch) — поэтому эффект
  // фиксируем по исходнику: мутация (убрать guard mobileView!=='sidebar' || setOpenFeatureId(null)
  // + navReplace view:'sidebar') красит тест.
  it('Minor: уход в сайдбар на мобиле закрывает страницу по-настоящему (эффект)', () => {
    const src = readFileSync(new URL('./WorkspacePage.tsx', import.meta.url), 'utf8');
    // Guard эффекта: mobileView='sidebar' + openFeatureId != null (и флаги пришли, фичи вкл).
    expect(src, 'guard «уход в сайдбар» не найден (mobileView !== \'sidebar\' || openFeatureId == null)')
      .toContain("mobileView !== 'sidebar' || openFeatureId == null");
    // Действие эффекта: настоящее закрытие — setOpenFeatureId(null) + navReplace view:'sidebar', feature:null.
    expect(src, 'действие «уход в сайдбар» не закрывает страницу (navReplace view:sidebar, feature:null)')
      .toContain("view: 'sidebar', file: null, task: null,");
  });

  // (M1, смена окна) Центр занят ДО открытия страницы; страницу открыли поверх; затем окно
  // СМЕНИЛОСЬ (файл уступил задаче) — флаг занятости не меняется, но окно позже страницы →
  // страница закрыта (штамп, а не ребро булева).
  it('M1: файл → страница поверх → окно сменилось на задачу — страница закрыта', async () => {
    recordHistory();
    (globalThis as any).sessionStorage.setItem('cc_pending_file', 'p1|src/foo.ts');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница не поверх файла').not.toBeNull();
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task1');
    act(() => { (globalThis as any).window.dispatchEvent(new Event('cc-pending-task')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'смена окна файл→задача не закрыла страницу (M1)').toBeNull();
    expect((globalThis as any).window.location.hash).not.toContain('/feature/');
  });

  // (MAJOR-1, смена окна того же типа) Страница открыта; окно ТОГО ЖЕ типа сменилось
  // (задача → другая задача / коммит → другой коммит) — отпечаток «тип:идентичность» меняется
  // (старый набор типов — нет) → штамп окна позже страницы → настоящее закрытие.
  // Мутация centerFingerprint (убрать идентичность, оставить набор типов) красит оба теста.
  const runTaskSwap = async (mobile: boolean) => {
    recordHistory();
    if (!mobile) (globalThis as any).window.innerWidth = 1280;
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task1');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница не поверх задачи').not.toBeNull();
    // Смена окна того же типа: задача task1 → task2 (идентичность меняется).
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task2');
    act(() => { (globalThis as any).window.dispatchEvent(new Event('cc-pending-task')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'смена окна задача→другая задача не закрыла страницу (MAJOR-1)').toBeNull();
    expect((globalThis as any).window.location.hash, 'адрес содержит /feature/ после смены задачи (MAJOR-1)')
      .not.toContain('/feature/');
  };
  it('MAJOR-1: задача → другая задача (мобила 360) — страница закрыта', () => runTaskSwap(true));
  it('MAJOR-1: задача → другая задача (десктоп) — страница закрыта', () => runTaskSwap(false));

  const runCommitSwap = async (mobile: boolean) => {
    recordHistory();
    if (!mobile) (globalThis as any).window.innerWidth = 1280;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Коммит abc123 открыт ДО страницы → явное открытие рисует страницу ПОВЕРХ (M2).
    act(() => { (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-commit', { detail: { projectId: 'p1', sha: 'abc123' } })); });
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница не поверх коммита').not.toBeNull();
    // Смена окна того же типа: коммит abc123 → def456 (идентичность меняется).
    act(() => { (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-commit', { detail: { projectId: 'p1', sha: 'def456' } })); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'смена окна коммит→другой коммит не закрыл страницу (MAJOR-1)').toBeNull();
    expect((globalThis as any).window.location.hash, 'адрес содержит /feature/ после смены коммита (MAJOR-1)')
      .not.toContain('/feature/');
  };
  it('MAJOR-1: коммит → другой коммит (мобила 360) — страница закрыта', () => runCommitSwap(true));
  it('MAJOR-1: коммит → другой коммит (десктоп) — страница закрыта', () => runCommitSwap(false));

  // (M2, мобила) Страница поверх файла видима: контейнер центра display:flex.
  it('M2 (мобила): страница поверх файла — контейнер центра виден (flex)', async () => {
    recordHistory();
    (globalThis as any).sessionStorage.setItem('cc_pending_file', 'p1|src/foo.ts');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    const center = findNodeByAttr(container, 'data-testid', 'workspace-center-container');
    expect((center as any).style?.display, 'контейнер центра скрыт при странице над файлом').toBe('flex');
  });

  // (MAJOR-2, мобила) Страница над файлом: файл (FileViewer) — СОСЕД контейнера центра, а не
  // его ребёнок, поэтому прячется display:none (без размонтирования — состояние файла
  // сохраняется для Esc), иначе на 360 страница и файл делили экран 50/50. «Закрыть»/Esc →
  // файл виден снова. Мутация (убрать display:none у файла/ридера) красит тест.
  it('MAJOR-2 (мобила): страница над файлом — файл скрыт (display:none), страница занимает экран; «Закрыть» → файл виден', async () => {
    recordHistory();
    (globalThis as any).sessionStorage.setItem('cc_pending_file', 'p1|src/foo.ts');
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    await openFeature(container);
    // Страница над файлом: file-name в DOM, но у него есть скрытый предок (display:none).
    const fileName = findNodeByAttr(container, 'data-testid', 'file-name');
    expect(fileName, 'file-name не в DOM (файл не открыт)').not.toBeNull();
    expect(hasHiddenAncestor(fileName!), 'файл не скрыт при открытой странице (MAJOR-2, сплит 50/50)').toBe(true);
    // Страница занимает экран: контейнер центра flex (страница в нём), «Закрыть» есть.
    const center = findNodeByAttr(container, 'data-testid', 'workspace-center-container');
    expect((center as any).style?.display, 'контейнер центра не flex при странице над файлом').toBe('flex');
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), '«Закрыть» страницы нет').not.toBeNull();
    // «Закрыть» → файл виден снова (скрытый предок снят, display не none).
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-close')!;
    act(() => { (reactProps(back.childNodes[0]) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница не закрылась по «Закрыть»').toBeNull();
    const fileNameAfter = findNodeByAttr(container, 'data-testid', 'file-name');
    expect(fileNameAfter, 'file-name пропал после «Закрыть»').not.toBeNull();
    expect(hasHiddenAncestor(fileNameAfter!), 'файл остался скрыт после «Закрыть» (MAJOR-2)').toBe(false);
  });

  // Тест на сброс featureBackNav при автоскрытии: страница скрыта (другое окно открыто),
  // указатель возврата (chat → фича) сброшен — «назад» не ведёт на невидимую страницу.
  // Окно-победитель — задача (cc_pending_task + событие cc-pending-task: слушатель есть,
  // в отличие от файла, который потребляется только на mount).
  it('сброс featureBackNav при автоскрытии: указатель возврата (chat → фича) гаснет', async () => {
    (globalThis as any).window.location.hash = '';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Указатель возврата записан (cc-open-feature, fromChat: true → recordBackNav).
    await openFeature(container);
    expect(backNavTest.getBackNav(), 'указатель возврата не записан').not.toBeNull();
    // Задача открыта (cc_pending_task + событие) → автоскрытие → clearBackNav.
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task1');
    act(() => { (globalThis as any).window.dispatchEvent(new Event('cc-pending-task')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(backNavTest.getBackNav(), 'указатель возврата не сброшен при автоскрытии').toBeNull();
    void container;
  });

  // M1 (полный цикл): страница открыта → открывается окно (переход otherCenterOpen false→true)
  // → НАСТОЯЩЕЕ закрытие (openFeatureId сброшен, адрес без /feature/). Закрыли окно → страница
  // НЕ «воскресает» (в старом гейте openFeatureId оставался и страница возвращалась сама).
  // Окно — задача (cc_pending_task + событие: слушатель есть, файл потребляется только на mount).
  it('M1: страница → задача (переход) → закрытие задачи → страница НЕ возвращается, адрес без /feature/', async () => {
    recordHistory();
    (globalThis as any).window.location.hash = '';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница открыта (адрес /feature/).
    await openFeature(container);
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'), 'страница не открыта (M1)').not.toBeNull();
    expect((globalThis as any).window.location.hash, 'адрес без /feature/ до окна (M1)').toContain('/feature/');

    // Задача открывается ПОСЛЕ страницы → переход otherCenterOpen false→true → настоящее закрытие.
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task1');
    act(() => { (globalThis as any).window.dispatchEvent(new Event('cc-pending-task')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница закрыта (openFeatureId сброшен), адрес без /feature/.
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница не закрыта при открытии задачи (M1, переход)').toBeNull();
    expect((globalThis as any).window.location.hash, 'адрес содержит /feature/ после перехода (M1)')
      .not.toContain('/feature/');

    // Закрытие задачи: выбор чата (cc-pending-project-chat → handleSelectSession →
    // setSelectedTaskId(null)) — тот же механизм, что в тестах «выбор чата».
    (globalThis as any).sessionStorage.setItem('cc_pending_project_chat', 'p1|chatA');
    act(() => { (globalThis as any).window.dispatchEvent(new Event('cc-pending-project-chat')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // Закрыли задачу → страница НЕ возвращается (openFeatureId=null, а не «скрыта гейтом»).
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница «воскресла» после закрытия задачи (M1: openFeatureId не сброшен)').toBeNull();
  });

  // m2 (minor): автоскрытие стирает адрес НОВОГО окна. Страница → задача: адрес ДОЛЖЕН
  // содержать задачу (#/project/{id}/task/{task}), а не схлопнуться на #/project/{id}/chat/{chat}.
  // Старый код: navReplace в эффекте автоскрытия писал file:null, task:null → адрес терял окно,
  // перезагрузка открывала чат, а не задачу. Мутация: вернуть task:null в navReplace → краснеет.
  it('m2: страница → задача — адрес содержит задачу (не /chat/), перезагрузка её открывает', async () => {
    recordHistory();
    (globalThis as any).window.location.hash = '';
    (globalThis as any).window.history.state = null;
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница открыта (адрес /feature/).
    await openFeature(container);
    expect((globalThis as any).window.location.hash, 'адрес без /feature/ до окна (m2)').toContain('/feature/');
    // Задача открывается ПОСЛЕ страницы → автоскрытие.
    (globalThis as any).sessionStorage.setItem('cc_pending_task', 'p1|task1');
    act(() => { (globalThis as any).window.dispatchEvent(new Event('cc-pending-task')); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // Страница закрыта, адрес содержит ЗАДАЧУ (m2: не затёрт на /chat/).
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-close'),
      'страница не закрыта при открытии задачи (m2)').toBeNull();
    const hash = (globalThis as any).window.location.hash;
    expect(hash, `адрес не содержит задачу после перехода (m2, адрес=${hash})`)
      .toContain('/task/task1');
  });
});

// === centerWinner: чистая функция «кто выигрывает центр» — табличная проверка ===
// Порядок определяется seq событий «открыли» (монотонный счётчик WorkspacePage), а не
// выводом из сравнения значений: файл выигрывает, только если его seq БОЛЬШЕ seq страницы.
// 0 = не открыто (восстановленный файл при холодном старте, закрытая страница).
describe('centerWinner: файл выигрывает, только если его seq > seq страницы', () => {
  it('таблица: (featureOpenedAt, fileOpenedAt) → победитель', () => {
    // [featureOpenedAt, fileOpenedAt, ожидаемый победитель]: файл выигрывает, только
    // если его seq БОЛЬШЕ seq страницы (открыт позже).
    const rows: Array<[number, number, 'page' | 'file']> = [
      // Ничего не открыто (0,0) — страница (центр по умолчанию чат, файл не открыт).
      [0, 0, 'page'],
      // Только страница открыта (1,0) — страница.
      [1, 0, 'page'],
      // Только файл открыт (0,1) — файл.
      [0, 1, 'file'],
      // Файл (seq 1) открыт ДО страницы (seq 2) → страница позже → страница.
      [2, 1, 'page'],
      // Страница (seq 1) открыта ДО файла (seq 2) → файл позже → файл.
      [1, 2, 'file'],
      // Повторное открытие той же фичи: страница seq 3, файл seq 2 → страница позже → страница.
      [3, 2, 'page'],
      // Повторное открытие того же файла: файл seq 3, страница seq 2 → файл позже → файл.
      [2, 3, 'file'],
      // Равные seq невозможны (счётчик строго растёт), но при равенстве — страница
      // (файл выигрывает строго при >, не при >=).
      [2, 2, 'page'],
    ];
    for (const [f, fl, expected] of rows) {
      expect(centerWinner(f, fl), `centerWinner(${f}, ${fl})`).toBe(expected);
    }
  });
  it('файл выигрывает строго при fileOpenedAt > featureOpenedAt (граница)', () => {
    // Граница: файл seq ровно на 1 больше → файл; ровно на 1 меньше → страница.
    expect(centerWinner(5, 6)).toBe('file');
    expect(centerWinner(6, 5)).toBe('page');
  });
});

// Вспомогательный: toHash для фейк-истории (m5) — упрощённый, только project/feature/chat
function toHashOf(s: any): string {
  if (!s || s.screen !== 'project' || !s.project) return '#/projects';
  let h = `#/project/${s.project.id}`;
  if (s.feature) h += `/feature/${encodeURIComponent(s.feature)}`;
  else if (s.chatId) h += `/chat/${encodeURIComponent(s.chatId)}`;
  return h;
}
