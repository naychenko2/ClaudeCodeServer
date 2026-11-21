// Х-W4-79 · фронт-сторож И11 (вердикт Александра 3333058a, п.3(а)): пустое состояние
// центра ребёнка и запросы, которые его кормят.
// 1) Взрослые ChatEmptyState — а с ним NewChatSetup, который монтируется только из
//    него (EmptyState.tsx:147) — ребёнку не монтируются; взрослому монтируются:
//    без второй половины отрицательная проверка была бы вакуумом.
// 2) Гейт стоит в МЕСТЕ ОПРОСА, а не в рендере: запросы взрослой пустой ленты —
//    список персон для ряда «Поговорить с…» (GET /api/personas без [ChildAllowed]
//    ответил бы 403) и обмер файлов ради CTA «Инициализировать проект» — у ребёнка
//    не уходят вовсе. Гейт по снимку «я» (me.isChild — паттерн конвейера, как ensureGit
//    в FileExplorer.tsx:869 или стор персон lib/personas.ts:53), а не по пропам рендера:
//    третий кейс проверяет именно это — ребёнок без childFeedKinds всё равно не спрашивает.
// (Рендер гасится пропом childFeedKinds — его даёт владелец экрана, WorkspacePage.tsx:2869;
// здесь проверяется запросная сторона инварианта.)
// Окружение — node + linkedom, как в ChildLessonHeader.test: тяжёлые соседи ChatPanel
// подменены стендами, api — журнал вызовов, useMe — подменяемый снимок.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { Project } from '../../types';
import { CHILD_TURN_ERROR_TEXT } from './childFeedFilter';

// navigator обязан встать ДО импорта моков: lib/offline читает navigator.onLine в свой
// верхний уровень (offline.ts:143), а его модуль тянут importOriginal-ы ниже — иначе
// _online = undefined и «online»-ветки ChatPanel не сработали бы никогда
vi.hoisted(() => {
  Object.defineProperty(globalThis, 'navigator', {
    configurable: true, writable: true,
    value: { language: 'ru-RU', onLine: true, userAgent: 'node' },
  });
});

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
const nodeSetTimeout = globalThis.setTimeout.bind(globalThis);
const nodeClearTimeout = globalThis.clearTimeout.bind(globalThis);
Object.assign(win, {
  innerWidth: 1280,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {} }),
  // Таймеры/кадры: эффект спиннера истории и переход ленты в ChatPanel зовут их при mount
  setTimeout: (fn: () => void, ms?: number) => nodeSetTimeout(fn, ms),
  clearTimeout: (id: number) => { nodeClearTimeout(id); },
  requestAnimationFrame: (cb: (t: number) => void) => { cb(0); return 1; },
  cancelAnimationFrame: () => {},
  getSelection: () => null,
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('IntersectionObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('requestAnimationFrame', (cb: (t: number) => void) => { cb(0); return 1; });
vi.stubGlobal('cancelAnimationFrame', () => {});
const lsStore: Record<string, string> = {};
vi.stubGlobal('localStorage', { getItem: (k: string) => lsStore[k] ?? null, setItem: (k: string, v: string) => { lsStore[k] = v; }, removeItem: (k: string) => { delete lsStore[k]; } });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Снимок «я»: тот же объект, который читает useMe в ChatPanel (меняется в тесте)
const meRef = vi.hoisted(() => ({ v: { userId: 'u1', isChild: false } as { userId: string; isChild: boolean } }));
// Журнал сети: все вызовы api, сделанные из графа ChatPanel (имена «группа.метод»)
const net = vi.hoisted(() => ({ calls: [] as string[] }));
// Счётникmount'ов взрослой пустой ленты: 0 = модуль ни разу не позван
const emptyStateRenders = vi.hoisted(() => ({ n: 0 }));
// Лента, которую показывает ChatPanel: подменяем items на выходе useSession (К9g) —
// чтобы построить детскую ленту с плашкой без живого SignalR/истории
const itemsRef = vi.hoisted(() => ({ v: [] as any[] }));

vi.mock('../../lib/defaultPersona', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/defaultPersona')>();
  return { ...orig, useMe: () => meRef.v as never };
});

