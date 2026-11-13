// Блок «Вопросы к вам» (F13): карточка, отвеченная запись, «Ответили в чате», пусто, и защита
// записи: один POST на вопрос при двух быстрых кликах и двух экземплярах, ответ после ухода с
// этапа не применяется, отказ POST показывается под полем.
//
// Мутации (в отчёте): (1) убрать inFlight.has(q.id) → тест «два клика — одна отправка» красный;
// (2) заменить guard на состояние экземпляра → тест «два экземпляра» красный; (3) убрать
// alive.current перед onAnswered → тест «после ухода» красный; (4) убрать .catch → тест
// «отказ» красный; (5) answerText без ветки пустого текста → тест «Ответили в чате» красный.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStageQuestions, answerText } from './FeatureStageQuestions';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { StageQuestion } from '../../types/featureStage';

const { answerMock } = vi.hoisted(() => ({ answerMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, tasks: { ...orig.api.tasks, answerStageQuestion: answerMock } } };
});

type FNode = Parameters<typeof findNodeByAttr>[0];

const OPEN: StageQuestion = {
  id: 'q1', status: 'open', askedBy: { personaId: 'p', name: 'Глеб' }, askedAt: '2026-11-12T17:20:00',
  text: 'Оставляем несъёмным?', waitedMinutes: 35, answer: null,
};
const DONE: StageQuestion = {
  id: 'q2', status: 'answered', askedBy: { personaId: 'p2', name: 'Тимур' }, askedAt: '2026-11-12T10:00:00',
  text: 'Слепок пишем и для rework?', waitedMinutes: 5, answer: { text: 'Да, текущая версия', by: 'u', at: '2026-11-12T10:05:00' },
};

const roots: ReturnType<typeof mount>['root'][] = [];
const flush = () => new Promise(r => setTimeout(r, 0));
const text = (c: FNode) => textNodes(c).join('');

function render(questions: StageQuestion[], onAnswered = vi.fn(), taskId = 'T') {
  installFakeDom();
  const r = mount(createElement(FeatureStageQuestions, { taskId, questions, isMobile: false, onAnswered }));
  roots.push(r.root);
  return { ...r, onAnswered };
}

function findTag(c: FNode, tag: string): FNode | null {
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && (x as unknown as { nodeName: string }).nodeName.toLowerCase() === tag) { hit = x; return; }
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c);
  return hit;
}

function type(c: FNode, value: string) {
  const ta = findTag(c, 'textarea');
  expect(ta).not.toBeNull();
  act(() => { (reactProps(ta!)!.onChange as (e: unknown) => void)({ target: { value } }); });
}

function clickSend(c: FNode) {
  const n = findNodeByAttr(c, 'data-testid', 'stage-question-send');
  expect(n).not.toBeNull();
  act(() => { (reactProps(n!)!.onClick as (e: unknown) => void)({ stopPropagation() {}, preventDefault() {} }); });
}

beforeEach(() => { answerMock.mockReset(); });
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('FeatureStageQuestions', () => {
  it('открытый вопрос — карточка с тегом, автором, ожиданием, текстом и «Ответить»', () => {
    const { container } = render([OPEN]);
    const t = text(container);
    expect(t).toContain('вопрос');
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-question-when')!)).toBe('Глеб · 12.11 17:20 · ждёт 35 мин');
    expect(t).toContain('Оставляем несъёмным?');
    expect(t).toContain('Ответить');
  });

  it('отвеченный — запись «отвечено» с вопросом и ответом', () => {
    const { container } = render([DONE]);
    const t = text(container);
    expect(t).toContain('отвечено');
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-question-answered-text')!)).toBe('Тимур: «Слепок пишем и для rework?» — «Да, текущая версия»');
  });

  it('ответ без текста — «Ответили в чате», без пустых кавычек и null', () => {
    const q: StageQuestion = { ...DONE, answer: { text: '', by: null, at: '2026-11-12T10:05:00' } };
    expect(answerText(q)).toBe('Ответили в чате');
    const { container } = render([q]);
    const t = text(findNodeByAttr(container, 'data-testid', 'stage-question-answered-text')!);
    expect(t).toBe('Тимур: «Слепок пишем и для rework?» — Ответили в чате');
    expect(t).not.toContain('null');
    expect(t).not.toContain('«»');
  });

  it('вопросов нет — пустое состояние', () => {
    const { container } = render([]);
    expect(text(container)).toContain('Вопросов к вам нет');
  });

  it('два быстрых клика — одна отправка, после успеха экран перезапрашивается', async () => {
    let resolve!: () => void;
    answerMock.mockReturnValue(new Promise<void>(r => { resolve = r; }));
    const { container, onAnswered } = render([OPEN]);
    type(container, ' Да ');
    clickSend(container);
    clickSend(container);
    expect(answerMock).toHaveBeenCalledTimes(1);
    expect(answerMock).toHaveBeenCalledWith('T', 'q1', 'Да');
    await act(async () => { resolve(); await flush(); });
    expect(onAnswered).toHaveBeenCalledTimes(1);
  });

  it('два экземпляра блока на один вопрос — тоже одна отправка (guard по id)', async () => {
    answerMock.mockReturnValue(new Promise(() => {}));
    const a = render([OPEN]);
    const b = render([OPEN]);
    type(a.container, 'один'); type(b.container, 'два');
    clickSend(a.container);
    clickSend(b.container);
    expect(answerMock).toHaveBeenCalledTimes(1);
  });

  it('ответ, пришедший после ухода с этапа, не применяется', async () => {
    let resolve!: () => void;
    answerMock.mockReturnValue(new Promise<void>(r => { resolve = r; }));
    const { container, root, onAnswered } = render([{ ...OPEN, id: 'q-stale' }]);
    type(container, 'ответ');
    clickSend(container);
    act(() => { root.unmount(); });
    await act(async () => { resolve(); await flush(); });
    expect(onAnswered).not.toHaveBeenCalled();
  });

  it('отказ POST — ошибка под полем, без необработанного исключения, повтор возможен', async () => {
    answerMock.mockRejectedValueOnce(new Error('Вопрос уже отвечен'));
    const { container, onAnswered } = render([{ ...OPEN, id: 'q-fail' }]);
    type(container, 'ответ');
    clickSend(container);
    await act(async () => { await flush(); });
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-question-error')!)).toBe('Вопрос уже отвечен');
    expect(onAnswered).not.toHaveBeenCalled();
    answerMock.mockResolvedValueOnce(undefined);
    clickSend(container);
    await act(async () => { await flush(); });
    expect(answerMock).toHaveBeenCalledTimes(2);
    expect(onAnswered).toHaveBeenCalledTimes(1);
  });
});
