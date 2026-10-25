// Карточка фичи (режим «Исполнение фичи») — НОВОЕ тело по утверждённому макету
// (turn-plan-feature-popup-1). Рендер — renderToStaticMarkup (окружение node:
// useIsMobile()=false, effects не идут). Проверяем:
//   • statusLineFor (чистая функция) — тон/заголовок/действие на 9 состояний;
//   • шапка (имя mono + ветка + StatusTag), мета (ветка · роль чата);
//   • этапы (раскрыт один, «впереди» не кликается), подвал «Открыть страницу фичи»;
//   • загрузка (скелет) и ошибка («Повторить»);
//   • что УШЛО: «Сейчас», «Попросить Марка», «Итог финала», «Перезапустить», легенда, «Конвейер»;
//   • гейт фич-флага (M2) и меню пилюли (K-3) — через TurnPlanPill.
import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureRun, FeatureTask, FeatureNow, FeatureAttention, FeatureSummary, FeatureStage } from '../../../types/featureRun';
import type { StepProgress } from '../../../lib/featureProgress';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import { FeatureRunView, statusLineFor } from './FeatureRunView';
import { TurnPlanPill } from '../TurnPlanPill';
import { __test } from '../../../lib/featureRuns';
import { __test as backNav } from '../../../lib/featureBackNav';
import { installFakeDom, mount, reactProps, uninstallFakeDom, findNodeByAttr, textNodes } from '../__tests__/fakeDom';

// Стор персон (lib/personas) — мок: hoisted-карта id→имя (PersonaAvatar/имена исполнителей).
// ensurePersonasLoaded/usePersonasVersion — no-op: в node-окружении они тянут signalr/localStorage.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return {
    ...orig,
    getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
    ensurePersonasLoaded: () => {},
    usePersonasVersion: () => 0,
  };
});
// useIsMobile — управляемый флаг (SSR по умолчанию false).
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});
// api (lib/api) — FeatureRunView вызывает api.tasks.mergeFeature (SSR — не идёт, но мок нужен).
vi.mock('../../../lib/api', () => ({
  api: { tasks: { mergeFeature: async (_id: string) => {} } },
}));
// toast (lib/toast) — «Задача мержа создана» при успехе мержа.
const { toastMock } = vi.hoisted(() => ({ toastMock: { calls: [] as string[] } }));
vi.mock('../../../lib/toast', () => ({
  showToast: (title: string) => { toastMock.calls.push(title); },
}));
// openChat (lib/openChat) — управляемый результат открытия: для теста точки возврата
// (major 1) — true; для порядка «открыть → записать» (ревью Ф9b, зам. 2) — false.
const openChatMock = vi.hoisted(() => ({ result: true }));
vi.mock('../../../lib/openChat', () => ({
  openChatById: async (_id: string) => openChatMock.result,
}));

// openTaskInSection (lib/tasks) — запись вызовов: переход в задачу обязан нести проект эпика.
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
vi.mock('../../../lib/tasks', () => ({ openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } }));

beforeEach(() => { mockOpenTask.calls = []; personaMap.clear(); mobileFlag.value = false; toastMock.calls.length = 0; openChatMock.result = true; backNav.reset(); });

// Задача-заглушка (подмножество TaskItem, как несёт GET /api/tasks/features).
function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

// Базовый агрегат фичи: эпик + state; остальное «тихое», тесты покрывают по состоянию.
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator-console' }),
    steps: [], reviews: [], qa: [],
    final: null, merge: null,
    state: 'running',
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

const render = (r: FeatureRun, extra: Record<string, unknown> = {}) =>
  renderToStaticMarkup(createElement(FeatureRunView, { run: r, ...extra }));

// now[]-элемент.
function nowItem(over: Partial<FeatureNow> = {}): FeatureNow {
  return { taskId: 'n', stage: 'prod', title: 'Задача', ...over };
}

// === statusLineFor: тон/заголовок/действие на 9 состояний (чистая функция) ===
describe('шапка: состояние вне словаря', () => {
  it('неизвестный state не роняет рендер — тег «статус неизвестен»', () => {
    const html = render(run({ state: 'нет-такого' as FeatureRun['state'] }));
    expect(html).toContain('статус неизвестен');
  });
});

