// Г3б-д1 (доработка ревью Р-Г3б, дефект 36e0160d): связка «шапка → computeTabsFit».
// Чистая функция уже покрыта mobileTabsFit.test.ts; не покрыта связка с местом вызова:
// ЖИВАЯ мобильная шапка WorkspacePage обязана передавать функции резерв детских слотов,
// посчитанный из РЕАЛЬНЫХ ширин плашки времени и меню ребёнка. Мутация
// «const childReserve = 0» (ровно прежнее поведение из-за которого был дефект: слоты
// не резервировали место, пилюли выдавливали правые элементы шапки за край) обязана
// краснить этот тест. Проверка — рендер на 360: после пересчёта не влезшая вкладка
// уходит в шторку «Ещё», а не стоит пилюлей рядом.
// Мутации, красящие кейс:
// 1) childReserve = 0 — spy-проверка «резерв = плашка+зазор + меню+зазор» краснеет,
//    а «Файлы» появляется в ряду пилюль (бюджет без резерва впускает вторую пилюлю);
// 2) падение рефов (обёртки детских слотов не в DOM) — offsetWidth ?? 0 даёт тот же
//    нулевой резерв и ту же проверку.
//
// Окружение — общий fake-dom хелпер и набор моков WorkspacePage.child.test.tsx;
// отличия: управляемый ResizeObserver (пересчёт шапки триггерит тест выстрелом
// коллбеков) и спичка computeTabsFit (запись аргументов + пропуск в настоящую функцию).

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, createElement } from 'react';
import type { AuthState, ChildSurface, Project, Session } from '../../types';
import type { ChildStatus } from '../../features/childMode/types';
import { childSubjectCapabilities } from '../../lib/projectCapabilities';
import { clearMe, setMeFromServer } from '../../lib/defaultPersona';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr } from '../../components/chat/__tests__/fakeDom';
import { __test as sessionsListTest } from '../../lib/sessionsList';
import { __resetWatchdogPresence } from '../../lib/watchdogPresence';

// ── localStorage / sessionStorage: их читают модули при импорте (workspaceState,
//     panelStackState, taskFilters), до module-level кода теста ──
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
  return { store, sstore };
});

// ── Спичка computeTabsFit: WorkspacePage зовёт её в layout-эффекте шапки; тест
//     проверяет аргументы места вызова (резерв из реальных ширин) ──
const fitSpy = vi.hoisted(() => ({
  calls: [] as Array<{ avail: number; pillWidths: number[]; moreW: number; childReserve: number }>,
}));
vi.mock('../../pages/workspace/mobileTabsFit', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../pages/workspace/mobileTabsFit')>();
  return {
    ...orig,
    computeTabsFit: (input: { avail: number; pillWidths: number[]; moreW: number; childReserve: number }) => {
      fitSpy.calls.push({ avail: input.avail, pillWidths: [...input.pillWidths], moreW: input.moreW, childReserve: input.childReserve });
      return orig.computeTabsFit(input);
    },
  };
});

// ── Детские ручки: мок с управляемым результатом (семантика — их тесты) ──
const kid = vi.hoisted(() => ({
  status: null as unknown,
  subjects: [] as unknown[],
  rulesStartCalls: 0,
  rulesStartFail: false,
  createLessonCalls: 0,
}));
vi.mock('../../features/childMode/useChildStatus', () => ({
  __esModule: true,
  useChildStatus: () => ({ status: kid.status, offline: false }),
}));
vi.mock('../../features/childMode/api', () => ({
  __esModule: true,
  childApi: {
    getSubjects: () => Promise.resolve(kid.subjects),
    createLesson: () => { kid.createLessonCalls += 1; return Promise.resolve({ sessionId: 'chatNew' }); },
    spaceChats: () => Promise.resolve({ chats: [] }),
    rulesStart: () => {
      kid.rulesStartCalls += 1;
      return kid.rulesStartFail ? Promise.reject(new Error('offline')) : Promise.resolve({ sessionId: 'r-rules' });
    },
    stopSignal: () => Promise.resolve({ accepted: true }),
  },
  tutorAvatarUrl: () => '',
}));