vi.mock('../../lib/signalr', () => {
  const sub = () => () => {};
  const act2 = () => Promise.resolve();
  return {
    __esModule: true,
    onMessage: sub, onReconnected: sub, onFilesChanged: sub, onPreviewMessage: sub,
    onLocalModelMessage: sub, onGitStatusChanged: sub, onAgentHubMessage: sub,
    joinProject: act2, leaveProject: act2, joinSession: act2, leaveSession: act2,
    joinUser: act2, leaveUser: act2, sendMessage: act2, respondPermission: act2,
    interruptSession: act2, compactSession: act2, answerQuestion: act2, respondPlan: act2,
    respondTeamPlan: act2, respondTeamEscalation: act2, setMode: act2, resetConnection: act2,
    joinPreviewLog: act2, leavePreviewLog: act2,
  };
});

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
  // Формы ответов, без которых сторы падают на срезе (тот же набор, что в ChildLessonHeader.test)
  const overrides: Record<string, Record<string, (...a: unknown[]) => any>> = {
    tasks: { featuresIndex: () => Promise.resolve({ features: [], chats: {} }), getFeature: () => Promise.resolve(null), listAll: () => Promise.resolve([]) },
    chats: { get: () => Promise.resolve(null) },
    sessions: { list: () => Promise.resolve([]) },
    projects: { list: () => Promise.resolve([]) },
    personas: { list: () => Promise.resolve([]) },
    files: { list: () => Promise.resolve([]) },
  };
  const ns = (group: string, ov: Record<string, (...a: unknown[]) => any>): any => new Proxy({}, {
    get: (_t, prop) => {
      if (prop === 'then' || prop === Symbol.toPrimitive) return undefined;
      const name = String(prop);
      const impl = ov[name] ?? (() => Promise.resolve(null));
      return (...a: unknown[]) => { net.calls.push(`${group}.${name}`); return impl(...a); };
    },
  });
  const api: any = new Proxy({}, {
    get: (_t, group) => {
      if (group === 'then' || group === Symbol.toPrimitive) return undefined;
      const g = String(group);
      return ns(g, overrides[g] ?? {});
    },
  });
  return { api, setGitSessionContext: () => {} };
});

// useSession: реальный хук (его join/сторы подменяют моки выше), но items на выходе —
// из itemsRef: так тест строит конкретную ленту (К9g). Остальные поля хука живые.
vi.mock('../../hooks/useSession', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../hooks/useSession')>();
  const real = orig.useSession;
  return { ...orig, useSession: (...a: Parameters<typeof real>) => ({ ...real(...a), items: itemsRef.v }) };
});

vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return {
    ...orig,
    usePersonasVersion: () => 0, getPersonaById: () => null,
    getPersonasSnapshot: () => [], ensurePersonasLoaded: () => Promise.resolve(), personaLabel: () => '',
  };
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
vi.mock('../../lib/subsystems', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems')>();
  return { ...orig, SUBSYSTEMS: {}, useSubsystem: () => ({ id: 'x', enabled: false }) };
});
vi.mock('../../lib/subsystems/registry', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/subsystems/registry')>();
  return { ...orig, useSlot: () => [], useSlotItem: () => null };
});
vi.mock('../../lib/projectCapabilities', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/projectCapabilities')>();
  return { ...orig, useProjectFeature: () => false, isLocalProject: () => false, featureReason: () => null };
});
// Связь — внешнее условие, в тесте оно задано прямо: «online» обязана быть true,
// иначе ветки ленты, требующие связи (в т.ч. пустое состояние), не проверяются вовсе
vi.mock('../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/offline')>();
  return { ...orig, isOnline: () => true, subscribeOnline: () => () => {} };
});
// Взрослая шапка и взрослая пустая лента — маркеры-заглушки: их присутствие/отсутствие
// в DOM и есть проверка «ребёнку не монтируется» (отрицательное утверждение без
// маркера — вакуум)
vi.mock('../../components/chat/ChatHeaderBar', async () => {
  const React = await import('react');
  return { ChatHeaderBar: () => React.createElement('div', { 'data-testid': 'chat-header-bar-stub' }) };
});
vi.mock('../../components/chat/EmptyState', async () => {
  const React = await import('react');
  return {
    ChatEmptyState: () => {
      emptyStateRenders.n++;
      return React.createElement('div', { 'data-testid': 'chat-empty-state-stub' });
    },
  };
});