describe('statusLineFor: 9 состояний', () => {
  const now = Date.parse('2026-10-20T12:00:00Z');

  it('not_started / idle → neutral «Не начата.», без действия', () => {
    for (const st of ['not_started', 'idle'] as const) {
      const line = statusLineFor(run({ state: st }), now);
      expect(line.tone).toBe('neutral');
      expect(line.headline).toBe('Не начата.');
      expect(line.action).toBeNull();
    }
  });

  it('running → now «…идёт», без действия (всё идёт само)', () => {
    personaMap.set('kira', 'Кира');
    const line = statusLineFor(run({ state: 'running', now: [nowItem({ personaId: 'kira' })] }), now);
    expect(line.tone).toBe('now');
    expect(line.headline).toContain('идёт');
    expect(line.action).toBeNull();
  });

  it('waiting → info «ждёт вас» + действие chat «Ответить {имя}»', () => {
    personaMap.set('kira', 'Кира');
    const att: FeatureAttention = { kind: 'wait', reason: 'нужен выбор линии', personaId: 'kira', linkedSessionId: 'sess-k' };
    const line = statusLineFor(run({ state: 'waiting', attention: att }), now);
    expect(line.tone).toBe('info');
    expect(line.headline).toContain('ждёт вас');
    expect(line.why).toBe('нужен выбор линии');
    expect(line.action?.kind).toBe('chat');
    // featurePrimaryAction (страницы) — «Ответить {имя}» (имя из стора, именительный).
    expect(line.action?.label).toBe('Ответить Кира');
    expect(line.action?.chatId).toBe('sess-k');
  });

  it('stopped → danger «стоит» + действие chat «Чат {имя}» (БЕЗ «Перезапустить»)', () => {
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', personaId: 'denis', linkedSessionId: 'sess-d' };
    const line = statusLineFor(run({ state: 'stopped', attention: att }), now);
    expect(line.tone).toBe('danger');
    expect(line.headline).toContain('стоит');
    expect(line.action?.kind).toBe('chat');
    expect(line.action?.label).toBe('Чат Денис');
    expect(line.action?.chatId).toBe('sess-d');
  });

  it('merge_pending → info «Мерж ждёт согласия» + действие merge «Мерж…» + пара финала', () => {
    const line = statusLineFor(run({ state: 'merge_pending', final: task({ id: 'f1', linkedSessionId: 'sess-f' }) }), now);
    expect(line.tone).toBe('info');
    expect(line.headline).toContain('Мерж ждёт согласия');
    expect(line.action?.kind).toBe('merge');
    expect(line.action?.label).toBe('Мерж…');
    expect(line.action?.showPair).toBe(true);
  });

  it('merging → now «Мерж идёт» + пара (чат/задача мержа)', () => {
    const line = statusLineFor(run({ state: 'merging', merge: task({ id: 'm1', linkedSessionId: 'sess-m' }) }), now);
    expect(line.tone).toBe('now');
    expect(line.headline).toContain('Мерж идёт');
    expect(line.action?.kind).toBe('pair');
    expect(line.action?.chatId).toBe('sess-m');
    expect(line.action?.taskId).toBe('m1');
  });

  it('merged → success «Смержена {dt}» + итог из summary', () => {
    const summary: FeatureSummary = { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 370, tasks: 14, steps: 9, loops: 3 };
    const line = statusLineFor(run({ state: 'merged', summary }), now);
    expect(line.tone).toBe('success');
    expect(line.headline).toContain('Смержена');
    expect(line.why).toContain('6 ч 10 мин');
    expect(line.why).toContain('↺3');
  });

  it('closed → neutral «Закрыта без мержа» + пара (эпик)', () => {
    const line = statusLineFor(run({ state: 'closed' }), now);
    expect(line.tone).toBe('neutral');
    expect(line.headline).toContain('Закрыта без мержа');
    expect(line.action?.kind).toBe('pair');
    expect(line.action?.taskId).toBe('epic-1');
  });
});

