// Экран этапа (D1c/D1e, вариант A): гонка ответов при смене задачи, один запрос диффа на коммит,
// отказ диффа не гасит экран, metrics = null — вкладки «Метрики» нет.
// Вкладки: активна одна — остальных блоков в DOM нет; сводка — над полосой при любой вкладке
// (порядок проверяется относительно самой полосы, а не только относительно её содержимого);
// при выборе другой задачи открыта первая вкладка; полоса на 360 скроллится внутри себя без
// видимой прокрутки (класс + инжект стиля), активная вкладка докручивается — и заново при
// смене задачи (deps эффекта помечены рендером состояния).
//
// Мутации (в отчёте): (1) убрать `if (signal.aborted) return` в StageBody → тест гонки красный;
// (2) убрать защиту inFlight в FeatureStageArtifacts → тест «один запрос» красный;
// (3) убрать пометку taskId у состояния → тест гонки красный;
// (4) убрать пометку taskId у выбора вкладки → тест «первая вкладка» красный;
// (5) убрать сводку из StageBody → тест «сводка над полосой» красный;
// (6) показывать четыре вкладки независимо от metrics → тест «Метрики при metrics = null» красный;
// (7) вернуть Date.now() в SummaryKpis вместо useNow → тест «живое ждёт вас» красный: после
//     клика по вкладке сводка считает по часам рендера («3 ч 02 мин») вместо последнего тика;
// (8) не передать nowMs в FeatureStageMetrics → тест «живое ждёт вас» красный: метрики считают
//     по Date.now() момента клика («3 ч 02 мин») и расходятся со сводкой («3 ч 01 мин»);
// (9) перенести сводку под Tabs в StageBody → тест «сводка над полосой» красный: порядок детей
//     body меняется (сводка за tablist);
// (10) не добавить NO_SB_CLASS в className у Tabs → тест «без видимой прокрутки» красный:
//     класса на tablist нет;
// (11) вернуть deps эффекта докрутки к [tabSel, isMobile] (без метки рендера) → тест
//     «докручивается при смене задачи» красный: после возврата к задаче полоса недокручена.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureStageScreen } from './FeatureStageScreen';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { StageScreen, StageCommitDiff } from '../../types/featureStage';
import type { StageMetrics } from '../../types/featureStats';

const { stageMock, diffMock } = vi.hoisted(() => ({ stageMock: vi.fn(), diffMock: vi.fn() }));
vi.mock('../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/api')>();
  return { ...orig, api: { ...orig.api, tasks: { ...orig.api.tasks, stage: stageMock, stageCommitDiff: diffMock } } };
});

type FNode = Parameters<typeof findNodeByAttr>[0];

const SHA_A = 'a'.repeat(40);

const METRICS: StageMetrics = {
  mode: 'journal',
  window: { from: '2026-11-12T10:00:00Z', to: '2026-11-12T12:30:00Z', live: false, elapsedMinutes: 150 },
  executor: { minutes: 75, live: false, turns: 48, errorTurns: null, tokens: null, costUsd: null, costPartial: false, hiddenCostModels: [], models: [] },
  review: null,
  human: { minutes: 35, waits: 1, replies: null, waitingNow: null },
  idleMinutes: null,
  spendFrom: null,
};

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
    questions: [],
    ...over,
  };
}

const flush = () => new Promise(r => setTimeout(r, 0));
const roots: ReturnType<typeof mount>['root'][] = [];

function render(tasks: { id: string; title: string; stepRef?: string }[] = [{ id: 'A', title: 'A' }, { id: 'B', title: 'B' }], isMobile = false, chrome?: Parameters<typeof FeatureStageScreen>[0]['chrome']) {
  installFakeDom();
  const r = mount(createElement(FeatureStageScreen, { tasks, isMobile, chrome }));
  roots.push(r.root);
  return r;
}

function click(container: FNode, testid: string) {
  const n = findNodeByAttr(container, 'data-testid', testid);
  expect(n, testid).not.toBeNull();
  act(() => { (reactProps(n!)!.onClick as (e: unknown) => void)({ stopPropagation() {}, preventDefault() {} }); });
}

