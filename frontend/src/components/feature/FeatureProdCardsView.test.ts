// Верстка ленты «Производство» (W4-2F2, вариант 2А2): карточка на шаг в порядке
// старта, цепочка узлов «работа → вернул → доработка → принят» с переходами в чаты
// витков, счётчик циклов (2+ — тон предупреждения), обведённый акцентом текущий узел,
// причина возврата строкой под цепочкой. Строки «старт · чат · задача» нет.
//
// Тесты: обычный цикл; возврат + доработка (клик по узлу → чат витка); активная
// задача («сейчас» только над текущей карточкой, «текущий» узел — акцентная рамка,
// пунктирный «ревью» — не узел-действие); пустая фича (карточек нет); шаг без данных
// (код — не кнопка); статус — слово из chain.status бэка; 2+ циклов — тон предупреждения;
// поле касания 40 px на 360.
//
// Мутации (проверялись прогоном, см. отчёт):
//   (1) «текущий» узел без акцентной рамки (cur → обычный тон) → краснеет
//       «текущий узел обведён акцентом»;
//   (2) узел игнорирует chatId (не кнопка) → краснеет «клик по узлу возврата
//       открывает чат этого витка»;
//   (3) cycles < 2 красить предупреждением → краснеет «2 цикла — предупреждение,
//       1 цикл — нет»;
//   (4) 40 px вернуть некликабельной обёртке (кнопка без minHeight) → краснеет
//       «зона касания 40 px — на самой кнопке»;
//   (5) убрать onPointerCancel из обработчиков → краснеет
//       «pointercancel снимает pressed».

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureProdCardsView } from './FeatureProdCardsView';
import { buildNavMarks } from '../../lib/featureNavMarks';
import { C, SHADOW } from '../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun, FeatureTask, FeatureStepChain, FeatureRound, FeatureStage } from '../../types/featureRun';

// === Мок персон: id → имя; аватар — инициалы ===
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) =>
    personaMap.has(id) ? { id, name: personaMap.get(id), avatar: { kind: 'initials' as const } } : undefined,
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/api', () => ({ api: { personas: { avatarUrl: () => '' } } }));
vi.mock('../../lib/toast', () => ({ showToast: (..._a: unknown[]) => {} }));
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
vi.mock('../../lib/openChat', () => ({ openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } }));
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

// === Фикстуры (те же, что в тесте модели) ===

function task(over: Partial<FeatureTask>): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', role: null, stepRef: null, createdAt: null, completedAt: null, claudeStartedAt: null, ...over };
}

function round(over: Partial<FeatureRound>): FeatureRound {
  return { n: 1, verdict: 'accepted', reviewChatId: null, ...over };
}

function chain(over: Partial<FeatureStepChain>): FeatureStepChain {
  return {
    stepRef: 'Ш1', lane: null, title: 'Шаг', personaId: null, taskIds: [], outcome: 'running',
    status: '', durationMinutes: 0, source: null, loops: 0, rounds: [], chatId: null, ...over,
  };
}

function run(over: Partial<FeatureRun>): FeatureRun {
  return {
    featureId: 'F', state: 'running', steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, ...over,
  };
}

// Стадия «Производство» с исполнителями (для чипа модели и ссылок «доработки»).
function prodStage(executors: Array<{ taskId: string; chatId: string | null }>): FeatureStage {
  return {
    key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], runningSince: null, rounds: [],
    longestStepRef: null, firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false,
    executors: executors.map(x => ({
      personaId: 'p-kira', taskId: x.taskId, taskRole: 'rework', stepRef: 'Ш1', lane: null,
      status: 'done', chatId: x.chatId, chatArchived: false, modelLabel: 'Qwen', modelFull: 'qwen-3.8',
    })),
  };
}

