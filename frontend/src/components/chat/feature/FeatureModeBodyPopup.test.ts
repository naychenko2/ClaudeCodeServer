// Тело ПОПАПА «Исполнение фичи» (FeatureModeBody, variant="pill") по утверждённому
// макету (turn-plan-feature-popup-1). Интеграционные проверки поверх FeatureRunView:
//   • 9 состояний: строка состояния + главное действие (по таблице макета);
//   • «стоит» — без «Перезапустить» (только «Чат {имя}»);
//   • «Мерж…» открывает FeatureMergeDialog (общая модалка Ф6);
//   • раскрыт ОДИН этап; «впереди» (pending) не кликается (div, не button);
//   • переход (чат/задача) закрывает попап — вызывает openChatById/openTaskInSection;
//   • загрузка (скелет) / ошибка («Повторить») / повтор;
//   • 360: тач-цели ≥ 40;
//   • счётчик «acc/tot · ↺N» из шапки УБРАН.
// Мутации (проверяют инварианты): раскрыть два этапа сразу → краснеет;
// вернуть «Перезапустить» → краснеет.
import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureRun, FeatureTask, FeatureStage, FeatureStageExecutor, FeatureAttention } from '../../../types/featureRun';
import { FeatureModeBody } from './FeatureModeBody';
import { __test } from '../../../lib/featureRuns';
import { installFakeDom, mount, reactProps, uninstallFakeDom } from '../__tests__/fakeDom';

// useIsMobile — управляемый флаг (SSR по умолчанию false).
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});
// Стор персон — мок (hoisted-карта id→имя); ensurePersonasLoaded/usePersonasVersion — no-op.
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
// Навигация: openChatById (lib/openChat) и openTaskInSection (lib/tasks) — vi.fn (попап закрывается переходом).
const { navMock } = vi.hoisted(() => ({ navMock: { openChat: [] as string[], openTask: [] as string[] } }));
vi.mock('../../../lib/openChat', () => ({
  openChatById: (id: string) => { navMock.openChat.push(id); },
}));
vi.mock('../../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return { ...orig, openTaskInSection: (t: { id: string }) => { navMock.openTask.push(t.id); } };
});
// api (lib/api) — mergeFeature (мерж) + execute (не используется попапом, но мок нужен).
const { apiMock } = vi.hoisted(() => ({ apiMock: { mergeImpl: async (_id: string) => {} } }));
vi.mock('../../../lib/api', () => ({
  api: { tasks: { mergeFeature: (id: string) => apiMock.mergeImpl(id), execute: async (_id: string) => {} } },
}));
// toast (lib/toast) — «Задача мержа создана» при успехе.
const { toastMock } = vi.hoisted(() => ({ toastMock: { calls: [] as string[] } }));
vi.mock('../../../lib/toast', () => ({
  showToast: (title: string) => { toastMock.calls.push(title); },
}));

beforeEach(() => {
  personaMap.clear(); mobileFlag.value = false;
  navMock.openChat.length = 0; navMock.openTask.length = 0;
  apiMock.mergeImpl = async () => {}; toastMock.calls.length = 0;
  __test.reset();
});

// Задача-заглушка.
function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

// Агрегат фичи: эпик + state; остальное «тихое».
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

// Рендер попапа (меню пилюли, variant="pill"). Стор: срез фич → режим feature.
function renderPopup(runs: FeatureRun[], sessionId: string): string {
  __test.setRuns(sessionId, runs);
  return renderToStaticMarkup(
    createElement(FeatureModeBody, { sessionId, todos: [], runs, isMobile: mobileFlag.value, variant: 'pill' }),
  );
}

// Исполнитель стадии (для проверок «один этап раскрыт»).
function ex(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return { personaId: null, taskId: 'x', taskRole: null, stepRef: null, lane: null, status: 'done', chatId: null, chatArchived: false, modelLabel: null, modelFull: null, ...over };
}

