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

// Итог в десктопной шапке — две колонки «время | статус»: текст колонок через пробел
// («0:15 готово»), без времени — одно слово. null — колонок нет (мобила, короткий вызов)
const headStatus = (html: string): string | null => {
  const m = /data-tool-status=""[^>]*>(.*?)<\/span><\/span>/.exec(html);
  return m ? m[1].replace(/<!-- -->/g, '').replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim() : null;
};

// Мобила переключается флагом: статический рендер window не видит, а раскладка карточки
// на мобиле своя (подпись прогресса — строкой под шапкой)
const viewport = vi.hoisted(() => ({ mobile: false }));
vi.mock('../../../lib/breakpoints', async (orig) => ({
  ...(await orig<typeof import('../../../lib/breakpoints')>()),
  useIsMobile: () => viewport.mobile,
}));

// Раскрытая карточка: статический рендер кликов не знает, поэтому флагом подменяем
// начальное false у useState на true — так рендерится тело (раскрытие и прочие
// булевы флаги карточки, тут это безвредно). Рамка копирования выставляет свой text
// атрибутом: что уйдёт в буфер, видно прямо в разметке
const forceOpen = vi.hoisted(() => ({ on: false }));
vi.mock('react', async (orig) => {
  const react = await orig<typeof import('react')>();
  const useState = ((init: unknown) => react.useState(forceOpen.on && init === false ? true : init)) as typeof react.useState;
  return { ...react, default: react, useState };
});
vi.mock('../CodeCopyButton', async () => {
  const { createElement: h } = await import('react');
  return {
    CodeBlockFrame: ({ text, children }: { text: string; children: unknown }) =>
      h('div', { 'data-copy': text }, children as never),
  };
});

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
    expect(headStatus(html)).toBe('0:15 готово');
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

  it('десктоп: итог остаётся в шапке двумя колонками, отдельной строки нет', () => {
    const done = render(wait({ result: '{"all_done":true}', finishedAt: 16_000 }), false);
    expect(captionLines(done)).toBe(0);
    expect(headStatus(done)).toBe('0:15 готово');
  });

  it('десктоп: время раньше слова статуса, слово — в слоте фиксированной ширины', () => {
    const done = render(wait({ result: '{"all_done":true}', finishedAt: 16_000 }), false);
    expect(done).toMatch(/data-tool-status=""[^>]*><span[^>]*>0:15<\/span><span style="min-width:6\.5ch[^"]*">готово</);
  });

  it('десктоп: место шеврона держится и у карточки без тела', () => {
    const done = render(wait({ result: '', finishedAt: 16_000 }), false);
    expect(done).toContain('aria-hidden="true" style="width:11px');
  });

  it('настоящие шаги ComfyUI — сплошная заливка полосы, оценка по ETA — пунктир и «≈»', () => {
    const exact = render(wait({ progress: { stage: 'running', percent: 40, exact: true } }), true);
    expect(exact).toContain('aria-valuenow="40"');
    expect(exact).not.toContain('repeating-linear-gradient');
    expect(exact).toContain('>40%<');
    const estimate = render(wait({ progress: { stage: 'running', percent: 40 } }), true);
    expect(estimate).toContain('repeating-linear-gradient');
    expect(estimate).toContain('≈40%');
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

  // Регрессия с боя: длинный фильтр, «идёт 1:0» — последняя цифра под полосой прокрутки ленты
  // Живой «идёт M:SS» статикой не отрисовать (секунды тикают эффектом) — проверяем итог
  // итог «время | статус», он стоит на том же месте шапки по тем же правилам
  it('длинный фильтр: время справа не ужимается и не переносится, у шапки запас справа под полосу', () => {
    const filter = 'FullyQualifiedName~DevServer|FullyQualifiedName~DevServerPortMemory|FullyQualifiedName~DevServerLaunchPolicy';
    const html = renderTool(run({ target: 'backend/ClaudeHomeServer.Tests', filter }, { result: 'dotnet test: все тесты прошли', finishedAt: 62_000 }));
    expect(html).toMatch(/data-tool-status="" style="[^"]*flex-shrink:0;[^"]*white-space:nowrap/);
    expect(headStatus(html)).toBe('1:01 готово');
    expect(html).toContain('padding:3px 8px 3px 0;min-width:0;display:flex');
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
  it('живость несёт сама иконка: у идущей она дышит, у готовой — нет; пустого места слева нет', () => {
    const live = render(wait());
    expect(live).toMatch(/class="cc-live-dot"[^>]*><svg/);
    const done = render(wait({ result: '{"all_done":true}', finishedAt: 16_000 }), undefined, false);
    expect(done).not.toContain('cc-live-dot');
    expect(done).not.toContain('width:18px');
  });

  it('процента нет — живая точка без полосы; с процентом — полоса на дорожке, процент и «осталось» справа', () => {
    const idle = render(wait());
    expect(idle).toContain('cc-live-dot');
    expect(idle).not.toContain('progressbar');
    expect(idle).not.toContain('cc-progress-run');
    const html = render(wait({ progress: { stage: 'running', label: 'шаг 8 из 20', percent: 40, exact: true, etaSeconds: 45 } }));
    // Полоса — на видимой дорожке во всю строку: конец виден
    expect(html).toMatch(/role="progressbar"[^>]*aria-valuenow="40"/);
    expect(html).toContain('background:var(--c-progress-track)');
    // Справа от полосы — процент; «осталось» отсчитывается от события прогресса (progressAt) по
    // часам карточки, которых у статики нет, — отсчёт покрыт тестами meterText. Застывшего ETA
    // источника нет ни глазами, ни в подписи для скринридера
    expect(html).toContain('>40%<');
    expect(html).not.toContain('осталось ~0:45');
    // Процент и «осталось» только у полосы: в подписи остаётся шаг
    // (полная подпись с процентом — только в aria-label полосы, для скринридера)
    expect(html).toContain('>шаг 8 из 20<');
    expect(html).toContain('шаг 8 из 20');
    expect(html).toContain('cc-live-dot');
  });

  it('полоса серая (muted): факт — сплошная, оценка — пунктир тем же цветом, без акцента', () => {
    const exact = render(wait({ progress: { stage: 'running', percent: 40, exact: true } }));
    expect(exact).toMatch(/width:40%[^"]*background:var\(--c-text-muted\)/);
    expect(exact).not.toMatch(/role="progressbar"[^]*background:var\(--c-accent\)/);
    const estimate = render(wait({ progress: { stage: 'running', percent: 40 } }));
    expect(estimate).toContain('repeating-linear-gradient(90deg, var(--c-text-muted)');
  });

  it('десктоп: справа от полосы — процент и место шеврона, как колонки шапки', () => {
    const html = render(wait({ progress: { stage: 'running', percent: 40, exact: true } }));
    expect(html).toMatch(/role="progressbar"[^]*>40%<\/span><span aria-hidden="true" style="width:11px/);
  });

  it('мобила: процент одной строкой, без колонок', () => {
    viewport.mobile = true;
    const html = render(wait({ progress: { stage: 'running', percent: 40, exact: true } }));
    expect(html).toContain('>40%<');
    expect(html).not.toMatch(/>40%<\/span><span aria-hidden="true" style="width:11px/);
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
    expect(headStatus(html)).toBe('1:10 прервано');
    // Красное — только слово статуса, время серое
    expect(html).toMatch(/>1:10<\/span><span style="min-width:6\.5ch;text-align:right;color:var\(--c-danger-text\)">прервано</);
  });

  // Карточка PowerShell, как и Bash, ждёт tool_started: до фактического старта команда не
  // работала — у «прервано» нет длительности; после старта — есть
  it('PowerShell до tool_started — «прервано» без времени, после старта — с временем', () => {
    const ps: ToolItem = { kind: 'tool_use', id: 'p1', name: 'PowerShell', input: { command: 'Start-Sleep 99' }, startedAt: 1_000 };
    const before = render(ps, [ps, { kind: 'interrupted', ts: 71_000 }], false);
    expect(headStatus(before)).toBe('прервано');
    const started = { ...ps, started: true };
    expect(headStatus(render(started, [started, { kind: 'interrupted', ts: 71_000 }], false))).toBe('1:10 прервано');
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
    expect(headStatus(html)).toBe('2:15 готово');
    expect(html).toContain('174 из 177 · <span style="color:var(--c-danger-text)">упало 3</span>');
    // Счётчики — левее колонок «время | статус»
    expect(html.indexOf('174 из 177')).toBeLessThan(html.indexOf('data-tool-status'));
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
    expect(headStatus(html)).toBe('1:10 прервано');
    // Видимый этап один (очередь короче 2 с не показываем) — его время уже в шапке
    expect(html).toContain('✕ сборка');
    expect(html).not.toContain('✕ сборка 1:10');
    expect(html).not.toContain('очередь');
    expect(html).not.toContain(' из ');
  });

  // Замечание Киры (Б): упавшая сборка (код 1/2) — штатный результат вызова без isError, и
  // шапка писала «готово · 0:16» над «✕ сборка»
  it.each([
    ['mcp__dev__build', { target: 'backend' }],
    ['mcp__dev__build', { kind: 'npm', target: 'frontend' }],
    ['mcp__tests__run_tests', { target: 'backend/App.Tests' }],
  ])('упавшая сборка %s %j: шапка «0:16 | ошибка», красное только слово, а не «готово»', (name, input) => {
    const stages = [{ stage: 'build', label: 'сборка', startedAt: 0, endedAt: 16_000, failed: true }];
    const html = render(run({ name, input, result: 'сборка упала (код выхода 1)', finishedAt: 16_000, stages }), undefined, false);
    expect(headStatus(html)).toBe('0:16 ошибка');
    expect(html).toMatch(/>0:16<\/span><span style="min-width:6\.5ch;text-align:right;color:var\(--c-danger-text\)">ошибка</);
    expect(html).not.toContain('готово');
    expect(html).toContain('✕ сборка');
  });

  it('идёт: текущий этап выделен и не режется, подпись прогресса при нём, полоса — строкой ниже', () => {
    const item = run({
      stages: [{ stage: 'build', label: 'сборка', startedAt: 0, endedAt: 102_000 }, { stage: 'running', label: 'тесты', startedAt: 102_000 }],
      progress: { stage: 'running', label: '412 из 7951 · упало 2', percent: 5, exact: true },
    });
    const html = render(item);
    expect(html).toContain('✓ сборка 1:42');
    expect(html).toContain('flex-shrink:0');
    expect(html).toContain('font-weight:600">тесты');
    expect(html).toContain('412 из 7951 · <span style="color:var(--c-danger-text)">упало 2</span>');
    expect(html.match(/role="progressbar"/g)?.length).toBe(1);
    // Полоса после строки этапов, а не внутри неё
    expect(html.indexOf('role="progressbar"')).toBeGreaterThan(html.indexOf('412 из 7951'));
    expect(html).toContain('>5%<');
    expect(html).toContain('cc-live-dot');
  });

  it('иконка по смыслу операции: тесты — колба, сборка — молоток, прочий MCP — вилка', () => {
    expect(render(run({ result: 'ok', finishedAt: 1_000 }), undefined, false)).toContain('lucide-flask-conical');
    expect(render(run({ name: 'mcp__dev__build', input: { target: 'backend' }, result: 'ok', finishedAt: 1_000 }), undefined, false)).toContain('lucide-hammer');
    expect(render(run({ name: 'mcp__notes__notes_list', input: {}, result: 'ok', finishedAt: 1_000 }), undefined, false)).toContain('lucide-plug');
  });

  it('единственный прошедший этап у готовой карточки строку не держит: итог и время — в шапке', () => {
    const stages = [{ stage: 'build', label: 'сборка', startedAt: 0, endedAt: 16_000 }];
    const html = render(run({ name: 'mcp__dev__build', input: { target: 'backend' }, result: 'ok', finishedAt: 16_000, stages }), undefined, false);
    expect(html).not.toContain('сборка');
    expect(headStatus(html)).toBe('0:16 готово');
  });

  it('единственный этап, пока идёт, — строкой с прогрессом', () => {
    const item = run({ stages: [{ stage: 'running', label: 'тесты', startedAt: 0 }], progress: { stage: 'running', label: '12 из 63' } });
    const html = render(item);
    expect(html).toContain('font-weight:600">тесты');
    expect(html).toContain('12 из 63');
  });

  it('готово с упавшими: первые упавшие списком без раскрытия, сверх — «ещё N»', () => {
    const failures = [
      { name: 'App.Tests.Foo.Падает', message: 'Expected 1 but was 2' },
      { name: 'App.Tests.Bar.ТожеПадает' },
    ];
    const html = render(run({ result: 'итог', finishedAt: 135_000, stages: done3, totals: { passed: 174, failed: 3, total: 177, failures } }), undefined, false);
    expect(html).toContain('App.Tests.Foo.Падает');
    expect(html).toContain('— Expected 1 but was 2');
    expect(html).toContain('App.Tests.Bar.ТожеПадает');
    expect(html).toContain('ещё 1 — в выводе');
    expect(html).toContain('cc-trunc-left');
  });

  it('пока идёт, списка упавших нет', () => {
    const item = run({ stages: done3.slice(0, 1), totals: { passed: 1, failed: 1, total: 2, failures: [{ name: 'X' }] } });
    const html = render(item);
    expect(html).not.toContain('ещё');
    expect(html).not.toContain('cc-trunc-left');
  });

  // Регрессия с 320 px: хвост «упало 2» строки этапов уезжал под полосу прокрутки ленты.
  // Счётчики не режутся и не переносятся, у строки этапов тот же запас справа, что у шапки
  it('320 px: счётчики при текущем этапе не ужимаются, у строки этапов запас справа под полосу', () => {
    viewport.mobile = true;
    const item = run({
      stages: [{ stage: 'build', label: 'сборка', startedAt: 0, endedAt: 102_000 }, { stage: 'running', label: 'тесты', startedAt: 102_000 }],
      progress: { stage: 'running', label: '412 из 7951 · упало 2', percent: 5, exact: true },
    });
    const html = render(item);
    expect(html).toMatch(/<div style="padding-right:8px;padding-bottom:\d+px"><div style="display:flex/);
    expect(html).toMatch(/<span style="flex-shrink:0;white-space:nowrap">[^]*412 из 7951 · <span style="color:var\(--c-danger-text\)">упало 2<\/span><\/span><\/div>/);
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

  it('раскрытая: в теле сама команда, копируется именно она, а не вывод', () => {
    forceOpen.on = true;
    try {
      const html = renderTool(bash({ result: 'M a.ts', finishedAt: 1 }));
      expect(html).toContain('data-copy="git status --short"');
      expect(html).toContain('>git status --short</pre>');
      // вывод — своим блоком со своим текстом для копирования
      expect(html).toContain('data-copy="M a.ts"');
      expect(html.indexOf('data-copy="git status --short"')).toBeLessThan(html.indexOf('data-copy="M a.ts"'));
    } finally {
      forceOpen.on = false;
    }
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
