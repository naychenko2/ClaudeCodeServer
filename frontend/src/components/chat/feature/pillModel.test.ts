// Чистая функция buildPillModel (feature/pillModel.ts): из FeatureRun[] строит модель
// пилюли. Тестирую ВСЕ состояния макета v1/v2: идёт, wait, stopped, закрыта одна,
// все закрыты (N>1), параллельные исполнители, никто не идёт, +N, ↺≥3 (горячий),
// 360 px (без слова «фича»).
//
// Окружение node (без jsdom): getPersonaById — мок (hoisted-карта id→имя).
// «360 px» — тест компонента (TurnPlanPill) с matchMedia→mobile: слово «фича» скрыто.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { C } from '../../../lib/design';
import type { FeatureNow, FeatureRun, FeatureTask } from '../../../types/featureRun';
import { buildPillModel } from './pillModel';
import { TurnPlanPill } from '../TurnPlanPill';
import { __test, setSelectedFeatureId } from '../../../lib/featureRuns';
import { installFakeDom, uninstallFakeDom } from '../__tests__/fakeDom';

// Стор персон (lib/personas) — мок: hoisted-карта id→имя. Исполнитель резолвится по имени.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', () => ({
  getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
}));
beforeEach(() => { personaMap.clear(); });

// ── Фикстуры ─────────────────────────────────────────────────────────────────

function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

// Агрегат фичи: только поля, которые использует buildPillModel.
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator' }),
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    ...over,
  };
}

// «Сейчас»: одна идущая задача (стадия + исполнитель).
function now(stage: string, personaId?: string | null): FeatureNow {
  return { taskId: 'n1', stage, personaId: personaId ?? null, title: 'Шаг', startedAt: null, linkedSessionId: null };
}

// Имена персон для фикстур.
function setNames() {
  personaMap.set('denis', 'Денис');
  personaMap.set('kira', 'Кира');
}

// ── run: идёт ─────────────────────────────────────────────────────────────────

describe('buildPillModel: идёт (run)', () => {
  it('одна идущая задача: фича · 8/9 ↺3 · Производство · Денис', () => {
    setNames();
    const m = buildPillModel([run({
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 3, withdrawn: 0, loops: 3 },
      stepsAccepted: 8, stepsTotal: 9,
      now: [now('prod', 'denis')],
    })])!;
    expect(m.state).toBe('run');
    expect(m.word).toBe('фича');
    expect(m.counter).toBe('8/9');
    expect(m.loops).toBe(3);
    expect(m.more).toBe(0);
    expect(m.text).toBe('Производство · Денис');
    expect(m.icon).toBe('route');
    expect(m.iconColor).toBe(C.accent);
    expect(m.textColor).toBeNull();
    expect(m.textWeight).toBe(400);
    expect(m.loopsHot).toBe(true); // ↺3 — горячий
  });

  it('параллельная работа: имена через запятую «Кира, Денис»', () => {
    setNames();
    const m = buildPillModel([run({
      stepsAccepted: 8, stepsTotal: 9,
      now: [now('prod', 'kira'), now('prod', 'denis')],
    })])!;
    expect(m.text).toBe('Производство · Кира, Денис');
  });

  it('дубликаты исполнителей — уникальные (один Денис, не два)', () => {
    setNames();
    const m = buildPillModel([run({
      stepsAccepted: 8, stepsTotal: 9,
      now: [now('prod', 'denis'), now('prod', 'denis')],
    })])!;
    expect(m.text).toBe('Производство · Денис');
  });

  it('никто не идёт (now пуст): стадия из stages, без имени', () => {
    const m = buildPillModel([run({
      stepsAccepted: 8, stepsTotal: 9,
      stages: [
        { key: 'cut', state: 'done', passes: 1, taskIds: [], rounds: [] },
        { key: 'plan', state: 'done', passes: 1, taskIds: [], rounds: [] },
        { key: 'prod', state: 'done', passes: 1, taskIds: [], rounds: [] },
        { key: 'review', state: 'active', passes: 1, taskIds: [], rounds: [] },
        { key: 'qa', state: 'pending', passes: 0, taskIds: [], rounds: [] },
        { key: 'final', state: 'pending', passes: 0, taskIds: [], rounds: [] },
        { key: 'merge', state: 'pending', passes: 0, taskIds: [], rounds: [] },
      ],
    })])!;
    expect(m.text).toBe('Ревью'); // активная стадия, без имени
  });

  it('никто не идёт и stages нет: дефолт «Разрез»', () => {
    const m = buildPillModel([run({ stepsAccepted: 0, stepsTotal: 0 })])!;
    expect(m.text).toBe('Разрез');
    expect(m.counter).toBe('0/—');
  });

  it('↺ < 3 — не горячий', () => {
    const m = buildPillModel([run({
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 2, withdrawn: 0, loops: 2 },
      stepsAccepted: 8, stepsTotal: 9, now: [now('prod', 'denis')],
    })])!;
    expect(m.loopsHot).toBe(false);
  });
});