// Все узлы по атрибуту (findNodeByAttr даёт только первый).
function findAll(root: unknown, attr: string, value: string): unknown[] {
  const out: unknown[] = [];
  const walk = (x: { nodeType: number; childNodes: unknown[]; getAttribute: (a: string) => string | null }) => {
    if (x.nodeType === 1 && x.getAttribute(attr) === value) out.push(x);
    for (const c of x.childNodes) walk(c as never);
  };
  walk(root as never);
  return out;
}

// Подписи цепочки в порядке разметки: сами узлы и разделители «→».
function chainLabels(card: unknown): string[] {
  const chainEl = findNodeByAttr(card as never, 'data-testid', 'prod-chain')!;
  return chainEl.childNodes.filter(n => n.nodeType === 1).flatMap(n => textNodes(n));
}

function styleOf(node: unknown): Record<string, string> {
  return (reactProps(node as never)!.style ?? {}) as Record<string, string>;
}

// Раскрыть карточку шага кликом по всей карточке (цепочка, меню кода и причина — в раскрытой части).
function expand(container: unknown, stepRef: string) {
  const card = findNodeByAttr(container as never, 'data-testid', `prod-card-${stepRef}`)!;
  act(() => { (reactProps(card)!.onClick as () => void)(); });
}

beforeEach(() => {
  installFakeDom();
  personaMap.set('p-kira', 'Кира');
  personaMap.set('p-gleb', 'Глеб');
  mockOpenChat.calls.length = 0;
  mockOpenTask.calls.length = 0;
});
afterEach(() => { uninstallFakeDom(); });

// === Обычный цикл: работа → принят ===

describe('обычный цикл', () => {
  it('одна карточка на шаг: узлы «работа(ok) → принят(ok)», строки «старт · чат · задача» нет', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stages: [prodStage([{ taskId: 's1', chatId: 'chat-kira' }])],
      stepChains: [chain({
        stepRef: 'Ш1', title: 'Сделать X', personaId: 'p-kira', taskIds: ['s1'], status: 'принят', outcome: 'accepted', chatId: 'chat-kira',
        rounds: [round({ n: 1, verdict: 'accepted', personaId: 'p-gleb', reviewChatId: 'chat-gleb' })],
      })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    expand(container, 'Ш1');
    const card = findNodeByAttr(container, 'data-testid', 'prod-card-Ш1');
    expect(card).not.toBeNull();
    expect(findAll(container, 'data-testid', 'prod-card-Ш1').length).toBe(1);
    // Имя в подписи — из единого резолвера ленты (дефолт — getPersonaById).
    expect(chainLabels(card!)).toEqual(['работа', '→', 'Глеб: принят']);
    // «старт» живёт только в меню кода шага, не строкой карточки.
    expect(textNodes(card!).some(t => t === 'старт')).toBe(false);
    // Статус: слово — из chain.status бэка, тон — из состояния карточки, одной парой (successBg/successText).
    const status = findNodeByAttr(card!, 'data-testid', 'prod-status')!;
    expect(textNodes(status)[0]).toBe('принят');
    expect(styleOf(status).background).toBe(C.successBg);
    expect(styleOf(status).color).toBe(C.successText);
    // Готово — на втором плане: без фона, рамка borderLight, padding SP.sm × SP.md; название приглушено.
    const st = styleOf(card!);
    expect(st.background).toBe('transparent');
    expect(styleOf(findNodeByAttr(card!, 'data-testid', 'prod-title')!).color).toBe(C.textSecondary);
    expect(st.border).toBe(`1px solid ${C.borderLight}`);
    expect(st.padding).toBe('8px 12px');
  });

  it('пустая фича: ни ленты, ни карточек', () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: run({}), featureId: 'F' }));
    expect(findNodeByAttr(container, 'data-testid', 'prod-lane')).toBeNull();
    expect(findAll(container, 'data-testid', 'prod-card-Ш1').length).toBe(0);
  });

  it('узел без чата — не кнопка: обычный текст, без перехода', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({ stepRef: 'Ш1', taskIds: ['s1'], status: 'принят', outcome: 'accepted' })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    expand(container, 'Ш1');
    const plain = findNodeByAttr(container, 'data-testid', 'prod-node-plain');
    expect(plain).not.toBeNull();
    expect(plain!.nodeName).toBe('SPAN');
    expect(findNodeByAttr(container, 'data-testid', 'prod-node')).toBeNull();
  });
});

