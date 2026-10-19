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
import { setAllFlags, __test as flagsTest } from '../lib/featureFlags';
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
      // list отдаёт активный чат: эффект «чат-призрак» (WorkspacePage) сбрасывает
      // activeSession в null, если его id нет в списке — пустой список молча терял бы чат.
      if (prop === 'sessions') return ns({ list: () => Promise.resolve(mockData.chat ? [mockData.chat] : []) });
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

  it('открытая страница: ChatPanel в DOM (data-testid), черновик композера переживает возврат, крошка закрывает страницу', async () => {
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
    //    сам пишет указатель возврата (chatA → фича) — крошка «← К чату «…»».
    act(() => {
      (globalThis as any).window.dispatchEvent(new CustomEvent('cc-open-feature', {
        detail: { featureId: 'f1', fromChatId: 'chatA', fromChat: true },
      }));
    });
    await act(async () => { vi.advanceTimersByTime(100); });

    // 4. Страница фичи показана (крошка «← К чату «…»»).
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка страницы фичи не найдена').not.toBeNull();
    expect(textNodes(back!).join('')).toContain('К чату');

    // 5. КЛЮЧЕВОЕ (MAJOR-3): ChatPanel НЕ размонтирован при открытой странице
    //    (display:none, а не unmount) — testid в DOM. Мутация «display → условный
    //    рендер» краснит этот тест (testid пропадает из DOM).
    expect(chatPanelInDom(), 'ChatPanel размонтирован (data-testid="chat-panel" пропал из DOM)').toBe(true);
    // 6. Композер (обязательно): textarea в DOM и черновик переживает открытие страницы.
    const taAfterOpen = composerTextarea(container);
    expect(taAfterOpen, 'textarea композера пропал из DOM (ChatPanel размонтирован)').not.toBeNull();
    expect((reactProps(taAfterOpen!) as any).value, 'черновик композера потерян при открытой странице').toBe(draft);

    // 7. Возврат: клик по крошке → страница фичи закрывается (центр возвращается).
    act(() => { (reactProps(back!) as any).onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-back'), 'крошка не скрыта после возврата').toBeNull();
    // ChatPanel/черновик переживают переход: страница закрыта, центр снова чат.
    expect(chatPanelInDom(), 'центр не вернулся к чату (ChatPanel пропал) после возврата').toBe(true);
    // Композер (обязательно): textarea в DOM и черновик пережил возврат.
    const taAfterReturn = composerTextarea(container);
    expect(taAfterReturn, 'textarea композера пропал после возврата').not.toBeNull();
    expect((reactProps(taAfterReturn!) as any).value, 'черновик композера не пережил возврат').toBe(draft);
  });

  it('системное «назад» (popstate): setOpenFeatureId по снимку — в исходнике центра', () => {
    // Страница слишком велика для полного прогона popstate без DOM-истории; связку
    // «назад → закрытие страницы» проверяем по исходнику (паттерн workspaceLeaveActiveChat).
    const src = readFileSync(new URL('./WorkspacePage.tsx', import.meta.url), 'utf8');
    // Обработчик popstate выставляет openFeatureId из снимка навигации.
    expect(src, 'popstate не выставляет openFeatureId').toContain('setOpenFeatureId(s.feature ?? null)');
    // Обёртка центра (FeatureCenter) прячет ChatPanel display:none (не размонтирует) при
    // открытой странице: общий паттерн мобильной и десктопной раскладки.
    const centerSrc = readFileSync(new URL('../components/feature/FeatureCenter.tsx', import.meta.url), 'utf8');
    expect(centerSrc, 'центр не прячет ChatPanel display:none').toContain("display: open ? 'none' : 'flex'");
  });

  it('диплинк #/project/p1/feature/f1: страница открывается при первом монтировании (чат в DOM, крошка)', async () => {
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

    // Страница фичи открыта: крошка есть (указателя нет → «← К ключевому чату»).
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка страницы фичи не найдена (диплинк не открыл страницу)').not.toBeNull();
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

    // Страница не открыта: ни крошки, ни testid feature-page (сегмент игнорируется).
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-back'), 'крошка есть при выключенном флаге').toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница фичи открыта при выключенном флаге').toBeNull();
  });

  it('крошка: navReplace синхронизирует URL (feature исчезает из хэша), «назад» ведёт туда же', async () => {
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

    // Клик по крошке (kind 'chat', chatA = текущий чат): navReplace на снимок БЕЗ feature.
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка не найдена').not.toBeNull();
    act(() => { (reactProps(back!) as any).onClick(); });
    // Хэш больше не содержит feature (крошка и история — в одно место).
    expect(win.window.location.hash, 'feature остался в hash после возврата').not.toContain('/feature/');
    // Снимок истории (history.state) тоже без feature: «назад» ведёт туда же, что крошка.
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
    //    красит этот тест: feature-page/feature-page-back пропадают из DOM.
    const page = findNodeByAttr(container, 'data-testid', 'feature-page');
    expect(page, 'страница фичи не в DOM на десктопе (C1)').not.toBeNull();
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка страницы фичи не найдена (C1)').not.toBeNull();

    // 4. ChatPanel НЕ размонтирован (display:none): testid в DOM.
    expect(chatPanelInDom(), 'ChatPanel размонтирован на десктопе (C1)').toBe(true);

    // 5. Возврат: клик по крошке → страница закрывается, центр (чат) возвращается.
    act(() => { (reactProps(back!) as any).onClick(); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-page'), 'страница не скрыта после возврата (C1)').toBeNull();
    expect(chatPanelInDom(), 'чат не восстановлен после возврата (C1)').toBe(true);
  });

  // === M3 + m4: вход из списка (fromChat: false) — крошка «К списку», не в активный чат ===
  it('M3/m4: вход из списка — крошка «К списку» (мобила), не в активный (не открытый) чат', async () => {
    // Активный чат (chatA) ≠ ключевой чат фичи (chat-key). m4: крошка и системное «назад»
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

    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка страницы фичи не найдена (M3/m4)').not.toBeNull();
    // m4: крошка «К списку» (мобила), НЕ «К ключевому чату» и НЕ «К чату».
    const text = textNodes(back!).join('');
    expect(text, 'крошка не «К списку» (m4: из списка — в список, не в ключевой/активный чат)').toContain('К списку');
    expect(text, 'крошка ведёт в ключевой чат (m4: должна вести в список)').not.toContain('К ключевому чату');
    expect(text, 'крошка ведёт в активный (не открытый) чат (M3)').not.toContain('К чату');
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
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-back'), 'страница открыта до прихода флагов').toBeNull();

    // Флаги «пришли» включёнными (ответ /me): страница открывается, крошка есть.
    act(() => { setAllFlags({ 'features-overview': true }); });
    await act(async () => { vi.advanceTimersByTime(100); });
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка не появилась после прихода флагов (M1: страница не открылась)').not.toBeNull();
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
    expect(findNodeByAttr(container, 'data-testid', 'feature-page-back'), 'крошка есть при выключенном флаге (M1б)').toBeNull();
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

    // Страница открыта (крошка + testid feature-page)
    const page = findNodeByAttr(container, 'data-testid', 'feature-page');
    expect(page, 'страница фичи не открыта (M1-360)').not.toBeNull();
    // M1-360: на мобиле страница живёт в виде chat. При featureId в адресе mobileView='chat',
    // поэтому контейнер центра (родитель feature-page) виден (display:flex), а не скрыт
    // (display:none = вид sidebar = список). Мутация «mobileView всегда sidebar» красит:
    // контейнер display:none, на экране список, хотя адрес /feature/{fid}.
    const centerContainer = (page as any).parentNode;
    expect(centerContainer?.style?.display, 'центр (страница) не виден на 360 (M1-360: на экране список)').toBe('flex');
  });

  // === m5: вход из списка → крошка «К списку» → history.back() (не navReplace) ===
  it('m5: вход из списка — крошка «К списку» делает history.back() (индекс 0), не navReplace (дубль)', async () => {
    const win = globalThis as any;
    // Фейк-история со стеком и индексом: navPush=pushState, крошка=history.back()/navReplace.
    // Сценарий: [список] (index 0) → открытие страницы (navPush → index 1) → крошка.
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
    const back = findNodeByAttr(container, 'data-testid', 'feature-page-back');
    expect(back, 'крошка не найдена (m5)').not.toBeNull();
    act(() => { (reactProps(back!) as any).onClick(); });
    await act(async () => { vi.advanceTimersByTime(100); });
    // КЛЮЧЕВОЕ (m5): index=0 (history.back() снял запись страницы). Мутация «navReplace»
    // красит: index остался бы 1 (дубль [список, список]), первое «назад» — пустой шаг.
    expect(index, 'крошка не сняла запись страницы (m5: navReplace дал дубль, index=1)').toBe(0);
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
});

// Вспомогательный: toHash для фейк-истории (m5) — упрощённый, только project/feature/chat
function toHashOf(s: any): string {
  if (!s || s.screen !== 'project' || !s.project) return '#/projects';
  let h = `#/project/${s.project.id}`;
  if (s.feature) h += `/feature/${encodeURIComponent(s.feature)}`;
  else if (s.chatId) h += `/chat/${encodeURIComponent(s.chatId)}`;
  return h;
}
