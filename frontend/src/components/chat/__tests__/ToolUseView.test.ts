// Карточка вызова инструмента: статус прерванного фонового субагента (bgAborted).
// Регрессия: карточка обычного (не персоны) фонового агента, остановленного пользователем,
// показывала «готово», как будто задача успешно завершена, — bgAborted нигде не читался.
// Рендерим статикой через react-dom/server — как соседний TeamEscalationView.test.
import { describe, it, expect, vi, afterEach } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import { ToolUseView } from '../ToolUseView';
import { PersonaConsultCard } from '../PersonaTaskView';
import { ToolLivenessContext } from '../contexts';
import { normalizeHistory } from '../../../lib/chatReducer';
import { toolLiveness } from '../../../lib/toolTiming';
import { consoleCaption } from '../../../lib/toolLabels';

type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;

// Мобила переключается флагом: статический рендер window не видит, а раскладка карточки
// на мобиле своя (подпись прогресса — строкой под шапкой)
const viewport = vi.hoisted(() => ({ mobile: false }));
vi.mock('../../../lib/breakpoints', async (orig) => ({
  ...(await orig<typeof import('../../../lib/breakpoints')>()),
  useIsMobile: () => viewport.mobile,
}));

// Квитанция фонового запуска (isAsyncLaunchAck): tool_result приходит мгновенно,
// завершение агента отслеживается по bgDone/bgAborted, а не по этому тексту
const ASYNC_ACK =
  'Async agent launched successfully.\nagentId: a011da168d23b9e32\noutput_file: /tmp/out.txt';

function bgAgent(over: Partial<ToolItem>): ToolItem {
  return {
    kind: 'tool_use',
    id: 't1',
    name: 'Task',
    input: { description: 'Прочитать 100 файлов', prompt: 'Прочитай построчно…' },
    result: ASYNC_ACK,
    bgDone: true,
    ...over,
  };
}

const renderTool = (item: ToolItem) =>
  renderToStaticMarkup(createElement(ToolUseView, { item, online: true }));

describe('ToolUseView — прерванный фоновый субагент', () => {
  it('bgDone + bgAborted: шапка показывает «прервано», а не «готово»', () => {
    const html = renderTool(bgAgent({ bgAborted: true }));
    expect(html).toContain('прервано');
    expect(html).not.toContain('готово');
  });

  it('bgDone без прерывания: честное «готово», «прервано» нет', () => {
    const html = renderTool(bgAgent({}));
    expect(html).toContain('готово');
    expect(html).not.toContain('прервано');
  });

  it('isError сильнее bgAborted: статус «ошибка»', () => {
    const html = renderTool(bgAgent({ bgAborted: true, isError: true }));
    expect(html).toContain('ошибка');
    expect(html).not.toContain('прервано');
  });
});

describe('PersonaConsultCard — шапка при прерванном агенте', () => {
  const base = {
    question: 'Разберись в коде',
    running: false,
    isError: false,
    answer: '',
    emptyAnswerNote: 'Выдача прервана — ответа нет',
  };

  it('aborted: статус «прервано» в шапке и пометка про обрыв выдачи', () => {
    const html = renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, aborted: true }));
    expect(html).toContain('прервано');
    expect(html).toContain('Выдача прервана — ответа нет');
    // P8: категоричного «ответа не будет» карточка не утверждает
    expect(html).not.toContain('ответа не будет');
  });

  it('без aborted: шапка без статуса, дефолтная пометка пустого ответа', () => {
    const html = renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, emptyAnswerNote: undefined }));
    expect(html).not.toContain('прервано');
    expect(html).toContain('Ответ передан без текста');
  });
});

// Карточку переиспользуют консультации персон (PersonaTaskView) и агенты Workflow
// (WorkflowBlockView) — новых пропсов они не передают и обязаны рендериться как раньше.
// Всё новое поведение — строго под условием переданного пропса.
describe('PersonaConsultCard — старые вызовы без новых пропсов', () => {
  const base = {
    question: 'Разберись в коде',
    running: false,
    isError: false,
    answer: 'Готово',
  };
  const render = (props: Record<string, unknown>) =>
    renderToStaticMarkup(createElement(PersonaConsultCard, { ...base, ...props }));

  it('без персоны — заголовок «Агент», без чипа роли и без шеврона сворачивания', () => {
    const html = render({ badge: null });
    expect(html).toContain('Агент');
    expect(html).toContain('var(--c-bg-white)');   // поверхность прежняя, не quiet
    expect(html).not.toContain('role="button"');   // шапка не кликабельна без onCollapse
  });

  it('isError без statusLine — прежняя danger-коробка с фолбэком', () => {
    const html = render({ isError: true, answer: '' });
    expect(html).toContain('var(--c-danger-bg)');
    expect(html).toContain('Не удалось получить ответ персоны');
  });

  it('running без statusLine — спиннер с подписью и italic-ожидание в теле', () => {
    const html = render({ running: true });
    expect(html).toContain('Консультируется…');
    expect(html).toContain('изучает материалы и готовит ответ');
  });
});