// === 9 состояний: строка состояния + главное действие (по таблице макета) ===
describe('попап: 9 состояний — строка и действие', () => {
  it('not_started/idle → «Не начата.», без действия', () => {
    for (const st of ['not_started', 'idle'] as const) {
      const html = renderPopup([run({ state: st })], `s-${st}`);
      expect(html).toContain('Не начата.');
    }
  });

  it('running → «идёт» (тон now), без действия', () => {
    const html = renderPopup([run({ state: 'running' })], 's-running');
    expect(html).toContain('идёт');
  });

  it('waiting → «ждёт вас» + «Ответить {имя}» (чат)', () => {
    personaMap.set('kira', 'Кира');
    const att: FeatureAttention = { kind: 'wait', reason: 'нужен выбор', personaId: 'kira', linkedSessionId: 'sess-k' };
    const html = renderPopup([run({ state: 'waiting', attention: att })], 's-waiting');
    expect(html).toContain('ждёт вас');
    expect(html).toContain('Ответить Кира');
  });

  it('stopped → «стоит» + «Чат {имя}» — БЕЗ «Перезапустить»', () => {
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', personaId: 'denis', linkedSessionId: 'sess-d' };
    const html = renderPopup([run({ state: 'stopped', attention: att })], 's-stopped');
    expect(html).toContain('стоит');
    expect(html).toContain('Чат Денис');
    expect(html).not.toContain('Перезапустить');
  });

  it('merge_pending → «Мерж ждёт согласия» + «Мерж…» + пара финала', () => {
    const html = renderPopup([run({ state: 'merge_pending', final: task({ id: 'f1', linkedSessionId: 'sess-f' }) })], 's-mergep');
    expect(html).toContain('Мерж ждёт согласия');
    expect(html).toContain('Мерж…');
  });

  it('merging → «Мерж идёт» + пара (чат/задача мержа)', () => {
    const html = renderPopup([run({ state: 'merging', merge: task({ id: 'm1', linkedSessionId: 'sess-m' }) })], 's-merging');
    expect(html).toContain('Мерж идёт');
  });

  it('merged → «Смержена» (тон success)', () => {
    const html = renderPopup([run({ state: 'merged', summary: { mergedAt: '2026-10-15T21:09:00Z', durationMinutes: 370, tasks: 14, steps: 9, loops: 3 } })], 's-merged');
    expect(html).toContain('Смержена');
  });

  it('closed → «Закрыта без мержа» + пара (эпик)', () => {
    const html = renderPopup([run({ state: 'closed' })], 's-closed');
    expect(html).toContain('Закрыта без мержа');
  });
});

// === «Мерж…» открывает FeatureMergeDialog (общая модалка Ф6) ===
describe('попап: «Мерж…» → FeatureMergeDialog', () => {
  it('клик по «Мерж…» открывает диалог «Смержить «…» в master?»', () => {
    installFakeDom();
    __test.setRuns('s-md', [run({ state: 'merge_pending', final: task({ id: 'f1', linkedSessionId: 'sess-f' }) })]);
    const { root, container } = mount(createElement(FeatureModeBody, { sessionId: 's-md', todos: [], runs: [run({ state: 'merge_pending', final: task({ id: 'f1', linkedSessionId: 'sess-f' }) })], isMobile: false, variant: 'pill' }));
    // Кнопка «Мерж…» (не fullWidth).
    const btn = findButtonByText(container, 'Мерж…', false);
    expect(btn, 'кнопка «Мерж…» не найдена').not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    // Диалог (портал в body): заголовок «Смержить «Пульт куратора» в master?».
    const body = (globalThis as { document: { body: unknown } }).document.body;
    expect(collectText(body)).toContain('Смержить «Пульт куратора» в master?');
    root.unmount();
    uninstallFakeDom();
  });
});

// === Раскрыт один этап; «впереди» не кликается ===
describe('попап: этапы', () => {
  it('раскрыт ОДИН этап (active): исполнитель active виден, done — нет', () => {
    personaMap.set('denis', 'Денис');
    personaMap.set('kira', 'Кира');
    const stages: FeatureStage[] = [
      { key: 'cut', state: 'done', passes: 1, taskIds: [], rounds: [], executors: [ex({ personaId: 'kira', taskId: 'cut-1' })] },
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [], runningSince: '2026-10-20T10:00:00Z', executors: [ex({ personaId: 'denis', taskId: 'prod-1', status: 'running' })] },
      { key: 'review', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'qa', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'final', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'merge', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
    ];
    const html = renderPopup([run({ state: 'running', stages })], 's-stages');
    // Active (prod) раскрыт по умолчанию → его исполнитель (Денис) виден.
    expect(html).toContain('Денис');
    // Done (cut) закрыт → его исполнитель (Кира) НЕ виден. Мутация «два этапа разом» красит.
    expect(html).not.toContain('Кира');
  });

  it('«впереди» (pending) — не кликается: строка — div, не button (aria-expanded нет)', () => {
    const stages: FeatureStage[] = [
      { key: 'cut', state: 'done', passes: 1, taskIds: [], rounds: [], executors: [] },
      { key: 'plan', state: 'done', passes: 1, taskIds: [], rounds: [], executors: [] },
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [], runningSince: '2026-10-20T10:00:00Z', executors: [] },
      { key: 'review', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'qa', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'final', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
      { key: 'merge', state: 'pending', passes: 0, taskIds: [], rounds: [], executors: [] },
    ];
    const html = renderPopup([run({ state: 'running', stages })], 's-pending');
    // 4 pending-стадии (review/qa/final/merge) — не кликабельны: aria-expanded у них нет.
    // Кликабельных (done/active) — 3 (cut, plan, prod) → aria-expanded="true"/"false" ровно 3.
    const expandedCount = (html.match(/aria-expanded=/g) ?? []).length;
    expect(expandedCount).toBe(3);
  });
});