// === Ф-pr: ↺ — один глиф, два смысла (review-корзина vs возвраты) различаются ===
// formatStepProgress: корзина ревью «R в ревью» БЕЗ ↺; mergedSummary: возвраты «↺K» С ↺.
// Рядом в карточке — оба числа; различимость = review-корзина не несёт глифа возвратов.
describe('FeatureRunView: Ф-pr — ↺ один глиф, два смысла различаются', () => {
  it('рядом: review-корзина «R в ревью» (БЕЗ ↺) и возвраты «↺K» — оба числа видны и различаются', () => {
    const summary: FeatureSummary = { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 370, tasks: 14, steps: 9, loops: 3 };
    // Фикстура согласована с семантикой бэка: «готово» только при done; здесь полное
    // принятие ещё не достигнут — cut (метки плана нет, «готово» нет).
    const stepProgress: StepProgress = { planState: 'cut', total: 5, accepted: 2, review: 2, work: 0, notCreated: 0, withdrawn: 0, unlinkedReworks: 0 };
    const html = render(run({ state: 'merged', summary, stepProgress }));
    // Оба числа видны: review-корзина (2) и возвраты (3).
    expect(html).toContain('2 в ревью');
    expect(html).toContain('↺3');
    // Различимость: review-корзина (formatStepProgress) НЕ несёт ↺ (глиф за возвратами).
    const progress = html.match(/<div data-testid="feature-run-progress"[^>]*>([\s\S]*?)<\/div>/);
    expect(progress, 'progress-нода не найдена').not.toBeNull();
    expect(progress![1], 'review-корзина без ↺ (глиф за возвратами)').not.toContain('↺');
  });
});