describe('PersonaConsultCard — ход координатора (statusLine)', () => {
  const coord = {
    question: '',
    statusLine: 'Разбирает доклады волны 2',
    running: false,
    isError: true,
    answer: 'Волна 2: три задачи закрыты, одна вернулась на доработку',
    metrics: { tokens: 4200, toolUses: 4, durationMs: 12000 },
    badge: 'координатор',
    fallbackTitle: 'Координатор',
  };
  const html = () => renderToStaticMarkup(createElement(PersonaConsultCard, coord));

  it('сорвавшийся ход: тело — сводка, а не danger-коробка', () => {
    const h = html();
    expect(h).not.toContain('var(--c-danger-bg)');
    expect(h).not.toContain('Не удалось получить ответ персоны');
    expect(h).toContain('Волна 2: три задачи закрыты');
    expect(h).toContain('ошибка');   // признак сбоя несёт шапка
  });

  it('сорвавшийся ход: футер метрик на месте', () => {
    const h = html();
    expect(h).toContain('токенов');
    expect(h).toContain('4 действия');
    expect(h).toContain('12с');
  });

  it('без персоны карточка называет роль: заголовок и чип', () => {
    const h = html();
    expect(h).toContain('Координатор');
    expect(h).toContain('координатор');
  });

  it('quiet + onCollapse: приглушённая поверхность и кликабельная шапка', () => {
    const h = renderToStaticMarkup(createElement(PersonaConsultCard, { ...coord, quiet: true, onCollapse: () => {} }));
    expect(h).toContain('var(--c-bg-card)');
    expect(h).not.toContain('var(--c-bg-white)');
    expect(h).toContain('role="button"');
  });
});

// Таймер карточки после F5 (регресс QA, сценарий 2): в истории сервера у незавершённого
// вызова лежит "result": null, и карточка принимала null за результат — «готово» без
// времени у идущего инструмента и у прерванного «Стопом». Плюс гейт по ходу (ревью):
// карточка оборванного хода не оживает, когда в том же чате начался следующий ход.
describe('ToolUseView — таймер и статус после F5', () => {
  const renderIn = (raw: unknown[], id: string, busy: boolean) => {
    const items = normalizeHistory(raw);
    const item = items.find(it => it.kind === 'tool_use' && it.id === id) as ToolItem;
    return renderToStaticMarkup(createElement(ToolLivenessContext.Provider, { value: toolLiveness(items, busy) },
      createElement(ToolUseView, { item, online: true })));
  };
  const user = (text: string) => ({ kind: 'user_message', text, timestamp: 1 });
  const bash = (id: string, over: Record<string, unknown> = {}) =>
    ({ kind: 'tool_use', id, name: 'Bash', input: { command: 'sleep 20' }, result: null, isError: false, parentToolUseId: null, bgDone: null, startedAt: 1_000, finishedAt: null, ...over });

  it('идущий инструмент с "result": null — крутится, а не «готово»', () => {
    const html = renderIn([user('в'), bash('t1')], 't1', true);
    expect(html).toContain('cc-live-dot');
    expect(html).not.toContain('готово');
    expect(html).not.toContain('прервано');
  });

  it('прерванный «Стопом» с "result": null — «прервано», без спиннера', () => {
    const html = renderIn([user('в'), bash('t1', { bgDone: true }), { kind: 'interrupted', timestamp: 2 }], 't1', false);
    expect(html).toContain('прервано');
    expect(html).not.toContain('готово');
    expect(html).not.toContain('cc-live-dot');
  });

  it('оборванный ход + следующий ход: старая карточка «прервано» и не тикает, новая идёт', () => {
    const raw = [user('в'), bash('t1'), { kind: 'interrupted', timestamp: 2 }, user('ещё'), bash('t2', { startedAt: 5_000 })];
    const old = renderIn(raw, 't1', true);
    expect(old).toContain('прервано');
    expect(old).not.toContain('идёт');
    expect(old).not.toContain('progressbar');
    expect(renderIn(raw, 't2', true)).toContain('cc-live-dot');
  });

  it('завершённый — «готово» с длительностью по отметкам сервера', () => {
    const html = renderIn([user('в'), bash('t1', { result: 'ok', finishedAt: 16_000 })], 't1', false);
    expect(html).toContain('готово · 0:15');
  });
});

