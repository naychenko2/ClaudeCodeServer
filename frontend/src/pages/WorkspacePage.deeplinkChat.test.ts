// QA-D2 (дефект e18f03f2): прямая ссылка #/project/{P}/chat/{chatId} должна открывать
// ровно чат из адреса, а не «последний активный» из локального хранилища (ws:{P}.activeSession).
// Риск без починки: набранное сообщение уходит в ЧУЖОЙ чат (общий ↔ приватный).
// QA-D2b (хвост ревью f7b2057b): и когда адрес называет ровно тот чат, что сохранён, а его
// на сервере нет (удалён) — отказ, но центр не должен оставаться с удалённым чатом под тостом.
//
// Окружение node (без jsdom): fakeDom + desktop 1280; StrictMode как в main.tsx; тяжёлые либы —
// no-op-моки (по образцу workspaceFeatureCenter.test.ts). ChatPanel — записывающая заглушка:
// маркер «открытый чат» — session, который WorkspacePage передал в центр (seen — все показанные).
// SessionList — заглушка с автовыбором как в продукте (при activeSession=null открывает live[0]):
// без неё «приоритет адреса» проверялся бы формально — список открыл бы первый чат сам.

import { act } from 'react';
import { createElement } from 'react';
import { StrictMode } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { WorkspacePage } from './WorkspacePage';
import type { Project, Session } from '../types';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr,
} from '../components/chat/__tests__/fakeDom';

// ── localStorage / sessionStorage — vi.hoisted: модули читают их при импорте ──
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
  // Раскладка панельного стора (cc_ws_zones) читается при импорте модуля
  store.set('cc_ws_zones', JSON.stringify({ left: { layout: [['chats']] }, right: { layout: [] } }));
  return { store, sstore };
});

// ── workspaceState: восстановленный «последний чат» задаётся тестом (инициализатор WorkspacePage) ──
const wsState = vi.hoisted(() => ({ activeSession: null as unknown, openFile: null as string | null, leftTab: undefined as string | undefined }));
vi.mock('../lib/workspaceState', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/workspaceState')>();
  return {
    ...orig,
    loadWorkspaceState: () => ({ activeSession: wsState.activeSession, openFile: wsState.openFile, leftTab: wsState.leftTab }),
    saveWorkspaceState: () => {},
    loadFileFullscreenPref: () => false, saveFileFullscreenPref: () => {},
    isLeftTab: (t: string) => ['sessions', 'files', 'changes', 'tasks', 'knowledge', 'personas', 'skills', 'tools', 'plugins'].includes(t),
  };
});

// ── signalr / terminalSignalr: подписки и действия no-op ──
vi.mock('../lib/signalr', () => {
  const sub = () => () => {};
  const act = () => Promise.resolve();
  return {
    __esModule: true,
    onMessage: sub, onReconnected: sub, onFilesChanged: sub, onPreviewMessage: sub,
    onLocalModelMessage: sub, onGitStatusChanged: sub, onAgentHubMessage: sub,
    onProjectAccessRevoked: sub,
    joinProject: act, leaveProject: act, joinSession: act, leaveSession: act,
    joinUser: act, leaveUser: act, sendMessage: act, respondPermission: act,
    interruptSession: act, compactSession: act, answerQuestion: act, respondPlan: act,
    respondTeamPlan: act, respondTeamEscalation: act, setMode: act, resetConnection: act,
    joinPreviewLog: act, leavePreviewLog: act,
  };
});
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

// ── api: sessions.list отдаёт mockData.chats; projects.list — проект; остальное no-op ──
const mockData = vi.hoisted(() => ({ chats: [] as Session[] }));
vi.mock('../lib/api', () => {
  const ns = (overrides: Record<string, () => any>): any => new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      const o = overrides[prop as string];
      return o ?? (() => Promise.resolve(null));
    },
  });
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
  const api: any = new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'sessions') return ns({ list: () => Promise.resolve(mockData.chats) });
      if (prop === 'projects') return ns({ list: () => Promise.resolve([{ id: 'p1', name: 'Проект', rootPath: '/p' }]) });
      if (prop === 'tasks') return ns({ listAll: () => Promise.resolve([]), featuresIndex: () => Promise.resolve(null) });
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
      return deep(null);
    },
  });
  return { api, setGitSessionContext: () => {} };
});

