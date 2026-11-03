// Пилюля «План хода» (TurnPlanPill) — F9: одноразовый сигнал «открыть меню» (стор Ф1, per chat).
// Чип «Шаг N фичи» (TaskCreatedView) шлёт его, когда в ленте нет живой карточки плана:
// пилюля потребляет сигнал (seq) и открывает меню. Повторный сигнал (seq растёт) —
// меню открывается заново; сигнал другого чата (sessionId) пилюлю не трогает.
//
// Окружение node (без jsdom): fake-DOM (паттерн FeatureRunView.test.ts). Меню в anchor-режиме
// рисуется ПОРТАЛОМ в document.body — поэтому «меню открыто» проверяем текстом «План хода»
// в дереве body, а не в контейнере.
import { createElement } from 'react';
import { act } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { TurnPlanPill, pillMenuWidths, pillMenuFullWidth } from './TurnPlanPill';
import { __test, openPillMenu, setTurnPlanMode } from '../../lib/featureRuns';
import type { TodoItem } from '../../hooks/useSessionArtifacts';
import type { FeatureNow, FeatureRun, FeatureStage, FeatureTask } from '../../types/featureRun';
import { findNodeByAttr, installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from './__tests__/fakeDom';

// Стор персон (lib/personas) — мок (hoisted-карта id→имя): для имён в тексте пилюли.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/personas')>();
  return {
    ...orig,
    getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
    ensurePersonasLoaded: () => {},
    usePersonasVersion: () => 0,
  };
});

const zeroRect = { top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 };

function todos(): TodoItem[] {
  return [{ content: 'Шаг плана', status: 'pending' }];
}

describe('TurnPlanPill: F9 (сигнал «открыть меню»)', () => {
  beforeAll(() => {
    installFakeDom();
    // Меню в anchor-режиме читает window.innerWidth/innerHeight (расчёт позиции) —
    // фейковому window их нет, подставляем нули (позиция в тесте не важна).
    const w = (globalThis as { window: Record<string, unknown> }).window;
    w.innerWidth = 1280;
    w.innerHeight = 800;
  });
  afterAll(() => { uninstallFakeDom(); });
  beforeEach(() => { __test.reset(); });

  function mountPill(sessionId: string) {
    const node = createElement(TurnPlanPill, { todos: todos(), runs: [], sessionId });
    return mount(node);
  }

  // Меню-портал живёт в document.body, не в контейнере. Ищем по data-testid="turn-plan-menu"
  // (обёртка, которую рисует TurnPlanPill; role="menu" в общем Menu.tsx откатили — Глеб).
  function bodyHasMenu(): boolean {
    const doc = (globalThis as { document: { body: unknown } }).document as { body: Parameters<typeof findNodeByAttr>[0] };
    return findNodeByAttr(doc.body, 'data-testid', 'turn-plan-menu') !== null;
  }

  it('сигнал: меню открывается (появляется в body-портале)', () => {
    const { root } = mountPill('sess-1');
    expect(bodyHasMenu(), 'до сигнала меню нет').toBe(false);
    act(() => { openPillMenu('sess-1'); });
    expect(bodyHasMenu(), 'после сигнала меню открыто').toBe(true);
    root.unmount();
  });

  it('повторный сигнал (seq растёт): закрытое меню открывается заново', () => {
    const { root, container } = mountPill('sess-1');
    act(() => { openPillMenu('sess-1'); });
    expect(bodyHasMenu(), 'первый сигнал: меню открыто').toBe(true);
    // Пользователь закрывает меню кликом по самой пилюле
    const btn = findNodeByAttr(container, 'aria-label', 'Показать план хода');
    expect(btn, 'кнопка-пилюля').not.toBeNull();
    act(() => (reactProps(btn!)!.onClick as (e: unknown) => void)({ currentTarget: { getBoundingClientRect: () => zeroRect } }));
    expect(bodyHasMenu(), 'после клика меню закрыто').toBe(false);
    // Повторный клик по чипу — новый сигнал (seq 2): меню открывается снова
    act(() => { openPillMenu('sess-1'); });
    expect(bodyHasMenu(), 'повторный сигнал: меню открыто снова').toBe(true);
    root.unmount();
  });

  it('сигнал другого чата (sessionId) пилюлю не трогает', () => {
    const { root } = mountPill('sess-A');
    act(() => { openPillMenu('sess-B'); });
    expect(bodyHasMenu(), 'сигнал чата B меню чата A не открыл').toBe(false);
    root.unmount();
  });
});

