// Ф1f (ADR-041 ред. 8, решения 76–84): сторож детской поверхности на фронте.
// Живой рендер WorkspacePage с детскими props проверяет:
// 1) ребёнок со снимком → разрешённый набор ровно шесть ключей снимка (∩ набор экрана ∩
//    матрица проекта-предмета; 'chats' в наборе есть, но карточкой не становится — список
//    уроков постоянная колонка DesktopWorkspace); собранный объект child доходит до владельца раскладок: features/
//    feedKinds — из снимка, header — предмет открытого проекта с именами кураторов из
//    subject.viewers (подзадача ревью Ф1h), dock — предметы с ключом активного предмета;
//    плашка времени на месте;
// 2) ребёнок без снимка → «Нет связи» вместо взрослого набора: ноль панелей;
// 3) правила не приняты → ChildRulesOnboarding вместо работы с уроком;
// 4) время вышло → ChildTimeUpScreen вместо центра;
// 5) доработка ревью 1f80483c: отказ rules/start — ровно одна попытка и спокойный текст
//    с кнопкой (не кольцо запросов); взрослой обвязки (ProjectIntroCard, OwnerSettingsNotice)
//    на экране ребёнка нет, на взрослом — на месте;
// 6) Х-W4-30: ребёнок не зовёт лишние page-level опросы страницы (tasks-listAll/projects-list/
//    skills). tasks-listAll и skills — без [ChildAllowed] (403); projects-list — метка есть
//    (ProjectsController.cs:166-172), но эффект экрана ребёнку закрыт: опрос существует ради
//    взрослого онбординга (default-persona, «доступ отозван»), которого ребёнку не показывают.
//    Взрослый зовёт всё это как раньше.
//    Список сессий своего предмета — исключение: он [ChildAllowed] (e7021183e) и разрешён
//    ребёнку: и для открытия созданного урока (п.8), и для валидации восстановленного чата
//    при входе (доработка Х-W4-30, п.3 вердикта ревью).
// 7) Х-W4-34: то же для опросов внутри хуков и сторов: терминал-хаб /hubs/terminal (реальный
//    useProjectTerminals, мокнутый транспорт termSpy) и снимок сторожей GET /api/watchdogs
//    (реальный стор, спичка apiSpy) — ребёнку ни того, ни другого; взрослому — как раньше.
// 8) доработка Х-W4-30 (п.1-2 вердикта ревью): «Новый урок» ребёнка открывает созданный чат.
//    Цепочка реальна до конца: плашка активного предмета (настоящий ProjectRail в заглушке)
//    → кнопка → childApi.createLesson (мок) → pending-канал страницы → эффект-потребитель
//    (ребёнку не закрыт) → список чатов → новый чат активен.
// 9) Х-W4-75 (дефект a2aae946): на 360 ребёнку — только его поверхность и в рельсе
//    вкладок, и в шторке «Ещё» (тот же childWorkspaceKeys, что десктопной); взрослому
//    на 360 — та же рельса и шторка, что до правки.
// 10) Х-W4-80 (дефект fab7e418, факт 3(б)): разбор чата из адреса у ребёнка — тот же
//    pending-канал, что у взрослого: названный урок из списка открывается и виден в
//    списке открытым, сохранённый не показан никогда; чужой/несуществующий чат —
//    детское пустое состояние центра и детский текст отказа, не взрослый.
// Открытый урок при входе — сохранённое состояние проекта (ws:<id>), когда адрес его не
// называет: начальный читатель канала у ребёнка общий с взрослым (Х-W4-80) — адрес с
// другим чатом гасит сохранённый до разрешения.
// Детские ручки (status/subjects/rulesStart) здесь — моки со своим результатом: их
// семантика проверяется их тестами (useChildStatus, api); важен разбор экрана по ним.
// DesktopWorkspace — записывающая заглушка: она рисует разрешённый набор (data-panel-key
// — только там, где у панели есть контент, как настоящий keyAvailable) и настоящую
// ChildLessonHeader из переданного child.header. Проводка внутри DesktopWorkspace →
// ChatPanel → Composer — предмет теста Ф1j (DesktopWorkspace.child.test.tsx); здесь —
// что владелец экрана собрал и передал правильно.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, createElement } from 'react';
import type { AuthState, ChildSurface, Project, Session } from '../../types';
import type { ChildStatus } from '../../features/childMode/types';
import type { Me } from '../../features/account/types';
import { childSubjectCapabilities } from '../../lib/projectCapabilities';
import { clearMe, setMeFromServer } from '../../lib/defaultPersona';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../../components/chat/__tests__/fakeDom';
import { __test as sessionsListTest } from '../../lib/sessionsList';
// Стор сторожей НЕ мокнут (Х-W4-34): он реальный — именно его первичный снимок проверяется
// на детском экране; импорт нужен для сброса модульного состояния между кейсами
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

// ── Детские ручки: мок с управляемым результатом (их семантика — их тесты) ──
// rulesStartFail — отказ rules/start (доработка ревью 1f80483c): экран обязан
// сделать ровно одну попытку и дать спокойный текст, а не молотить сервер
const kid = vi.hoisted(() => ({
  status: null as unknown,
  subjects: [] as unknown[],
  rulesStartCalls: 0,
  rulesStartFail: false,
  createLessonCalls: 0,
}));
// Модуль «Архитектура» подключён — управляемый мок слотов (Х-W4-75): без него раздела
// «Архитектура» нет ни у кого и гейт шторки нечего проверять
const archSlots = vi.hoisted(() => ({ enabled: false }));
vi.mock('../../features/childMode/useChildStatus', () => ({
  __esModule: true,
  useChildStatus: () => ({ status: kid.status, offline: false }),
}));
vi.mock('../../features/childMode/api', () => ({
  __esModule: true,
  childApi: {
    getSubjects: () => Promise.resolve(kid.subjects),
    // «Новый урок» (п.8): мок отдаёт новый id чата — его дальше ведёт страница (pending-канал)
    createLesson: () => { kid.createLessonCalls += 1; return Promise.resolve({ sessionId: 'chatNew' }); },
    // «Старые уроки» — ручка детской панели уроков: она нужна, когда она смонтирована
    // (мобильная ветка), в десктопной панель рисует заглушка DesktopWorkspace
    spaceChats: () => Promise.resolve({ chats: [] }),
    rulesStart: () => {
      kid.rulesStartCalls += 1;
      return kid.rulesStartFail
        ? Promise.reject(new Error('offline'))
        : Promise.resolve({ sessionId: 'r-rules' });
    },
    stopSignal: () => Promise.resolve({ accepted: true }),
  },
  // Живой ChildLessonHeader (через заглушку DesktopWorkspace) импортирует ручку аватара;
  // в графе она не зовётся (фикстура hasAvatar=false) — здесь только экспорт для связи модуля
  tutorAvatarUrl: () => '',
}));