// Регресс QA (Кира, сценарий 2): на мобиле при завершении карточка сжималась 70 → 43 px —
// уходили строка подписи и полоса разом, лента прыгала вверх. Теперь итог встаёт на место
// строки подписи той же высоты, уходит только полоса
describe('ToolUseView — мобила: строка подписи переживает завершение', () => {
  afterEach(() => { viewport.mobile = false; });
  const wait = (over: Partial<ToolItem> = {}): ToolItem => ({
    kind: 'tool_use', id: 'w1', name: 'mcp__local-media__local_jobs_wait', input: { job_ids: ['lm_x'] },
    startedAt: 1_000, ...over,
  });
  const render = (item: ToolItem, busy: boolean) =>
    renderToStaticMarkup(createElement(ToolLivenessContext.Provider, { value: toolLiveness([item], busy) },
      createElement(ToolUseView, { item, online: true })));
  // Строки высотой со строку подписи (16px) — под шапкой
  const captionLines = (html: string) => html.match(/height:16px;line-height:16px/g)?.length ?? 0;

  it('идёт: строка подписи и полоса; завершён: та же строка с «готово · M:SS», полосы нет', () => {
    viewport.mobile = true;
    const running = render(wait({ progress: { stage: 'running', label: 'шаг 3 из 20', percent: 15, exact: true } }), true);
    expect(captionLines(running)).toBe(1);
    expect(running).toContain('шаг 3 из 20');
    expect(running).toContain('progressbar');

    const done = render(wait({ result: '{"all_done":true}', finishedAt: 16_000 }), false);
    expect(captionLines(done)).toBe(1);
    expect(done).toContain('готово · 0:15');
    expect(done).not.toContain('progressbar');
  });

  it('десктоп: итог остаётся в шапке, отдельной строки нет', () => {
    const done = render(wait({ result: '{"all_done":true}', finishedAt: 16_000 }), false);
    expect(captionLines(done)).toBe(0);
    expect(done).toContain('готово · 0:15');
  });

  it('настоящие шаги ComfyUI — сплошная заливка, оценка по ETA — пунктир', () => {
    const exact = render(wait({ progress: { stage: 'running', percent: 40, exact: true } }), true);
    expect(exact).not.toContain('repeating-linear-gradient');
    const estimate = render(wait({ progress: { stage: 'running', percent: 40 } }), true);
    expect(estimate).toContain('repeating-linear-gradient');
  });
});

// Просьба Гриши: вид прогона тестов виден на карточке — «Тесты · dotnet/vitest/Playwright»,
// цель и фильтр — в описании шапки
describe('ToolUseView — прогон тестов (run_tests)', () => {
  afterEach(() => { viewport.mobile = false; });
  const run = (input: Record<string, unknown>, over: Partial<ToolItem> = {}): ToolItem => ({
    kind: 'tool_use', id: 'rt1', name: 'mcp__tests__run_tests', input, startedAt: 1_000, ...over,
  });

  it.each([
    [{ target: 'backend/App.Tests', filter: 'FullyQualifiedName~X' }, 'Тесты · dotnet', 'backend/App.Tests · FullyQualifiedName~X'],
    [{ kind: 'vitest', target: 'frontend', files: ['frontend/src/lib/a.test.ts'] }, 'Тесты · vitest', 'frontend · frontend/src/lib/a.test.ts'],
    [{ kind: 'playwright', target: 'frontend', filter: 'офлайн' }, 'Тесты · Playwright', 'frontend · офлайн'],
  ])('вид и описание в шапке: %j', (input, title, arg) => {
    const html = renderTool(run(input, { result: 'vitest: все тесты прошли', finishedAt: 5_000 }));
    expect(html).toContain(title);
    expect(html).toContain(arg);
    expect(html).not.toContain('tests · run_tests');
  });

  it('несколько файлов — числом, а не списком', () => {
    const html = renderTool(run({ kind: 'vitest', files: ['a.test.ts', 'b.test.ts', 'c.test.ts'] }, { result: 'ok' }));
    expect(html).toContain('3 файла');
  });

  it('мобила: прогресс прогона строкой под шапкой — вид и цель в шапке не вытесняются', () => {
    viewport.mobile = true;
    const item = run({ kind: 'vitest', target: 'frontend' }, { progress: { stage: 'running', label: '87 из 171 файла · упало 2', percent: 50, exact: true } });
    const html = renderToStaticMarkup(createElement(ToolLivenessContext.Provider, { value: toolLiveness([item], true) },
      createElement(ToolUseView, { item, online: true })));
    expect(html.match(/height:16px;line-height:16px/g)?.length ?? 0).toBe(1);
    expect(html).toContain('Тесты · vitest');
    expect(html).toContain('87 из 171 файла · ');
    expect(html).toContain('упало 2');
  });
});

