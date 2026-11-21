// Ф1j (ADR-041 ред. 8, решения 76–84): сквозная детская проводка десктопного экрана.
// Инвариант И11, часть «проводка»: DesktopWorkspace → ChatPanel → Composer и
// DesktopWorkspace → ProjectRail. Источник child (useChildSurface в WorkspacePage) — Ф1f,
// вне этого шага: здесь тест сам кладёт готовый проп-объект и смотрит, куда он доходит.
//
// Что проверяется живым рендером (не моками пропов):
// 1) child.features=[text, voiceInput] → дошёл до композера: детского набора нет —
//    взрослый композер (кнопка «Модель» на месте); есть — композер default-deny
//    (поля и «Голосовой ввод» есть, «Модель» — нет).
// 2) child.feedKinds+child.header → детская шапка урока со строкой «Уроки видит Папа».
// 3) child.dock → ProjectRail на рельсе — детский ряд предметов (data-child-subject).
// 4) без child — взрослый экран без изменений: модель есть, детской шапки и ряда нет.
// Мутация c2 («ChatPanel не передаёт childFeatures в Composer») краснит кейс 1.
// 5) Х-W4-34 (доработка возврата ревью a915c769): при переданном onOpenWall док стены
//    остаётся только взрослому. Это РАСХОДНАЯ проверка: WorkspacePage.child.test мокнет
//    сам DesktopWorkspace, поэтому потребитель глубже страницы (док → стор сторожей)
//    им не ловился вовсе. Здесь раскладка настоящая: ребёнку ни самого дока, ни снимка
//    сторожей GET /api/watchdogs, которого он не спросит.
//
// Окружение — общий fake-dom хелпер (тот же, что workspaceRevealPanelGate.test.ts):
// тяжёлые соседи (PanelZone, ChatHeaderBar, ChildHome/TutorMark) — заглушки; транспорт,
// api и тяжёлые хуки — no-op. PanelZone заглушена РОВНО так, чтобы её railFooter
// (где стоит ProjectRail) остался настоящим: проводка до дока — предмет теста,
// внутренности зоны — нет. Доки (проектов и «Стены») не заглушены вовсе: их опросы —
// снимок сторожей и состав стены — и есть предмет п.5.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project, Session } from '../../../types';
import type { ChildWorkspaceProps } from '../../../features/childMode/types';
import type { ChildSubjectWithViewers } from '../../../features/childMode/api';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes } from '../../../components/chat/__tests__/fakeDom';
// Стор сторожей НЕ мокнут (п.5): он реальный — именно его первичный снимок и есть проверяемое
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

// ── PanelZone: заглушка, рисующая свой railFooter — док (ProjectRail) остаётся
//     настоящим; внутренности зоны (стек панелей, дроверы) этому тесту не нужны ──
vi.mock('../PanelZone', async () => {
  const { createElement } = await import('react');
  return { PanelZone: (p: { railFooter?: unknown }) => createElement('div', { 'data-testid': 'panelzone-stub' }, p.railFooter as any) };
});

// ── тяжёлые дети: маркеры вместо графики (тот же приём, что ChildLessonHeader.test) ──
vi.mock('../../../components/chat/ChatHeaderBar', async () => {
  const React = await import('react');
  return { ChatHeaderBar: () => React.createElement('div', { 'data-testid': 'chat-header-bar-stub' }) };
});