// Кнопка вкладки (role="tab") по её подписи; null — нет (тот же приём, что у полосы страницы фичи).
function findTab(c: FNode, label: string): FNode | null {
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.getAttribute?.('role') === 'tab' && textNodes(x).join('').includes(label)) { hit = x; return; }
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c);
  return hit;
}

function clickTab(c: FNode, label: string) {
  const t = findTab(c, label);
  expect(t, `вкладка ${label}`).not.toBeNull();
  act(() => { (reactProps(t!)!.onClick as (e: unknown) => void)({ stopPropagation() {}, preventDefault() {} }); });
}

const text = (c: FNode) => textNodes(c).join('|');

// Текст ряда ReviewKv по подписи (FeatureReviewParts: каждый ряд — отдельный узел).
// null — блока или ряда нет.
function kvRow(c: FNode, blockTestid: string, label: string): string | null {
  const block = findNodeByAttr(c, 'data-testid', blockTestid);
  if (!block) return null;
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.getAttribute?.('data-testid') === 'review-kv-row' && textNodes(x).join('').includes(label)) { hit = x; return; }
    for (const ch of x.childNodes) walk(ch);
  };
  walk(block);
  return hit ? textNodes(hit).join('') : null;
}

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
  it('открыт один блок: первая вкладка — «Ревью и вопросы», артефактов и входа в DOM нет', async () => {
    stageMock.mockResolvedValue(screen('A'));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    const t = text(container);
    expect(t).toContain('замечание');
    expect(findNodeByAttr(container, 'data-testid', 'stage-review')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-input')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-artifacts')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-screen-picker')).toBeNull(); // одна задача — выбора нет
  });

  it('переключение вкладок: активен один блок, остальные не отрисованы', async () => {
    stageMock.mockResolvedValue(screen('A'));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    clickTab(container, 'Вход');
    expect(findNodeByAttr(container, 'data-testid', 'stage-input')).not.toBeNull();
    expect(text(container)).toContain('/x/A.cs');
    expect(findNodeByAttr(container, 'data-testid', 'stage-artifacts')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-review')).toBeNull();
    clickTab(container, 'Артефакты');
    expect(findNodeByAttr(container, 'data-testid', 'stage-artifacts')).not.toBeNull();
    expect(text(container)).toContain('коммит A');
    expect(findNodeByAttr(container, 'data-testid', 'stage-input')).toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-review')).toBeNull();
  });

  it('сводка — над полосой вкладок и видна при любой открытой вкладке', async () => {
    stageMock.mockResolvedValue(screen('A', { metrics: METRICS }));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    clickTab(container, 'Артефакты');
    const summary = findNodeByAttr(container, 'data-testid', 'stage-screen-summary');
    expect(summary).not.toBeNull();
    const t = text(container);
    // сводка стоит раньше и названия задачи, и содержимого открытого блока
    // (stepRef и title — соседние текстовые узлы, в склейке разделены «|»)
    expect(t).toContain('A · ');
    expect(t).toContain('задача A');
    expect(t).toContain('готово');
    expect(t.indexOf('задача A'), 'сводка над содержимым вкладки').toBeLessThan(t.indexOf('коммит A'));
    // ключевые цифры — из metrics как есть
    expect(t).toContain('Время');
    expect(t).toContain('Ходы');
    expect(t).toContain('Ждёт вас');
    // порядок — относительно самой полосы: сводка стоит раньше tablist среди детей body
    // (мутация «перенести сводку под вкладки» — red: порядок детей меняется)
    const kids = findNodeByAttr(container, 'data-testid', 'stage-screen-body')!.children;
    const iSummary = kids.findIndex(k => k.getAttribute('data-testid') === 'stage-screen-summary');
    const iTabs = kids.findIndex(k => k.getAttribute('data-testid') === 'stage-screen-tabs');
    expect(iSummary, 'сводки нет среди детей body').toBeGreaterThanOrEqual(0);
    expect(iTabs, 'полосы нет среди детей body').toBeGreaterThanOrEqual(0);
    expect(iSummary, 'сводка обязана стоять перед полосой вкладок').toBeLessThan(iTabs);
  });

  it('metrics = null — вкладки «Метрики» нет и блока метрик нет', async () => {
    stageMock.mockResolvedValue(screen('A', { metrics: null }));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    expect(findNodeByAttr(container, 'data-testid', 'stage-screen-body')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-metrics')).toBeNull();
    expect(findTab(container, 'Метрики')).toBeNull();
    expect(text(container)).not.toContain('Метрики');
    // сводка без цифр остаётся: название и состояние
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-screen-summary')!)).toContain('задача A');
    expect(findNodeByAttr(container, 'data-testid', 'stage-screen-kpis')).toBeNull();
  });

  it('metrics есть — вкладка «Метрики» открывается как блок', async () => {
    stageMock.mockResolvedValue(screen('A', { metrics: METRICS }));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    clickTab(container, 'Метрики');
    expect(findNodeByAttr(container, 'data-testid', 'stage-metrics')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-artifacts')).toBeNull();
  });

  it('выбор другой задачи открывает первую вкладку, а не вкладку прошлой задачи', async () => {
    stageMock.mockImplementation((id: string) => Promise.resolve(screen(id)));
    const { container } = render();
    await act(async () => { await flush(); });
    clickTab(container, 'Артефакты'); // у A открыты «Артефакты»
    expect(text(container)).toContain('коммит A');
    click(container, 'stage-screen-pick-B');
    await act(async () => { await flush(); });
    const body = findNodeByAttr(container, 'data-testid', 'stage-screen-body')!;
    expect(body.getAttribute('data-task-id')).toBe('B');
    expect(text(container)).toContain('замечание');
    expect(text(container)).not.toContain('коммит B'); // чужая вкладка не перешла
    expect(findTab(container, 'Ревью')!.getAttribute('aria-selected')).toBe('true');
  });

  it('полоса на 360: горизонтальный скролл без видимой прокрутки — класс на tablist и инжект стиля (R-D1e)', async () => {
    stageMock.mockResolvedValue(screen('A'));
    // Инжект — побочный эффект загрузки модуля: при статическом импорте в тестовом окружении
    // (node) document ещё нет. Перегрузить модуль при установленном fake-DOM — как браузер
    // при загрузке страницы (тот же приём с vi.resetModules, что в App.deeplink.test).
    installFakeDom();
    vi.resetModules();
    const { FeatureStageScreen: FreshScreen } = await import('./FeatureStageScreen');
    const r = mount(createElement(FreshScreen, { tasks: [{ id: 'A', title: 'A' }], isMobile: true }));
    roots.push(r.root);
    await act(async () => { await flush(); });
    const style = document.getElementById('cc-stage-screen-tabs-style');
    expect(style, 'инжекта стиля полосы нет').not.toBeNull();
    expect(style!.textContent).toContain('.cc-stage-screen-tabs::-webkit-scrollbar{display:none;}');
    expect(style!.textContent).toContain('.cc-stage-screen-tabs{scrollbar-width:none;}');
    const tablist = findNodeByAttr(r.container, 'role', 'tablist');
    expect(tablist).not.toBeNull();
    expect(tablist!.getAttribute('class'), 'класса без прокрутки на полосе нет').toContain('cc-stage-screen-tabs');
    expect((tablist as { style?: Record<string, string> }).style?.overflowX).toBe('auto');
  });

  it('360: активная вкладка докручивается; при смене задачи — докручивается заново (R-D1e)', async () => {
    stageMock.mockImplementation((id: string) => Promise.resolve(screen(id)));
    const { container } = render([{ id: 'A', title: 'A' }, { id: 'B', title: 'B' }], true);
    await act(async () => { await flush(); });
    const anyTab = findTab(container, 'Артефакты');
    expect(anyTab, 'вкладки «Артефакты»').not.toBeNull();
    // узлы полосы пересоздаются при каждом монтировании — шпион на прототипе узла,
    // чтобы ловить вызовы на любых узлах
    const proto = Object.getPrototypeOf(anyTab!) as { scrollIntoView: (...args: unknown[]) => void };
    const spy = vi.spyOn(proto, 'scrollIntoView');
    clickTab(container, 'Артефакты');
    expect(spy.mock.calls.length, 'клик по вкладке обязан докрутить полосу').toBeGreaterThanOrEqual(1);
    click(container, 'stage-screen-pick-B');
    await act(async () => { await flush(); });
    click(container, 'stage-screen-pick-A');
    await act(async () => { await flush(); });
    // возврат к A: выбор вкладки помечен taskId, активна снова «Артефакты» (не первая) — полоса
    // обязана докрутиться к ней (с deps [tabSel, isMobile] — вызовов больше не будет)
    expect(spy.mock.calls.length, 'после смены задачи полоса осталась недокрученной').toBeGreaterThanOrEqual(2);
    expect(findTab(container, 'Артефакты')!.getAttribute('aria-selected')).toBe('true');
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

  it('повторный клик по диффу не шлёт второй запрос, пока первый идёт', async () => {
    stageMock.mockResolvedValue(screen('A'));
    const d = deferred<StageCommitDiff>();
    diffMock.mockReturnValue(d.promise);
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    clickTab(container, 'Артефакты');
    click(container, `stage-commit-toggle-${SHA_A}`); // открыть, запрос ушёл
    click(container, `stage-commit-toggle-${SHA_A}`); // скрыть
    click(container, `stage-commit-toggle-${SHA_A}`); // снова открыть, пока первый в полёте
    expect(diffMock).toHaveBeenCalledTimes(1);
    await act(async () => { d.resolve({ sha: SHA_A, subject: 's', files: ['A.cs'], diff: '@@ -1 +1 @@\n-x\n+y', truncated: false, maxChars: 200000 }); await flush(); });
    expect(findNodeByAttr(container, 'data-testid', `stage-commit-diff-${SHA_A}`)).not.toBeNull();
  });

  it('ошибка диффа показывается в блоке и не гасит экран: другие вкладки живы', async () => {
    stageMock.mockResolvedValue(screen('A'));
    diffMock.mockRejectedValue(new Error('Коммит не найден'));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    clickTab(container, 'Артефакты');
    click(container, `stage-commit-toggle-${SHA_A}`);
    await act(async () => { await flush(); });
    expect(text(findNodeByAttr(container, 'data-testid', `stage-commit-error-${SHA_A}`)!)).toContain('Коммит не найден');
    const t = text(container);
    expect(t).toContain('коммит A'); // артефакты живы
    clickTab(container, 'Ревью и вопросы');
    expect(text(container)).toContain('замечание'); // ревью живо
    clickTab(container, 'Вход');
    expect(text(container)).toContain('/x/A.cs'); // вход живой
  });

  it('ошибка загрузки экрана показывается текстом с «Повторить»', async () => {
    stageMock.mockRejectedValue(new Error('Задача не найдена'));
    const { container } = render([{ id: 'A', title: 'A' }]);
    await act(async () => { await flush(); });
    expect(text(findNodeByAttr(container, 'data-testid', 'stage-screen-error')!)).toContain('Задача не найдена');
  });

  // === Живое «ждёт вас» (R-D1e) ===
  // Фейковые часы: старт T0, ожидание началось за час до T0. Сценарий вердикта: человек
  // открыл экран и ушёл — сводка обязана идти за временем, а блок «Метрики» давать то же
  // число, что сводка (не своё, по Date.now() момента перерисовки).
  // Сдвиг 121,5 мин — НЕ кратный интервалу 60 с: последний тик useNow = T0+121 мин, а
  // виртуальное время клика по вкладке (следующий рендер без тика) = T0+121,5 мин.
  // Кто считает по Date.now() в рендере, а не по useNow, — покажет «3 ч 02 мин» против
  // «3 ч 01 мин» у живого: расхождение ровно то, что ловит этот тест.
  const T0 = Date.parse('2026-11-13T12:00:00Z');
  const MIN = 60_000;
  const waitingMetrics = (sinceAgoMin: number): StageMetrics => ({
    ...METRICS,
    human: { ...METRICS.human, waitingNow: { taskId: 'A', since: new Date(T0 - sinceAgoMin * MIN).toISOString(), reason: null } },
  });
  // Флуш ответа под фейковыми таймерами (тот же приём, что settle в FeaturePage.test).
  const settleFake = async () => { await act(async () => { await vi.advanceTimersByTimeAsync(0); }); };

  it('живое «ждёт вас»: тик useNow двигает сводку, «Метрики» дают то же число (R-D1e)', async () => {
    stageMock.mockResolvedValue(screen('A', { metrics: waitingMetrics(60) }));
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    try {
      const { container } = render([{ id: 'A', title: 'A' }]);
      await settleFake();
      const kpis = findNodeByAttr(container, 'data-testid', 'stage-screen-kpis')!;
      expect(text(kpis)).toContain('ждёт 1 ч');
      expect(vi.getTimerCount(), 'с waitingNow интервал должен жить').toBe(1);
      await act(async () => { await vi.advanceTimersByTimeAsync(121.5 * MIN); });
      // Рендер без тика (клик по вкладке) — на нём и видно, кто считает по часам рендера.
      clickTab(container, 'Метрики');
      const kt = text(kpis);
      // сводка: ровно значение последнего тика useNow (T0+121 мин + час ожидания = 181 мин)
      expect(kt, 'сводка обязана идти за временем (181 мин = «3 ч 01 мин»)').toContain('ждёт 3 ч 01 мин');
      const row = kvRow(container, 'stage-metrics-human', 'Ждёт сейчас');
      expect(row, 'ряда «Ждёт сейчас» в метриках нет').not.toBeNull();
      // метрики — то же число, что сводка, а не Date.now() момента клика (182 мин = «3 ч 02 мин»)
      expect(row!, 'метрики должны совпадать со сводкой, а не считать своё').toContain('3 ч 01 мин');
      expect(row!, 'метрики не должны опережать последний тик').not.toContain('02 мин');
    } finally {
      vi.useRealTimers();
    }
  });

  it('без waitingNow интервал не ставится: нечего ждать — нечего и считать', async () => {
    stageMock.mockResolvedValue(screen('A', { metrics: METRICS })); // waitingNow = null
    vi.useFakeTimers();
    vi.setSystemTime(T0);
    try {
      const { container } = render([{ id: 'A', title: 'A' }]);
      await settleFake();
      // цифра без ожидания — статическая сумма human.minutes, не живая длительность
      expect(text(findNodeByAttr(container, 'data-testid', 'stage-screen-kpis')!)).toContain('35 мин');
      expect(vi.getTimerCount(), 'waitingNow нет — интервал не должен ставиться').toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  describe('шапка и каркас (F12)', () => {
    const SUM = {
      executorSessionId: 's1',
      executor: { personaId: 'p1', name: 'Тимур', roleTitle: 'Backend-разработчик', modelLabel: 'Sonnet', modelIsOwnCell: true, silenceMinutes: 45, silenceAction: 'notify' },
      returns: 2,
      kpi: { totalMinutes: 90, execMinutes: 60, reviewMinutes: 20, waitedHumanMinutes: 10, turns: 5, tokens: { input: 1000, output: 500, cache: 200 }, approx: false },
      tasks: [
        { taskId: 'A', stepRef: 'B1', title: 'a', state: 'done', executor: null, minutes: 1, verdict: null, returns: 0 },
        { taskId: 'B', stepRef: 'B2', title: 'b', state: 'inProgress', executor: null, minutes: 1, verdict: null, returns: 0 },
      ],
    };
    const q = (status: 'open' | 'answered') => ({ id: 'q' + status, status, askedBy: { personaId: null, name: 'Тимур' }, askedAt: '2026-11-12T10:00:00Z', text: 'вопрос', waitedMinutes: 3, answer: null });

    it('шапка: «Этап · …», чип линии, тег «доработка» по роли, «↺ N возвраты», h2 serif', async () => {
      stageMock.mockResolvedValue(screen('A', { role: 'rework', stage: { templateId: 't', templateVersion: 1, role: 'rework', stageId: 's', lane: 'back', after: [] }, stageSummary: SUM }));
      const { container } = render([{ id: 'A', title: 'A' }], false, { featureTitle: 'Ф', stageTitle: 'Шаги', onBack: () => {}, onOpenChat: () => {} });
      await act(async () => { await flush(); });
      const c = container;
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-head-eyebrow')!)).toContain('Этап · Шаги');
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-head-lane')!)).toContain('бэк');
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-head-tag')!)).toContain('доработка');
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-head-returns')!)).toContain('↺ 2 возврата');
      const h2 = findNodeByAttr(c, 'data-testid', 'stage-screen-title')!;
      expect(h2.tagName.toLowerCase()).toBe('h2');
      expect((h2 as { style?: Record<string, string> }).style?.fontFamily).toContain('PT Serif');
    });

    it('тег «доработка» не рисуется по умолчанию: шаг в статусе done — тега нет', async () => {
      stageMock.mockResolvedValue(screen('A', { stageSummary: SUM }));
      const { container } = render([{ id: 'A', title: 'A' }]);
      await act(async () => { await flush(); });
      expect(findNodeByAttr(container, 'data-testid', 'stage-head-tag')).toBeNull();
    });

    it('строка исполнителя и пять плиток — по данным выбранной задачи', async () => {
      stageMock.mockResolvedValue(screen('A', { stageSummary: SUM }));
      const { container } = render([{ id: 'A', title: 'A' }]);
      await act(async () => { await flush(); });
      const c = container;
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-head-executor-name')!)).toContain('Тимур · Backend-разработчик');
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-head-executor-model')!)).toContain('Sonnet (своя ячейка этапа) · молчит 45 мин → только сообщить');
      for (const k of ['total', 'exec', 'review', 'waited', 'tokens']) expect(findNodeByAttr(c, 'data-testid', `stage-plate-${k}`), k).not.toBeNull();
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-plate-tokens-value')!)).toContain('2 тыс.');
    });

    it('чипы задач несут пометки ✓ и ●', async () => {
      stageMock.mockImplementation((id: string) => Promise.resolve(screen(id, { stageSummary: SUM })));
      const { container } = render([{ id: 'A', title: 'A', stepRef: 'B1' }, { id: 'B', title: 'B', stepRef: 'B2' }]);
      await act(async () => { await flush(); });
      const c = container;
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-screen-pick-A')!)).toContain('B1 ✓');
      expect(text(findNodeByAttr(c, 'data-testid', 'stage-screen-pick-B')!)).toContain('B2 ●');
    });

    it('порядок вкладок макета, счётчики и точка при открытом вопросе', async () => {
      stageMock.mockResolvedValue(screen('A', { metrics: METRICS, questions: [q('open')] }));
      const { container } = render([{ id: 'A', title: 'A' }]);
      await act(async () => { await flush(); });
      const c = container;
      const tabs: string[] = [];
      const walk = (x: FNode) => { if (x.nodeType === 1 && x.getAttribute?.('role') === 'tab') tabs.push(textNodes(x).join('')); for (const ch of x.childNodes) walk(ch); };
      walk(c);
      expect(tabs[0]).toContain('Ревью и вопросы');
      expect(tabs[0]).toContain('2'); // одно ревью + один вопрос
      expect(tabs[1]).toContain('Артефакты');
      expect(tabs[1]).toContain('1');
      expect(tabs[2]).toContain('Вход и транскрипт');
      expect(tabs[3]).toContain('Метрики');
      expect(findTab(c, 'Ревью и вопросы')!.getAttribute('aria-selected')).toBe('true');
    });

    it('полоса вкладок липкая', async () => {
      stageMock.mockResolvedValue(screen('A'));
      const { container } = render([{ id: 'A', title: 'A' }]);
      await act(async () => { await flush(); });
      const strip = findNodeByAttr(container, 'data-testid', 'stage-screen-tabs') as { style?: Record<string, string> };
      expect(strip.style?.position).toBe('sticky');
    });
  });
});