// Правки по витрине Веры (пункты 2–9)
describe('ToolUseView — витрина Веры', () => {
  afterEach(() => { viewport.mobile = false; });
  const render = (item: ToolItem, items: unknown[] = [item], busy = true) =>
    renderToStaticMarkup(createElement(ToolLivenessContext.Provider, { value: toolLiveness(items as ToolItem[], busy) },
      createElement(ToolUseView, { item, online: true })));
  const wait = (over: Partial<ToolItem> = {}): ToolItem => ({
    kind: 'tool_use', id: 'w1', name: 'mcp__local-media__local_jobs_wait', input: { job_ids: ['lm_x', 'lm_y'] }, startedAt: 1_000, ...over,
  });
  // Пустое место под живую точку у готовой карточки
  const SLOT = 'aria-hidden="true" style="width:18px;flex-shrink:0;display:flex;justify-content:center"';

  it('п. 2: у готовой карточки место под точку держится той же ширины', () => {
    const done = render(wait({ result: '{"all_done":true}', finishedAt: 16_000 }), undefined, false);
    expect(done).not.toContain('cc-live-dot');
    expect(done).toContain(SLOT);
  });

  it('вариант B: процента нет — живая точка и никакой полосы; с процентом — короткая полоса', () => {
    const idle = render(wait());
    expect(idle).toContain('cc-live-dot');
    expect(idle).not.toContain('progressbar');
    expect(idle).not.toContain('cc-progress-run');
    const html = render(wait({ progress: { stage: 'running', percent: 40, exact: true } }));
    expect(html).toContain('progressbar');
    expect(html).toContain('max-width:200px');
    expect(html).toContain('cc-live-dot');
  });

  it('п. 4: ожидание в очереди — только точка, полосы нет', () => {
    const html = render(wait({ progress: { stage: 'queued', queuePosition: 2, percent: 0 } }));
    expect(html).not.toContain('progressbar');
    expect(html).toContain('cc-live-dot');
    expect(html).toContain('2-я в очереди');
  });

  it('п. 5: «упало K» выделено цветом ошибки, «упало» без цифры — нет', () => {
    const html = render(wait({ progress: { stage: 'running', label: '412 из 7951 · упало 2', percent: 5, exact: true } }));
    expect(html).toContain('<span style="color:var(--c-danger-text)">упало 2</span>');
  });

  it('п. 6: local-media по-русски, описание — сколько задач ждём', () => {
    const html = render(wait({ result: '{}', finishedAt: 2_000 }), undefined, false);
    expect(html).toContain('Локальная генерация');
    expect(html).toContain('ожидание 2 задач');
    expect(html).not.toContain('local_jobs_wait');
  });

  it('п. 8: мобила — итог строкой под шапкой у любой карточки, и у Bash', () => {
    viewport.mobile = true;
    const bash: ToolItem = { kind: 'tool_use', id: 'b1', name: 'Bash', input: { command: 'ls' }, startedAt: 1_000, started: true, result: 'ok', finishedAt: 16_000 };
    const html = render(bash, undefined, false);
    expect(html.match(/height:16px;line-height:16px/g)?.length ?? 0).toBe(1);
    expect(html).toContain('готово · 0:15');
  });

  it('п. 9: «прервано» — с длительностью до обрыва', () => {
    const bash: ToolItem = { kind: 'tool_use', id: 'b1', name: 'Bash', input: { command: 'sleep 99' }, startedAt: 1_000, started: true };
    const html = render(bash, [bash, { kind: 'interrupted', ts: 71_000 }], false);
    expect(html).toContain('прервано · 1:10');
  });
});