// ── транспорт и сторы: no-op (тот же набор, что workspaceRevealPanelGate.test.ts) ──
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
// Х-W4-34: спичка снимка сторожей — её поднимает стор presence при первом подписчике
// (док стены, док проектов). По ней и проверяется, что с детского экрана запрос не уходит
const watchdogSpy = vi.hoisted(() => ({ snapshot: 0 }));
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
      // featuresIndex — пустой срез правильной формы: стор применяет ответ как есть
      if (prop === 'tasks') return ns({ featuresIndex: () => Promise.resolve({ features: [], chats: {} }), getFeature: () => Promise.resolve(null), listAll: () => Promise.resolve([]) });
      if (prop === 'chats') return ns({ get: () => Promise.resolve(null) });
      if (prop === 'sessions') return ns({ list: () => Promise.resolve(mockChats) });
      if (prop === 'projects') return ns({ list: () => Promise.resolve([]) });
      // personas.list → []: иначе проп personas Composer'а null → «not iterable» при полном рендере
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
      // Х-W4-34: снимок сторожей — ответ правильной формы (стор применяет его как есть);
      // без этой ветки был бы не «нет запроса», а падение fetchSnapshot в его catch
      if (prop === 'watchdogs') return ns({ snapshot: () => { watchdogSpy.snapshot += 1; return Promise.resolve({ sessions: [], projects: [] }); } });
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

import { DesktopWorkspace } from '../DesktopWorkspace';