// Импорт после глобалов и моков: модульный граф ChatPanel не должен читать DOM/сторы
// до их подстановки (тот же приём, что в ChildLessonHeader.test)
const { ChatPanel } = await import('../../components/ChatPanel');

const chatSession = { id: 's1', name: 'Дроби', projectId: null, mode: 'default', createdAt: '', updatedAt: '' } as never;
const project = {
  id: 'p1', name: 'Математика', rootPath: '/p', createdAt: '', updatedAt: '',
  tagRegistry: [], subjectRegistry: [], subjectsSweepSince: null,
} as unknown as Project;

// Живой root: перед следующим кейсом он снимается — иначе его асинхронная цепочка
// (join/история) доживает до следующего теста и перерисовывает старый экран внутри
// чужого замера (счётчик чужого mount'а оказался бы в этом)
let liveRoot: { unmount: () => void } | null = null;

async function renderChatPanel(props: Record<string, unknown>) {
  // Свежий контейнер на каждый кейс: повторный createRoot на том же узле — «залипший»
  // рендер предыдущего кейса (предупреждение React), а не чистый монтаж
  const host = doc.createElement('div');
  host.id = 'root';
  doc.body.appendChild(host);
  const root = createRoot(host);
  liveRoot = root;
  await act(async () => {
    root.render(createElement(ChatPanel, {
      session: chatSession,
      attachedFiles: [],
      onAttachedFilesChange: () => {},
      ...props,
    }));
  });
  // Дать доехать цепочке «join → история → снимок состояния»: её звенья разведены
  // реальными таймерами (JOIN_WAIT_MS), один микрофлеш их не покрывает
  await act(async () => { await new Promise(r => setTimeout(r, 60)); });
  return root;
}

// Только запросы взрослой пустой ленты: список персон (ряд «Поговорить с…») и обмер
// файлов (CTA «Инициализировать проект»). Остальные вызовы (settings, history) — вне темы
const EMPTY_STATE_CALLS = ['personas.list', 'files.list'];
const emptyStateCalls = () => net.calls.filter(c => EMPTY_STATE_CALLS.includes(c));

