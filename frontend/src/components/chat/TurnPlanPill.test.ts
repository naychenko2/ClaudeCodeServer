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

// R2 minor 4: проводка chatTaskId (ChatPanel → TurnPlanPill → FeatureModeBody → FeatureRunView → Meta).
// Мутация: chatTaskId={null} в ChatPanel.tsx → роль «этот чат — …» исчезает из меты.
describe('TurnPlanPill: проводка chatTaskId → мета «этот чат — …»', () => {
  it('chatTaskId совпадает с taskId исполнителя → мета показывает роль', () => {
    const stages: FeatureStage[] = [
      { key: 'prod', state: 'active', tone: 'run', passes: 1, taskIds: [], rounds: [],
        executors: [{ personaId: null, taskId: 'task-front', taskRole: 'step', stepRef: 'Ш2', lane: 'front', status: 'running', chatId: null, chatArchived: false, modelLabel: null, modelFull: null }] },
    ];
    const r: FeatureRun = {
      featureId: 'epic-1',
      epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator' }),
      steps: [], reviews: [], qa: [], final: null, merge: null,
      state: 'running',
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
      stages,
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
    // Мета в теле (body-портал меню): «этот чат — Ш2 · фронт».
    const body = (globalThis as { document: { body: Parameters<typeof findNodeByAttr>[0] } }).document.body;
    const allText = textNodes(body).join('');
    expect(allText, 'мета «этот чат — Ш2 · фронт»').toContain('этот чат — Ш2 · фронт');
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
  return { key, state: 'active', passes: 1, taskIds: [], rounds: [], firstStartedAt: null, lastCompletedAt: null, approxMinutes: 0, overlapsActive: false, executors: [] };
}
function now(stage: string, personaId: string | null): FeatureNow {
  return { taskId: 'n', stage, personaId, title: 'Шаг', startedAt: null, linkedSessionId: null };
}

// Рендер кнопки-пилюли в режиме фичи (runs в сторе + явный режим feature).
function renderFeaturePill(runs: FeatureRun[], sessionId: string): string {
  __test.reset();
  __test.setRuns(sessionId, runs);
  setTurnPlanMode(sessionId, 'feature');
  return renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs, sessionId }));
}

describe('TurnPlanPill: режим фичи — текст по state (макет S[state].pill)', () => {
  beforeEach(() => { personaMap.clear(); });

  it('running: «фича · <b>Производство</b> · Кира, Денис» (жирная часть + имена)', () => {
    personaMap.set('kira', 'Кира');
    personaMap.set('denis', 'Денис');
    const html = renderFeaturePill([run({
      state: 'running', stages: [activeStage('prod')], now: [now('prod', 'kira'), now('prod', 'denis')],
    })], 's-run');
    expect(html).toContain('фича · ');
    expect(html).toContain('<b');
    expect(html).toContain('Производство');
    expect(html).toContain(' · Кира, Денис');
  });

  it('waiting: «фича · <b>{этап}</b> · ждёт вас»', () => {
    const html = renderFeaturePill([run({ state: 'waiting', stages: [activeStage('prod')] })], 's-wait');
    expect(html).toContain('Производство');
    expect(html).toContain(' · ждёт вас');
  });

  it('stopped: «фича · <b>стоит</b> · {имя}»', () => {
    personaMap.set('denis', 'Денис');
    const html = renderFeaturePill([run({
      state: 'stopped', attention: { kind: 'stopped', personaId: 'denis', reason: 'таймаут' },
    })], 's-stop');
    expect(html).toContain('стоит');
    expect(html).toContain(' · Денис');
  });

  it('merge_pending: «фича · <b>Мерж</b> · ждёт согласия»', () => {
    const html = renderFeaturePill([run({ state: 'merge_pending' })], 's-mergep');
    expect(html).toContain('Мерж');
    expect(html).toContain(' · ждёт согласия');
  });

  it('merging: «фича · <b>Мерж</b> · идёт · {имя}»', () => {
    personaMap.set('mark', 'Марк');
    const html = renderFeaturePill([run({ state: 'merging', merge: task({ id: 'm1', personaId: 'mark' }) })], 's-merging');
    expect(html).toContain('Мерж');
    expect(html).toContain(' · идёт · Марк');
  });

  it('merged: «фича · <b>смержена</b>»', () => {
    const html = renderFeaturePill([run({ state: 'merged' })], 's-merged');
    expect(html).toContain('смержена');
  });

  it('closed: «фича · <b>закрыта</b>»', () => {
    const html = renderFeaturePill([run({ state: 'closed' })], 's-closed');
    expect(html).toContain('закрыта');
  });

  it('not_started: «<b>Не начата</b>» (без префикса «фича · »)', () => {
    const html = renderFeaturePill([run({ state: 'not_started' })], 's-notstart');
    expect(html).toContain('Не начата');
    expect(html).not.toContain('фича · Не начата');
  });

  it('idle: «<b>Простаивает</b>»', () => {
    const html = renderFeaturePill([run({ state: 'idle' })], 's-idle');
    expect(html).toContain('Простаивает');
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

  // Старый вид пилюли: счётчик шагов (8/9) + ↺ + +N + отдельное слово «фича».
  // Мутации: верни счётчик/↺ в рендер → краснеет.
  it('нет старых строк: счётчика шагов, ↺, +N', () => {
    personaMap.set('denis', 'Денис');
    const r = run({
      state: 'running', stages: [activeStage('prod')], now: [now('prod', 'denis')],
      stepsAccepted: 8, stepsTotal: 9,
      verdicts: { accepted: 8, acceptedWithRemarks: 0, returned: 1, withdrawn: 0, loops: 3 },
    });
    const html = renderFeaturePill([r], 's-nocounter');
    expect(html).not.toContain('8/9');   // счётчик шагов убран
    expect(html).not.toContain('↺');      // возвраты убраны
    expect(html).toContain('Производство');
    expect(html).toContain(' · Денис');
  });
});
