// Верстка ленты «Производство» (W4-2F2, вариант 2А2): карточка на шаг в порядке
// старта, цепочка узлов «работа → вернул → доработка → принят» с переходами в чаты
// витков, счётчик циклов (2+ — тон предупреждения), обведённый акцентом текущий узел,
// причина возврата строкой под цепочкой. Строки «старт · чат · задача» нет.
//
// Тесты: обычный цикл; возврат + доработка (клик по узлу → чат витка); активная
// задача («сейчас» только над текущей карточкой, «текущий» узел — акцентная рамка,
// пунктирный «ревью» — не узел-действие); пустая фича (карточек нет); шаг без данных
// (код — не кнопка); статус — из stepStatusView; 2+ циклов — тон предупреждения;
// поле касания 40 px на 360.
//
// Мутации (проверялись прогоном, см. отчёт):
//   (1) «текущий» узел без акцентной рамки (cur → обычный тон) → краснеет
//       «текущий узел обведён акцентом»;
//   (2) узел игнорирует chatId (не кнопка) → краснеет «клик по узлу возврата
//       открывает чат этого витка»;
//   (3) cycles < 2 красить предупреждением → краснеет «2 цикла — предупреждение,
//       1 цикл — нет».

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureProdCardsView } from './FeatureProdCardsView';
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
    longestStepRef: null, firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false,
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
    const card = findNodeByAttr(container, 'data-testid', 'prod-card-Ш1');
    expect(card).not.toBeNull();
    expect(findAll(container, 'data-testid', 'prod-card-Ш1').length).toBe(1);
    expect(chainLabels(card!)).toEqual(['работа', '→', 'принят']);
    // «старт» живёт только в меню кода шага, не строкой карточки.
    expect(textNodes(card!).some(t => t === 'старт')).toBe(false);
    // Статус — из stepStatusView: подпись и тон одной парой (successBg/successText).
    const status = findNodeByAttr(card!, 'data-testid', 'prod-status')!;
    expect(textNodes(status)[0]).toBe('принят');
    expect(styleOf(status).background).toBe(C.successBg);
    expect(styleOf(status).color).toBe(C.successText);
    // Карточка: фон C.bgCard, рамка borderLight, радиус R.xl, padding SP.sm × SP.md.
    const st = styleOf(card!);
    expect(st.background).toBe(C.bgCard);
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
    const card = findNodeByAttr(container, 'data-testid', 'prod-card-Ш1')!;
    expect(chainLabels(card)).toEqual(['работа', '→', 'вернул', '→', 'доработка']);
    const reason = findNodeByAttr(card, 'data-testid', 'prod-reason')!;
    expect(textNodes(reason)[0]).toBe('нет пустого состояния');
    expect(styleOf(reason).borderLeft).toBe(`2px solid ${C.warning}`);
    expect(styleOf(reason).color).toBe(C.textSecondary);
  });

  it('клик по узлу возврата открывает reviewChatId этого витка', async () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: returnedRun(1), featureId: 'F' }));
    const node = findNodeByAttr(container, 'aria-label', 'Открыть чат витка: вернул');
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

  it('«сейчас» — только над текущей карточкой; её рамка accentMuted и кромка accent', () => {
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    expect(findAll(container, 'data-testid', 'prod-nowline').length).toBe(1);
    const current = findNodeByAttr(container, 'data-testid', 'prod-card-Ш2')!;
    const st = styleOf(current);
    expect(st.border).toBe(`1px solid ${C.accentMuted}`);
    expect(st.boxShadow).toBe(`inset 3px 0 0 ${C.accent}`);
    // Карточка без «сейчас» — обычная рамка, без кромки.
    const past = findNodeByAttr(container, 'data-testid', 'prod-card-Ш1')!;
    expect(styleOf(past).border).toBe(`1px solid ${C.borderLight}`);
    expect(styleOf(past).boxShadow).toBeUndefined();
  });

  it('«ждёт ревью» (не текущая) — кромка info; порядок карточек — по старту', () => {
    const rr = run({
      steps: [
        task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' }),
        task({ id: 's2', status: 'inProgress', stepRef: 'Ш2', claudeStartedAt: '2026-10-21T09:00:00Z' }),
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
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stages: [prodStage([{ taskId: 's1', chatId: 'chat-kira' }])],
      stepChains: [chain({ stepRef: 'Ш1', title: 'Сделать X', personaId: 'p-kira', taskIds: ['s1'], status: 'принят', outcome: 'accepted', chatId: 'chat-kira' })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F' }));
    const code = findNodeByAttr(container, 'data-testid', 'prod-code-Ш1')!;
    expect(code.nodeName).toBe('BUTTON');
    expect(code.getAttribute('aria-label')).toBe('Шаг Ш1: старт, чат и задача');
    act(() => { (reactProps(code)!.onClick as () => void)(); });
    // Меню — портал в document.body.
    const body = (globalThis as { document: { body: never } }).document.body as never;
    expect(findAll(body, 'data-testid', 'prod-menu-start').length).toBe(1);
    // Пункт меню — обёртка с кнопкой внутри: кликаем по кнопке.
    const chatItem = findAll(body, 'data-testid', 'prod-menu-chat')[0] as { children: never[] };
    const taskItem = findAll(body, 'data-testid', 'prod-menu-task')[0];
    expect(chatItem).toBeTruthy();
    expect(taskItem).toBeTruthy();
    expect(textNodes(chatItem as never).join('')).toContain('Чат Кира · работа');
    expect(textNodes(taskItem as never).join('')).toContain('Карточка задачи Ш1');
    // Пункт закрывает меню и ведёт в чат — проверяем переход.
    await act(async () => { (reactProps(chatItem.children[0] as never)!.onClick as () => void)(); });
    expect(mockOpenChat.calls).toEqual(['chat-kira']);
  });
});

// === Мобильная ширина (360) ===

describe('360', () => {
  it('у узла-кнопки невидимое поле касания 40 px по вертикали, видимая высота не раздувается', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done', stepRef: 'Ш1', claudeStartedAt: '2026-10-20T10:00:00Z' })],
      stepChains: [chain({
        stepRef: 'Ш1', taskIds: ['s1'], status: 'принят', outcome: 'accepted', chatId: 'chat-kira',
        rounds: [round({ n: 1, verdict: 'accepted', reviewChatId: 'chat-gleb' })],
      })],
    });
    const { container } = mount(createElement(FeatureProdCardsView, { run: r, featureId: 'F', isMobile: true }));
    const nodes = findAll(container, 'data-testid', 'prod-node');
    expect(nodes.length).toBe(2);
    for (const n of nodes) {
      // Обёртка-поле: 40 px высоты, отрицательные отступы — раскладка не смещается.
      const wrap = (n as { parentNode: { nodeType: number; getAttribute: (a: string) => string | null } }).parentNode;
      expect(wrap.nodeType).toBe(1);
      expect(styleOf(wrap as never).height).toBe(40);
      expect(styleOf(wrap as never).margin).toBe('-10px 0');
      expect(styleOf(n as never).lineHeight).toBe('20px');
    }
    // Название уходит на вторую строку шапки (flexBasis 100%).
    const header = findNodeByAttr(container, 'data-testid', 'prod-card-Ш1')!.children[0];
    const title = header.children[1];
    expect(styleOf(title as never).flex).toBe('1 1 100%');
  });
});
