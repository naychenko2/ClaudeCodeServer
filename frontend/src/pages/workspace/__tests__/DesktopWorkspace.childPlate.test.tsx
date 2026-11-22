// Г3б-д1 (доработка ревью Р-Г3б, дефект 36e0160d): правило видимости плашки предмета.
// childPlateVisible («обе зоны пусты» и не планшет) вычисляется в DesktopWorkspace и
// доезжает до SubjectPlate через ProjectRail. Мутация «const childPlateVisible =
// true && !p.isTablet» (плашка снова постоянная, поверх открытой панели «Чаты») обязана
// краснить этот тест. Проверка — живой рендер DesktopWorkspace с детским доком:
// при открытой панели «Чаты» (дефолт раскладки) плашки в DOM нет вовсе (SubjectPlate
// при visible=false возвращает null), при закрытых зones — плашка стоит порталом в body,
// при возврате раскладки — снова уходит.
// Окружение и набор моков — копия DesktopWorkspace.child.test.tsx (там же объяснение,
// почему PanelZone заглушена ровно до railFooter, а транспорт — no-op).

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project, Session } from '../../../types';
import type { ChildWorkspaceProps } from '../../../features/childMode/types';
import type { ChildSubjectWithViewers } from '../../../features/childMode/api';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr } from '../../../components/chat/__tests__/fakeDom';
import { wsPanels } from '../panelStackState';
import { __resetWatchdogPresence } from '../../../lib/watchdogPresence';

// ── localStorage / sessionStorage vi.hoisted: panelStackState и useActionVisibility
//     читают их при импорте модулей, до module-level кода теста ──
const { store, sstore } = vi.hoisted(() => {
  (globalThis as any).self = globalThis;
  const store = new Map<string, string>();
  const sstore = new Map<string, string>();
  (globalThis as any).localStorage = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => { store.set(k, String(v)); },
    removeItem: (k: string) => { delete store[k]; },
  } as Storage;
  (globalThis as any).sessionStorage = {
    getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {},
    key: () => null, get length() { return 0; },
  } as Storage;
  return { store, sstore };
});

// ── PanelZone: заглушка, рисующая свой railFooter — док (ProjectRail) остаётся
//     настоящим; внутренности зоны этому тесту не нужны ──
vi.mock('../PanelZone', async () => {
  const { createElement } = await import('react');
  return { PanelZone: (p: { railFooter?: unknown }) => createElement('div', { 'data-testid': 'panelzone-stub' }, p.railFooter as any) };
});

// ── тяжёлые дети: маркеры вместо графики ──
vi.mock('../../../components/chat/ChatHeaderBar', async () => {
  const React = await import('react');
  return { ChatHeaderBar: () => React.createElement('div', { 'data-testid': 'chat-header-bar-stub' }) };
});