// ── toast: журнал тостов (тост «чат недоступен») ──
const toastMock = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../lib/toast', () => ({
  __esModule: true,
  showToast: (title: string, body: string) => { toastMock.calls.push(`${title}: ${body}`); },
}));

// ── subsystems / registry / capabilities / personas / git: no-op ──
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

// ── SessionList: заглушка с автовыбором как в продукте (SessionList.tsx:463-477):
//     «нет активного чата → выбираем live[0]». Без неё «приоритет адреса» оставался бы
//     формальным: список открыл бы первый чат, пока диплинк ещё разрешается. ──
const mockSessionList = vi.hoisted(() => [] as Session[]);
vi.mock('../components/SessionList', async () => {
  const { createElement, useEffect, useRef } = await import('react');
  return {
    SessionList: ({ activeSession, onSessionsListChanged, onSelect }: { activeSession?: Session | null; onSessionsListChanged?: (s: Session[]) => void; onSelect?: (s: Session, first?: string, autoSelect?: boolean) => void }) => {
      const activeRef = useRef<Session | null>(null);
      const initializedRef = useRef(false);
      // Как в продукте: ref-зеркало активного чата (автовыбор читает его, а не проп)
      useEffect(() => { activeRef.current = activeSession ?? null; }, [activeSession]);
      useEffect(() => {
        if (onSessionsListChanged && mockSessionList.length) onSessionsListChanged(mockSessionList);
        if (!initializedRef.current && mockSessionList.length) {
          initializedRef.current = true;
          const live = mockSessionList.filter(s => !s.archivedAt);
          if (!activeRef.current && live.length > 0) onSelect?.(live[0], undefined, true);
        }
      }, [onSessionsListChanged, onSelect, mockSessionList.length]);
      return createElement('div', { 'data-testid': 'sessionlist-stub' });
    },
  };
});

// ── ChatPanel: записывающая заглушка — маркер «открытый чат» (session.id центра).
//     seen — все чаты, хоть раз показанные в центре: «приоритет адреса» значит, что
//     сохранённый не показан НИКОГДА — ни в окно до разрешения списка, ни позже. ──
const chatPanelMock = vi.hoisted(() => ({ sessionId: null as string | null, seen: [] as string[] }));
vi.mock('../components/ChatPanel', async () => {
  const { createElement } = await import('react');
  return {
    ChatPanel: ({ session }: { session: Session }) => {
      chatPanelMock.sessionId = session.id;
      if (!chatPanelMock.seen.includes(session.id)) chatPanelMock.seen.push(session.id);
      return createElement('div', { 'data-testid': 'chat-panel-stub' });
    },
  };
});

// ── useProjectTerminals: terminals=[] (как в образце — deep(null) дал бы null и .find упал) ──
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

