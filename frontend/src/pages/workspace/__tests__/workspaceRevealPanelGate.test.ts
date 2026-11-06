// R-S2b-2c, п. 4: сторож обработчика показа панели (WorkspacePage.onReveal).
//
// Инвариант: обработчик REVEAL_PANEL_EVENT читает ТОТ ЖЕ набор разрешённых панелей,
// что и рельса, — мемоизированный wsAllowedKeys (WorkspacePage:335, построен по
// useActiveSubsystemKeys), а не свою копию, построенную по геттеру стора
// isSubsystemEnabled на каждое событие (так было до 8dc51cf91). Значение-wise те
// две копии совпадают ровно до момента, когда кто-то поменяет предикат у одной из
// них; тогда обработчик начнёт показывать панели, которых рельса не показывает
// (и наоборот). Сторож обязан зафиксировать ИСТОЧНИК набора, а не совпадение значений.
//
// Как это сделано: тест намеренно разводит два состава подсистем в противоположные
// стороны:
//   • состав рендера — хук useActiveSubsystemKeys (мокирован): активна «tasks»;
//   • реальный стор — setAllSubsystems: активна «notes» (её геттер isSubsystemEnabled
//     и назвал бы «notes» включённой, а «tasks» — выключенной).
// Рельса получает allowedKeys из мемоизированного набора рендера — значит «правильный»
// исход: «tasks» раскрывается, «notes» — нет. Мутация (вернуть в onReveal второй
// построитель workspaceKeysFor(isSubsystemEnabled)) даёт ровно наоборот: оба кейса
// краснеют. Реальный реестр (SUBSYSTEMS, panelCatalog) при этом НЕ мокирован —
// фильтр работает настоящей картой PANEL_SUBSYSTEM.
//
// Окружение — то же, что у workspaceOpenFileSession.test.ts: настоящий WorkspacePage
// в fake-DOM (desksotp, 1600px), тяжёлые либы — no-op-моки. Открытость панели
// читается по стору раскладки (wsPanels.getZones() + openKeysOf) — прямому состоянию,
// которое рельса и обработчик делят.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { WorkspacePage } from '../../WorkspacePage';
import { openKeysOf, wsPanels } from '../panelStackState';
import { setAllFlags } from '../../../lib/featureFlags';
import { __test as backNavTest } from '../../../lib/featureBackNav';
import { __test as indexTest } from '../../../lib/featuresIndex';
import { SUBSYSTEMS, __resetSubsystems, setAllSubsystems } from '../../../lib/subsystems';
import { REVEAL_PANEL_EVENT } from '../../../lib/subsystems/registry';
import type { FeatureIndexFeature, FeaturesIndex } from '../../../types/featureIndex';
import type { Project, Session } from '../../../types';
import { mount, installFakeDom, uninstallFakeDom } from '../../../components/chat/__tests__/fakeDom';

// ── localStorage / sessionStorage (loadWorkspaceState, panelStackState) — vi.hoisted:
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
  // Раскладка панельного стора (cc_ws_zones) читается при ИМПОРТЕ (module-level
  // singleton). «Задачи» и «Заметки» намеренно НЕ открыты: их открытость —
  // результат теста (показ через onReveal), а не начальное состояние.
  store.set('cc_ws_zones', JSON.stringify({
    left: { layout: [['chats']] },
    right: { layout: [] },
  }));
  return { store, sstore };
});

// ── состав рендера: хук useActiveSubsystemKeys под контролем теста ──
// Остальные экспорты стора — настоящие: setAllSubsystems/isSubsystemEnabled живут
// в реальном модуле и intentionalно описывают ДРУГОЙ состав (см. преамбулу).
const subsMock = vi.hoisted(() => ({ hookActive: [] as string[] }));
vi.mock('../../../lib/subsystems', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/subsystems')>();
  return { ...orig, useActiveSubsystemKeys: () => subsMock.hookActive };
});

// ── workspaceState: activeSession, openFile и leftTab задаются из хранилища (инициализатор WorkspacePage) ──
const wsState = vi.hoisted(() => ({ activeSession: null as unknown, openFile: null as string | null, leftTab: undefined as string | undefined }));
vi.mock('../../../lib/workspaceState', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/workspaceState')>();
  return {
    ...orig,
    loadWorkspaceState: () => ({ activeSession: wsState.activeSession, openFile: wsState.openFile, leftTab: wsState.leftTab }),
    saveWorkspaceState: () => {},
    loadFileFullscreenPref: () => false, saveFileFullscreenPref: () => {},
    isLeftTab: (t: string) => ['sessions', 'changes', 'tasks', 'personas', 'skills', 'tools', 'knowledge'].includes(t),
  };
});