describe('Х-W4-79: пустое состояние центра и его запросы — по снимку «я», в месте опроса', () => {
  beforeEach(() => {
    liveRoot?.unmount();
    liveRoot = null;
    for (const el of Array.from(doc.querySelectorAll('#root'))) el.remove();
    net.calls.length = 0;
    emptyStateRenders.n = 0;
    meRef.v = { userId: 'u1', isChild: false };
    itemsRef.v = [];
  });

  it('взрослому пустому чату: empty state смонтирован, его запросы ушли', async () => {
    await renderChatPanel({ project });
    // Каркас должен быть: без этого «не смонтировано» ниже было бы вакуумом
    expect(doc.querySelector('[data-testid="chat-panel"]'), 'каркаса чата нет — проверка несерьёзна').not.toBeNull();
    expect(doc.querySelector('[data-testid="chat-empty-state-stub"]'), 'взрослому пустое состояние не смонтировано').not.toBeNull();
    expect(emptyStateCalls(), 'взрослому не ушли запросы его пустой ленты').toEqual(expect.arrayContaining(EMPTY_STATE_CALLS));
  });

  it('ребёнку (childFeedKinds): ни взрослого empty state с NewChatSetup, ни его запросов', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    await renderChatPanel({ project, childFeedKinds: ['assistant', 'user'] });
    // Детская шапка урока в DOM — доказательство, что лента ребёнка отрендерена:
    // отсутствие взрослого empty state — не сорванный монтаж
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'детской ленты нет — проверка несерьёзна').not.toBeNull();
    expect(emptyStateRenders.n, 'взрослый ChatEmptyState (и NewChatSetup внутри него) смонтирован ребёнку').toBe(0);
    expect(doc.querySelector('[data-testid="chat-empty-state-stub"]'), 'взрослая пустая лента в DOM ребёнка').toBeNull();
    expect(emptyStateCalls(), 'ребёнку ушли взрослые запросы пустой ленты').toEqual([]);
  });

  it('гейт в месте опроса, а не в рендере: ребёнок без childFeedKinds тоже не спрашивает', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    await renderChatPanel({ project });
    expect(doc.querySelector('[data-testid="chat-panel"]'), 'каркаса чата нет — проверка несерьёзна').not.toBeNull();
    expect(emptyStateCalls(), 'запросы гасятся пропом рендера, а не снимком «я»').toEqual([]);
  });
});

// К9g · две правки детского пути ChatPanel:
// А (дефект 5f7854e7): плашка отказа маршрута показывает его текст (plateText), а не общую
// CHILD_TURN_ERROR_TEXT; отказ приходит парой error(action child-route-refusal) → result(error),
// первой идёт error — значит в единственной плашке текст отказа (ряд «подрядный ряд — одна плашка»).
// Б (дефект cc43582b): ребёнку GET /settings (взрослая ручка без [ChildAllowed]) не уходит вовсе
// и подписка на cc-claude-billing-changed не вешается — ребёнку стоимость не показывается.
describe('К9g: плашка отказа маршрута и отсутствие GET /settings у ребёнка', () => {
  beforeEach(() => {
    liveRoot?.unmount();
    liveRoot = null;
    for (const el of Array.from(doc.querySelectorAll('#root'))) el.remove();
    net.calls.length = 0;
    emptyStateRenders.n = 0;
    meRef.v = { userId: 'u1', isChild: false };
    itemsRef.v = [];
  });

  it('ребёнку, отказ маршрута (error+result): одна плашка с текстом отказа, без общей CHILD_TURN_ERROR_TEXT', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    const refusal = 'Занятие пока недоступно, взрослым отправлено уведомление.';
    itemsRef.v = [
      { kind: 'error', text: refusal, action: 'child-route-refusal', ts: 1 },
      { kind: 'result', subtype: 'error', durationMs: 0, numTurns: 0 },
    ];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result'] });
    // Доказательство, что именно детская лента отрендерена (иначе «нет плашки» — вакуум)
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'детской ленты нет — проверка несерьёзна').not.toBeNull();
    const plates = Array.from(doc.querySelectorAll('[data-child-turn-error]'));
    expect(plates.length, 'подрядный ряд error→result должен дать ровно одну плашку').toBe(1);
    expect(plates[0].textContent ?? '', 'в плашке нет текста отказа маршрута').toContain(refusal);
    expect(doc.body.textContent ?? '', 'общая CHILD_TURN_ERROR_TEXT появилась рядом с отказом').not.toContain(CHILD_TURN_ERROR_TEXT);
  });

  it('ребёнку, сбой без action: одна плашка с общей CHILD_TURN_ERROR_TEXT, сырой текст провайдера скрыт', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [{ kind: 'error', text: 'Invalid API key', ts: 1 }];
    await renderChatPanel({ project, childFeedKinds: ['error'] });
    const plates = Array.from(doc.querySelectorAll('[data-child-turn-error]'));
    expect(plates.length, 'плашка обычного сбоя не отренилась').toBe(1);
    expect(plates[0].textContent ?? '', 'в плашке нет общей CHILD_TURN_ERROR_TEXT').toContain(CHILD_TURN_ERROR_TEXT);
    expect(plates[0].textContent ?? '', 'сырой текст провайдера показался ребёнку').not.toContain('Invalid API key');
  });

  it('ребёнку GET /settings не уходит вовсе', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    await renderChatPanel({ project });
    expect(doc.querySelector('[data-testid="chat-panel"]'), 'каркаса чата нет — проверка несерьёзна').not.toBeNull();
    expect(net.calls.filter(c => c === 'settings.get').length, 'ребёнку ушёл GET /settings').toBe(0);
  });

  it('взрослому GET /settings уходит ровно один раз', async () => {
    meRef.v = { userId: 'u1', isChild: false };
    await renderChatPanel({ project });
    expect(doc.querySelector('[data-testid="chat-panel"]'), 'каркаса чата нет — проверка несерьёзна').not.toBeNull();
    expect(net.calls.filter(c => c === 'settings.get').length, 'взрослому settings.get не ушёл ровно один').toBe(1);
  });
});