// R2 minor 4 / F2: проводка chatTaskId (ChatPanel → TurnPlanPill → FeatureModeBody →
// FeatureStepView → подвал «этот чат — …»). Мутация: chatTaskId={null} в ChatPanel.tsx →
// роль исчезает из подвала.
describe('TurnPlanPill: проводка chatTaskId → подвал «этот чат — …»', () => {
  it('chatTaskId совпадает с задачей шага → подвал показывает роль', () => {
    const r: FeatureRun = {
      featureId: 'epic-1',
      epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator' }),
      steps: [task({ id: 'task-front', title: 'Ш2', role: 'step', stepRef: 'Ш2', lane: 'front', status: 'inProgress' })], reviews: [], qa: [], final: null, merge: null,
      state: 'running',
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
      stepChains: [{ stepRef: 'Ш2', title: 'Ш2', taskIds: ['task-front'], outcome: 'running', status: 'идёт', durationMinutes: 5, rounds: [], chatId: null, lane: 'front' }],
    } as FeatureRun;
    __test.reset();
    __test.setRuns('s-chatid', [r]);
    setTurnPlanMode('s-chatid', 'feature');
    const { root, container } = mount(createElement(TurnPlanPill, {
      todos: [], runs: [r], sessionId: 's-chatid', chatTaskId: 'task-front',
    }));
    // Открываем меню (клик по пилюле).
    const btn = findNodeByAttr(container, 'aria-label', 'Показать исполнение фичи');
    expect(btn, 'кнопка-пилюля фичи').not.toBeNull();
    act(() => (reactProps(btn!)!.onClick as (e: unknown) => void)({ currentTarget: { getBoundingClientRect: () => zeroRect } }));
    // Подвал в теле (body-портал меню): «этот чат —» + чип роли «Ш2 · фронт» (два узла).
    const body = (globalThis as { document: { body: Parameters<typeof findNodeByAttr>[0] } }).document.body;
    const allText = textNodes(body).join(' ');
    expect(allText, 'подвал «этот чат —» + роль').toContain('этот чат —');
    expect(allText, 'роль «Ш2 · фронт»').toContain('Ш2 · фронт');
    root.unmount();
  });
});

describe('TurnPlanPill: n-2/n-3 (ширина меню)', () => {
  it('десктоп: фиксированная 380 в обоих режимах (план и фича)', () => {
    expect(pillMenuWidths(false)).toEqual({ minWidth: 380, maxWidth: 380 });
  });
  it('мобила: прежние min/max (280–380); фактическая ширина — во всю (pillMenuFullWidth)', () => {
    expect(pillMenuWidths(true)).toEqual({ minWidth: 280, maxWidth: 380 });
  });
  // r-3: на мобиле fullWidth в обоих режимах (одинаковая ширина, переключение не дёргает);
  // на десктопе — false (фикс. 380). Мутация: верни fullWidth={featureMode && isMobile}
  // (только фича) → план (featureMode=false) даст false → тест краснеет.
  it('r-3: на мобиле fullWidth в обоих режимах (одинаковая ширина); десктоп — false', () => {
    expect(pillMenuFullWidth(true, true)).toBe(true);   // фича
    expect(pillMenuFullWidth(true, false)).toBe(true);   // план
    expect(pillMenuFullWidth(false, true)).toBe(false);   // десктоп — фикс. 380
    expect(pillMenuFullWidth(false, false)).toBe(false);
  });
});

// ── Пилюля в режиме фичи: текст по run.state (макет S[state].pill) ─────────────
// Рендер renderToStaticMarkup (node, без jsdom): window нет → isMobile=false (десктоп).
// Режим «фича» — из стора: setRuns (срез) + setTurnPlanMode('feature').

function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator' }),
    steps: [], reviews: [], qa: [], final: null, merge: null,
    state: 'running',
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    ...over,
  } as FeatureRun;
}
function activeStage(key: FeatureStage['key']): FeatureStage {
  return { key, state: 'active', passes: 1, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, barEnd: null, approxMinutes: 0, overlapsActive: false, executors: [] };
}
function now(stage: string, personaId: string | null): FeatureNow {
  return { taskId: 'n', stage, personaId, title: 'Шаг', startedAt: null, linkedSessionId: null };
}

// Рендер кнопки-пилюли в режиме фичи (runs в сторе + явный режим feature).
function renderFeaturePill(runs: FeatureRun[], sessionId: string, chatTaskId: string | null = null): string {
  __test.reset();
  __test.setRuns(sessionId, runs);
  setTurnPlanMode(sessionId, 'feature');
  return renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs, sessionId, chatTaskId }));
}