// === Переход (чат/задача) закрывает попап ===
describe('попап: переход закрывает попап', () => {
  it('клик по «Чат {имя}» (stopped) вызывает openChatById и закрывает попап (onClose)', () => {
    installFakeDom();
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', personaId: 'denis', linkedSessionId: 'sess-d' };
    __test.setRuns('s-nav', [run({ state: 'stopped', attention: att })]);
    // R2 major 3 + minor 3: onClose — шпиг; тест проверяет именно ЗАКРЫТИЕ (не только openChatById).
    const onCloseCalls = { n: 0 };
    const { root, container } = mount(createElement(FeatureModeBody, {
      sessionId: 's-nav', todos: [], runs: [run({ state: 'stopped', attention: att })],
      isMobile: false, variant: 'pill',
      onClose: () => { onCloseCalls.n++; },
    }));
    const btn = findButtonByText(container, 'Чат Денис', false);
    expect(btn, 'кнопка «Чат Денис» не найдена').not.toBeNull();
    act(() => { (reactProps(btn!)!.onClick as () => void)(); });
    expect(navMock.openChat).toContain('sess-d');
    expect(onCloseCalls.n, 'onClose не вызван — попап не закрылся').toBe(1);
    root.unmount();
    uninstallFakeDom();
  });
});

// === Загрузка / ошибка / повтор ===
describe('попап: загрузка / ошибка / повтор', () => {
  it('loading: скелет (без имени фичи)', () => {
    __test.setRuns('s-load', []);
    __test.setLoading('s-load', true);
    // R2 major 4: runs пуст, но загрузка → умолчание режима 'feature' (без ручного setTurnPlanMode).
    const html = renderToStaticMarkup(createElement(FeatureModeBody, { sessionId: 's-load', todos: [], runs: [], isMobile: false, variant: 'pill' }));
    expect(html).not.toContain('Пульт куратора');
    expect(html).toContain('bg-selected');
  });

  it('error: «Не удалось загрузить фичу.» + «Повторить»', () => {
    __test.setRuns('s-err', []);
    __test.setError('s-err', 'Сервер не ответил');
    // R2 major 4: runs пуст, но ошибка → умолчание режима 'feature' (без ручного setTurnPlanMode).
    const html = renderToStaticMarkup(createElement(FeatureModeBody, { sessionId: 's-err', todos: [], runs: [], isMobile: false, variant: 'pill' }));
    expect(html).toContain('Не удалось загрузить фичу.');
    expect(html).toContain('Повторить');
  });
});

// === 360: тач-цели ≥ 40 ===
describe('попап: 360 (тач-цели ≥ 40)', () => {
  it('на мобиле кнопки строки состояния — minHeight 40', () => {
    mobileFlag.value = true;
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', personaId: 'denis', linkedSessionId: 'sess-d' };
    const html = renderPopup([run({ state: 'stopped', attention: att })], 's-mob');
    // Кнопка «Чат Денис» на 360 — minHeight 40 (тач-цель).
    expect(html).toContain('min-height:40px');
  });
});

// === Счётчик убран ===
describe('попап: счётчик убран', () => {
  it('в шапке нет «acc/tot · ↺N»', () => {
    const r = run({ state: 'running', stepsAccepted: 2, stepsTotal: 4, verdicts: { accepted: 2, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 1 } });
    const html = renderPopup([r], 's-counter');
    expect(html).not.toContain('2/4 · ↺1');
    expect(html).not.toMatch(/>100%</);
  });
});

// === Мутации (инварианты) ===
describe('попап: мутации', () => {
  it('МУТАЦИЯ: «Перезапустить» у «стоит» → краснеет (в макете ghost, не реализовывать)', () => {
    // Текущий код: у stopped только «Чат {имя}». Если вернуть «Перезапустить» — тест краснеет.
    personaMap.set('denis', 'Денис');
    const att: FeatureAttention = { kind: 'stopped', reason: 'auth_failed', personaId: 'denis', linkedSessionId: 'sess-d' };
    const html = renderPopup([run({ state: 'stopped', attention: att })], 's-mut1');
    expect(html).not.toContain('Перезапустить');
  });
});

// === Вспомогательные: поиск кнопки по тексту, сбор текста ===
type AnyNode = { nodeType: number; nodeName: string; childNodes: AnyNode[]; style: Record<string, string>; getAttribute?: (a: string) => string | null };
function collectText(n: AnyNode): string {
  let out = '';
  const walk = (x: AnyNode) => { if (x.nodeType === 3) out += (x as { nodeValue?: string }).nodeValue ?? ''; for (const c of x.childNodes) walk(c); };
  walk(n);
  return out;
}
function findButtonByText(root: AnyNode, text: string, fullWidth: boolean): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && (x.style?.width === '100%') === fullWidth && collectText(x).includes(text)) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(root);
  return hit;
}