// === Возврат и доработка ===

describe('возврат и доработка', () => {
  const returnedRun = (loops: number) => run({
    steps: [
      task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
      task({ id: 's2', status: 'done', role: 'rework', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T12:00:00Z' }),
    ],
    stages: [prodStage([{ taskId: 's2', chatId: 'chat-kira-2' }])],
    stepChains: [chain({
      stepRef: 'Ш1', title: 'Сделать X', personaId: 'p-kira', taskIds: ['s1', 's2'], status: 'вернул', outcome: 'returned', loops, chatId: 'chat-kira',
      rounds: [round({ n: 1, verdict: 'returned', personaId: 'p-gleb', reviewChatId: 'chat-gleb', reason: 'нет пустого состояния' })],
    })],
  });

  it('цепочка «работа → вернул → доработка», причина возврата — строкой под цепочкой', () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: returnedRun(1), featureId: 'F' }));
    expand(container, 'Ш1');
    const card = findNodeByAttr(container, 'data-testid', 'prod-card-Ш1')!;
    expect(chainLabels(card)).toEqual(['работа', '→', 'Глеб: вернул', '→', 'доработка']);
    const reason = findNodeByAttr(card, 'data-testid', 'prod-reason')!;
    expect(textNodes(reason)[0]).toBe('нет пустого состояния');
    expect(styleOf(reason).borderLeft).toBe(`2px solid ${C.warning}`);
    expect(styleOf(reason).color).toBe(C.textSecondary);
  });

  it('клик по узлу возврата открывает reviewChatId этого витка', async () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: returnedRun(1), featureId: 'F' }));
    expand(container, 'Ш1');
    const node = findNodeByAttr(container, 'aria-label', 'Открыть чат витка: Глеб: вернул');
    expect(node).not.toBeNull();
    expect(node!.nodeName).toBe('BUTTON');
    await act(async () => { (reactProps(node!)!.onClick as () => void)(); });
    expect(mockOpenChat.calls).toEqual(['chat-gleb']);
  });

  it('один цикл — без предупреждения; два цикла — тон warning', () => {
    const { container: one } = mount(createElement(FeatureProdCardsView, { run: returnedRun(1), featureId: 'F' }));
    const c1 = findNodeByAttr(one, 'data-testid', 'prod-cycles')!;
    expect(textNodes(c1).join('')).toBe('1 цикл');
    expect(styleOf(c1).color).toBe(C.textSecondary);
    expect(styleOf(c1).border).toBe(`1px solid ${C.border}`);

    const { container: two } = mount(createElement(FeatureProdCardsView, { run: returnedRun(2), featureId: 'F' }));
    const c2 = findNodeByAttr(two, 'data-testid', 'prod-cycles')!;
    expect(textNodes(c2).join('')).toBe('2 цикла');
    expect(styleOf(c2).color).toBe(C.warningText);
    expect(styleOf(c2).border).toBe(`1px solid ${C.warning}`);
  });

  // Мутация (1): тон «текущего» без акцентной рамки краснит этот тест.
  it('текущий узел обведён акцентом, а не статусным тоном', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      now: [{ taskId: 's1', stage: 'review', title: 'Ревью', stepRef: 'Ш1' }],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1'], status: 'ждёт ревью', outcome: 'pending', chatId: 'chat-kira',
        rounds: [round({ n: 1, verdict: 'running' })],
      })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    expand(container, 'Ш1');
    const cur = findNodeByAttr(container, 'aria-label', 'Открыть чат витка: работа')!;
    expect(styleOf(cur).border).toBe(`1px solid ${C.accent}`);
    expect(styleOf(cur).boxShadow).toBe(SHADOW.focus);
    expect(styleOf(cur).color).toBe(C.textHeading);
    // Пунктирный «ревью» — не узел-действие и не кнопка.
    const pend = findNodeByAttr(container, 'data-testid', 'prod-node-plain')!;
    expect(styleOf(pend).border).toBe(`1px dashed ${C.dashed}`);
    expect(styleOf(pend).color).toBe(C.textMuted);
  });
});