// === Рендер: шапка + мета + строка состояния + этапы + подвал ===
describe('FeatureRunView — рендер', () => {
  it('шапка: имя mono + ветка + StatusTag; «100%» нет', () => {
    const html = render(run({ state: 'running' }));
    expect(html).toContain('Пульт куратора');
    expect(html).toContain('feat/curator-console');
    // R2 minor 1: разные id — тег шапки (feature-run-status-tag) и строка (feature-run-status-line).
    expect(html).toContain('feature-run-status-tag');
    expect(html).toContain('feature-run-status-line');
    expect(html).not.toMatch(/>100%</);
  });

  it('строка состояния (running): «идёт» в тоне now', () => {
    const html = render(run({ state: 'running' }));
    expect(html).toContain('role="status"');
    expect(html).toContain('идёт');
  });

  it('stopped: «стоит» + «Чат {имя}» (без «Перезапустить»)', () => {
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', personaId: 'denis', linkedSessionId: 'sess-d' };
    const html = render(run({ state: 'stopped', attention: att }));
    expect(html).toContain('стоит');
    expect(html).toContain('Чат Денис');
    expect(html).not.toContain('Перезапустить');
  });

  // R2 major 2: в waiting/stopped рядом с главным действием (чат) — кнопка «задача»
  // по attention.taskId (макет: «Ответить Кире →» · «задача»).
  it('waiting/stopped: кнопка «задача» рядом с чатом (attention.taskId)', () => {
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', personaId: 'denis', linkedSessionId: 'sess-d', taskId: 'task-d' };
    const html = render(run({ state: 'stopped', attention: att }));
    // Главный чат + рядом кнопка «задача» (nav-target-task).
    expect(html).toContain('Чат Денис');
    expect(html).toContain('nav-target-task');
  });

  it('merge_pending: «Мерж…» (кнопка) + пара «задача» финала', () => {
    const html = render(run({ state: 'merge_pending', final: task({ id: 'f1', linkedSessionId: 'sess-f' }) }));
    expect(html).toContain('Мерж…');
    expect(html).toContain('nav-target-pair');
  });

  it('этапы: раскрыт один (active), «впереди» не кликается (div, не button)', () => {
    const stages: FeatureStage[] = [
      { key: 'cut', state: 'done', passes: 1, taskIds: [], rounds: [], executors: [] },
      { key: 'plan', state: 'done', passes: 1, taskIds: [], rounds: [], executors: [] },
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [], runningSince: '2026-10-20T10:00:00Z', executors: [] },
      { key: 'review', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'qa', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'final', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'merge', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
    ];
    const html = render(run({ state: 'running', stages }));
    // Все 7 стадий на месте.
    expect(html).toContain('Разрез');
    expect(html).toContain('План');
    expect(html).toContain('Производство');
    expect(html).toContain('Ревью');
    expect(html).toContain('Проверка');
    expect(html).toContain('Финальное ревью');
    expect(html).toContain('Мерж');
    // «впереди» (pending) — слово на месте.
    expect(html).toContain('впереди');
  });

  // R2 major 5: роль «этот чат — {role}» — по chatTaskId (Session.TaskId) среди executors.
  it('мета: роль чата «этот чат — {role}» по chatTaskId', () => {
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'task-front', taskRole: 'step', stepRef: 'Ш2', lane: 'front', status: 'running', chatId: null, chatArchived: false, modelLabel: null, modelFull: null }] },
    ];
    const html = render(run({ state: 'running', stages }), { chatTaskId: 'task-front' });
    expect(html).toContain('этот чат — Ш2 · фронт');
  });

  // R2 major 6: чип модели — КНОПКА (фокусируемый): тап раскрывает подсказку,
  // aria-label несёт полную модель (на 360 полная модель доступна).
  it('чип модели — кнопка, aria-label с полной моделью', () => {
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'x', taskRole: null, stepRef: null, lane: null, status: 'running', chatId: null, chatArchived: false, modelLabel: 'Qwen', modelFull: 'Qwen3.8-27B' }] },
    ];
    const html = render(run({ state: 'running', stages }));
    expect(html).toContain('Qwen');
    expect(html).toMatch(/aria-label="Модель чата: Qwen3\.8-27B"/);
  });

  // R2 minor 3: тап по чипу модели раскрывает подсказку (onClick → setTapped → Tooltip focused).
  it('тап по чипу модели раскрывает подсказку (onClick)', () => {
    installFakeDom();
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'x', taskRole: null, stepRef: null, lane: null, status: 'running', chatId: null, chatArchived: false, modelLabel: 'Qwen', modelFull: 'Qwen3.8-27B' }] },
    ];
    const { root, container } = mount(createElement(FeatureRunView, { run: run({ state: 'running', stages }) }));
    // Чип модели — кнопка с aria-label.
    const chip = findNodeByAttr(container, 'aria-label', 'Модель чата: Qwen3.8-27B');
    expect(chip, 'чип модели не найден').not.toBeNull();
    // До тапа: подсказки нет (role="tooltip" отсутствует в body-портале).
    const body = (globalThis as { document: { body: Parameters<typeof findNodeByAttr>[0] } }).document.body;
    expect(findNodeByAttr(body, 'role', 'tooltip'), 'tooltip до тапа').toBeNull();
    // Тап: onClick переключает tapped → Tooltip focused → плашка появляется в body-портале.
    act(() => { (reactProps(chip!)!.onClick as () => void)(); });
    expect(findNodeByAttr(body, 'role', 'tooltip'), 'tooltip после тапа').not.toBeNull();
    root.unmount();
    uninstallFakeDom();
  });

  it('подвал «Открыть страницу фичи» — только когда есть onOpenPage', () => {
    const withFooter = renderToStaticMarkup(createElement(FeatureRunView, { run: run({ state: 'running' }), onOpenPage: () => {} }));
    const noFooter = render(run({ state: 'running' }));
    expect(withFooter).toContain('Открыть страницу фичи');
    expect(noFooter).not.toContain('Открыть страницу фичи');
  });

  it('загрузка: скелет (без шапки/этапов)', () => {
    const html = renderToStaticMarkup(createElement(FeatureRunView, { run: run({ state: 'running' }), loading: true }));
    // Скелет — серые полосы; шапки фичи (имя) нет.
    expect(html).not.toContain('Пульт куратора');
    expect(html).toContain('bg-selected');
  });

  it('ошибка: «Не удалось загрузить фичу.» + «Повторить»', () => {
    const html = renderToStaticMarkup(createElement(FeatureRunView, { run: run({ state: 'running' }), error: 'Сервер не ответил', onRetry: () => {} }));
    expect(html).toContain('Не удалось загрузить фичу.');
    expect(html).toContain('Сервер не ответил');
    expect(html).toContain('Повторить');
  });

  it('что ушло: «Сейчас», «Попросить Марка», «Итог финала», «Перезапустить», «Конвейер», легенда', () => {
    const html = render(run({ state: 'running' }));
    expect(html).not.toContain('Сейчас');
    expect(html).not.toContain('Попросить Марка');
    expect(html).not.toContain('Итог финала');
    expect(html).not.toContain('Перезапустить');
    expect(html).not.toContain('Конвейер');
    expect(html).not.toContain('нет в трекере');
  });

  it('M7: useIsMobile не рвёт SSR-рендер', () => {
    expect(() => render(run({ state: 'running' }))).not.toThrow();
  });
});