// F2 (макет v4 §П2 «Мой шаг»): пилюля в режиме фичи говорит про ШАГ ЭТОГО ЧАТА, а не
// пересказывает фичу (её показывает строка шапки В2). Чат без шага — «фича · {pct} %».
describe('TurnPlanPill: режим фичи — П2 «Мой шаг» (F2)', () => {
  beforeEach(() => { personaMap.clear(); });

  it('чат шага: «Ш8 · <b>идёт</b> · 24 мин» (stepRef + бэковый статус + давность)', () => {
    const html = renderFeaturePill([run({
      stepChains: [{ stepRef: 'Ш8', title: 'Вёрстка шапки', taskIds: ['t-8'], outcome: 'running', status: 'идёт', durationMinutes: 24, rounds: [], chatId: null } as never],
    })], 's-step', 't-8');
    expect(html).toContain('Ш8 · ');
    expect(html).toContain('<b');
    expect(html).toContain('идёт');
    expect(html).toContain(' · 24 мин');
  });

  it('чат без шага: «фича · 56 %» (процент по трекеру)', () => {
    const html = renderFeaturePill([run({
      stepProgress: { planState: 'cut', total: 9, accepted: 5, review: 1, work: 2, notCreated: 1, withdrawn: 0, unlinkedReworks: 0 },
    })], 's-orch', 't-куратора');
    expect(html).toContain('фича · ');
    expect(html).toContain('56 %');
  });

  it('waiting: «Ждёт вас · Кира» тоном warn (текст перекрашен)', () => {
    personaMap.set('kira', 'Кира');
    const html = renderFeaturePill([run({
      state: 'waiting', attention: { kind: 'wait', personaId: 'kira', reason: null },
    })], 's-wait', 't-1');
    expect(html).toContain('Ждёт вас · Кира');
    expect(html).toContain('var(--c-warning-text)');
  });

  it('stopped: «Стоит · Денис» тоном danger', () => {
    personaMap.set('denis', 'Денис');
    const html = renderFeaturePill([run({
      state: 'stopped', attention: { kind: 'stopped', personaId: 'denis', reason: 'таймаут' },
    })], 's-stop', 't-2');
    expect(html).toContain('Стоит · Денис');
    expect(html).toContain('var(--c-danger-text)');
  });

  it('merge_pending: «Ждёт согласия на мерж»', () => {
    const html = renderFeaturePill([run({ state: 'merge_pending' })], 's-mergep', null);
    expect(html).toContain('Ждёт согласия на мерж');
  });
});

describe('TurnPlanPill: режим фичи — загрузка/ошибка и старые строки', () => {
  beforeEach(() => { personaMap.clear(); });

  it('loading (run ещё нет): «фича · …»', () => {
    __test.reset();
    __test.setRuns('s-load', []);
    __test.setLoading('s-load', true);
    // R2 major 4: runs пуст, но загрузка → умолчание режима 'feature' (без ручного setTurnPlanMode).
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [], sessionId: 's-load' }));
    expect(html).toContain('фича · …');
  });

  it('error (run ещё нет): «фича · …»', () => {
    __test.reset();
    __test.setRuns('s-err', []);
    __test.setError('s-err', 'Сервер не ответил');
    // R2 major 4: runs пуст, но ошибка → умолчание режима 'feature' (без ручного setTurnPlanMode).
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [], sessionId: 's-err' }));
    expect(html).toContain('фича · …');
  });

  // Старый вид пилюли: счётчик шагов (8/9) + ↺. Мутации: верни счётчик/↺ в рендер → краснеет.
  it('нет старых строк: счётчика шагов, ↺', () => {
    const r = run({
      state: 'running',
      stepChains: [{ stepRef: 'Ш8', title: 'Вёрстка', taskIds: ['t-8'], outcome: 'running', status: 'идёт', durationMinutes: 24, rounds: [], chatId: null } as never],
      stepsAccepted: 8, stepsTotal: 9,
      verdicts: { accepted: 8, acceptedWithRemarks: 0, returned: 1, withdrawn: 0, loops: 3 },
    });
    const html = renderFeaturePill([r], 's-nocounter', 't-8');
    expect(html).not.toContain('8/9');   // счётчик шагов убран
    expect(html).not.toContain('↺');      // возвраты убраны
    expect(html).toContain('Ш8 · ');
    expect(html).toContain('идёт');
  });
});