// К9g-д · дефект ab12ca2d (QA-6): под сообщением ребёнка вставались ДВЕ плашки — честная
// «Занятие пока недоступно…» и рядом общая CHILD_TURN_ERROR_TEXT. Причина: гонка с
// pending-сообщением (result(subtype=error) дозагрузкой хвоста истории) разрывает
// подрядность, и склейка ряда по соседству feed[k-1] промахивалась. Новое правило:
// наличие в ряду плашки с plateText гасит общие плашки вовсе — независимо от порядка
// прихода и разрывов; плашка отказа не гасится соседней общей. Тесты ниже — оба порядка
// и разрыв pending-элементом (user_message) между плашками; прежний кейс «подрядный
// error→result» выше остаётся зелёным (тот же итог, другое основание).
const REFUSAL_TEXT = 'Занятие пока недоступно, взрослым отправлено уведомление.';
const refusalItem = { kind: 'error', text: REFUSAL_TEXT, action: 'child-route-refusal', ts: 1 };
const genericErrorItem = { kind: 'result', subtype: 'error', durationMs: 0, numTurns: 0 };
const pendingUserItem = { kind: 'user_message', text: 'реши 2+2', ts: 2 };

describe('К9g-д: одна плашка отказа — общий рядом нет, в любом порядке и с разрывом', () => {
  beforeEach(() => {
    liveRoot?.unmount();
    liveRoot = null;
    for (const el of Array.from(doc.querySelectorAll('#root'))) el.remove();
    net.calls.length = 0;
    emptyStateRenders.n = 0;
    meRef.v = { userId: 'u1', isChild: false };
    itemsRef.v = [];
  });

  // Единая проверка «ровно одна плашка — с текстом отказа, без общей»: по всем кейсам
  const expectSingleRefusalPlate = () => {
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'детской ленты нет — проверка несерьёзна').not.toBeNull();
    const plates = Array.from(doc.querySelectorAll('[data-child-turn-error]'));
    expect(plates.length, 'плашек больше одной').toBe(1);
    expect(plates[0].textContent ?? '', 'в плашке нет текста отказа маршрута').toContain(REFUSAL_TEXT);
    expect(doc.body.textContent ?? '', 'общая CHILD_TURN_ERROR_TEXT появилась рядом с отказом').not.toContain(CHILD_TURN_ERROR_TEXT);
  };

  it('сбой первым, отказ вторым (рядом): одна плашка, с текстом отказа', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [genericErrorItem, refusalItem];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result'] });
    expectSingleRefusalPlate();
  });

  it('отказ и сбой разделены pending-сообщением: одна плашка, с текстом отказа', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [refusalItem, pendingUserItem, genericErrorItem];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result', 'user_message'] });
    expectSingleRefusalPlate();
  });

  it('сбой и отказ разделены pending-сообщением, сбой первым: одна плашка, с текстом отказа', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [genericErrorItem, pendingUserItem, refusalItem];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result', 'user_message'] });
    expectSingleRefusalPlate();
  });

  it('без отказа ряда общая плашка жива и не плодится: подрядные сбои — одна плашка', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [{ kind: 'error', text: 'Invalid API key', ts: 1 }, genericErrorItem];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result'] });
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'детской ленты нет — проверка несерьёзна').not.toBeNull();
    const plates = Array.from(doc.querySelectorAll('[data-child-turn-error]'));
    expect(plates.length, 'подрядные сбои без отказа дали больше одной плашки').toBe(1);
    expect(plates[0].textContent ?? '', 'в плашке нет общей CHILD_TURN_ERROR_TEXT').toContain(CHILD_TURN_ERROR_TEXT);
  });
});