// ── wait: ждёт вас ───────────────────────────────────────────────────────────

describe('buildPillModel: ждёт вас (wait)', () => {
  it('мерж: ромб, «ждёт вас · мерж», C.info 600, aria «фича ждёт вас»', () => {
    const m = buildPillModel([run({
      stepsAccepted: 9, stepsTotal: 9,
      attention: { kind: 'wait', reason: 'мерж' },
    })])!;
    expect(m.state).toBe('wait');
    expect(m.icon).toBe('diamond');
    expect(m.iconColor).toBe(C.info);
    expect(m.text).toBe('ждёт вас · мерж');
    expect(m.textColor).toBe(C.info);
    expect(m.textWeight).toBe(600);
    expect(m.ariaExtra).toBe('фича ждёт вас');
  });

  it('reason бэка (не мерж) — в текст', () => {
    const m = buildPillModel([run({
      stepsAccepted: 9, stepsTotal: 9,
      attention: { kind: 'wait', reason: 'нужен выбор линии' },
    })])!;
    expect(m.text).toBe('ждёт вас · нужен выбор линии');
  });

  it('reason пуст → «мерж» (дефолт)', () => {
    const m = buildPillModel([run({
      stepsAccepted: 9, stepsTotal: 9,
      attention: { kind: 'wait', reason: null },
    })])!;
    expect(m.text).toBe('ждёт вас · мерж');
  });
});

// ── stopped: исполнитель упал ─────────────────────────────────────────────────

describe('buildPillModel: исполнитель упал (stopped)', () => {
  it('квадрат, «стоит · Денис», C.dangerText 600, aria «исполнитель остановлен»', () => {
    setNames();
    const m = buildPillModel([run({
      stepsAccepted: 8, stepsTotal: 9,
      attention: { kind: 'stopped', personaId: 'denis', reason: 'таймаут' },
    })])!;
    expect(m.state).toBe('stopped');
    expect(m.icon).toBe('square');
    expect(m.iconColor).toBe(C.danger);
    expect(m.text).toBe('стоит · Денис');
    expect(m.textColor).toBe(C.dangerText);
    expect(m.textWeight).toBe(600);
    expect(m.ariaExtra).toBe('исполнитель остановлен');
  });

  it('исполнитель не найден → «стоит» (без имени)', () => {
    const m = buildPillModel([run({
      stepsAccepted: 8, stepsTotal: 9,
      attention: { kind: 'stopped', personaId: 'ghost', reason: 'таймаут' },
    })])!;
    expect(m.text).toBe('стоит');
  });

  it('приоритет: stopped сильнее wait (attention stopped + awaitingHumanSince → stopped)', () => {
    // Оба сигнала на месте: attention.kind='stopped' (новое) И awaitingHumanSince (старое).
    // Мутация «wait первым» (проверить awaitingHumanSince до stopped) покрасит: state станет 'wait'.
    setNames();
    const m = buildPillModel([run({
      stepsAccepted: 8, stepsTotal: 9,
      awaitingHumanSince: '2026-10-17T10:00:00Z',
      attention: { kind: 'stopped', personaId: 'denis', reason: 'таймаут' },
    })])!;
    expect(m.state).toBe('stopped');
    expect(m.icon).toBe('square');
  });
});

// ── closed: закрыта одна фича ─────────────────────────────────────────────────

describe('buildPillModel: закрыта одна фича (closed)', () => {
  it('«Фича смержена», Route C.textMuted, счётчик 9/9', () => {
    const m = buildPillModel([run({
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 3, withdrawn: 0, loops: 3 },
      stepsAccepted: 9, stepsTotal: 9, closed: true,
    })])!;
    expect(m.state).toBe('closed');
    expect(m.word).toBe('фича');
    expect(m.counter).toBe('9/9');
    expect(m.text).toBe('Фича смержена');
    expect(m.icon).toBe('route');
    expect(m.iconColor).toBe(C.textMuted);
  });
});

// ── allClosed: все закрыты (N>1) ───────────────────────────────────────────────

describe('buildPillModel: все закрыты (N>1)', () => {
  it('«фичи · 6 · все закрыты», без счётчика шагов, без +N', () => {
    const runs = [1, 2, 3, 4, 5, 6].map(n => run({
      featureId: `epic-${n}`,
      epic: task({ id: `epic-${n}`, title: `Фича ${n}` }),
      stepsAccepted: 9, stepsTotal: 9, closed: true,
    }));
    const m = buildPillModel(runs)!;
    expect(m.state).toBe('allClosed');
    expect(m.word).toBe('фичи');
    expect(m.counter).toBeNull(); // без счётчика шагов
    expect(m.more).toBe(0); // без +N
    expect(m.text).toBe('6 · все закрыты');
    expect(m.icon).toBe('route');
    expect(m.iconColor).toBe(C.textMuted); // приглушённый Route (как у closed)
  });

  it('N=1 закрыта → НЕ allClosed (closed, «Фича смержена»)', () => {
    const m = buildPillModel([run({ stepsAccepted: 9, stepsTotal: 9, closed: true })])!;
    expect(m.state).toBe('closed');
    expect(m.text).toBe('Фича смержена');
  });

  it('не все закрыты (одна идёт) → run, +N = N-1', () => {
    setNames();
    const runs = [
      run({ featureId: 'a', epic: task({ id: 'a', title: 'A' }), stepsAccepted: 8, stepsTotal: 9, now: [now('prod', 'denis')] }),
      run({ featureId: 'b', epic: task({ id: 'b', title: 'B' }), stepsAccepted: 9, stepsTotal: 9, closed: true }),
    ];
    const m = buildPillModel(runs)!;
    expect(m.state).toBe('run');
    expect(m.more).toBe(1); // +1 (остальная фича)
  });
});