// === Активная задача ===

describe('активная задача', () => {
  const r = run({
    steps: [
      task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
      task({ id: 's2', status: 'inProgress', stepRef: 'Ш2', claudeStartedAt: '2026-10-21T09:00:00Z' }),
    ],
    now: [{ taskId: 's2', stage: 'prod', title: 'Идущий', stepRef: 'Ш2' }],
    stepChains: [
      chain({ stepRef: 'Ш1', title: 'Ранний', taskIds: ['s1'], status: 'принят', outcome: 'accepted' }),
      chain({ stepRef: 'Ш2', title: 'Идущий', personaId: 'p-kira', taskIds: ['s2'], status: 'идёт', outcome: 'running', chatId: 'chat-kira' }),
    ],
  });

  it('«сейчас» — только над текущей карточкой; идёт — фон bgCard и кромка accent, готово — без фона и кромки', () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    expect(findAll(container, 'data-testid', 'prod-nowline').length).toBe(1);
    const current = findNodeByAttr(container, 'data-testid', 'prod-card-Ш2')!;
    const st = styleOf(current);
    expect(st.background).toBe(C.bgCard);
    expect(st.boxShadow).toBe(`inset 3px 0 0 ${C.accent}`);
    // Карточка без «сейчас» (готова) — без фона и кромки.
    const past = findNodeByAttr(container, 'data-testid', 'prod-card-Ш1')!;
    expect(styleOf(past).background).toBe('transparent');
    expect(styleOf(past).border).toBe(`1px solid ${C.borderLight}`);
    expect(styleOf(past).boxShadow).toBeUndefined();
  });

  it('«ждёт ревью» (не текущая) — кромка info; порядок карточек — по старту', () => {
    const rr = run({
      steps: [
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
        task({ id: 's2', status: 'done', stepRef: 'Ш2', claudeStartedAt: '2026-10-21T09:00:00Z' }),
      ],
      stepChains: [
        chain({ stepRef: 'Ш2', title: 'Ждёт', taskIds: ['s2'], status: 'ждёт ревью', outcome: 'pending' }),
        chain({ stepRef: 'Ш1', title: 'Ранний', taskIds: ['s1'], status: 'принят', outcome: 'accepted' }),
      ],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: rr, featureId: 'F' }));
    const lane = findNodeByAttr(container, 'data-testid', 'prod-lane')!;
    const cards = lane.children.map(c => c.getAttribute('data-testid'));
    expect(cards).toEqual(['prod-card-Ш1', 'prod-card-Ш2']);
    const waiting = findNodeByAttr(container, 'data-testid', 'prod-card-Ш2')!;
    expect(styleOf(waiting).boxShadow).toBe(`inset 3px 0 0 ${C.info}`);
    expect(textNodes(findNodeByAttr(waiting, 'data-testid', 'prod-status')!)[0]).toBe('ждёт ревью');
  });
});

// === Код шага: меню и «не кнопка» ===