// ── signalr: экспортируемые символы → no-op (подписки → cleanup, действия → Promise) ──
vi.mock('../../../lib/signalr', () => {
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
//     chats.get/tasks.listAll), остальное — no-op. mockData — hoisted, задаётся в beforeEach ──
const mockData = vi.hoisted(() => ({ index: null as any, featurePage: null as any, chat: null as any, chats: [] as Session[] }));
vi.mock('../../../lib/api', () => {
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
vi.mock('../../../lib/terminalSignalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve(null);
  return {
    __esModule: true,
    resetTerminalConnection: act, createTerminal: act, connectTerminal: act,
    listTerminals: act, stopTerminal: act, renameTerminal: act,
    sendTerminalInput: act, resizeTerminal: act, onTerminalMessage: sub,
  };
});

// ── registry: слоты no-op (тела панелей подсистем в тесте не рисуются); REVEAL_PANEL_EVENT —
//     настоящий: тест диспатчит ровно то событие, на которое вешается обработчик страницы ──
vi.mock('../../../lib/subsystems/registry', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/subsystems/registry')>();
  return { ...orig, useSlot: () => [], useSlotItem: () => null };
});

// ── projectCapabilities / personas / git: no-op ──
vi.mock('../../../lib/projectCapabilities', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/projectCapabilities')>();
  return { ...orig, useProjectFeature: () => false, isLocalProject: () => false, featureReason: () => null };
});
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return {
    ...orig,
    usePersonasVersion: () => 0, getPersonaById: () => null,
    getPersonasSnapshot: () => [], ensurePersonasLoaded: () => Promise.resolve(), personaLabel: () => '',
  };
});
vi.mock('../../../lib/git', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/git')>();
  return {
    ...orig,
    useGitState: () => ({ status: null, unpushed: [], changedBy: new Map() }),
    ensureGit: () => Promise.resolve(),
    loadUnpushedLog: () => Promise.resolve([]), setActiveSessionForChangedBy: () => {},
  };
});

// ── SessionList (сайдбар): заглушка — тяжёлый список чатов не живёт в fake-DOM ──
const mockSessionList = vi.hoisted(() => [] as Session[]);
const sessionListMock = vi.hoisted(() => ({
  onSessionUpdated: null as ((s: Session) => void) | null,
  onSelect: null as ((s: Session, first?: string, autoSelect?: boolean) => void) | null,
}));
vi.mock('../../../components/SessionList', async () => {
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

// ── TaskBoard / BoardToolbar: тяжёлая доска (select/options) не живёт в fake-DOM —
//     заглушки; для проверки открытости панели «Задачи» хватает самого факта её места в раскладке. ──
vi.mock('../../../features/tasks/board/TaskBoard', async () => {
  const { createElement } = await import('react');
  return { TaskBoard: () => createElement('div', { 'data-testid': 'task-board-stub' }) };
});
vi.mock('../../../features/tasks/board/BoardToolbar', async () => {
  const { createElement } = await import('react');
  return { BoardToolbar: () => createElement('div', { 'data-testid': 'board-toolbar-stub' }) };
});

// ── ProjectPersonasPanel (сайдбар): заглушка (тот же приём, что в workspaceOpenFileSession.test.ts) ──
const personasPanelMock = vi.hoisted(() => ({ onShowTeam: null as (() => void) | null }));
vi.mock('../../../features/personas/ProjectPersonasPanel', async (importOriginal) => {
  const { createElement } = await import('react');
  const orig = await importOriginal<typeof import('../../../features/personas/ProjectPersonasPanel')>();
  return {
    ...orig,
    ProjectPersonasPanel: ({ onShowTeam }: { onShowTeam?: () => void }) => {
      personasPanelMock.onShowTeam = onShowTeam ?? null;
      return createElement('div', { 'data-testid': 'personas-panel-stub' });
    },
  };
});

// ── useFeaturePage: состояние страницы (run.epic.title) — no-op api отдаёт null ──
vi.mock('../../../lib/featurePage', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/featurePage')>();
  return { ...orig, useFeaturePage: () => ({ status: 'ok', page: mockData.featurePage ?? { run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0 } }) };
});

// ── useProjectTerminals: terminals — no-op ──
vi.mock('../../../hooks/useProjectTerminals', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../hooks/useProjectTerminals')>();
  return {
    ...orig,
    useProjectTerminals: () => ({
      terminals: [], activeTerminalId: null, setActiveTerminalId: () => {},
      refresh: () => {}, create: () => {}, stop: () => {}, rename: () => {},
    }),
  };
});