// ── DesktopWorkspace: записывающая заглушка (мобильная ветка страницы её не рендерит,
//     но импортирует — без мока тянет ChatPanel/FileViewer в граф теста) ──
const dwPropsReg = vi.hoisted(() => [] as any[]);
vi.mock('../workspace/DesktopWorkspace', async () => {
  const React = await import('react');
  const { ChildLessonHeader } = await import('../../features/childMode/ChildLessonHeader');
  const { ProjectRail } = await import('../../features/projects/ProjectRail');
  return {
    DesktopWorkspace: (p: any) => {
      dwPropsReg.push(p);
      const keys = (p.allowedKeys ?? []).filter((k: string) => p.panels?.[k] !== undefined);
      return React.createElement('div', { 'data-testid': 'dw' }, [
        ...keys.map((k: string) => React.createElement('span', { 'data-panel-key': k })),
        p.child?.dock ? React.createElement(ProjectRail, { child: p.child.dock, side: 'left' }) : null,
        p.child?.header
          ? React.createElement(ChildLessonHeader, { subject: p.child.header.subject, viewers: p.child.header.viewers, title: 'Дроби' })
          : null,
      ]);
    },
    pageYieldsCenterToFile: () => false,
  };
});

// ── транспорт и тяжёлые сторы: no-op (тот же набор, что WorkspacePage.child) ──
vi.mock('../../lib/signalr', () => {
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
const mockSessions = vi.hoisted(() => [] as unknown[]);
const apiSpy = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/api', () => {
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
      if (prop === 'then' || prop === Symbol.toPrimitive || prop === Symbol.iterator) return undefined;
      const o = overrides[prop as string];
      return o ?? (() => Promise.resolve(null));
    },
  });
  const spy = (name: string, val: any) => () => { apiSpy.calls.push(name); return Promise.resolve(val); };
  const api: any = new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'sessions') return ns({ list: () => { apiSpy.calls.push('sessions.list'); return Promise.resolve(mockSessions); } });
      if (prop === 'projects') return ns({ list: spy('projects.list', []), backgroundTileUrl: () => '', subjects: ns({ list: () => Promise.resolve([]) }) });
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
      if (prop === 'knowledge') return ns({ getStatus: () => Promise.resolve({ datasetId: null, documents: [], total: 0 }) });
      if (prop === 'tasks') return ns({ featuresIndex: () => Promise.resolve({ features: [], chats: {} }), getFeature: () => Promise.resolve(null), listAll: spy('tasks.listAll', []) });
      if (prop === 'skills') return ns({ list: spy('skills.list', null) });
      if (prop === 'watchdogs') return ns({ snapshot: spy('watchdogs.snapshot', { sessions: [], projects: [] }) });
      return deep(null);
    },
  });
  return { api, setGitSessionContext: () => {} };
});
vi.mock('../../lib/git', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/git')>();
  return {
    ...orig,
    useGitState: () => ({ status: null, unpushed: [], changedBy: new Map() }),
    ensureGit: () => Promise.resolve(),
    loadUnpushedLog: () => Promise.resolve([]), setActiveSessionForChangedBy: () => {},
  };
});
vi.mock('../../hooks/useExternalPreviewLinks', () => ({
  __esModule: true,
  useExternalPreviewLinksState: () => ({ enabled: false, links: [] }),
  ensureExternalPreviewLinks: () => {},
}));
const termSpy = vi.hoisted(() => ({ list: 0, sub: 0 }));
vi.mock('../../lib/terminalSignalr', () => {
  const p = () => Promise.resolve(null);
  return {
    resetTerminalConnection: p,
    createTerminal: p,
    connectTerminal: p,
    listTerminals: () => { termSpy.list += 1; return Promise.resolve([]); },
    stopTerminal: p,
    renameTerminal: p,
    sendTerminalInput: p,
    resizeTerminal: p,
    onTerminalMessage: () => { termSpy.sub += 1; return () => {}; },
  };
});
vi.mock('../../hooks/useProjectServices', () => ({
  __esModule: true,
  useProjectServices: () => ({
    services: [], activePreviewId: null, setActivePreviewId: () => {},
    activate: () => {}, refresh: () => Promise.resolve(), start: () => Promise.resolve(null), stop: () => Promise.resolve(),
  }),
}));
vi.mock('../../lib/subsystems', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems')>();
  return {
    ...orig,
    useActiveSubsystemKeys: () => [orig.SUBSYSTEMS.tasks, orig.SUBSYSTEMS.knowledge, orig.SUBSYSTEMS.notes],
    useSubsystem: () => true,
  };
});
vi.mock('../../lib/subsystems/registry', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems/registry')>();
  const React = await import('react');
  return {
    ...orig,
    useSlot: () => [],
    useSlotItem: () => null,
  };
});

import { WorkspacePage } from '../WorkspacePage';

// ── Данные: тот же шим, что WorkspacePage.child.test ──
const subject = {
  id: 's1', label: 'Математика', glyph: '×÷', color: 'info',
  tutor: { personaId: 't1', name: 'Сова Ульяна', hasAvatar: false },
  viewers: ['Папа'],
};
const project: Project = {
  id: 'p-subj', name: 'Математика', rootPath: '/projects/liza/Предметы/s1',
  createdAt: '', updatedAt: '',
  childSubjectId: 's1',
  capabilities: childSubjectCapabilities(),
} as Project;
const session: Session = {
  id: 'chatK', name: 'Дроби', projectId: 'p-subj', mode: 'default', status: 'active',
  messageCount: 0, model: 'sonnet', createdAt: '', updatedAt: '',
} as Session;
const auth: AuthState = { serverUrl: 'http://x', token: 't', username: 'liza', displayName: 'Лиза' };