// ── +N: число остальных фич ───────────────────────────────────────────────────

describe('buildPillModel: +N', () => {
  it('3 фичи, runs[0] идёт → more = 2', () => {
    setNames();
    const runs = [
      run({ featureId: 'a', epic: task({ id: 'a', title: 'A' }), stepsAccepted: 8, stepsTotal: 9, now: [now('prod', 'denis')] }),
      run({ featureId: 'b', epic: task({ id: 'b', title: 'B' }), stepsAccepted: 9, stepsTotal: 9, closed: true }),
      run({ featureId: 'c', epic: task({ id: 'c', title: 'C' }), stepsAccepted: 9, stepsTotal: 9, closed: true }),
    ];
    const m = buildPillModel(runs)!;
    expect(m.more).toBe(2);
  });
});

// ── null: фич нет ─────────────────────────────────────────────────────────────

describe('buildPillModel: null', () => {
  it('пустой runs → null', () => {
    expect(buildPillModel([])).toBeNull();
  });
});

// ── пилюля всегда runs[0]: выбор фичи в сторе её не меняет ────────────────────

describe('TurnPlanPill: всегда runs[0] (выбор фичи в сторе не меняет)', () => {
  it('в сторе выбрана runs[1], но пилюля показывает runs[0]', () => {
    setNames();
    __test.reset();
    const a = run({
      featureId: 'a', epic: task({ id: 'a', title: 'Фича A' }),
      stepsAccepted: 8, stepsTotal: 9, now: [now('prod', 'denis')],
    });
    const b = run({
      featureId: 'b', epic: task({ id: 'b', title: 'Фича B' }),
      stepsAccepted: 9, stepsTotal: 9,
      attention: { kind: 'wait', reason: 'мерж' },
    });
    __test.setRuns('s1', [a, b]);
    // В сторе выбрана runs[1] (B) — но пилюля её не слушает (всегда runs[0]).
    setSelectedFeatureId('s1', b.featureId);
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [a, b], sessionId: 's1' }));
    // Пилюля показывает runs[0] (A): «Производство · Денис», не B («ждёт вас · мерж»).
    expect(html).toContain('Производство · Денис');
    expect(html).not.toContain('ждёт вас · мерж');
  });
});

// ── 360 px: слово «фича» скрыто (компонент) ────────────────────────────────────
// Модель всегда несёт word; на мобиле компонент его не рендерит. Проверяю рендер
// TurnPlanPill с matchMedia→mobile: слово «фича» в разметке нет, остальное на месте.

describe('TurnPlanPill: 360 px — без слова «фича»', () => {
  beforeAll(() => installFakeDom());
  afterAll(() => uninstallFakeDom());

  // matchMedia → mobile (matches: true) / desktop (matches: false) для useIsMobile.
  // Явно в каждом тесте: window общий на файл, override из соседнего теста не должен
  // перетекать (изоляция).
  function setMatchMedia(matches: boolean) {
    const w = (globalThis as { window?: { matchMedia?: unknown } }).window as { matchMedia?: (q: string) => unknown } | undefined;
    if (w) w.matchMedia = () => ({ matches, addEventListener: () => {}, removeEventListener: () => {} });
  }

  it('мобила: слова «фича» нет, счётчик/↺/текст на месте', () => {
    setNames();
    __test.reset();
    const r = run({
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 3, withdrawn: 0, loops: 3 },
      stepsAccepted: 8, stepsTotal: 9, now: [now('prod', 'denis')],
    });
    __test.setRuns('s1', [r]);
    setMatchMedia(true);
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [r], sessionId: 's1' }));
    // Слово «фича» (слово-метка режима) скрыто на мобиле.
    expect(html).not.toContain('>фича<');
    // Счётчик, ↺ и текст состояния — на месте.
    expect(html).toContain('8/9');
    expect(html).toContain('↺3');
    expect(html).toContain('Производство · Денис');
  });

  it('десктоп: слово «фича» присутствует', () => {
    setNames();
    __test.reset();
    const r = run({
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 3, withdrawn: 0, loops: 3 },
      stepsAccepted: 8, stepsTotal: 9, now: [now('prod', 'denis')],
    });
    __test.setRuns('s1', [r]);
    setMatchMedia(false);
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [r], sessionId: 's1' }));
    expect(html).toContain('>фича<');
  });
});
