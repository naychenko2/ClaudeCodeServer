// Г5-д1 (дефект QA-8 68812ebd): на 768 (планшет) ребёнок не видел списка уроков.
// Компактная левая зона DesktopWorkspace (PanelZone compact, drawer поверх) стартует
// с пустым эфемерным стеком tabletPanels=[] — панель «Чаты» (ChildLessonsPanel) при
// маунте не монтировалась вовсе: на 360 список в постоянном сайдбаре мобильной
// раскладки, на 1280 — постоянная колонка рельсы, а на 768 ребёнок мог только создать
// урок, открыть существующий — нет. Уроки при этом существуют (QA: на 360 и 1280
// строка «Новый урок» видна) — то есть дело в раскладке, не в данных.
//
// Фикс: defaultCompactPanel у левой зоны — панель, которую дровер открывает при маунте,
// один раз за жизнь зоны (PanelZone.defaultCompactPanel), и маунт планшета больше не
// бьёт closeCompactStack (DesktopWorkspace): тот сигнал — «смена сессии», а не первый
// кадр, иначе дефолт стирался бы на первом же эффект-цикле.
//
// Здесь ПанельZone настоящая (не заглушка, как в DesktopWorkspace.child.test): предмет
// теста — сама открытость дровера. Тяжёлые либы — no-op-моки того же набора, что в
// DesktopWorkspace.child.test; транспорт, api и childApi — как там же.
// Замер QA (clientWidth/scrollWidth) в fake-DOM невозможен — доказательство структурное:
// панель есть в DOM при маунте на 768 и её нет взрослому.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project, Session } from '../../../types';
import type { ChildWorkspaceProps } from '../../../features/childMode/types';
import type { ChildSubjectWithViewers } from '../../../features/childMode/api';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes } from '../../../components/chat/__tests__/fakeDom';
import { __test as sessionsListTest } from '../../../lib/sessionsList';

// ── localStorage / sessionStorage vi.hoisted: panelStackState и useActionVisibility
//     читают их при импорте модулей, до module-level кода теста ──
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
    getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {},
    key: () => null, get length() { return 0; },
  } as Storage;
  return { store, sstore };
});

// ── транспорт и сторы: no-op (тот же набор, что DesktopWorkspace.child.test.tsx) ──
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
      // list отдаёт уроки предмета: список панели на 768 — сам предмет теста
      if (prop === 'sessions') return ns({ list: () => Promise.resolve(mockChats) });
      if (prop === 'projects') return ns({ list: () => Promise.resolve([]) });
      // personas.list → []: иначе проп personas Composer'а null → «not iterable» при полном рендере
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
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
// Композеру нужен полный рендер: голосовой движок в node нет — hasSpeech=true даёт
// ровно то состояние, при котором человек видит кнопку «Голосовой ввод»
vi.mock('../../../hooks/useVoiceInput', () => ({
  useVoiceInput: () => ({ hasSpeech: true, isListening: false, recSeconds: 0, startMic: () => {}, stopMic: () => {} }),
}));
vi.mock('../../../hooks/useHandsFree', () => ({
  useHandsFree: () => ({ phase: 'off', active: false, buffer: '', barren: 0, warned: false, seq: 0 }),
}));
vi.mock('../../../hooks/useMicLevel', () => ({
  useMicLevel: () => ({ reportMicDead: () => {}, reportCycleEnd: () => {}, reportEngineHeard: () => {}, reportCycleStart: () => {} }),
}));
vi.mock('../../../hooks/useOnline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../hooks/useOnline')>();
  return { ...orig, useOnline: () => true };
});
// Взрослая шапка — маркер-заглушка (тот же приём, что DesktopWorkspace.child.test)
vi.mock('../../../components/chat/ChatHeaderBar', async () => {
  const React = await import('react');
  return { ChatHeaderBar: () => React.createElement('div', { 'data-testid': 'chat-header-bar-stub' }) };
});

import { DesktopWorkspace } from '../DesktopWorkspace';