// К9g-д2 · доработка по вердикту R-К9g-д (returned: robustness), дефект ab12ca2d: гашение
// общей плашки — только в блоке отказанного хода. Прежнее hasRefusal считало по всему фиду:
// после первого отказа в чате каждый новый сбой хода гасился навсегда. Блок: подрядные
// плашки, разделённые только pending-сообщениями; текст ассистента/успешный result
// разрывают блок. Инвариант: «отказ не дублируется общей плашкой своего хода; общая
// плашка другого хода жива».
describe('К9g-д2: старый отказ не гасит общую плашку нового сбоя', () => {
  beforeEach(() => {
    liveRoot?.unmount();
    liveRoot = null;
    for (const el of Array.from(doc.querySelectorAll('#root'))) el.remove();
    net.calls.length = 0;
    emptyStateRenders.n = 0;
    meRef.v = { userId: 'u1', isChild: false };
    itemsRef.v = [];
  });

  const successResultItem = { kind: 'result', subtype: 'success', durationMs: 10, numTurns: 1 };

  // Законный итог разорванного блока: плашка старого отказа остаётся (это история чата),
  // и новый сбой своего блока получает общую плашку — их ровно две, в порядке ленты
  const expectRefusalPlusGeneric = () => {
    const plates = Array.from(doc.querySelectorAll('[data-child-turn-error]'));
    expect(plates.length, 'плашек стало не две (отказ + общий сбой)').toBe(2);
    expect(plates[0].textContent ?? '', 'плашка старого отказа пропала').toContain(REFUSAL_TEXT);
    expect(plates[1].textContent ?? '', 'сбой нового хода не получил общей плашки').toContain(CHILD_TURN_ERROR_TEXT);
  };

  it('старый отказ → успешный ход → новый сбой: общая плашка нового сбоя есть', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [refusalItem, successResultItem, genericErrorItem];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result'] });
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'детской ленты нет — проверка несерьёзна').not.toBeNull();
    expectRefusalPlusGeneric();
  });

  it('старый отказ → текст ассистента → новый сбой: общая плашка нового сбоя есть', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [refusalItem, { kind: 'text', text: 'Две минуты назад всё работало', ts: 3 }, genericErrorItem];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result', 'text'] });
    expectRefusalPlusGeneric();
  });

  it('отказ и сбой в одном блоке (разрыв только pending): по-прежнему одна плашка отказа', async () => {
    meRef.v = { userId: 'kid', isChild: true };
    itemsRef.v = [refusalItem, pendingUserItem, genericErrorItem];
    await renderChatPanel({ project, childFeedKinds: ['error', 'result', 'user_message'] });
    expect(doc.querySelector('[data-testid="child-lesson-header"]'), 'детской ленты нет — проверка несерьёзна').not.toBeNull();
    const plates = Array.from(doc.querySelectorAll('[data-child-turn-error]'));
    expect(plates.length, 'плашек больше одной').toBe(1);
    expect(plates[0].textContent ?? '', 'в плашке нет текста отказа маршрута').toContain(REFUSAL_TEXT);
    expect(doc.body.textContent ?? '', 'общая CHILD_TURN_ERROR_TEXT появилась рядом с отказом').not.toContain(CHILD_TURN_ERROR_TEXT);
  });
});