// ── транспорт: no-op ──
vi.mock('../../../lib/signalr', () => {
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
const mockChats = vi.hoisted(() => [] as Session[]);
vi.mock('../../../lib/api', () => {
  const deep = (val: any): any => {
    const fn: any = () => Promise.resolve(val ?? null);
    return new Proxy(fn, {
      get: (_t, prop) => {
        if (prop === 'then' || prop === Symbol.toPrimitive || prop === Symbol.iterator) return undefined;
        if (prop === 'catch' || prop === 'finally') return () => Promise.resolve(val ?? null);
        return deep(val);
      },
      apply: () => Promise.resolve(val ?? null),
    });
  };
  const ns = (overrides: Record<string, () => any>): any => new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      const o = overrides[prop as string];
      return o ?? (() => Promise.resolve(null));
    },
  });
  const api: any = new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'tasks') return ns({ featuresIndex: () => Promise.resolve({ features: [], chats: {} }), getFeature: () => Promise.resolve(null), listAll: () => Promise.resolve([]) });
      if (prop === 'chats') return ns({ get: () => Promise.resolve(null) });
      if (prop === 'sessions') return ns({ list: () => Promise.resolve(mockChats) });
      if (prop === 'projects') return ns({ list: () => Promise.resolve([]) });
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
      if (prop === 'watchdogs') return ns({ snapshot: () => Promise.resolve({ sessions: [], projects: [] }) });
      return deep(null);
    },
  });
  return { api, setGitSessionContext: () => {} };
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
vi.mock('../../../lib/projectCapabilities', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/projectCapabilities')>();
  return { ...orig, useProjectFeature: () => false, isLocalProject: () => false, featureReason: () => null };
});
vi.mock('../../../lib/subsystems/registry', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/subsystems/registry')>();
  return { ...orig, useSlot: () => [], useSlotItem: () => null };
});
vi.mock('../../../lib/videoStage', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/videoStage')>();
  return { ...orig, useVideoCenter: () => null, useVideoCenterSplit: () => false, setVideoCenterBlocked: () => {} };
});
vi.mock('../../../hooks/useVoiceInput', () => ({
  __esModule: true,
  useVoiceInput: () => ({ hasSpeech: true, isListening: false, recSeconds: 0, startMic: () => {}, stopMic: () => {} }),
}));
vi.mock('../../../hooks/useHandsFree', () => ({
  __esModule: true,
  useHandsFree: () => ({ phase: 'off', active: false, buffer: '', barren: 0, warned: false, seq: 0 }),
}));
vi.mock('../../../hooks/useMicLevel', () => ({
  __esModule: true,
  useMicLevel: () => ({ reportMicDead: () => {}, reportCycleEnd: () => {}, reportCycleStart: () => {} }),
}));
vi.mock('../../../hooks/useOnline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../hooks/useOnline')>();
  return { ...orig, useOnline: () => true };
});

import { DesktopWorkspace } from '../DesktopWorkspace';