// ── DesktopWorkspace: записывающая заглушка. Рисует разрешённый набор (только там,
//     где у панели есть контент — как настоящий keyAvailable), настоящий детский ряд
//     ProjectRail (его плашка — единственная точка создания урока, цепочка п.8) и настоящую
//     ChildLessonHeader из child.header ──
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
        // Ряд предмета — реальный ProjectRail: значки те же (data-child-subject), плюс
        // плашка активного предмета с кнопкой «Новый урок» (портал в body) — без неё
        // потребительская цепочка «Нового урока» (п.8) не проверялась бы вовсе
        p.child?.dock ? React.createElement(ProjectRail, { child: p.child.dock, side: 'left' }) : null,
        p.child?.header
          ? React.createElement(ChildLessonHeader, { subject: p.child.header.subject, viewers: p.child.header.viewers, title: 'Дроби' })
          : null,
      ]);
    },
    pageYieldsCenterToFile: () => false,
  };
});
// ── транспорт и тяжёлые сторы: no-op (тот же набор, что Ф1j и App.childGate) ──
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
// Спичка взрослых page-level опросов (Х-W4-30): какие ручки реально позваны с экрана
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
      // backgroundTileUrl — синхронный генератор URL (CanvasBackdrop): без темы пустая строка
      // subjects.list — реестр тем проекта: его зовёт реальный SessionList, который
      // монтируется только в мобильной ветке (Х-W4-75 mounts на 360); в десктопной
      // его рисует заглушка DesktopWorkspace, поэтому мока раньше не требовалось
      if (prop === 'projects') return ns({ list: spy('projects.list', []), backgroundTileUrl: () => '', subjects: ns({ list: () => Promise.resolve([]) }) });
      if (prop === 'personas') return ns({ list: () => Promise.resolve([]) });
      if (prop === 'knowledge') return ns({ getStatus: () => Promise.resolve({ datasetId: null, documents: [], total: 0 }) });
      if (prop === 'tasks') return ns({ featuresIndex: () => Promise.resolve({ features: [], chats: {} }), getFeature: () => Promise.resolve(null), listAll: spy('tasks.listAll', []) });
      if (prop === 'skills') return ns({ list: spy('skills.list', null) });
      // Х-W4-34: снимок сторожей — реальный стор зовёт именно эту ручку (её отсутствие
      // в моке давало бы не «нет запроса», а падение fetchSnapshot в его catch)
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
// Х-W4-34: useProjectTerminals больше НЕ мокнут — хук реальный, мокнут только его транспорт
// (тот же образец, что WorkspacePage.draftReply.test.ts:82: в node относительный URL
// /hubs/terminal не резолвится). Спичка фиксирует реальные опросы хука: ребёнку — ни
// listTerminals (поднимает связь с хабом), ни подписки onTerminalMessage.
const termSpy = vi.hoisted(() => ({ list: 0, sub: 0 }));
vi.mock('../../lib/terminalSignalr', () => {
  const p = () => Promise.resolve(null);
  return {
    resetTerminalConnection: p,
    createTerminal: p,
    connectTerminal: p,
    // listTerminals → [] (реальный хук делает setTerminals(await listTerminals) — null роняет .find)
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
// Подсистемы: активный состав — как живой инстанс (tasks/knowledge/notes включены);
// без этого wsAllowedKeys потерял бы их ещё до пересечения со снимком (S2b-2c)
vi.mock('../../lib/subsystems', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems')>();
  return {
    ...orig,
    useActiveSubsystemKeys: () => [orig.SUBSYSTEMS.tasks, orig.SUBSYSTEMS.knowledge, orig.SUBSYSTEMS.notes],
    useSubsystem: () => true,
  };
});
// Реестр слотов: панель «Заметки проекта» — заглушка с контентом (её тело — не предмет
// этого теста; её наличие — условие того, что кнопка notes в рельсе реальна)
vi.mock('../../lib/subsystems/registry', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems/registry')>();
  const React = await import('react');
  return {
    ...orig,
    useSlot: () => [],
    useSlotItem: (_slot: string, key: string) => {
      if (key === 'project-notes') return { render: () => React.createElement('span', { 'data-slot-notes': '1' }) };
      // Модуль «Архитектура» подключён (слоты панели рельсы и документа центра) —
      // иначе раздела нет и гейт шторки нечего проверять (Х-W4-75)
      if (archSlots.enabled && (key === 'architecture' || key === 'arch')) {
        return { render: () => React.createElement('span', { 'data-slot-arch': '1' }) };
      }
      return null;
    },
  };
});
// Взрослые приглашения онбординга (ProjectIntroCard, OwnerSettingsNotice) БОЛЬШЕ не мокнут:
// доработка ревью 1f80483c проверяет их отсутствие на экране ребёнка настоящими
// компонентами — мок-`null` делал бы проверку вакуумом (отсутствовало бы всё, и без гейта).

import { WorkspacePage } from '../WorkspacePage';

// ── Данные: живой проект-предмет глазами бэка (тот же шим, что Ф1b) ──
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
// Новый урок мока createLesson ('chatNew'): он и в списке чатов — страница разрешает
// pending-канал по ответу списка (цепочка п.8)
const newChat: Session = {
  id: 'chatNew', name: 'Новый урок', projectId: 'p-subj', mode: 'default', status: 'active',
  messageCount: 0, model: 'sonnet', createdAt: '', updatedAt: '',
} as Session;
const auth: AuthState = { serverUrl: 'http://x', token: 't', username: 'liza', displayName: 'Лиза' };

// Копия замороженного снимка бэка (ChildModeAllowList.Surface.Panels,
// ChildModeAllowList.cs:23). Копия ничего не строит и раньше разъезжалась с бэком молча
// (без 'chats' — вердикт R-Х-W4-75): теперь её сверяет сторож в конце файла — изменение
// бэка без ответа фронта красит его (тот же приём, что сверка типов страниц с
// WikiFrontmatter в wikiLabels.test.ts).
const CHILD_PANELS = ['files', 'knowledge', 'notes', 'chatContext', 'tasks', 'chats'];
const surface: ChildSurface = {
  panels: CHILD_PANELS,
  composerFeatures: ['text', 'attachFromSubject', 'voiceInput', 'askUserAnswer'],
  feedKinds: ['user_message', 'text', 'result', 'tool_use'],
};

// Я — владелец своих проектов (стор defaultPersona): без него ProjectIntroCard
// не видит владельца и молчит сама, проверка её отсутствия была бы вакуумом
const meOwner = (): Me => ({ userId: 'u1', username: 'liza', displayName: 'Лиза', role: 'user' });

const statusBase = {
  space: 'lessons', minutesLeft: 35, windowEndsAt: null, nextWindowAt: null, warnMinutes: 5,
  maxAttachmentBytes: 10_000_000, attachmentTypes: [], dailyMinutes: 45, windows: [], minutesToday: 12,
};
const statusOk: ChildStatus = { ...statusBase, state: 'ok', rulesAgreedAt: '2026-11-01T10:00:00Z' } as ChildStatus;
const statusRules: ChildStatus = { ...statusBase, state: 'rules_pending', rulesAgreedAt: null } as ChildStatus;
const statusTimeUp: ChildStatus = { ...statusBase, state: 'time_up', minutesLeft: 0 } as ChildStatus;

// ── Поиск ──
type NodeLike = { nodeType: number; nodeName: string; childNodes: NodeLike[]; getAttribute: (a: string) => string | null };
const walk = (n: NodeLike, cb: (n: NodeLike) => void): void => { cb(n); for (const c of n.childNodes ?? []) walk(c, cb); };
const countAttr = (container: NodeLike, attr: string): number => {
  let hits = 0;
  walk(container, (n) => { if (n.nodeType === 1 && n.getAttribute(attr) !== null) hits += 1; });
  return hits;
};
const attrValues = (container: NodeLike, attr: string): string[] => {
  const out: string[] = [];
  walk(container, (n) => { const v = n.getAttribute(attr); if (n.nodeType === 1 && v !== null) out.push(v); });
  return out;
};
// Модалки (Modal, ChildRulesOnboarding) — портал в document.body: их ищут по документу
const docText = (): string[] => textNodes((globalThis as any).document.body as never);

// Кнопка по подписи: текстовый узел → ближайший предок с onClick в reactProps (ui/Button)
function clickByLabel(container: any, label: string): (() => void) | null {
  let hit: (() => void) | null = null;
  const walkNode = (n: any): void => {
    if (hit) return;
    if (n.nodeType === 3 && n.nodeValue === label) {
      let p = n.parentNode;
      while (p) {
        const props = reactProps(p) as { onClick?: unknown } | null;
        if (props && typeof props.onClick === 'function') { hit = props.onClick as () => void; return; }
        p = p.parentNode;
      }
      return;
    }
    for (const c of n.childNodes ?? []) walkNode(c);
  };
  walkNode(container);
  return hit;
}

async function mountChild(over: { childSurface?: ChildSurface | null; status?: ChildStatus | null; project?: Project; width?: number } = {}) {
  kid.status = 'status' in over ? over.status ?? null : statusOk;
  kid.subjects = [subject];
  const p = over.project ?? project;
  // Х-W4-75: ширина mounts — параметр: мобильная рельса и шторка живут при
  // windowWidth ≤ MOBILE_MAX (WorkspacePage.tsx:999), до этого — десктопная ветка
  if (over.width) (globalThis as any).window.innerWidth = over.width;
  // Открытый урок при входе — сохранённое состояние проекта (ws:<id>), детский реальный путь.
  // Созданный «Новым уроком» идёт через pending-канал страницы (п.8): его разрешение —
  // список чатов своего предмета, [ChildAllowed] (e7021183e)
  store.set('ws:' + p.id, JSON.stringify({ activeSession: session, openFile: null, leftTab: 'sessions' }));
  const { root, container } = mount(createElement(WorkspacePage, {
    project: p, onGoToProjects: () => {}, onSwitchHub: () => {}, auth, onLogout: () => {},
    onOpenSubjectProject: () => {},
    childSurface: 'childSurface' in over ? over.childSurface : surface,
  } as any));
  await act(async () => { vi.advanceTimersByTime(300); }); // детские загрузки отработали
  return { root, container: container as unknown as NodeLike };
}

describe('WorkspacePage: детская поверхность (Ф1f)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    installFakeDom();
    const win = globalThis as any;
    win.window.innerWidth = 1280; win.window.innerHeight = 800;
    win.window.location = { hash: '', href: 'http://x/', origin: 'http://x', protocol: 'http:', host: 'x', pathname: '/', search: '' };
    win.window.history = { state: null, pushState: () => {}, replaceState: () => {}, back: () => {}, go: () => {}, forward: () => {}, length: 1 };
    // Таймеры окна: их нет в fake-dom, а page-level опросы экрана (useLocalModelWatcher,
    // WorkspacePage.tsx:486) живут на window.setInterval. Под vi.useFakeTimers()
    // глобальные таймеры уже фейковые — прокидываем их в window, как в браузере
    win.window.setInterval = (cb: any, ms?: number) => setInterval(cb, ms);
    win.window.clearInterval = (id: any) => clearInterval(id);
    win.window.setTimeout = (cb: any, ms?: number) => setTimeout(cb, ms);
    win.window.clearTimeout = (id: any) => clearTimeout(id);
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
    // Открытый урок — через сохранённое состояние проекта (пишет его mountChild);
    // новый чат — в списке: им страница разрешает pending-канал «Нового урока» (п.8)
    mockSessions.length = 0; mockSessions.push(session, newChat);
    apiSpy.calls.length = 0;
    termSpy.list = 0; termSpy.sub = 0;
    // стор сторожей — модульный синглтон: без сброса подписка предыдущего кейса
    // осталась бы живой и следующий mounts считался бы уже стартовавшим
    __resetWatchdogPresence();
    // кэш/дедуп списка чатов — модульные: без сброса валидация восстановленного чата
    // могла бы молчать, опираясь на ответ предыдущего теста
    sessionsListTest.reset();
    dwPropsReg.length = 0;
    kid.rulesStartCalls = 0;
    kid.rulesStartFail = false;
    // модуль «Архитектура» по умолчанию выключен — включают его кейсы Х-W4-75
    archSlots.enabled = false;
    // стор defaultPersona — модульный: без сброса me «владелец» протёк бы в следующий кейс
    clearMe();
  });
  afterEach(() => { vi.useRealTimers(); uninstallFakeDom(); vi.restoreAllMocks(); });

  it('ребёнок со снимком: разрешённый набор ровно снимок, карточки с контентом на месте, child доходит до раскладок', async () => {
    const { root, container } = await mountChild();
    try {
      // Набор ровно снимок, ничего лишнего (default-deny, решение 76 + И11) — прямой
      // проверкой того, что страница передала владельцу раскладок: все шесть ключей бэка
      // проходят пересечение (К9к). 'chats' в наборе есть, но карточкой не становится:
      // список уроков — постоянная колонка DesktopWorkspace, не панель из panels
      // (снимок открывает его мобильной вкладкой, Х-W4-75); карточки с контентом —
      // остальной снимок
      const dw = dwPropsReg[dwPropsReg.length - 1];
      expect(dw, 'DesktopWorkspace не смонтирован').toBeTruthy();
      expect((dw.allowedKeys as string[]).sort(), 'разрешённый набор ребёнка не равен снимку').toEqual([...CHILD_PANELS].sort());
      const cards = CHILD_PANELS.filter(k => k !== 'chats');
      expect([...attrValues(container, 'data-panel-key')].sort(), 'карточки панелей ребёнка').toEqual([...cards].sort());
      expect(countAttr(container, 'data-panel-key'), 'карточек с контентом не ровно пять').toBe(cards.length);
      // Плашка времени: ok + 35 минут → «Осталось около 35 минут» (aria — полный смысл)
      expect(findNodeByAttr(container as any, 'aria-label', 'Осталось около 35 минут'), 'плашки времени нет').not.toBeNull();

      // Собранный объект дошёл до владельца раскладок (Ф1j проп)
      expect(dw.child.features, 'features не из снимка').toEqual(surface.composerFeatures);
      expect(dw.child.feedKinds, 'feedKinds не из снимка').toEqual(surface.feedKinds);
      // Шапка урока: предмет открытого проекта-предмета + имена кураторов из subject.viewers
      // (подзадача ревью Ф1h: отдельного поля viewers нет — только запись предмета)
      expect(dw.child.header.subject?.id, 'шапка не знает предмет проекта').toBe('s1');
      expect(dw.child.header.viewers, 'имена кураторов не донесены из subject.viewers').toEqual(['Папа']);
      // ДоК: предметы ребёнка и ключ активного предмета — маркер проекта
      expect((dw.child.dock.subjects ?? []).map((s: any) => s.id), 'дока с предметами').toEqual(['s1']);
      expect(dw.child.dock.activeSubjectId, 'доок не знает активного предмета').toBe('s1');

      // Живая шапка урока: строка «Уроки видит Папа» собрана из донесённых имён
      const line = findNodeByAttr(container as any, 'data-testid', 'child-curators-line');
      expect(line, 'строки кураторов в шапке урока нет').not.toBeNull();
      expect(textNodes(line!).join('').trim(), 'текст строки кураторов').toBe('Уроки видит Папа');
    } finally { root.unmount(); }
  });

  it('ребёнок без снимка: «Нет связи» вместо взрослого набора, ноль панелей', async () => {
    const { root, container } = await mountChild({ childSurface: null });
    try {
      const all = textNodes(container as any).join(' ');
      expect(all.includes('Нет связи'), 'экрана «нет связи» нет').toBe(true);
      expect(findNodeByAttr(container as any, 'data-testid', 'dw'), 'взрослый набор смонтирован без снимка').toBeNull();
      expect(countAttr(container, 'data-panel-key'), 'панелей без снимка быть не должно').toBe(0);
    } finally { root.unmount(); }
  });

  it('правила не приняты: ChildRulesOnboarding вместо работы с уроком', async () => {
    const { root, container } = await mountChild({ status: statusRules });
    try {
      // Ворота открыты сами (решение 84): чат правил запрошён, окно договорённости показано
      expect(kid.rulesStartCalls, 'чат правил не запрошен (rules/start)').toBeGreaterThan(0);
      const body = docText().join(' ');
      expect(body.includes('Кто такой помощник'), 'окна правил нет').toBe(true);
      // Имена кураторов при этом не потеряны: шаг Ф1h живёт и за воротами
      expect(findNodeByAttr(container as any, 'data-testid', 'child-curators-line'), 'шапки урока под воротами').not.toBeNull();
    } finally { root.unmount(); }
  });

  it('время вышло: ChildTimeUpScreen вместо центра', async () => {
    const { root, container } = await mountChild({ status: statusTimeUp });
    try {
      const all = textNodes(container as any).join(' ');
      expect(all.includes('На сегодня с уроками всё'), 'заставки «время вышло» нет').toBe(true);
      // Плашки времени при этом нет: состояние говорит заставка (ChildTimeBadge молчит вне ok/warn)
      expect(findNodeByAttr(container as any, 'aria-label', 'Осталось около 35 минут'), 'плашки времени при «время вышло» быть не должно').toBeNull();
    } finally { root.unmount(); }
  });

  // Доработка ревью 1f80483c, п.1 (major): прежде флаг попытки стоял в зависимостях
  // эффекта и снимался в .finally(): отказ → флаг снят → эффект прогнан заново → новый
  // POST, по кругу (офлайн — плотный цикл). Сторож: ровно одна попытка на молчащий
  // экран; повтор — только по кнопке. Мутация «вернуть rulesStarting в deps» красит
  // этот кейс: .finally() возвращает флаг в deps → вторая попытка без кнопки.
  it('отказ правил: одна попытка, спокойный текст, повтор только по кнопке', async () => {
    kid.rulesStartFail = true;
    const { root, container } = await mountChild({ status: statusRules });
    try {
      expect(kid.rulesStartCalls, 'rules/start не запрошен').toBe(1);
      const all = textNodes(container as any).join(' ');
      expect(all.includes('Правила пока не открываются'), 'спокойного текста отказа нет').toBe(true);
      expect(all.includes('Попроси взрослого помочь'), 'строки с просьбой позвать взрослого нет').toBe(true);
      // Экран не молотит сервер: без тиков статуса (мок статичен) новых попыток нет
      await act(async () => { vi.advanceTimersByTime(5000); });
      expect(kid.rulesStartCalls, 'повтор без кнопки — экран молотит сервер').toBe(1);
      // Единственный выход — кнопка (образец прежней оболочки: повтор явный)
      const click = clickByLabel(container, 'Попробовать ещё раз');
      expect(click, 'кнопки «Попробовать ещё раз» нет').not.toBeNull();
      act(() => { (click as () => void)(); });
      await act(async () => { vi.advanceTimersByTime(100); });
      expect(kid.rulesStartCalls, 'повтор по кнопке не отправил запрос').toBe(2);
    } finally { root.unmount(); }
  });

  // Доработка ревью 1f80483c, п.2 (major): ProjectIntroCard и OwnerSettingsNotice
  // рисовались вне isChildScreen — у ребёнка проекта-предмета всплывало «Разложить
  // проект по полочкам?» со взрослым флоу. Данные подобраны так, чтобы обе реально
  // просились на экран (владелец + участник): без гейта кейс краснеет сам, без моков
  // компонентов. Мутация «убрать условие isChildScreen» красит его.
  it('ребёнку взрослых приглашений нет: ни карточки знакомства, ни плашки участника', async () => {
    setMeFromServer(meOwner());
    const { root, container } = await mountChild({
      project: { ...project, ownerId: 'u1', myRole: 'editor' } as Project,
    });
    try {
      const all = textNodes(container as any).join(' ');
      expect(all.includes('У проекта пока нет руководителя'), 'карточка знакомства на экране ребёнка').toBe(false);
      expect(all.includes('Разложить проект по полочкам'), 'карточка каркаса на экране ребёнка').toBe(false);
      expect(findNodeByAttr(container as any, 'data-testid', 'owner-settings-notice'), 'плашка участника на экране ребёнка').toBeNull();
      expect(findNodeByAttr(container as any, 'data-testid', 'access-revoked-notice'), 'плашка отозванного доступа на экране ребёнка').toBeNull();
    } finally { root.unmount(); }
  });

  // Тот же гейт с другой стороны: взрослому карточка на месте (иначе правка — просто
  // выключенный онбординг для всех). projects.list в моке пуст → участник видит
  // вариант «доступ отозван» — это тоже взрослая плашка, её и ждём на взрослом экране.
  it('взрослому карточка онбординга на месте', async () => {
    setMeFromServer(meOwner());
    const { root, container } = await mountChild({
      childSurface: undefined,
      project: { ...project, ownerId: 'u1', myRole: 'editor' } as Project,
    });
    try {
      const all = textNodes(container as any).join(' ');
      expect(all.includes('У проекта пока нет руководителя'), 'карточки знакомства на взрослом экране нет').toBe(true);
      expect(findNodeByAttr(container as any, 'data-testid', 'access-revoked-notice'), 'плашки участника на взрослом экране нет').not.toBeNull();
    } finally { root.unmount(); }
  });

  // Х-W4-30 (дефект Глеба X-W4-29) + Х-W4-34: детский экран звал лишние для себя опросы.
  // Без [ChildAllowed] на бэке — tasks-listAll (TasksController.cs:500), навыки (SkillsController.cs:74)
  // и снимок сторожей; список сессий и сервисов своего предмета — [ChildAllowed] (e7021183e);
  // projects-list — метка есть (ProjectsController.cs:166-172), но эффект экрана ребёнку закрыт:
  // опрос ради взрослого онбординга, которого ребёнку не показывают; хаб /hubs/terminal — то же,
  // что и раньше (Х-W4-34). Гейт — существующий isChildScreen,
  // переданный хуку и стору опцией, не отдельная проверка. Свой список чатов — разрешён
  // (e7021183e): доработка Х-W4-30 вернула ребёнку валидацию восстановленного чата, и на
  // детском mounts она — единственный page-level опрос. Мутации, красящие кейс:
  // снять isChildScreen-гейт эффекта ensureTasksLoaded (в детском списке появляется
  // tasks.listAll); вернуть isChildScreen-гейт эффекта валидации (sessions.list из
  // детского списка пропадает); снять { disabled: isChildScreen } у useProjectTerminals или
  // useWatchdogPresence (в детском прогоне появляется listTerminals/onTerminalMessage
  // или watchdogs.snapshot). Спичка — все опросы этого mounts: page-level ручки по apiSpy,
  // терминал-хук по termSpy (хук реальный, мокнут только транспорт), снимок сторожей
  // по apiSpy (стор реальный). Сервисы-панель остаётся мокнутым — предмет отдельного шага.
  it('ребёнку неразрешённых опросов страницы не уходит; своё и взрослому — как раньше', async () => {
    const { root } = await mountChild();
    try {
      // из page-level ребёнку разрешён только свой список чатов (валидация восстановленного)
      expect([...new Set(apiSpy.calls)], 'детский экран позвал не своё').toEqual(['sessions.list']);
      // Х-W4-34: опросы внутри хука терминалов — не по apiSpy (это SignalR-хаб), а по спичке
      // транспорта: ребёнку ни первичного списка, ни подписки — обе поднимают /hubs/terminal
      expect(termSpy.list, 'ребёнку ушёл список терминалов (/hubs/terminal)').toBe(0);
      expect(termSpy.sub, 'ребёнку ушла подписка на терминалы (/hubs/terminal)').toBe(0);
    } finally { root.unmount(); }
    // та же страница без детского снимка: опросы на месте (иначе гейт — просто выключенный экран).
    // Стор задач — модульный синглтон: его listAll — один на процесс, поэтому он в проверке
    // не участвует; проверяются per-mount опросы этой страницы.
    apiSpy.calls.length = 0;
    // кэш списка чатов — модульный: детский mounts выше его заполнил (валидация разрешена);
    // без сброса взрослый mount взял бы детский кэш и его опрос не был бы виден
    sessionsListTest.reset();
    const adult = await mountChild({ childSurface: undefined });
    try {
      const calls = new Set(apiSpy.calls);
      expect(calls.has('projects.list'), 'взрослому не загружается список проектов').toBe(true);
      expect(calls.has('skills.list'), 'взрослому не загружаются навыки').toBe(true);
      expect(calls.has('sessions.list'), 'взрослому не проверяется восстановленный чат').toBe(true);
      // Х-W4-34 с другой стороны: хук терминалов и стор сторожей стартуют взрослому
      // (иначе правка — просто выключенный экран для всех)
      expect(calls.has('watchdogs.snapshot'), 'взрослому не снялся снимок сторожей').toBe(true);
      expect(termSpy.list, 'взрослому не запросился список терминалов').toBeGreaterThan(0);
      expect(termSpy.sub, 'взрослому не открылась подписка на терминалы').toBeGreaterThan(0);
    } finally { adult.root.unmount(); }
  });

  // Доработка Х-W4-30 (п.1-2 вердикта ревью 57958a7e, major): гард диплинка оборвал
  // единственный потребитель канала, который производит детский док, — «Новый урок» не
  // открывался. Цепочка в этом кейсе реальна до конца: настоящий ProjectRail (в заглушке)
  // → настоящий слушатель страницы → listProjectSessions → новый чат активен. Мутация:
  // вернуть «if (isChildScreen) return;» в эффект диплинка — клик оставляет страницу
  // на прежнем чате (активным остаётся chatK), кейс краснеет.
  it('ребёнок жмёт «Новый урок»: созданный чат открывается', async () => {
    const { root } = await mountChild();
    try {
      // Плашка активного предмета — портал в body: кнопка ищется по документу
      const btn = findNodeByAttr((globalThis as any).document.body as any, 'data-testid', 'child-new-lesson');
      expect(btn, 'кнопки «Новый урок» в плашке предмета нет').not.toBeNull();
      const props = reactProps(btn!) as { onClick?: () => void };
      expect(typeof props.onClick, 'кнопка «Нового урока» не активна').toBe('function');
      act(() => { props.onClick!(); });
      // createLesson в полёте: await мока + setCreating закрывают кнопку до ответа
      await act(async () => { vi.advanceTimersByTime(100); });
      expect(kid.createLessonCalls, 'ручка создания урока не позвана').toBe(1);
      // страница приняла свой канал: запись съедена потребителем
      expect(sstore.has('cc_pending_project_chat'), 'страница не съела pending-канал созданного урока').toBe(false);
      // активный чат — новый: проп активного окна (с той стороны его рисует ChatPanel)
      const dw = dwPropsReg[dwPropsReg.length - 1];
      expect(dw.activeSession?.id, 'после «Нового урока» активен не созданный чат').toBe('chatNew');
    } finally { root.unmount(); }
  });

  // Х-W4-75 (дефект a2aae946, QA-4): мобильная рельса и шторка «Ещё» строились из
  // захардкоженного списка вкладок (leftTabOptions) и детский снимок не знали — на 360
  // ребёнку были «Изменения» (панель с плашкой «недоступно» по клику) и взрослый уход
  // в «Ещё»: «Граф» → 403 child_mode прямо на экране, «Команда» → падение
  // ErrorBoundary. Теперь здесь то же пересечение, что у десктопной allowedKeys:
  // childWorkspaceKeys по словарю «вкладка → панель» (TAB_PANEL_KEY в WorkspacePage),
  // второй копии правил нет. Мутации, красящие первый кейс: убрать .filter у
  // leftTabOptions (в детский ряд встаёт «Изменения»); вернуть безусловные «Граф» и
  // взрослый уход шторки (их подписи появляются на экране ребёнка).
  const CHILD_TAB_LABELS = ['Чаты', 'Файлы', 'Задачи', 'Знания'];
  const ADULT_TAB_LABELS = ['Изменения', 'Команда', 'Навыки', 'Инструменты'];
  const ADULT_SHEET_LABELS = ['Граф', 'Настройки проекта', 'Модели и расход'];
  // Раньше здесь был обходной surfaceNow — снимок с добавлением 'chats': копия
  // CHILD_PANELS была прежнего среза без неё, и без 'chats' у ребёнка на мобиле не
  // было бы пути к своим урокам вовсе (К9к). Копия приведена к бэку — обходной не нужен.

  // Достижимое с экрана: пилюли ряда (сегменты PillSwitch именованы aria-label,
  // PillSwitch.tsx:273) плюс содержимое открытой шторки «Ещё» (её карточка — портал
  // в body, как настоящий bottom-sheet). На 360 ряд вмещает не всё — остальное уходит
  // в шторку; важен состав, а не то, в какой из двух половин он лежит.
  const openSheetText = (container: NodeLike): string => {
    const trig = findNodeByAttr(container as any, 'aria-label', 'Ещё');
    if (!trig) return '';
    const props = reactProps(trig) as { onClick?: () => void };
    act(() => { props.onClick!(); });
    return textNodes((globalThis as any).document.body as never).join(' ');
  };

  it('ребёнку на 360 — только его поверхность: ни взрослых вкладок, ни чужих пунктов шторки', async () => {
    const { root, container } = await mountChild({ width: 360, childSurface: surface });
    try {
      const pillLabels = attrValues(container as any, 'aria-label');
      const sheet = openSheetText(container);
      // Своё — достижимо всё: chats→«Чаты», files, tasks, knowledge (notes и
      // chatContext на мобиле не имеют своего словаря — см. комментарий в WorkspacePage)
      for (const label of CHILD_TAB_LABELS) {
        expect(pillLabels.includes(label) || sheet.includes(label), `панели снимка «${label}» нет ни в ряду, ни в шторке`).toBe(true);
      }
      // Вне снимка — не монтируется вовсе: ни пилюли, ни пункта, ни плашки «недоступно»
      for (const label of [...ADULT_TAB_LABELS, ...ADULT_SHEET_LABELS]) {
        expect(pillLabels.includes(label), `вне снимка: в ряду «${label}»`).toBe(false);
        expect(sheet.includes(label), `вне снимка: в шторке «${label}»`).toBe(false);
      }
    } finally { root.unmount(); }
  });

  // Та же страница без детского снимка на той же ширине: взрослая мобильная поверхность
  // не изменилась (иначе правка — просто выключенный экран для всех). «Плагины» здесь
  // нет: плагинов ни у кого (hasPlugins=false) — это не предмет кейса.
  it('взрослому на 360 — та же рельса и шторка, что до правки', async () => {
    const { root, container } = await mountChild({ childSurface: undefined, width: 360 });
    try {
      const pillLabels = attrValues(container as any, 'aria-label');
      const sheet = openSheetText(container);
      expect(sheet.length, 'шторки «Ещё» взрослому нет').toBeGreaterThan(0);
      for (const label of [...CHILD_TAB_LABELS, ...ADULT_TAB_LABELS]) {
        expect(pillLabels.includes(label) || sheet.includes(label), `вкладки «${label}» нет ни в ряду, ни в шторке`).toBe(true);
      }
      for (const label of ADULT_SHEET_LABELS) {
        expect(sheet.includes(label), `пункта шторки «${label}» нет`).toBe(true);
      }
    } finally { root.unmount(); }
  });

  // ── Х-W4-75 (доработка Киры, вердикт c7c5d785): «Архитектура» в шторке ───────────
  // Модуль «Архитектура» подключён (слоты панели и документа центра) → раздел есть.
  // Ребёнку его нельзя: в вертикали Architecture нет [ChildAllowed], клик даёт 403
  // child_mode на весь экран, а на десктопе ребёнку «Архитектуру» не дают — разрыв
  // 360/1280. Взрослому — наоборот, пункт должен остаться.
  it('ребёнку на 360 с подключённым модулем «Архитектура» — пункта нет ни в ряду, ни в шторке', async () => {
    archSlots.enabled = true;
    const { root, container } = await mountChild({ width: 360, childSurface: surface });
    try {
      const pillLabels = attrValues(container as any, 'aria-label');
      const sheet = openSheetText(container);
      expect(pillLabels.includes('Архитектура'), 'в ряду ребёнка есть «Архитектура»').toBe(false);
      expect(sheet.includes('Архитектура'), 'в шторке ребёнка есть «Архитектура»').toBe(false);
    } finally { root.unmount(); }
  });

  it('взрослому на 360 с подключённым модулем «Архитектура» — пункт в шторке есть', async () => {
    archSlots.enabled = true;
    const { root, container } = await mountChild({ childSurface: undefined, width: 360 });
    try {
      const pillLabels = attrValues(container as any, 'aria-label');
      const sheet = openSheetText(container);
      expect(pillLabels.includes('Архитектура') || sheet.includes('Архитектура'),
        'взрослому на 360 «Архитектура» недоступна ни в ряду, ни в шторке').toBe(true);
    } finally { root.unmount(); }
  });

  // ── Х-W4-80 (дефект fab7e418, факт 3(б)): разбор чата из адреса у ребёнка ──────────
  // Тот же pending-канал, что у взрослого (его взрослая ветка — WorkspacePage.deeplinkChat.test.ts,
  // здесь не трогаем): адрес называет урок из списка ребёнка → он открывается, сохранённый
  // не показан никогда (под адресом, называющим другой чат, сообщение не должно уйти в
  // сохранённый); адрес называет чужой/несуществующий чат → в центре детское пустое
  // состояние и детский текст отказа — не сохранённый чат и не взрослый «Чат недоступен».
  const chatLesson: Session = {
    id: 'chatLesson', name: 'Дроби. Урок 2', projectId: 'p-subj', mode: 'default', status: 'active',
    messageCount: 0, model: 'sonnet', createdAt: '', updatedAt: '',
  } as Session;
  // Чата нет в списке ребёнка: своему предмету бэк отдаёт только его уроки
  // (SessionsController.cs:37-47), чужих и взрослых в проекте-предмете нет
  const chatGone: Session = {
    id: 'chatGone', name: 'Удалённый урок', projectId: 'p-subj', mode: 'default', status: 'active',
    messageCount: 0, model: 'sonnet', createdAt: '', updatedAt: '',
  } as Session;

  // Маунт по адресу — то, что делает App при загрузке по ссылке: pending-канал + хэш.
  // История пишет адрес, как живая: выбор чата должен доходить до URL. Сохранённое
  // состояние — по заданию (не указано — ничего не сохранено). 360 — реальная мобильная
  // раскладка с настоящей ChildLessonsPanel; 1280 — десктопная (заглушка пишет пропы).
  async function mountChildAt(opts: { addressChat: string; saved?: Session | null; list?: Session[]; width?: number }) {
    kid.status = statusOk;
    kid.subjects = [subject];
    const win = globalThis as any;
    win.window.history = {
      state: null, back: () => {}, go: () => {}, forward: () => {}, length: 1,
      pushState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
      replaceState: (st: any, _t: string, h: string) => { win.window.history.state = st; win.window.location.hash = h; },
    };
    mockSessions.length = 0; mockSessions.push(...(opts.list ?? [session, newChat]));
    if (opts.saved) store.set('ws:p-subj', JSON.stringify({ activeSession: opts.saved, openFile: null, leftTab: 'sessions' }));
    sstore.set('cc_pending_project_chat', `p-subj|${opts.addressChat}`);
    win.window.location.hash = `#/project/p-subj/chat/${opts.addressChat}`;
    if (opts.width) win.window.innerWidth = opts.width;
    const { root, container } = mount(createElement(WorkspacePage, {
      project, onGoToProjects: () => {}, onSwitchHub: () => {}, auth, onLogout: () => {},
      onOpenSubjectProject: () => {}, childSurface: surface,
    } as any));
    await act(async () => { vi.advanceTimersByTime(300); });
    return { root, container: container as unknown as NodeLike };
  }

  // Тосты страницы: NotificationToasts живёт в App, не в WorkspacePage — слушатель свой,
  // иначе проверка отказа была бы вакуумом (никто не слушает — никто не видит)
  function toastSink(sink: { title: string; body: string }[]): () => void {
    const on = (e: any) => { sink.push(e.detail); };
    (globalThis as any).window.addEventListener('cc-local-toast', on);
    return () => (globalThis as any).window.removeEventListener('cc-local-toast', on);
  }

  it('Х-W4-80 c1: адрес называет урок из списка ребёнка — он открыт, сохранённый не показан никогда', async () => {
    const win = globalThis as any;
    const { root } = await mountChildAt({ addressChat: 'chatLesson', saved: session, list: [session, chatLesson] });
    try {
      // КЛЮЧЕВОЕ (тот же порядок, что у взрослого QA-D2): ни одного рендера с сохранённым
      // чатом — ни до разрешения списка, ни после
      const seen = dwPropsReg.map(p => p.activeSession?.id);
      expect(seen.includes('chatK'), `сохранённый чат показался под адресом (${JSON.stringify(seen)})`).toBe(false);
      expect(sstore.has('cc_pending_project_chat'), 'страница не съела pending-канал адреса').toBe(false);
      const last = dwPropsReg[dwPropsReg.length - 1];
      expect(last.activeSession?.id, `открыт не чат из адреса (${last.activeSession?.id})`).toBe('chatLesson');
      // Выбор чата дошёл до адреса: «назад» вернёт в прежний
      expect(win.window.location.hash, `адрес потерял chatId (${win.window.location.hash})`).toContain('/chat/chatLesson');
    } finally { root.unmount(); }
  });

  it('Х-W4-80 c1: на мобиле открытый из адреса урок помечен в списке уроков', async () => {
    const { root, container } = await mountChildAt({ addressChat: 'chatLesson', list: [session, chatLesson], width: 360 });
    try {
      const rows: string[] = [];
      let activeName: string | null = null;
      walk(container, (n) => {
        if (n.nodeType === 1 && n.getAttribute('data-testid') === 'child-lesson-row') {
          const name = textNodes(n as never).join('').trim();
          rows.push(name);
          if (n.getAttribute('aria-current') === 'true') activeName = name;
        }
      });
      // Список жив и показывает оба урока; открыт и помечен — названный адресом
      expect(rows.some(r => r.includes('Дроби. Урок 2')), `в списке нет урока из адреса (${JSON.stringify(rows)})`).toBe(true);
      expect(activeName, `в списке нет строки открытого урока (${JSON.stringify(rows)})`).toContain('Дроби. Урок 2');
    } finally { root.unmount(); }
  });

  it('Х-W4-80 c2: адрес с несуществующим чатом — детское пустое состояние и детский отказ', async () => {
    const toasts: { title: string; body: string }[] = [];
    const offToast = toastSink(toasts);
    const { root, container } = await mountChildAt({ addressChat: 'chatGone', saved: session, list: [session], width: 360 });
    try {
      // КЛЮЧЕВОЕ: под несуществующий адрес сохранённый урок НЕ открыт — его нет ни в ленте
      // (row с именем сохранённого), ни в активной строке списка; центр — пустой
      const rows: string[] = [];
      let activeName: string | null = null;
      walk(container, (n) => {
        if (n.nodeType === 1 && n.getAttribute('data-testid') === 'child-lesson-row') {
          const name = textNodes(n as never).join('').trim();
          rows.push(name);
          if (n.getAttribute('aria-current') === 'true') activeName = name;
        }
      });
      expect(activeName ?? '', `под несуществующий адрес открылся другой урок (${JSON.stringify(rows)})`).toBe('');
      expect(rows.length, `под несуществующий адрес список уроков не жив (${JSON.stringify(rows)})`).toBeGreaterThan(0);
      // Центр — детское пустое состояние; взрослой заставки «С чего начнём?» нет
      expect(findNodeByAttr(container as any, 'data-testid', 'child-center-empty'), 'детского пустого состояния центра нет').not.toBeNull();
      const all = [...textNodes(container as never), ...docText()].join(' ');
      expect(all.includes('С чего начнём'), 'ребёнку показалась взрослая заставка').toBe(false);
      // Отказ — детский текст; взрослый «Чат недоступен» ребёнку не показывается
      expect(toasts.some(t => t.title === 'Урок недоступен'), `нет детского отказа (${JSON.stringify(toasts)})`).toBe(true);
      expect(toasts.some(t => t.title === 'Чат недоступен'), 'ребёнку показан взрослый отказ').toBe(false);
    } finally { offToast(); root.unmount(); }
  });
});