// major 1: переход в чат из попапа/панели пишет точку возврата «← к фиче {название}»
// (БЕЗ вкладки — showTab=false); возврат ведёт на последнюю использованную вкладку.
describe('FeatureRunView: точка возврата из попапа/панели (major 1)', () => {
  it('клик «чат» исполнителя → точка возврата с названием фичи, БЕЗ вкладки', async () => {
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'x', taskRole: null, stepRef: null, lane: null, status: 'running', chatId: 'chat-target', chatArchived: false, modelLabel: null, modelFull: null }] },
    ];
    installFakeDom();
    const { root, container } = mount(createElement(FeatureRunView, { run: run({ state: 'running', stages }) }));
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(btn, 'кнопка чата исполнителя').not.toBeNull();
    // Открытие — асинхронное (ревью Ф9b, зам. 2): возврат пишется после openChatById.
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); await Promise.resolve(); });
    // Точка возврата: featureId, название фичи, showTab=false (без вкладки).
    const info = backNav.featureBackNavFrom('chat-target');
    expect(info, 'точки возврата нет').not.toBeNull();
    expect(info!.featureId).toBe('epic-1');
    expect(info!.title).toBe('Пульт куратора');
    expect(info!.showTab, 'из попапа/панели — БЕЗ вкладки').toBe(false);
    root.unmount();
    uninstallFakeDom();
  });

  // Ревью Ф9b, зам. 2: если чат НЕ открылся (openChatById → false, тост «Чат не найден»),
  // указатель не перезаписывается на несуществующий чат — прежняя точка возврата жива.
  // Мутация «записать возврат до открытия» красит этот тест.
  it('openChatById вернул false → точка НЕ перезаписана, прежняя сохранена', async () => {
    openChatMock.result = false;
    // Прежняя точка возврата — на старый чат.
    backNav.recordFeatureReturn('epic-1', { kind: 'chat', id: 'chat-old' }, 'Пульт куратора', undefined, false);
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'x', taskRole: null, stepRef: null, lane: null, status: 'running', chatId: 'chat-target', chatArchived: false, modelLabel: null, modelFull: null }] },
    ];
    installFakeDom();
    const { root, container } = mount(createElement(FeatureRunView, { run: run({ state: 'running', stages }) }));
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(btn, 'кнопка чата исполнителя').not.toBeNull();
    await act(async () => { (reactProps(btn!)!.onClick as () => void)(); await Promise.resolve(); });
    expect(backNav.featureBackNavFrom('chat-target'), 'указатель на несуществующий чат').toBeNull();
    expect(backNav.featureBackNavFrom('chat-old'), 'прежняя точка возврата потеряна').not.toBeNull();
    root.unmount();
    uninstallFakeDom();
  });

  // Ревью Ф9b, зам. 3: пометка «в архиве» в попапе идёт из marks (агрегат фичи), а не из
  // ex.chatArchived по себе. Мутация «снять chatArchived={marks…}» красит этот тест.
  it('архивный чат исполнителя → пометка «в архиве» на кнопке «чат»', () => {
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'x', taskRole: null, stepRef: null, lane: null, status: 'running', chatId: 'chat-arch', chatArchived: true, modelLabel: null, modelFull: null }] },
    ];
    installFakeDom();
    const { root, container } = mount(createElement(FeatureRunView, { run: run({ state: 'running', stages }) }));
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-chat');
    expect(btn, 'кнопка чата исполнителя').not.toBeNull();
    expect(textNodes(btn!).join(''), 'пометки «в архиве» нет').toContain('в архиве');
    // Неархивный чат — без пометки.
    const { root: r2, container: c2 } = mount(createElement(FeatureRunView, {
      run: run({ state: 'running', stages: [{ key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'y', taskRole: null, stepRef: null, lane: null, status: 'running', chatId: 'chat-live', chatArchived: false, modelLabel: null, modelFull: null }] }] }),
    }));
    const btn2 = findNodeByAttr(c2, 'data-testid', 'nav-target-chat');
    expect(btn2, 'кнопка чата').not.toBeNull();
    expect(textNodes(btn2!).join(''), 'пометка на неархивном чате').not.toContain('в архиве');
    r2.unmount();
    root.unmount();
    uninstallFakeDom();
  });
});