describe('код шага', () => {
  it('шаг без данных (не начат): код — обычный текст, не кнопка', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'todo', stepRef: 'Ш5' })],
      stepChains: [chain({ stepRef: 'Ш5', title: 'Не начат', taskIds: ['s1'], status: '', outcome: 'pending' })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    const plain = findNodeByAttr(container, 'data-testid', 'prod-code-plain-Ш5');
    expect(plain).not.toBeNull();
    expect(plain!.nodeName).toBe('SPAN');
    expect(findNodeByAttr(container, 'data-testid', 'prod-code-Ш5')).toBeNull();
  });

  it('клик по коду открывает меню «старт · чат · задача»; клик по «чат» ведёт в чат исполнителя', async () => {
    const r = run({
      epic: { id: 'F', title: 'Фича', status: 'doing', projectId: 'proj-1' } as FeatureRun['epic'],
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stages: [prodStage([{ taskId: 's1', chatId: 'chat-kira' }])],
      stepChains: [chain({ stepRef: 'Ш1', title: 'Сделать X', personaId: 'p-kira', taskIds: ['s1'], status: 'принят', outcome: 'accepted', chatId: 'chat-kira' })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    expand(container, 'Ш1');
    const code = findNodeByAttr(container, 'data-testid', 'prod-code-Ш1')!;
    expect(code.nodeName).toBe('BUTTON');
    expect(code.getAttribute('aria-label')).toBe('Шаг Ш1: старт, чат и задача');
    act(() => { (reactProps(code)!.onClick as () => void)(); });
    // Меню — портал в document.body.
    const body = (globalThis as { document: { body: never } }).document.body as never;
    expect(findAll(body, 'data-testid', 'prod-menu-start').length).toBe(1);
    // Пункт меню — обёртка с кнопкой внутри: кликаем по кнопке.
    const chatItem = findAll(body, 'data-testid', 'prod-menu-chat')[0] as { children: never[] };
    const taskItem = findAll(body, 'data-testid', 'prod-menu-task')[0] as unknown as { children: never[] };
    expect(chatItem).toBeTruthy();
    expect(taskItem).toBeTruthy();
    expect(textNodes(chatItem as never).join('')).toContain('Чат Кира · работа');
    expect(textNodes(taskItem as never).join('')).toContain('Карточка задачи Ш1');
    // Пункт закрывает меню и ведёт в чат — проверяем переход.
    // Обработчики берём до кликов: после первого клика меню закрывается и узлы уходят.
    const clickChat = reactProps(chatItem.children[0] as never)!.onClick as () => void;
    const clickTask = reactProps(taskItem.children[0] as never)!.onClick as () => void;
    await act(async () => { clickChat(); });
    expect(mockOpenChat.calls).toEqual(['chat-kira']);
    // Пункт «Карточка задачи» ведёт в проект эпика, а не в личный «Календарь».
    await act(async () => { clickTask(); });
    expect(mockOpenTask.calls).toEqual([{ id: expect.any(String), projectId: 'proj-1' }]);
  });

  // Минор R4e: пометки меню — только из marks, не безусловно. Мутация «показывать
  // «в архиве»/«задача закрыта» всегда» краснит этот тест: живой чат получил бы пометку.
  it('пометки меню: живой чат — без «в архиве»; закрытая задача — «задача закрыта»', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stages: [prodStage([{ taskId: 's1', chatId: 'chat-live' }])],
      stepChains: [chain({ stepRef: 'Ш1', title: 'Сделать X', personaId: 'p-kira', taskIds: ['s1'], status: 'принят', outcome: 'accepted', chatId: 'chat-live' })],
    });
    // Пометки — из агрегата: задача s1 done → закрыта; чат chat-live не архивирован.
    const marks = buildNavMarks(r);
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F', marks }));
    expand(container, 'Ш1');
    act(() => { (reactProps(findNodeByAttr(container, 'data-testid', 'prod-code-Ш1') as never)!.onClick as () => void)(); });
    const body = (globalThis as { document: { body: never } }).document.body as never;
    const chatItem = findAll(body, 'data-testid', 'prod-menu-chat')[0] as never;
    const taskItem = findAll(body, 'data-testid', 'prod-menu-task')[0] as never;
    expect(chatItem).toBeTruthy();
    expect(taskItem).toBeTruthy();
    // Живой чат: пометки «в архиве» нет — иначе ложная пометка прошла бы незамеченной.
    expect(textNodes(chatItem).join(''), 'живой чат не должен быть помечен «в архиве»').not.toContain('в архиве');
    // Закрытая задача — помечена: пометки действительно текут из marks.
    expect(textNodes(taskItem).join(''), 'закрытая задача должна быть помечена').toContain('задача закрыта');
  });
});