// ── videoStage: no-op (эфир в этом тесте не открывается) ──
vi.mock('../../../lib/videoStage', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/videoStage')>();
  return {
    ...orig,
    useVideoCenter: () => null,
    setVideoStage: () => {}, setVideoPicker: () => {}, closeVideoCenter: () => {},
  };
});

// ── useHandsFree / useVoiceInput / useMicLevel / useOnline: композеру нужен полный рендер ──
vi.mock('../../../hooks/useHandsFree', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../hooks/useHandsFree')>();
  return { ...orig, useHandsFree: () => ({ phase: 'off', active: false, buffer: '', barren: 0, warned: false, seq: 0 }) };
});
vi.mock('../../../hooks/useVoiceInput', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../hooks/useVoiceInput')>();
  return {
    ...orig,
    useVoiceInput: () => ({ hasSpeech: false, isListening: false, recSeconds: 0, startMic: () => {}, stopMic: () => {} }),
  };
});
vi.mock('../../../hooks/useMicLevel', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../hooks/useMicLevel')>();
  return { ...orig, useMicLevel: () => ({ reportMicDead: () => {}, reportCycleEnd: () => {}, reportEngineHeard: () => {}, reportCycleStart: () => {} }) };
});
vi.mock('../../../hooks/useOnline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../hooks/useOnline')>();
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

describe('WorkspacePage: показ панели по REVEAL_PANEL_EVENT идёт по набору рельсы', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    // Десктоп (1600px): панельные рельсы по краям (DesktopWorkspace → PanelZone) —
    // тот экран, где allowedKeys рельсы и обработчик показа живут рядом.
    (globalThis as any).window.innerWidth = 1600;
    // PanelZone читает document.documentElement.style — в fake-DOM documentElement null
    (globalThis as any).document.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    const doc0 = (globalThis as any).document;
    if (!doc0.querySelector) doc0.querySelector = () => null;
    (globalThis as any).window.innerHeight = 800;
    (globalThis as any).window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
    (globalThis as any).window.history = { state: null, pushState: () => {}, replaceState: () => {}, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
    // Настоящая event-система окна: fakeDom'ов dispatchEvent — no-op, а WorkspacePage
    // вешает слушателей (cc-reveal-panel, popstate) через addEventListener
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
    // Состав рендера (хук) и реальный стор — противоположные (см. преамбулу):
    // рендер и рельса видят включённой «tasks», геттер стора — «notes».
    subsMock.hookActive = [SUBSYSTEMS.tasks];
    setAllSubsystems([SUBSYSTEMS.notes]);
    // Стор раскладки — module-level singleton: снять открытое предыдущим тестом.
    for (const k of openKeysOf(wsPanels.getZones())) {
      if (k !== 'chats') wsPanels.ops.close(k);
    }
  });
  afterEach(() => {
    __resetSubsystems();
    subsMock.hookActive = [];
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  const mountPage = () => mount(createElement(WorkspacePage, {
    project,
    auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
    onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
  } as any));

  // Открытость панели — прямое состояние стора раскладки, который делят рельса и обработчик
  const openKeys = () => openKeysOf(wsPanels.getZones());
  const revealKey = (key: string) => {
    act(() => { window.dispatchEvent(new CustomEvent(REVEAL_PANEL_EVENT, { detail: { key } })); });
  };
  const mountReady = async () => {
    mountPage();
    await act(async () => { vi.advanceTimersByTime(100); });
  };

  it('подсистема, которую рендер видит включённой: её панель раскрывается', async () => {
    await mountReady();
    // контроль: панель не открыта ни началом раскладки, чем-то раньше теста
    expect(openKeys(), '«tasks» открыта до показа').not.toContain('tasks');
    revealKey('tasks');
    expect(openKeys(), 'показ панели включённой подсистемы').toContain('tasks');
  });

  it('подсистема, которую рендер видит выключенной: её панель не раскрывается', async () => {
    // Снимается ДО mount: Page берёт ops.reveal через use() (spread на каждый рендер),
    // spy должен стоять в объекте ops до первого spread.
    const revealSpy = vi.spyOn(wsPanels.ops, 'reveal');
    await mountReady();
    // геттер реального стора назвал бы «notes» включённой — но набор рендера (и рельсы) её не содержит
    expect(subsMock.hookActive, 'контроль состава рендера').not.toContain(SUBSYSTEMS.notes);
    revealKey('notes');
    // не только итог (панели нет в раскладке — её вынесла бы и зона через evict),
    // но и сам факт: обработчик не попросил стор открыть панель вне набора рельсы
    expect(revealSpy.mock.calls.map(c => c[0]), 'обработчик запросил показ панели вне набора рендера').not.toContain('notes');
    expect(openKeys(), 'панель выключенной подсистемы раскрылась вопреки набору рельсы').not.toContain('notes');
  });
});