// ── Данные: тот же шим, что DesktopWorkspace.child.test ──
const project: Project = {
  id: 'p1', name: 'Математика', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;
// mode обязателен: ChatPanel инициализирует им состояние режима; model — чтобы взрослые
// проверки «выбор модели есть» были невакуумными (копия комментария шаблона)
const session: Session = {
  id: 'chatK', name: 'Дроби', projectId: 'p1', mode: 'default', status: 'active', messageCount: 0,
  model: 'sonnet', createdAt: '', updatedAt: '',
} as Session;

const subject = (over: Partial<ChildSubjectWithViewers> = {}): ChildSubjectWithViewers => ({
  id: 'math', label: 'Математика', glyph: '×÷', color: 'info',
  tutor: { personaId: 'p1', name: 'Сова Ульяна', hasAvatar: false }, ...over,
});

const childSurface: ChildWorkspaceProps = {
  features: ['text', 'voiceInput'],
  feedKinds: ['assistant', 'user'],
  header: { subject: subject(), viewers: ['Папа'] },
  dock: { subjects: [subject({ viewers: ['Папа'] })], activeSubjectId: 'math', onOpenSubject: () => {} },
};

// Полный набор обязательных пропов DesktopWorkspace (tsc держит полноту обязательных).
type DWProps = Parameters<typeof DesktopWorkspace>[0];
function baseProps(over: Partial<DWProps> = {}): DWProps {
  return {
    project, projectForEdit: project, onOpenProjectSettings: () => {},
    activeSession: session, onSelectSession: () => {}, onSessionUpdated: () => {},
    onCreateSession: () => {}, onClearSession: () => {},
    onPendingMessageSent: () => {}, onWorkflowRunning: () => {},
    attachedFiles: [], onAttachedFilesChange: () => {},
    openFile: null, openFileDiffMode: false, fileFullscreen: false, onToggleFullscreen: () => {},
    openCommitSha: null, onCloseCommit: () => {}, onOpenFileFromChat: () => {},
    readerState: { open: false, loading: false, url: null, mode: null, iframeUnavailable: false, page: null, error: null, canGoBack: false, expanded: false, bannerDismissed: false },
    readerActions: { openUrl: () => {}, follow: () => {}, back: () => {}, retry: () => {}, openInBrowser: () => {}, toggleExpand: () => {}, dismissBanner: () => {}, toggleMode: () => {}, onIframeLoad: () => {}, closeReader: () => {} },
    selectedTask: null, autoEditTaskId: null, onOpenTaskSession: () => {}, onOpenFileFromTree: () => {}, onCloseTask: () => {},
    selectedPersonaId: null, personaCreating: false, onOpenPersonaChat: () => {}, onPersonaSelectAfterCreate: () => {}, onPersonaCleared: () => {},
    teamCenterOpen: false, onCloseTeamCenter: () => {}, teamCenterArea: null,
    boardOpen: false, boardArea: null, previewOpen: false, previewArea: null, onClosePreview: () => {},
    graphOpen: false, graphArea: null,
    panels: {}, openFeatureId: null, onFeatureBack: () => {}, featureOpenedAt: 0, fileOpenedAt: 0,
    ...over,
  };
}

async function mountWorkspace(props: DWProps) {
  const { root, container } = mount(createElement(DesktopWorkspace, props));
  await act(async () => { vi.advanceTimersByTime(300); }); // спиннер истории (200ms) отработал, DOM стабилен
  return { root, container: container as unknown as NodeLike };
}

// ── Поиск по фейк-DOM ──
type NodeLike = { nodeType: number; nodeName: string; childNodes: NodeLike[]; getAttribute: (a: string) => string | null };
const walk = (n: NodeLike, cb: (n: NodeLike) => void): void => { cb(n); for (const c of n.childNodes ?? []) walk(c, cb); };
// Кнопка «Новый урок» — портал в body: ищем по документу
const plateButton = (): NodeLike | null =>
  findNodeByAttr((globalThis as any).document.body as any, 'data-testid', 'child-new-lesson');

describe('плашка предмета: видна только при закрытых зонах (Г3б-д1)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    const win = globalThis as any;
    win.window.innerWidth = 1600; win.window.innerHeight = 800;
    win.window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
    win.window.history = { state: null, pushState: () => {}, replaceState: () => {}, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
    win.window.requestAnimationFrame = (cb: any) => { cb(0); return 1; };
    win.window.cancelAnimationFrame = () => {};
    win.requestAnimationFrame = win.window.requestAnimationFrame;
    win.cancelAnimationFrame = win.window.cancelAnimationFrame;
    win.window.getSelection = () => null;
    win.window.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
    win.ResizeObserver = win.window.ResizeObserver;
    win.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
    win.MutationObserver = class { observe() {} disconnect() {} takeRecords() { return []; } };
    win.window.getComputedStyle = () => ({ overflowY: 'visible', paddingLeft: '0px', paddingRight: '0px', getPropertyValue: () => '' });
    win.getComputedStyle = win.window.getComputedStyle;
    win.Image = class { set src(_v: string) {} onload: any; onerror: any; width = 0; height = 0; };
    if (!win.document.documentElement) win.document.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    store.clear(); sstore.clear();
    mockChats.length = 0; mockChats.push(session);
    __resetWatchdogPresence();
  });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('при открытой панели «Чаты» плашки нет, при закрытых зонах — стоит, при возврате раскладки — уходит', async () => {
    // Дефолт раскладки wsPanels: left=[[chats]] — панель «Чаты» открыта
    const { root } = await mountWorkspace(baseProps({ child: childSurface }));
    try {
      expect(plateButton(), 'плашка предмета при открытой панели «Чаты»').toBeNull();

      // Свернули зону «Чаты» (кнопка «Свернуть все» зоны): обе зоны пусты — плашка встала
      act(() => { wsPanels.ops.toggleCollapsed('left'); });
      expect(plateButton(), 'плашки предмета при закрытых зонах нет').not.toBeNull();

      // Правило живо в обе стороны: вернули раскладку — плашка ушла
      act(() => { wsPanels.ops.toggleCollapsed('left'); });
      expect(plateButton(), 'плашка предмета после возврата раскладки').toBeNull();
    } finally { root.unmount(); }
  });
});