// ── Данные ──
const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;
// «Последний активный» из локального хранилища (приватный) и чат из адреса (общий)
const chatPriv: Session = { id: 'chatPriv', name: 'Ход приватный', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;
const chatGen: Session = { id: 'chatGen', name: 'Ход общий', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0, createdAt: '', updatedAt: '' } as Session;

// Как в продукте (main.tsx): StrictMode над деревом — двойной прогон эффектов на маунте.
// Инстанс выгружаем в afterEach: без этого живой WorkspacePage прошлого кейса рендерится
// в общем mock'е и его чат попадает в seen следующего (течьёт маркер).
let currentRoot: { unmount(): void } | null = null;
function mountWorkspace() {
  const m = mount(createElement(StrictMode, null, createElement(WorkspacePage, {
    project,
    auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
    onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
  } as any)));
  currentRoot = m.root;
  return m;
}

describe('WorkspacePage: диплинк на чат против сохранённого «последнего чата» (QA-D2)', { timeout: 15_000 }, () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    // Десктоп 1280: центр с чатом виден всегда (тот же прогон, что у QA на стенде)
    (globalThis as any).window.innerWidth = 1280;
    (globalThis as any).document.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    (globalThis as any).window.innerHeight = 800;
    const win = globalThis as any;
    win.window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
    // История пишет снимок и адрес (navPush/navReplace в выборе чата должны доходить до адреса)
    win.window.history = {
      state: null, back: () => {}, go: () => {}, forward: () => {}, length: 1,
      pushState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
      replaceState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
    };
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
    win.requestAnimationFrame = win.window.requestAnimationFrame;
    win.cancelAnimationFrame = win.window.cancelAnimationFrame;
    win.window.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
    win.window.getComputedStyle = () => ({ paddingLeft: '0px', paddingRight: '0px' });
    win.ResizeObserver = win.window.ResizeObserver;
    win.getComputedStyle = win.window.getComputedStyle;
    win.Image = class { set src(_v: string) {} onload: any; onerror: any; width = 0; height = 0; };
    win.MutationObserver = class { observe() {} disconnect() {} takeRecords() { return []; } };
    win.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
    store.clear(); sstore.clear();
    mockSessionList.length = 0;
    mockData.chats.length = 0;
    chatPanelMock.sessionId = null;
    chatPanelMock.seen.length = 0;
    toastMock.calls.length = 0;
    wsState.activeSession = null;
    wsState.openFile = null;
    wsState.leftTab = undefined;
  });
  afterEach(() => {
    // Свой инстанс — до выгрузки DOM: чат прошлого кейса не должен жить в общем маркере
    if (currentRoot) act(() => { currentRoot!.unmount(); });
    currentRoot = null;
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('c1: сохранён приватный, в адресе общий — открывается чат из адреса, сохранённый не показан никогда', async () => {
    const win = globalThis as any;
    // Что было до перезагрузки: открыт приватный, он же в ws:{p1} и первым в списке
    // (последний по активности — именно его открыл бы и автовыбор SessionList)
    wsState.activeSession = chatPriv;
    mockSessionList.push(chatPriv, chatGen);
    mockData.chats.push(chatPriv, chatGen);
    // Что делает App при загрузке по ссылке: pending-канал + адрес из ссылки, истории нет
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatGen');
    win.window.location.hash = '#/project/p1/chat/chatGen';
    win.window.history.state = null;
    const { container } = mountWorkspace();
    await act(async () => { vi.advanceTimersByTime(100); });
    for (let i = 0; i < 20 && !findNodeByAttr(container, 'data-testid', 'chat-panel-stub'); i++) {
      await act(async () => { vi.advanceTimersByTime(100); });
    }
    // КЛЮЧЕВОЕ (QA-D2): в центре чат из адреса, и сохранённый не показан НИКОГДА —
    // ни в окно до разрешения списка (иначе сообщение ушло бы в него), ни позже.
    // Мутация «вернуть приоритет сохранённого» (снять гейт в инициализаторе + перехват
    // автовыбора) — красный: при прежнем коде chatPriv показан сразу на маунте.
    expect(chatPanelMock.seen, `сохранённый чат показался в центре (${JSON.stringify(chatPanelMock.seen)})`).not.toContain('chatPriv');
    expect(chatPanelMock.sessionId, `открыт не чат из адреса (открыт: ${chatPanelMock.sessionId})`).toBe('chatGen');
    // Адрес не потерял чат из ссылки
    expect(win.window.location.hash, `адрес потерял chatId (${win.window.location.hash})`).toContain('/chat/chatGen');
  });

  it('c2: чата из адреса нет в списке — «чат недоступен», другой чат НЕ открыт', async () => {
    const win = globalThis as any;
    // Сохранён приватный; в адресе чат, которого нет на сервере (удалён/нет доступа)
    wsState.activeSession = chatPriv;
    mockSessionList.push(chatPriv);
    mockData.chats.push(chatPriv);
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatGone');
    win.window.location.hash = '#/project/p1/chat/chatGone';
    win.window.history.state = null;
    const { container } = mountWorkspace();
    await act(async () => { vi.advanceTimersByTime(100); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // КЛЮЧЕВОЕ: внятный тост и НИКАКОЙ чат в центре (приватный не открывается молча,
    // сообщение в него уйти не может). Мутация «вернуть приоритет сохранённого» — red:
    // при прежнем коде chatPriv показан на маунте и остаётся открытым без тоста.
    expect(toastMock.calls.some(t => t.includes('недоступен')), `тоста о недоступном чате нет (${JSON.stringify(toastMock.calls)})`).toBe(true);
    expect(chatPanelMock.seen, `под недоступный адрес открылся другой чат (${JSON.stringify(chatPanelMock.seen)})`).not.toContain('chatPriv');
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel-stub'), 'под недоступный адрес открылся другой чат').toBeNull();
  });

  // Сценарий ревью f7b2057b в пути загрузки: адрес называет ровно сохранённый чат, его нет на
  // сервере. Финальное состояние держат два механизма: валидация «чата-призрака» на маунте
  // (WorkspacePage ~1866) и сброс в ветке «чата нет» pending-эффекта. Кейс фиксирует итог —
  // он остаётся зелёным и без сброса (валидация на маунте справляет сама), но гасит регрессию,
  // если валидацию когда-нибудь перенесут/снимут.
  it('QA-D2b: адрес называет тот же чат, что сохранён, и он удалён (путь загрузки) — отказ, в центре пусто', async () => {
    const win = globalThis as any;
    // Сохранённый «последний чат» — ровно тот, что называет адрес: инициализатор возвращает
    // его («тот же чат, что сохранён», строка ~461) — это штатная перезагрузка в том же чате.
    // На сервере чата больше нет (удалён/доступ отозван): ни списка, ни разрешения pending.
    wsState.activeSession = chatPriv;
    // Соседний живой чат в списке — чтобы «никакой не остаётся открытым» проверялся против
    // реального соседа: после отказа центр не должен съехать и на него.
    mockSessionList.push(chatGen);
    mockData.chats.push(chatGen);
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatPriv');
    win.window.location.hash = '#/project/p1/chat/chatPriv';
    win.window.history.state = null;
    const { container } = mountWorkspace();
    await act(async () => { vi.advanceTimersByTime(100); });
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(toastMock.calls.some(t => t.includes('недоступен')), `тоста о недоступном чате нет (${JSON.stringify(toastMock.calls)})`).toBe(true);
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel-stub'), 'под отказом в центре остался удалённый чат').toBeNull();
  });

  it('QA-D2b: открытый в приложении чат удалили, и адрес называет его — отказ, центр не остаётся с удалённым', async () => {
    const win = globalThis as any;
    // Состояние «внутри приложения»: открыт chatGen (он же сохранённый), сервер его ещё знает —
    // валидация «чата-призрака» на маунте проходит, состояние валидно.
    wsState.activeSession = chatGen;
    mockSessionList.push(chatGen);
    mockData.chats.push(chatGen);
    const { container } = mountWorkspace();
    await act(async () => { vi.advanceTimersByTime(100); });
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel-stub'), 'состояние «открытый чат» не сформировано').not.toBeNull();
    // Чат удалили на сервере, и пользователь кликнул по ссылке на него: App пишет канал и
    // будит уже смонтированную страницу (путь openNotificationUrl / hashchange — QA-D2b).
    mockData.chats.length = 0;
    win.sessionStorage.setItem('cc_pending_project_chat', 'p1|chatGen');
    win.window.dispatchEvent(new Event('cc-pending-project-chat'));
    await act(async () => { vi.advanceTimersByTime(100); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // КЛЮЧЕВОЕ (хвост ревью f7b2057b): отказ есть, а центр НЕ остаётся с удалённым чатом.
    // Валидация маунта тут не помогает — страница уже смонтирована; без сброса в ветке
    // «чата нет» ChatPanel продолжает рендериться по activeSession (строка ~2583).
    // Мутация «убрать setActiveSession reset в ветке !s» — красный.
    expect(toastMock.calls.some(t => t.includes('недоступен')), `тоста о недоступном чате нет (${JSON.stringify(toastMock.calls)})`).toBe(true);
    expect(findNodeByAttr(container, 'data-testid', 'chat-panel-stub'), 'под отказом в центре остался удалённый чат').toBeNull();
  });
});