// === Мобильная ширина (360) ===

describe('360', () => {
  it('зона касания 40 px — на самой кнопке; видимая капсула остаётся 20 px, строки не наезжают', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1'], status: 'принят', outcome: 'accepted', chatId: 'chat-kira',
        rounds: [round({ n: 1, verdict: 'accepted', reviewChatId: 'chat-gleb' })],
      })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F', isMobile: true }));
    expand(container, 'Ш1');
    const nodes = findAll(container, 'data-testid', 'prod-node');
    expect(nodes.length).toBe(2);
    for (const n of nodes) {
      // Кнопка сама — поле касания: 40 px, отрицательные отступы — раскладка не смещается.
      const st = styleOf(n as never);
      expect(st.minHeight).toBe(40);
      expect(st.margin).toBe('-10px 0');
      // Видимая капсула — внутренний span: тон и высота узла, как на десктопе.
      const pill = (n as { childNodes: unknown[] }).childNodes[0];
      expect(styleOf(pill as never).lineHeight).toBe('20px');
    }
    // Перенос цепочки: вертикальный зазор строк 20 px = 2×выступа 10 px — зоны
    // соседних строк не соприкасаются; отступ от шапки 12 px > выступа.
    const chainEl = findNodeByAttr(container, 'data-testid', 'prod-chain')!;
    expect(styleOf(chainEl as never).gap).toBe('20px 4px');
    expect(styleOf(chainEl as never).marginTop).toBe(12);
    // Строка 1 — иконка, код, название (одна строка с обрезкой), шеврон с зоной касания 40 px.
    const row1 = findNodeByAttr(container, 'data-testid', 'prod-row1')!;
    expect(styleOf(findNodeByAttr(row1, 'data-testid', 'prod-title')!).whiteSpace).toBe('nowrap');
    expect(styleOf(row1.children[3] as never).width).toBe(40);
  });

  // Минор R4f: сход пальца с узла — pointerup вне узла, браузер посылает узлу
  // pointercancel. Без него pressed залипает. Здесь вызываем обработчик узла
  // напрямую: fakeDom не синтезирует pointercancel из pointerup на другом элементе.
  it('pointercancel (палец сошёл с узла) снимает pressed — залипания нет', () => {
    const r = run({
      steps: [task({ id: 's2', status: 'inProgress', stepRef: 'Ш2', claudeStartedAt: '2026-10-21T09:00:00Z' })],
      stepChains: [chain({ stepRef: 'Ш2', title: 'Идущий', personaId: 'p-kira', taskIds: ['s2'], status: 'идёт', outcome: 'running', chatId: 'chat-kira' })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F', isMobile: true }));
    expand(container, 'Ш2');
    const node = findNodeByAttr(container, 'data-testid', 'prod-node')!;
    const pill = (node as { childNodes: unknown[] }).childNodes[0];
    const handlers = reactProps(node)!;
    // Нажатие: «текущий» узел подсвечен (bgSelected).
    act(() => { (handlers.onPointerDown as () => void)(); });
    expect(styleOf(pill).background).toBe(C.bgSelected);
    // Палец сошёл: pointerup не на узле — узел получает pointercancel.
    act(() => { (handlers.onPointerCancel as () => void)(); });
    // Залипания нет: капсула вернулась в свой тон.
    expect(styleOf(pill).background).toBe(C.bgWhite);
  });
});

// === Макет A2: состояние видно сразу ===

describe('состояние шага (A2)', () => {
  const five = () => run({
    steps: [
      task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
      task({ id: 's2', status: 'inProgress', stepRef: 'Ш2', claudeStartedAt: new Date(Date.now() - 40 * 60000).toISOString() }),
      task({ id: 's3', status: 'done', stepRef: 'Ш3', claudeStartedAt: '2026-10-22T10:00:00Z' }),
      task({ id: 's4', status: 'todo', stepRef: 'Ш4' }),
    ],
    stepProgress: { planState: 'cut', total: 4, accepted: 1, review: 1, work: 1, notCreated: 1, withdrawn: 0, unlinkedReworks: 0 },
    stepChains: [
      chain({ stepRef: 'Ш1', title: 'А', taskIds: ['s1'], status: 'принят', outcome: 'accepted' }),
      chain({ stepRef: 'Ш2', title: 'Б', taskIds: ['s2'], status: 'идёт', outcome: 'running' }),
      chain({ stepRef: 'Ш3', title: 'В', taskIds: ['s3'], status: 'ждёт ревью', outcome: 'pending' }),
      chain({ stepRef: 'Ш4', title: 'Г', taskIds: ['s4'], status: '', outcome: 'pending' }),
    ],
  });

  it('плашка «закрыт без повторного ревью» — слово берётся из chain.status бэка', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepProgress: { planState: 'cut', total: 1, accepted: 0, review: 0, work: 1, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 },
      stepChains: [chain({ stepRef: 'Ш1', title: 'А', taskIds: ['s1'], status: 'ждёт повторного ревью', outcome: 'returned', loops: 1, rounds: [round({ n: 1, verdict: 'returned' })] })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    const b = findNodeByAttr(findNodeByAttr(container, 'data-testid', 'prod-card-Ш1')!, 'data-testid', 'prod-status')!;
    expect(textNodes(b).join('')).toContain('ждёт повторного ревью');
    // Сводка не противоречит карточке: шаг в корзине work без идущей задачи — «ждёт», не «идёт».
    expect(textNodes(findNodeByAttr(container, 'data-testid', 'prod-summary')!).join('')).toBe('1 ждёт');
  });

  it('иконка, плашка и тон карточки различают четыре состояния', () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: five(), featureId: 'F' }));
    const icon = (ref: string) => findNodeByAttr(findNodeByAttr(container, 'data-testid', `prod-card-${ref}`)!, 'data-testid', 'prod-state-icon')!;
    const badge = (ref: string) => findNodeByAttr(findNodeByAttr(container, 'data-testid', `prod-card-${ref}`)!, 'data-testid', 'prod-status')!;
    const card = (ref: string) => styleOf(findNodeByAttr(container, 'data-testid', `prod-card-${ref}`)!);
    expect(['Ш1', 'Ш2', 'Ш3', 'Ш4'].map(r => icon(r).getAttribute('data-state'))).toEqual(['done', 'run', 'wait', 'queue']);
    expect(icon('Ш1').getAttribute('aria-label')).toBe('Готово');
    expect(icon('Ш3').getAttribute('aria-label')).toBe('Ждёт');
    expect(icon('Ш4').getAttribute('aria-label')).toBe('В очереди');
    // Идёт — дышащая точка (LiveDot с подписью состояния).
    expect(findNodeByAttr(icon('Ш2'), 'aria-label', 'Идёт')).not.toBeNull();
    // Плашка: слово + чем занят + сколько (времени приёма/ожидания в агрегате нет — только слово).
    expect(textNodes(badge('Ш1')).join('')).toBe('принят');
    expect(textNodes(badge('Ш2')).join('')).toBe('идёт · работа · 40 мин');
    expect(textNodes(badge('Ш3')).join('')).toBe('ждёт ревью');
    expect(textNodes(badge('Ш4')).join('')).toBe('в очереди');
    // Тон: готово/очередь без фона, очередь — пунктирная рамка, идёт — accent, ждёт — info.
    expect(card('Ш1').background).toBe('transparent');
    expect(card('Ш4').background).toBe('transparent');
    expect(card('Ш4').border).toBe(`1px dashed ${C.dashed}`);
    expect(card('Ш2').boxShadow).toBe(`inset 3px 0 0 ${C.accent}`);
    expect(card('Ш3').boxShadow).toBe(`inset 3px 0 0 ${C.info}`);
  });

  it('есть waitingSince/acceptedAt — плашка с временем', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [
        chain({ stepRef: 'Ш1', taskIds: ['s1'], status: 'принят', outcome: 'accepted', acceptedAt: '2026-10-23T12:00:00Z' }),
        chain({ stepRef: 'Ш2', taskIds: ['s1'], status: 'ждёт ревью', outcome: 'pending', waitingSince: new Date(Date.now() - 15 * 60000).toISOString() }),
      ],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    const text = (ref: string) => textNodes(findNodeByAttr(findNodeByAttr(container, 'data-testid', `prod-card-${ref}`)!, 'data-testid', 'prod-status')!).join('');
    expect(text('Ш1')).toBe('принят · 23.10');
    expect(text('Ш2')).toBe('ждёт ревью · 15 мин');
  });

  it('мини-трек: сегмент на узел — ok/ret/cur/pend', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }), task({ id: 's2', status: 'inProgress', role: 'rework', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T12:00:00Z' })],
      stepChains: [chain({ stepRef: 'Ш1', taskIds: ['s1', 's2'], status: 'идёт', outcome: 'running', loops: 1, rounds: [round({ n: 1, verdict: 'returned' })] })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    const track = findNodeByAttr(container, 'data-testid', 'prod-track')!;
    expect(track.children.map(c => c.getAttribute('data-testid'))).toEqual(['prod-seg-ok', 'prod-seg-ret', 'prod-seg-cur']);
    const cur = track.children[2] as never;
    expect(reactProps(cur)!.className).toBe('cc-live-dot');
    expect(styleOf(cur).width).toBe(12);
    expect(styleOf(cur).height).toBe(6);
    expect(styleOf(track.children[1] as never).background).toBe(C.warning);
  });

  it('сводка этапа — числа из stepProgress и полоса сегментов', () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: five(), featureId: 'F' }));
    const sum = findNodeByAttr(container, 'data-testid', 'prod-summary')!;
    expect(textNodes(sum).join('')).toBe('1 готово · 1 идёт · 1 ждёт · 1 в очереди');
    expect(findAll(sum, 'data-testid', 'prod-summary-seg').length).toBe(4);
    // Без stepProgress сводки нет.
    const { container: c2 } = mount(createElement(FeatureProdCardsView, { run: { ...five(), stepProgress: undefined }, featureId: 'F' }));
    expect(findNodeByAttr(c2, 'data-testid', 'prod-summary')).toBeNull();
  });

  it('карточка целиком — кнопка с aria-expanded: клик раскрывает и сворачивает, клик по узлу не сворачивает', async () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({ stepRef: 'Ш1', title: 'Сделать X', taskIds: ['s1'], status: 'принят', outcome: 'accepted', chatId: 'c', rounds: [round({ n: 1, verdict: 'accepted', reviewChatId: 'chat-gleb' })] })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    const card = () => findNodeByAttr(container, 'data-testid', 'prod-card-Ш1')!;
    expect(card().getAttribute('role')).toBe('button');
    expect(card().getAttribute('aria-label')).toBe('Шаг Ш1, Готово: Сделать X');
    expect(card().getAttribute('aria-expanded')).toBe('false');
    expect(findNodeByAttr(container, 'data-testid', 'prod-chain')).toBeNull();
    // Space/Enter на самой карточке раскрывают.
    const prevent = vi.fn();
    act(() => { (reactProps(card())!.onKeyDown as (e: unknown) => void)({ key: 'Enter', target: card(), currentTarget: card(), preventDefault: prevent }); });
    expect(card().getAttribute('aria-expanded')).toBe('true');
    expect(findNodeByAttr(container, 'data-testid', 'prod-chain')).not.toBeNull();
    // Клик по узлу цепочки (всплывает в раскрытую часть) карточку не сворачивает.
    const stop = vi.fn();
    act(() => { (reactProps(findNodeByAttr(container, 'data-testid', 'prod-details')!)!.onClick as (e: unknown) => void)({ stopPropagation: stop }); });
    expect(stop).toHaveBeenCalled();
    act(() => { (reactProps(card())!.onClick as () => void)(); });
    expect(card().getAttribute('aria-expanded')).toBe('false');
  });
});
