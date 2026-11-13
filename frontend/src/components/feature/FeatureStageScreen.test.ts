// Экран этапа (D1c): гонка ответов при смене задачи, один запрос диффа на коммит, отказ диффа
// не гасит остальные блоки, metrics = null — блока метрик нет, три блока из ответа.
//
// Мутации (в отчёте): (1) убрать `if (signal.aborted) return` в StageBody → тест гонки красный;
// (2) убрать защиту inFlight в FeatureStageArtifacts → тест «один запрос» красный;
// (3) убрать пометку taskId у состояния → тест гонки красный.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStageScreen } from './FeatureStageScreen';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { StageScreen, StageCommitDiff } from '../../types/featureStage';

const { stageMock, diffMock } = vi.hoisted(() => ({ stageMock: vi.fn(), diffMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, tasks: { ...orig.api.tasks, stage: stageMock, stageCommitDiff: diffMock } } };
});

type FNode = Parameters<typeof findNodeByAttr>[0];

const SHA_A = 'a'.repeat(40);

function screen(taskId: string, over: Partial<StageScreen> = {}): StageScreen {
  return {
    taskId, featureId: 'f1', role: 'step', stepRef: taskId, title: `задача ${taskId}`, status: 'done', stage: null,
    artifacts: {
      commits: [{ sha: SHA_A, shortSha: 'aaaaaaa', subject: `коммит ${taskId}`, at: '2026-11-12T10:00:00Z', files: ['A.cs'], taskId, role: 'step', version: 1 }],
      commitsAvailable: true, linkedFiles: ['A.cs'], resultMarkdown: `итог ${taskId}`,
    },
    input: {
      stage: { templateId: 't', templateVersion: 1, role: 'step', stageId: 's', after: [], prompt: 'промпт этапа', stallMinutes: 30 },
      sessionId: 'c1', transcriptAvailable: true,
      transcript: { prompt: 'постановка', filesRead: ['/x/A.cs'], toolCalls: [{ name: 'Read', summary: '/x/A.cs', at: null, isError: false }], toolCallsTotal: 1, truncated: false },
    },
    review: { own: null, reviews: [{ taskId: 'r1', title: 'ревью', role: 'review', status: 'done', verdict: 'returned', returnReason: null, notes: 'замечание', blocker: true, verifiedAt: null }], awaitingHuman: null },
    metrics: null,
    ...over,
  };
}

const flush = () => new Promise(r => setTimeout(r, 0));
const roots: ReturnType<typeof mount>['root'][] = [];

function render(tasks = [{ id: 'A', title: 'A' }, { id: 'B', title: 'B' }]) {
  installFakeDom();
  const r = mount(createElement(FeatureStageScreen, { tasks, isMobile: false }));
  roots.push(r.root);
  return r;
}

function click(container: FNode, testid: string) {
  const n = findNodeByAttr(container, 'data-testid', testid);
  expect(n, testid).not.toBeNull();
  act(() => { (reactProps(n!)!.onClick as (e: unknown) => void)({ stopPropagation() {}, preventDefault() {} }); });
}

const text = (c: FNode) => textNodes(c).join('|');

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

beforeEach(() => { stageMock.mockReset(); diffMock.mockReset(); });
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('FeatureStageScreen', () => {
  it('рисует артефакты, вход и ревью из ответа', async () => {
    stageMock.mockResolvedValue(screen('A'));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    const t = text(container);
    expect(t).toContain('коммит A');
    expect(t).toContain('итог A');
    expect(t).toContain('/x/A.cs');
    expect(t).toContain('замечание');
    expect(findNodeByAttr(container, 'data-testid', 'stage-screen-picker')).toBeNull(); // одна задача — выбора нет
  });

  it('гонка: ответ этапа A после выбора этапа B не применяется', async () => {
    const a = deferred<StageScreen>();
    stageMock.mockImplementation((id: string) => (id === 'A' ? a.promise : Promise.resolve(screen('B'))));
    const { container } = render();
    click(container, 'stage-screen-pick-B');
    await act(async () => { await flush(); });
    expect(findNodeByAttr(container, 'data-testid', 'stage-screen-body')!.getAttribute('data-task-id')).toBe('B');
    // A отвечает уже после того, как выбрана B
    await act(async () => { a.resolve(screen('A')); await flush(); });
    const body = findNodeByAttr(container, 'data-testid', 'stage-screen-body')!;
    expect(body.getAttribute('data-task-id')).toBe('B');
    expect(text(container)).not.toContain('коммит A');
    expect(stageMock.mock.calls[0][1]).toBeInstanceOf(AbortSignal);
    expect((stageMock.mock.calls[0][1] as AbortSignal).aborted).toBe(true);
  });

  it('metrics = null — блока метрик нет', async () => {
    stageMock.mockResolvedValue(screen('A', { metrics: null }));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    expect(findNodeByAttr(container, 'data-testid', 'stage-screen-body')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-metrics')).toBeNull();
  });

  it('повторный клик по диффу не шлёт второй запрос, пока первый идёт', async () => {
    stageMock.mockResolvedValue(screen('A'));
    const d = deferred<StageCommitDiff>();
    diffMock.mockReturnValue(d.promise);
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    click(container, `stage-commit-toggle-${SHA_A}`); // открыть, запрос ушёл
    click(container, `stage-commit-toggle-${SHA_A}`); // скрыть
    click(container, `stage-commit-toggle-${SHA_A}`); // снова открыть, пока первый в полёте
    expect(diffMock).toHaveBeenCalledTimes(1);
    await act(async () => { d.resolve({ sha: SHA_A, subject: 's', files: ['A.cs'], diff: '@@ -1 +1 @@\n-x\n+y', truncated: false, maxChars: 200000 }); await flush(); });
    expect(findNodeByAttr(container, 'data-testid', `stage-commit-diff-${SHA_A}`)).not.toBeNull();
  });

  it('ошибка диффа показывается в блоке и не гасит остальные блоки', async () => {
    stageMock.mockResolvedValue(screen('A'));
    diffMock.mockRejectedValue(new Error('Коммит не найден'));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    click(container, `stage-commit-toggle-${SHA_A}`);
    await act(async () => { await flush(); });
    expect(text(findNodeByAttr(container, 'data-testid', `stage-commit-error-${SHA_A}`)!)).toContain('Коммит не найден');
    const t = text(container);
    expect(t).toContain('замечание'); // ревью живо
    expect(t).toContain('/x/A.cs');   // вход живой
  });

  it('ошибка загрузки экрана показывается текстом с «Повторить»', async () => {
    stageMock.mockRejectedValue(new Error('Задача не найдена'));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-screen-error')!)).toContain('Задача не найдена');
  });
});