// ── К9к: копия снимка vs бэк ────────────────────────────────────────────────────────
// CHILD_PANELS выше по файлу — копия замороженного снимка бэка: она ничего не строит
// и живёт только в этом тесте. Без сверки копия разъезжается с бэком молча — так и
// вышло (без 'chats', шесть против пяти — вердикт R-Х-W4-75). Сторож читает перечень
// прямо из бэкового файла: изменение бэка без ответа фронта красит этот тест, а не
// меняет детскую рельсу молча (тот же приём, что сверка типов страниц с
// WikiFrontmatter в wikiLabels.test.ts:219).
const ALLOW_LIST_PATH = resolve(dirname(fileURLToPath(import.meta.url)),
  '../../../../backend/ClaudeHomeServer.Core/Services/ChildMode/ChildModeAllowList.cs');

function backendPanels(): string[] {
  const src = readFileSync(ALLOW_LIST_PATH, 'utf8');
  // Panels: ["files", …] — строка снимка ChildModeAllowList.Surface (.:23)
  const m = src.match(/Panels:\s*\[([^\]]*)\]/);
  expect(m).not.toBeNull();
  return [...m![1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
}

describe('копия детской поверхности vs бэк (К9к)', () => {
  it('CHILD_PANELS — ровно ChildModeAllowList.Surface.Panels', () => {
    // сравнение по множеству, не по порядку: снимок для фронта — множество
    // разрешённого, порядок рельсы собирает сам фронт (panelCatalog)
    expect([...CHILD_PANELS].sort(), 'копия снимка разъехалась с бэком').toEqual(backendPanels().sort());
  });
});