// ── Данные ──
const project: Project = {
  id: 'p1', name: 'Математика', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;
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

// Полный набор обязательных пропов DesktopWorkspace (тот же, что DesktopWorkspace.child.test).
// isTablet=true — планшетная ветка раскладки (768 в живом приложении).
type DWProps = Parameters<typeof DesktopWorkspace>[0];
function baseProps(over: Partial<DWProps> = {}): DWProps {
  return {
    project, projectForEdit: project, onOpenProjectSettings: () => {},
    activeSession: null, onSelectSession: () => {}, onSessionUpdated: () => {},
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
    isTablet: true,
    ...over,
  };
}

// ── Поиск по фейк-DOM (обход от корня рендера) ──
type NodeLike = { nodeType: number; nodeName: string; childNodes: NodeLike[]; getAttribute: (a: string) => string | null; textContent?: string };
const walk = (n: NodeLike, cb: (n: NodeLike) => void): void => { cb(n); for (const c of n.childNodes ?? []) walk(c, cb); };
const rowNames = (container: NodeLike): string[] => {
  const rows: string[] = [];
  walk(container, (n) => {
    if (n.nodeType === 1 && n.getAttribute('data-testid') === 'child-lesson-row') rows.push(textNodes(n as never).join('').trim());
    });
  return rows;
};

async function mountWorkspace(props: DWProps) {
  const { root, container } = mount(createElement(DesktopWorkspace, props));
  await act(async () => { vi.advanceTimersByTime(300); }); // спиннер истории (200ms) отработал, DOM стабилен
  return { root, container: container as unknown as NodeLike };
}

describe('DesktopWorkspace: список уроков ребёнка на планшете (Г5-д1, 68812ebd)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    // 768 — планшетная зона (601–1199): компактная левая зона, дровер поверх контента
    (globalThis as any).window.innerWidth = 768;
    (globalThis as any).window.innerHeight = 1024;
    (globalThis as any).window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
    (globalThis as any).window.history = { state: null, pushState: () => {}, replaceState: () => {}, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
    (globalThis as any).window.requestAnimationFrame = (cb: any) => { cb(0); return 1; };
    (globalThis as any).window.cancelAnimationFrame = () => {};
    (globalThis as any).requestAnimationFrame = (globalThis as any).window.requestAnimationFrame;
    (globalThis as any).cancelAnimationFrame = (globalThis as any).window.cancelAnimationFrame;
    (globalThis as any).window.getSelection = () => null;
    (globalThis as any).window.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} };
    (globalThis as any).ResizeObserver = (globalThis as any).window.ResizeObserver;
    (globalThis as any).IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
    (globalThis as any).MutationObserver = class { observe() {} disconnect() {} takeRecords() { return []; } };
    (globalThis as any).window.getComputedStyle = () => ({ overflowY: 'visible', paddingLeft: '0px', paddingRight: '0px', getPropertyValue: () => '' });
    (globalThis as any).getComputedStyle = (globalThis as any).window.getComputedStyle;
    (globalThis as any).Image = class { set src(_v: string) {} onload: any; onerror: any; width = 0; height = 0; };
    // PanelZone пишет CSS-переменные через documentElement.style; в fake-DOM он null — шим
    if (!(globalThis as any).document.documentElement) (globalThis as any).document.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    store.clear(); sstore.clear();
    // кэш списка чатов (sessionsList) — модульный синглтон: без сброса кейс получил бы
    // список предыдущего кейса мгновенно и «доехал» бы ответ не тем числом шагов
    sessionsListTest.reset();
    mockChats.length = 0; mockChats.push(session);
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('ребёнок на 768: список уроков открыт при входе — панель и строки в DOM', async () => {
    const { root, container } = await mountWorkspace(baseProps({ child: childSurface }));
    try {
      expect(findNodeByAttr(container as any, 'data-testid', 'child-lessons-panel'), 'панели уроков на 768 нет').not.toBeNull();
      expect(rowNames(container), 'строк уроков в панели на 768 нет').toEqual(['Дроби']);
    } finally { root.unmount(); }
  });

  // Выбор урока (смена активной сессии) закрывает дровер поверх чата: ребёнок после
  // навигации должен видеть урок, а не список. Дефолт при этом не возвращается —
  // иначе панель воскресала бы поверх каждого открытого урока.
  it('ребёнок на 768: смена активного урока закрывает дровер списка', async () => {
    const { root, container } = await mountWorkspace(baseProps({ child: childSurface }));
    try {
      expect(findNodeByAttr(container as any, 'data-testid', 'child-lessons-panel'), 'панели уроков на 768 нет').not.toBeNull();
      await act(async () => {
        root.render(createElement(DesktopWorkspace, baseProps({ child: childSurface, activeSession: session })));
      });
      await act(async () => { vi.advanceTimersByTime(300); });
      expect(findNodeByAttr(container as any, 'data-testid', 'child-lessons-panel'), 'дровер списка остался поверх открытого урока').toBeNull();
    } finally { root.unmount(); }
  });

  it('взрослый на 768: детской панели нет — раскладка не меняется', async () => {
    const { root, container } = await mountWorkspace(baseProps());
    try {
      expect(findNodeByAttr(container as any, 'data-testid', 'child-lessons-panel'), 'детской панели у взрослого быть не должно').toBeNull();
    } finally { root.unmount(); }
  });
});