// Вариант B: строка этапов прогона тестов и итог с цифрами на закрытой карточке
describe('ToolUseView — этапы и итог run_tests', () => {
  afterEach(() => { viewport.mobile = false; });
  const render = (item: ToolItem, items: unknown[] = [item], busy = true) =>
    renderToStaticMarkup(createElement(ToolLivenessContext.Provider, { value: toolLiveness(items as ToolItem[], busy) },
      createElement(ToolUseView, { item, online: true })));
  const run = (over: Partial<ToolItem>): ToolItem => ({
    kind: 'tool_use', id: 'rt', name: 'mcp__tests__run_tests', input: { target: 'backend/App.Tests' }, startedAt: 0, ...over,
  });
  const done3 = [
    { stage: 'build', label: 'сборка', startedAt: 0, endedAt: 62_000 },
    { stage: 'list', label: 'подсчёт', startedAt: 62_000, endedAt: 64_000 },
    { stage: 'running', label: 'тесты', startedAt: 64_000, endedAt: 135_000 },
  ];

  it('готово с упавшими: счётчики в шапке, «упало» красным, этапы с галочками без раскрытия', () => {
    const html = render(run({ result: 'итог', finishedAt: 135_000, stages: done3, totals: { passed: 174, failed: 3, total: 177 } }), undefined, false);
    expect(html).toContain('готово · 2:15');
    expect(html).toContain('174 из 177 · <span style="color:var(--c-danger-text)">упало 3</span>');
    // Старая история с отдельным подсчётом: он схлопнут в «тесты», время прибавлено к ним
    expect(html).toContain('✓ сборка 1:02 · ✓ тесты 1:13');
    expect(html).not.toContain('подсчёт');
  });

  it('прервано на подсчёте (старая история): «✕ тесты» с временем подсчёта', () => {
    const stages = [
      { stage: 'build', label: 'сборка', startedAt: 0, endedAt: 60_000 },
      { stage: 'list', label: 'подсчёт', startedAt: 60_000 },
    ];
    const item = run({ stages });
    const html = render(item, [item, { kind: 'interrupted', ts: 63_000 }], false);
    expect(html).toContain('✓ сборка 1:00');
    expect(html).toContain('✕ тесты 0:03');
    expect(html).not.toContain('подсчёт');
  });

  it('идёт подсчёт: текущий этап «тесты» без подписи и полосы', () => {
    const item = run({
      stages: [{ stage: 'build', label: 'сборка', startedAt: 0, endedAt: 102_000 }, { stage: 'running', label: 'тесты', startedAt: 102_000 }],
      progress: { stage: 'list', label: 'подсчёт тестов' },
    });
    const html = render(item);
    expect(html).toContain('font-weight:600">тесты');
    expect(html).not.toContain('подсчёт');
    expect(html).not.toContain('progressbar');
  });

  it('готово без упавших — «177 из 177» без «упало»', () => {
    const html = render(run({ result: 'итог', finishedAt: 135_000, stages: done3, totals: { passed: 177, failed: 0, total: 177 } }), undefined, false);
    expect(html).toContain('177 из 177');
    expect(html).not.toContain('упало');
  });

  it('прервано на сборке: этап крестиком и красным, счётчиков нет', () => {
    const stages = [
      { stage: 'queued', label: 'очередь', startedAt: 0, endedAt: 500 },
      { stage: 'build', label: 'сборка', startedAt: 500, endedAt: 70_500, failed: true },
    ];
    const item = run({ stages });
    const html = render(item, [item, { kind: 'interrupted', ts: 70_500 }], false);
    expect(html).toContain('прервано · 1:10');
    expect(html).toContain('✕ сборка 1:10');
    expect(html).not.toContain('очередь'); // очередь короче 2 с не показываем
    expect(html).not.toContain(' из ');
  });

  it('идёт: текущий этап выделен и не режется, подпись прогресса при нём, полоса под этапами', () => {
    const item = run({
      stages: [{ stage: 'build', label: 'сборка', startedAt: 0, endedAt: 102_000 }, { stage: 'running', label: 'тесты', startedAt: 102_000 }],
      progress: { stage: 'running', label: '412 из 7951 · упало 2', percent: 5, exact: true },
    });
    const html = render(item);
    expect(html).toContain('✓ сборка 1:42');
    expect(html).toContain('flex-shrink:0');
    expect(html).toContain('font-weight:600">тесты');
    expect(html).toContain('412 из 7951 · <span style="color:var(--c-danger-text)">упало 2</span>');
    expect(html).toContain('progressbar');
    expect(html).toContain('cc-live-dot');
  });

  it('после F5: этапы и итог приходят из истории вызова', () => {
    const items = normalizeHistory([
      { kind: 'tool_use', id: 'rt', name: 'mcp__tests__run_tests', input: {}, result: 'итог', startedAt: 0, finishedAt: 135_000, stages: done3, totals: { passed: 174, failed: 3, total: 177 } },
    ]);
    const html = render(items[0] as ToolItem, items, false);
    expect(html).toContain('✓ тесты 1:13');
    expect(html).toContain('174 из 177');
  });

  it('мобила: итог строкой под шапкой, ниже строка этапов', () => {
    viewport.mobile = true;
    const html = render(run({ result: 'итог', finishedAt: 135_000, stages: done3, totals: { passed: 177, failed: 0, total: 177 } }), undefined, false);
    expect(html.indexOf('готово · 2:15')).toBeLessThan(html.indexOf('✓ сборка'));
    expect(html).toContain('177 из 177');
  });
});