const CHILD_PANELS = ['files', 'knowledge', 'notes', 'chatContext', 'tasks', 'chats'];
const surface: ChildSurface = {
  panels: CHILD_PANELS,
  composerFeatures: ['text', 'attachFromSubject', 'voiceInput', 'askUserAnswer'],
  feedKinds: ['user_message', 'text', 'result', 'tool_use'],
};

const statusBase = {
  space: 'lessons', minutesLeft: 35, windowEndsAt: null, nextWindowAt: null, warnMinutes: 5,
  maxAttachmentBytes: 10_000_000, attachmentTypes: [], dailyMinutes: 45, windows: [], minutesToday: 12,
};
const statusOk: ChildStatus = { ...statusBase, state: 'ok', rulesAgreedAt: '2026-11-01T10:00:00Z' } as ChildStatus;

// ── Поиск ──
type NodeLike = { nodeType: number; nodeName: string; childNodes: NodeLike[]; getAttribute: (a: string) => string | null; parentNode?: NodeLike | null };
const walk = (n: NodeLike, cb: (n: NodeLike) => void): void => { cb(n); for (const c of n.childNodes ?? []) walk(c, cb); };
const attrValues = (container: NodeLike, attr: string): string[] => {
  const out: string[] = [];
  walk(container, (n) => { const v = n.getAttribute(attr); if (n.nodeType === 1 && v !== null) out.push(v); });
  return out;
};

// ── Управляемый ResizeObserver: пересчёт шапки триггерит тест выстрелом коллбеков ──
const roFake = vi.hoisted(() => ({ instances: [] as Array<{ cb: (entries: unknown[], obs: unknown) => void }> }));
class FakeRO {
  cb: (entries: unknown[], obs: unknown) => void;
  constructor(cb: (entries: unknown[], obs: unknown) => void) { this.cb = cb; roFake.instances.push(this); }
  observe() {} unobserve() {} disconnect() {}
}
const fireResizes = () => { act(() => { for (const ro of roFake.instances) ro.cb([], ro); }); };

async function mountChildTabs(over: { width?: number } = {}) {
  kid.status = statusOk;
  kid.subjects = [subject];
  if (over.width) (globalThis as any).window.innerWidth = over.width;
  store.set('ws:' + project.id, JSON.stringify({ activeSession: session, openFile: null, leftTab: 'sessions' }));
  const { root, container } = mount(createElement(WorkspacePage, {
    project, onGoToProjects: () => {}, onSwitchHub: () => {}, auth, onLogout: () => {},
    onOpenSubjectProject: () => {},
    childSurface: surface,
  } as any));
  await act(async () => { vi.advanceTimersByTime(300); }); // детские загрузки отработали
  return { root, container: container as unknown as NodeLike };
}

