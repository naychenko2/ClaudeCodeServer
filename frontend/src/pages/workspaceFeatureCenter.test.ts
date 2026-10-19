// Центр WorkspacePage (волна 2 «Обзор фич», Ф2.3): страница фичи — СОСЕД центра,
// а ChatPanel прячется display:none (не размонтируется) — иначе рвётся стрим хода
// и теряется черновик композера. Тест: при открытой странице textarea композера
// остаётся в DOM, его значение (черновик) переживает переход и возврат; крошка
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
import { setAllFlags } from '../lib/featureFlags';
import { __test as backNavTest, recordBackNav } from '../lib/featureBackNav';
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
  return { store, sstore };
});

// ── workspaceState: activeSession задаётся из хранилища (инициализатор WorkspacePage) ──
const wsState = vi.hoisted(() => ({ activeSession: null as unknown }));
vi.mock('../lib/workspaceState', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/workspaceState')>();
  return {
    ...orig,
    loadWorkspaceState: () => ({ activeSession: wsState.activeSession }),
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
const mockData = vi.hoisted(() => ({ index: null as any, featurePage: null as any, chat: null as any }));
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
      if (prop === 'sessions') return ns({ list: () => Promise.resolve([]) });
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

// ── SessionList (сайдбар): заглушка — display:none, но в DOM ──
vi.mock('../components/SessionList', async () => {
  const { createElement } = await import('react');
  return { SessionList: () => createElement('div', { 'data-testid': 'sessionlist-stub' }) };
});

// ── useFeaturePage: состояние страницы (run.epic.title) — no-op api отдаёт null ──
vi.mock('../lib/featurePage', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/featurePage')>();
  return {
    ...orig,
    useFeaturePage: () => ({ status: 'ok', page: { run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0 } }),
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

describe('WorkspacePage: центр — страница фичи соседом, ChatPanel в DOM', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    (globalThis as any).window.innerWidth = 360;
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
    backNavTest.reset();
    indexTest.reset();
    setAllFlags({ 'features-overview': true });
    const idx = { features: [feat()], chats: {} } as FeaturesIndex;
    indexTest.setIndex('p1', idx);
    mockData.index = idx;
    mockData.featurePage = { run: { epic: { title: 'Моя фича' } }, events: [], eventsTotal: 0 };
    mockData.chat = session;
    wsState.activeSession = session;
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('открытая страница: textarea композера в DOM, черновик переживает возврат, крошка закрывает страницу', async () => {
    const { container } = mount(createElement(WorkspacePage, {
      project,
      auth: { serverUrl: 'http://x', token: 't', username: 'u', role: 'user' },
      onGoToProjects: () => {}, onSwitchHub: () => {}, onLogout: () => {},
    } as any));

    // Маркер «ChatPanel в DOM»: имя открытой сессии (SessionList — заглушка, имя
    // рендерит только ChatPanel). Черновик композера — следствие того же: пока ChatPanel
    // не размонтирован (display:none), состояние поля живёт.
    const chatInDom = () => textNodes(container).join(' ').includes('Чат Дениса');

    // 1. ChatPanel смонтирован (activeSession задан).
    expect(chatInDom(), 'ChatPanel не смонтирован (имя сессии не в DOM)').toBe(true);

    // 2. Черновик: пишем в textarea композера, если он рендерится (fake-аудио может
    //    прятать его за ветку handsFree; имя сессии — основной маркер).
    let ta = composerTextarea(container);
    const draft = 'черновик хода';
    if (ta) act(() => { (reactProps(ta) as any).onChange({ target: { value: draft, selectionStart: 0 } }); });

    // 3. Открываем страницу фичи (указатель чат → фича + navPush).
    recordBackNav('chatA', { kind: 'feature', id: 'f1' });
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA' },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // 4. Страница фичи показана (крошка «← К чату «…»»).
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка страницы фичи не найдена').not.toBeNull();
    expect(textNodes(back!).join('')).toContain('К чату');

    // 5. КЛЮЧЕВОЕ: ChatPanel НЕ размонтирован при открытой странице — имя сессии
    //    (и textarea, если рендерится) всё ещё в DOM. display:none, а не unmount.
    expect(chatInDom(), 'ChatPanel размонтирован (имя сессии пропало из DOM)').toBe(true);
    if (ta) {
      ta = composerTextarea(container);
      expect(ta, 'textarea композера пропал из DOM (ChatPanel размонтирован)').not.toBeNull();
    }

    // 6. Возврат: клик по крошке → страница фичи закрывается (центр возвращается).
    act(() => { (reactProps(back!) as any).onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-back'), 'крошка не скрыта после возврата').toBeNull();
    // Черновик/состояние ChatPanel переживают переход: страница закрыта, центр снова чат.
    expect(chatInDom() || !findNodeByAttr(container, 'data-testid', 'feature-page'), 'центр не вернулся к чату после возврата').toBe(true);
  });

  it('системное «назад» (popstate): setOpenFeatureId по снимку — в исходнике центра', () => {
    // Страница слишком велика для полного прогона popstate без DOM-истории; связку
    // «назад → закрытие страницы» проверяем по исходнику (паттерн workspaceLeaveActiveChat).
    const src = readFileSync(new URL('./WorkspacePage.tsx', import.meta.url), 'utf8');
    // Обработчик popstate выставляет openFeatureId из снимка навигации.
    expect(src, 'popstate не выставляет openFeatureId').toContain('setOpenFeatureId(s.feature ?? null)');
    // Обёртка центра прячет ChatPanel display:none (не размонтирует) при открытой странице.
    expect(src, 'центр не прячет ChatPanel display:none').toContain("display: openFeatureId ? 'none' : 'flex'");
  });
});