// === Гейт фич-флага (M2) + меню пилюли (K-3) — через TurnPlanPill ===
describe('TurnPlanPill: гейт фич-флага (M2)', () => {
  const todo: TodoItem = { content: 'Шаг плана', status: 'pending' };
  const sid = 's1';

  it('runs=[] → пилюля в режиме плана (нет «исполнение фичи»)', () => {
    __test.reset();
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [todo], runs: [], sessionId: sid }));
    expect(html).toContain('Показать план хода');
    expect(html).not.toContain('Показать исполнение фичи');
  });

  it('runs=[run] → пилюля в режиме фичи', () => {
    __test.reset();
    __test.setRuns(sid, [run()]);
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [run()], sessionId: sid }));
    expect(html).toContain('Показать исполнение фичи');
  });
});

describe('TurnPlanPill: K-3 (меню пилюли: fullWidth только на мобиле)', () => {
  const sid = 'k3-sess';
  // FNode не экспортирован из fakeDom — выводим тип через подпись reactProps.
  type FNode = Parameters<typeof reactProps>[0];

  function findPill(container: FNode): FNode | null {
    let hit: FNode | null = null;
    const walk = (x: FNode) => {
      if (hit) return;
      if (x.nodeType === 1 && x.nodeName === 'BUTTON') {
        const al = x.getAttribute('aria-label') ?? '';
        if (al.startsWith('Показать')) { hit = x; return; }
      }
      for (const c of x.childNodes) walk(c);
    };
    walk(container);
    return hit;
  }

  function findMenuCard(): FNode | null {
    const body = (globalThis as { document: FNode }).document.body;
    let hit: FNode | null = null;
    const walk = (x: FNode) => {
      if (hit) return;
      if (x.nodeType === 1 && x.style && typeof x.style.background === 'string' && x.style.background) { hit = x; return; }
      for (const c of x.childNodes) walk(c);
    };
    walk(body);
    return hit;
  }

  function openPillMenu(mobile: boolean): FNode | null {
    mobileFlag.value = mobile;
    __test.reset();
    __test.setRuns(sid, [run()]);
    installFakeDom();
    const { root, container } = mount(createElement(TurnPlanPill, { todos: [], runs: [run()], sessionId: sid }));
    const pill = findPill(container);
    if (pill) {
      act(() => {
        (reactProps(pill)!.onClick as (e: unknown) => void)({
          currentTarget: { getBoundingClientRect: () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 }) },
        });
      });
    }
    const card = findMenuCard();
    root.unmount();
    uninstallFakeDom();
    return card;
  }

  it('десктоп: fullWidth не включается — у карточки нет right', () => {
    const card = openPillMenu(false);
    expect(card, 'карточка меню (десктоп) не найдена').not.toBeNull();
    expect(card!.style.right, 'десктоп: right не задан (fullWidth off)').toBeUndefined();
  });

  it('мобила: fullWidth включён — у карточки right:8px (во всю ширину)', () => {
    const card = openPillMenu(true);
    expect(card, 'карточка меню (мобила) не найдена').not.toBeNull();
    expect(card!.style.right, 'мобила: right:8px (fullWidth on)').toBe('8px');
  });
});

describe('FeatureRunView: переход в задачу несёт проект эпика', () => {
  it('клик «задача» исполнителя → openTaskInSection({ id, projectId эпика })', () => {
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'task-x', taskRole: null, stepRef: null, lane: null, status: 'running', chatId: null, chatArchived: false, modelLabel: null, modelFull: null }] },
    ];
    installFakeDom();
    const r = run({ state: 'running', stages, epic: task({ id: 'epic-1', title: 'Пульт', projectId: 'proj-A' }) });
    const { root, container } = mount(createElement(FeatureRunView, { run: r }));
    const btn = findNodeByAttr(container, 'data-testid', 'nav-target-task');
    expect(btn, 'кнопка «задача» исполнителя').not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(mockOpenTask.calls).toEqual([{ id: 'task-x', projectId: 'proj-A' }]);
    // Панель в чате: точка возврата на фичу (без вкладки в чипе), чип на задаче читает её.
    expect(backNav.getBackNav()).toMatchObject({ fromKind: 'feature', to: { kind: 'task', id: 'task-x' }, showTab: false });
    expect(backNav.featureBackNavFromTask('task-x')).not.toBeNull();
    root.unmount();
    uninstallFakeDom();
  });
});