describe('мобильная шапка проекта: резерв детских слотов в расчёте пилюль (Г3б-д1)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    const win = globalThis as any;
    win.window.innerWidth = 1280; win.window.innerHeight = 800;
    win.window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
    win.window.history = { state: null, pushState: () => {}, replaceState: () => {}, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
    win.window.setInterval = (cb: any, ms?: number) => setInterval(cb, ms);
    win.window.clearInterval = (id: any) => clearInterval(id);
    win.window.setTimeout = (cb: any, ms?: number) => setTimeout(cb, ms);
    win.window.clearTimeout = (id: any) => clearTimeout(id);
    win.window.requestAnimationFrame = (cb: any) => { cb(0); return 1; };
    win.window.cancelAnimationFrame = () => {};
    win.requestAnimationFrame = win.window.requestAnimationFrame;
    win.cancelAnimationFrame = win.window.cancelAnimationFrame;
    win.window.getSelection = () => null;
    win.window.ResizeObserver = FakeRO;
    win.ResizeObserver = FakeRO;
    win.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} };
    win.MutationObserver = class { observe() {} disconnect() {} takeRecords() { return []; } };
    win.window.getComputedStyle = () => ({ overflowY: 'visible', paddingLeft: '0px', paddingRight: '0px', getPropertyValue: () => '' });
    win.getComputedStyle = win.window.getComputedStyle;
    win.Image = class { set src(_v: string) {} onload: any; onerror: any; width = 0; height = 0; };
    if (!win.document.documentElement) win.document.documentElement = { style: { setProperty: () => {}, removeProperty: () => {} } };
    store.clear(); sstore.clear();
    mockSessions.length = 0; mockSessions.push(session);
    apiSpy.calls.length = 0;
    termSpy.list = 0; termSpy.sub = 0;
    __resetWatchdogPresence();
    sessionsListTest.reset();
    dwPropsReg.length = 0;
    kid.rulesStartCalls = 0;
    kid.rulesStartFail = false;
    roFake.instances.length = 0;
    fitSpy.calls.length = 0;
    clearMe();
  });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  // Геометрия кейса (ширины в px — подбор так, чтобы разница была надёжно за зазором):
  // шапка 360 (padding 10/14 → avail 336), «Ещё» 40, плашка 36, меню 40, пилюли
  // [110, 60, 60, 60] (Чаты/Файлы/Задачи/Знания).
  // Реальный резерв 36+10 + 40+10 = 96 → бюджет 336−64−40−14−96 = 122: влезает одна
  // пилюля («Чаты»), «Файлы» уходит в шторку «Ещё». Мутация childReserve=0 → бюджет
  // 218 → влезают две, «Файлы» встаёт пилюлей — тест краснеет по обеим проверкам.
  it('шапка передаёт computeTabsFit резерв детских слотов из реальных ширин; не влезшая вкладка уходит в «Ещё»', async () => {
    const win = globalThis as any;
    // Геометрия шапки: getComputedStyle шапки возвращает её реальные паддинги
    // (WorkspacePage зовёт глобальный getComputedStyle — перезаписываю оба)
    win.window.getComputedStyle = () => ({ overflowY: 'visible', paddingLeft: '10px', paddingRight: '14px', getPropertyValue: () => '' });
    win.getComputedStyle = win.window.getComputedStyle;
    const { root, container } = await mountChildTabs({ width: 360 });
    try {
      // Детские слоты живы: плашка времени и «Меню» в шапке (иначе проверка — вакуум)
      const badgeNode = findNodeByAttr(container as any, 'aria-label', 'Осталось около 35 минут');
      const menuNode = findNodeByAttr(container as any, 'aria-label', 'Меню');
      expect(badgeNode, 'плашки времени в детской шапке нет').not.toBeNull();
      expect(menuNode, 'кнопки «Меню» в детской шапке нет').not.toBeNull();

      // Узлы шапки: пилюля → трек PillSwitch → шапка; скрытый эталон пилюль — первый
      // ребёнок шапки (WorkspacePage.tsx:2779); детские обёртки — родители слотов
      const pill = findNodeByAttr(container as any, 'aria-label', 'Чаты');
      expect(pill, 'пилюли «Чаты» нет в ряду').not.toBeNull();
      const header = pill!.parentNode!.parentNode! as NodeLike;
      const probe = (header as any).children[0];
      const probePills = (probe as any).children as NodeLike[];
      expect(probePills.length, 'числа вкладок ребёнка в эталоне').toBe(4);
      (header as any).clientWidth = 360;
      [110, 60, 60, 60].forEach((w, i) => { (probePills[i] as any).offsetWidth = w; });
      (badgeNode!.parentNode as any).offsetWidth = 36;
      (menuNode!.parentNode as any).offsetWidth = 40;
      const moreNode = findNodeByAttr(container as any, 'aria-label', 'Ещё');
      expect(moreNode, 'кнопки «Ещё» нет').not.toBeNull();
      (moreNode!.parentNode as any).offsetWidth = 40;

      // Пересчёт, как в живом браузере: ресайз шапки (ResizeObserver) перезапускает compute
      fireResizes();

      // Связка с местом вызова: функция получила РЕАЛЬНЫЕ ширины и ненулевой резерв
      const last = fitSpy.calls[fitSpy.calls.length - 1];
      expect(last, 'computeTabsFit не позван после пересчёта').toBeTruthy();
      expect(last.avail, 'доступная ширина шапки').toBe(336);
      expect(last.pillWidths, 'ширины пилюль из скрытого эталона').toEqual([110, 60, 60, 60]);
      expect(last.moreW, 'ширина «Ещё»').toBe(40);
      // 36+10 + 40+10 — плашка и меню, каждый со своим зазором шапки
      expect(last.childReserve, 'резерв детских слотов в вызове computeTabsFit').toBe(96);

      // Живое следствие: бюджет с резервом впускает одну пилюлю — «Файлы» ушла в «Ещё»
      const labels = attrValues(container, 'aria-label');
      expect(labels, 'пилюли «Чаты» нет в ряду').toContain('Чаты');
      expect(labels, '«Файлы» влезла в ряд вопреки резерву детских слотов').not.toContain('Файлы');
    } finally { root.unmount(); }
  });
});