// ── Данные ──
const project: Project = {
  id: 'p1', name: 'Математика', rootPath: '/p', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as Project;
// mode обязателен: ChatPanel инициализирует им состояние режима (тот же шим, что
// ChildLessonHeader.test.tsx:283); model — чтобы взроская проверка «выбор модели есть»
// была невакуумной: при model=undefined пикер не обязан рисоваться вовсе
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

// Полный набор обязательных пропов DesktopWorkspace: центр пустой → в нём стоит
// одиночный чат; все остальные режимы закрыты. onOpenWall по умолчанию не передан →
// док стены не монтируется; кейс п.5 передаёт его намеренно — он и проверяет гейт дока.
// Тип — проп самого компонента: tsc держит полноту обязательных, а тест — что доходит до DOM
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

// ── Поиск по фейк-DOM (обход от корня рендера) ──
type NodeLike = { nodeType: number; nodeName: string; childNodes: NodeLike[]; getAttribute: (a: string) => string | null; textContent?: string };
const walk = (n: NodeLike, cb: (n: NodeLike) => void): void => { cb(n); for (const c of n.childNodes ?? []) walk(c, cb); };
// Кнопки по началу title — те же имена контролов, что читает человек и диктор
const btnByTitle = (container: NodeLike, prefix: string): number => {
  let hits = 0;
  walk(container, (n) => { if (n.nodeType === 1 && n.nodeName === 'BUTTON' && (n.getAttribute('title') ?? '').startsWith(prefix)) hits += 1; });
  return hits;
};

async function mountWorkspace(props: DWProps) {
  const { root, container } = mount(createElement(DesktopWorkspace, props));
  await act(async () => { vi.advanceTimersByTime(300); }); // спиннер истории (200ms) отработал, DOM стабилен
  return { root, container: container as unknown as NodeLike };
}

describe('DesktopWorkspace: детская проводка до композера, шапки и дока (Ф1j)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    (globalThis as any).window.innerWidth = 1600;
    (globalThis as any).window.innerHeight = 800;
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
    // PanelZone в оригинале пишет CSS-переменные через documentElement.style; здесь он
    // заглушен, но шим дешёв и страхует чужие прямые записи в него
    if (!(globalThis as any).document.documentElement) (globalThis as any).document.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    store.clear(); sstore.clear();
    mockChats.length = 0; mockChats.push(session);
    // стор сторожей — модульный синглтон: без сброса подписка предыдущего кейса осталась бы
    // живой, и следующий mounts считался бы стартовавшим без нового снимка (п.5)
    __resetWatchdogPresence();
    watchdogSpy.snapshot = 0;
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  it('child.features: композер детский — поля и «Голосовой ввод» есть, «Модель» нет', async () => {
    const { root, container } = await mountWorkspace(baseProps({ child: childSurface }));
    try {
      // поле ввода — textarea (тот же признак, что Composer.childFeatures.test)
      let hasTextarea = false;
      walk(container, (n) => { if (n.nodeName === 'TEXTAREA') hasTextarea = true; });
      expect(hasTextarea, 'поля ввода детского композера нет').toBe(true);
      expect(btnByTitle(container, 'Голосовой ввод'), 'кнопки голосового ввода (ключ voiceInput в наборе) нет').toBe(1);
      // default-deny: взрослый контрол не должен дожить сквозь детский набор —
      // именно на этом краснеет мутация c2 (ChatPanel не передаёт childFeatures)
      expect(btnByTitle(container, 'Модель'), 'выбора модели в детском композере быть не должно').toBe(0);
    } finally { root.unmount(); }
  });

  it('child.feedKinds + child.header: шапка урока со строкой «Уроки видит Папа»', async () => {
    const { root, container } = await mountWorkspace(baseProps({ child: childSurface }));
    try {
      const line = findNodeByAttr(container as any, 'data-testid', 'child-curators-line');
      expect(line, 'строки кураторов детской шапки нет').not.toBeNull();
      // textContent у фейк-DOM-элемента не агрегирует детей (у linkedom агрегирует) —
      // текст собираем обходом текстовых нод
      expect(textNodes(line!).join('').trim(), 'текст строки кураторов').toBe('Уроки видит Папа');
    } finally { root.unmount(); }
  });

  it('child.dock: на рельсе детский ряд предметов вместо дока проектов', async () => {
    const { root, container } = await mountWorkspace(baseProps({ child: childSurface }));
    try {
      const subjectNode = findNodeByAttr(container as any, 'data-child-subject', 'math');
      expect(subjectNode, 'значка предмета в доке нет').not.toBeNull();
    } finally { root.unmount(); }
  });

  it('без child: взрослый экран без изменений — выбор модели есть, детской шапки и ряда нет', async () => {
    const { root, container } = await mountWorkspace(baseProps());
    try {
      expect(btnByTitle(container, 'Модель'), 'выбора модели у взрослого нет').toBe(1);
      expect(findNodeByAttr(container as any, 'data-testid', 'child-curators-line'), 'детской строки кураторов быть не должно').toBeNull();
      expect(findNodeByAttr(container as any, 'data-child-subject', 'math'), 'детского ряда быть не должно').toBeNull();
    } finally { root.unmount(); }
  });

  // Х-W4-34 (доработка возврата ревью a915c769): док стены — потребитель глубже страницы:
  // при смонтированном доке его стор поднимает GET /api/watchdogs и состав стены
  // GET /api/me/wall (обе без [ChildAllowed] → ребёнку 403), и page-level опросы, снятые
  // в X-W4-30/X-W4-34, этого не ловили: там DesktopWorkspace — заглушка. Здесь раскладка
  // настоящая, поэтому и гейт, и доказательство — на ней. Мутации, красящие кейс:
  // wallOn = !!p.onOpenWall без !p.child (у ребёнка встаёт док и снимок сторожей);
  // док, выключенный для всех (пустая взрослая ветка).
  it('ребёнку дока стены нет: ни самой панели, ни снимка сторожей; взрослому — на месте', async () => {
    const child = await mountWorkspace(baseProps({ child: childSurface, onOpenWall: () => {} }));
    try {
      // Сам док: его лупа пикера — единственная такая кнопка на экране
      expect(findNodeByAttr(child.container as any, 'aria-label', 'Найти чат для стены'), 'дока стены на детском экране').toBeNull();
      // И его опрос: на детском экране больше никого, кто подписан на стор сторожей
      // (ряд предметов — ChildSubjectsRail, панелей «Чаты» ребёнку нет)
      expect(watchdogSpy.snapshot, 'детский экран снял снимок сторожей (/api/watchdogs)').toBe(0);
    } finally { child.root.unmount(); }
    // Та же раскладка без child: док и его опрос на месте (иначе правка — просто выключенный док для всех)
    const adult = await mountWorkspace(baseProps({ onOpenWall: () => {} }));
    try {
      expect(findNodeByAttr(adult.container as any, 'aria-label', 'Найти чат для стены'), 'дока стены взрослому').not.toBeNull();
      expect(watchdogSpy.snapshot, 'взрослому не снялся снимок сторожей').toBeGreaterThan(0);
    } finally { adult.root.unmount(); }
  });
});