// Живая русская подпись консольной команды: description — в шапке, команда — в title шапки
// и в теле над выводом. Тело статикой не видно (карточка свёрнута), поэтому о нём говорит
// наличие стрелки раскрытия, а состав — чистая consoleCaption
describe('ToolUseView — русская подпись консольной команды', () => {
  const bash = (over: Partial<ToolItem>): ToolItem => ({
    kind: 'tool_use', id: 'c1', name: 'Bash',
    input: { command: 'git status --short', description: 'Смотрю статус рабочего дерева' },
    ...over,
  });

  it('с description: подпись в шапке, команда — подсказкой и не текстом шапки', () => {
    const html = renderTool(bash({ result: 'M a.ts', finishedAt: 1 }));
    expect(html).toContain('>Смотрю статус рабочего дерева<');
    expect(html).toContain('title="git status --short"');
    expect(html).not.toContain('>git status --short<');
  });

  it('с description: раскрывается и до результата — команда уехала в тело', () => {
    const html = renderTool(bash({}));
    expect(html).toContain('▾');
  });

  it('без description: как раньше — команда текстом в шапке, без подсказки', () => {
    const html = renderTool(bash({ input: { command: 'git status --short' } }));
    expect(html).toContain('>git status --short<');
    expect(html).not.toContain('title="git status --short"');
    expect(html).not.toContain('▾');
  });

  it('во время стрима аргументов: печатается сырой аргумент, подписи нет', () => {
    const html = renderTool(bash({ streamingArg: '{"command":"git st' }));
    expect(html).not.toContain('Смотрю статус');
    expect(html).not.toContain('title="git status --short"');
  });

  it('MCP с полем command: без изменений, даже если в имени есть shell', () => {
    const html = renderTool(bash({ name: 'mcp__box__run_shell' }));
    expect(html).toContain('>git status --short<');
    expect(html).not.toContain('>Смотрю статус рабочего дерева<');
  });
});

describe('consoleCaption', () => {
  const input = { command: 'ls -la', description: '  Список файлов  ' };

  it('Bash и PowerShell с подписью — подпись обрезана, команда как есть', () => {
    expect(consoleCaption('Bash', input)).toEqual({ description: 'Список файлов', command: 'ls -la' });
    expect(consoleCaption('PowerShell', input)).toEqual({ description: 'Список файлов', command: 'ls -la' });
  });

  it('нет подписи, пустая подпись, нет команды — null', () => {
    expect(consoleCaption('Bash', { command: 'ls' })).toBeNull();
    expect(consoleCaption('Bash', { command: 'ls', description: '   ' })).toBeNull();
    expect(consoleCaption('Bash', { description: 'Список' })).toBeNull();
  });

  it('не консоль, MCP и стрим аргументов — null', () => {
    expect(consoleCaption('Read', input)).toBeNull();
    expect(consoleCaption('mcp__box__run_shell', input)).toBeNull();
    expect(consoleCaption('Bash', input, '{"comm')).toBeNull();
  });
});
