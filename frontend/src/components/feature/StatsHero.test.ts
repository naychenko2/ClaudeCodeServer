// Герой-карточка «Пульса» (ST-F4). Рендер-тесты на fakeDom: четыре состояния героя
// и их чипы, копирайт состояний, шрифт числа, полоса состава и пунктир «few».
//
// Покрытие:
//   · testid hero-running/hero-waiting/hero-merged/hero-few и чипы «идёт»/«ждёт вас»/
//     «смержена»/«только начали» (к1);
//   · замечание (а): attention.kind='stopped' — чип «стоит» (из featureRunStatusTag),
//     не «идёт»;
//   · замечание (б): state 'closed' — чип «закрыта», не «смержена»;
//   · waiting: «ждёт 42 мин», ровно одна кнопка «Открыть задачу», текста вопроса и
//     кнопки чата нет; клик — openTaskInSection('t-1') ровно один раз (к2);
//   · число: FONT.serif + FS.display, на 360 — FS.h1 (сверяется с design.ts) (к3);
//   · few: пунктир «что появится и когда», долей и полос состава нет (к3);
//   · замечание (в): widths полос — от суммы частей (60+41 при elapsed 100 → 59.4+40.6
//     = 100 %), проценты — «60 %»/«41 %», не 0.6 (замечание (г));
//   · вердикт ST-F4: closed без mergedAt — копирайт «до закрытия» (п.1); few на
//     паузе/черновике — чип из run.state (п.2); «ждёт вас» — фон infoBg и рамка info
//     (п.3); в герое нет текста вопроса attention.reason (п.4).
//
// Мутации (проверены прогоном): (1) `isMobile ? FS.h1 : FS.display` → всегда
// FS.display — краснеет тест шрифта на 360; (2) `r.minutes / partsSum` →
// `r.minutes / totals.elapsedMinutes` — краснеет тест ширины полосы (сумма 101 ≠ 100);
// (3) убрать спецслучай 'few' в heroTag (чип из featureRunStatusTag) — краснеет
// тест чипа «только начал»; (4) `s?.mergedAt` → `s` в копирайте — краснеет тест
// п.1; (5) `state === 'few' && live` → `state === 'few'` — краснеет тест п.2;
// (6) всегда bgCard/borderLight в heroStyle — краснеет тест п.3; (7) добавить
// attention.reason в подпись waiting — краснеет тест п.4.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { C, FS, FONT } from '../../lib/design';
import { StatsHero } from './StatsHero';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

const mockOpenTask: { id: string; projectId?: string }[] = [];
vi.mock('../../lib/tasks', () => ({
  openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.push(t); },
}));

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

function mkTotals(over: Partial<FeatureTimeline['totals']> = {}): FeatureTimeline['totals'] {
  return {
    elapsedMinutes: 0,
    workWallMinutes: 0,
    agentsSumMinutes: 0,
    notWorkingMinutes: 0,
    waitingMinutes: null,
    idleMinutes: null,
    waitingCount: null,
    longestWaitMinutes: null,
    errorCount: null,
    errorMinutes: null,
    ...over,
  };
}

// Полноценный таймлайн (два этапа — «данные есть»); totals даёт состав.
function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  const totals = mkTotals({ elapsedMinutes: 100, workWallMinutes: 60, waitingMinutes: 41 });
  return {
    source: 'journal',
    truncated: false,
    timeline: {
      from: '2026-10-20T10:00:00Z',
      to: '2026-10-20T16:00:00Z',
      stages: [],
      segments: ['cut', 'prod'].map((stage, i) => ({
        from: `2026-10-20T1${i}:00:00Z`,
        to: `2026-10-20T1${i}:30:00Z`,
        kind: 'work' as const,
        track: 'main' as const,
        stage,
        taskId: null,
      })),
      current: null,
      totals,
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

// «Мало данных»: один этап и 10 мин — герой в состоянии few.
function fewStats(): FeatureStats {
  return {
    source: 'approx',
    truncated: false,
    timeline: {
      from: '2026-10-24T10:00:00Z',
      to: '2026-10-24T10:10:00Z',
      stages: [],
      segments: [{ from: '2026-10-24T10:00:00Z', to: '2026-10-24T10:10:00Z', kind: 'work', track: 'main', stage: 'cut', taskId: null }],
      current: null,
      totals: mkTotals({ elapsedMinutes: 10, workWallMinutes: 10 }),
    },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
  };
}

// «Ждёт 42 мин» — since ровно 42 минуты назад от реального now (компонент сам
// берёт Date.now()).
function waitingRun(): FeatureRun {
  return mkRun({
    state: 'waiting',
    attention: {
      kind: 'wait',
      since: new Date(Date.now() - 42 * 60_000).toISOString(),
      reason: 'Вопрос по шагу',
      personaId: null,
      linkedSessionId: 's-1',
      taskId: 't-1',
    },
    epic: { id: 't-epic', title: 'Эпик' },
  });
}

function textOf(node: unknown): string {
  return textNodes(node as never).join('');
}

// mount героя: в тестовом окружении node JSX в .test.ts недоступен — createElement.
function mountHero(run: FeatureRun, stats: FeatureStats, isMobile?: boolean) {
  return mount(createElement(StatsHero, { run, stats, isMobile }));
}

beforeEach(() => { installFakeDom(); mockOpenTask.length = 0; });
afterEach(() => { uninstallFakeDom(); });

describe('четыре состояния героя', () => {
  it('running — testid hero-running, чип «идёт», число — прошло', () => {
    const { container } = mountHero(mkRun(), mkStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-running');
    expect(hero, 'герой running не найден').toBeTruthy();
    expect(textOf(findNodeByAttr(hero, 'data-testid', 'hero-chip'))).toBe('идёт');
    expect(textOf(findNodeByAttr(hero, 'data-testid', 'hero-big'))).toBe('1 ч 40 мин');
  });

  it('waiting — testid hero-waiting, чип «ждёт вас», «ждёт 42 мин»', () => {
    const { container } = mountHero(waitingRun(), mkStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-waiting');
    expect(hero, 'герой waiting не найден').toBeTruthy();
    expect(textOf(findNodeByAttr(hero, 'data-testid', 'hero-chip'))).toBe('ждёт вас');
    expect(textOf(hero)).toContain('42 мин');
  });

  it('merged — testid hero-merged, чип «смержена», число — длительность итога', () => {
    const run = mkRun({
      state: 'merged',
      closed: true,
      summary: { mergedAt: '2026-10-20T16:00:00Z', durationMinutes: 360, tasks: 12, steps: 5, loops: 2 },
    });
    const { container } = mountHero(run, mkStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-merged');
    expect(hero, 'герой merged не найден').toBeTruthy();
    expect(textOf(findNodeByAttr(hero, 'data-testid', 'hero-chip'))).toBe('смержена');
    expect(textOf(findNodeByAttr(hero, 'data-testid', 'hero-big'))).toBe('6 ч');
    expect(textOf(hero)).toContain('12 задач');
    // Реальный мерж (mergedAt) — копирайт «до мержа» (вердикт ST-F4, п. 1).
    expect(textOf(hero)).toContain('от разреза до мержа');
  });

  it('few — testid hero-few, чип «только начали», пунктир без долей', () => {
    // Один этап и elapsed < 30 — «мало данных».
    const { container } = mountHero(mkRun(), fewStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-few');
    expect(hero, 'герой few не найден').toBeTruthy();
    expect(textOf(findNodeByAttr(hero, 'data-testid', 'hero-chip'))).toBe('только начали');
    const note = findNodeByAttr(hero, 'data-testid', 'hero-few-note');
    expect(note, 'пунктир «что появится и когда» отсутствует').toBeTruthy();
    expect(textOf(note)).toContain('появятся');
    // Долей нет: ни полосы состава, ни знака процента в тексте героя.
    expect(findNodeByAttr(hero, 'data-testid', 'hero-mix-bar')).toBeNull();
    expect(textOf(hero)).not.toContain('%');
  });
});

describe('состояние вне словаря', () => {
  it('неизвестный run.state — нейтральный чип, герой не падает', () => {
    const run = mkRun({ state: 'нет-такого' as FeatureRun['state'] });
    const { container } = mountHero(run, mkStats());
    expect(textOf(findNodeByAttr(container, 'data-testid', 'hero-chip'))).toBe('статус неизвестен');
  });
});

describe('замечания Глеба по ST-F3', () => {
  it('(а) attention.kind=stopped — чип «стоит», не «идёт»', () => {
    const run = mkRun({
      state: 'stopped',
      attention: { kind: 'stopped', since: '2026-10-20T15:00:00Z', reason: 'Нет ответа', taskId: 't-2' },
    });
    const { container } = mountHero(run, mkStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-running');
    expect(hero, 'герой не найден').toBeTruthy();
    const chip = textOf(findNodeByAttr(hero, 'data-testid', 'hero-chip'));
    expect(chip).toBe('стоит');
    expect(chip).not.toBe('идёт');
  });

  it('(б) state closed — чип «закрыта», не «смержена»', () => {
    const run = mkRun({ state: 'closed', closed: true, summary: { durationMinutes: 200, tasks: 3, steps: 1, loops: 0 } });
    const { container } = mountHero(run, mkStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-merged');
    const chip = textOf(findNodeByAttr(hero, 'data-testid', 'hero-chip'));
    expect(chip).toContain('закрыта');
    expect(chip).not.toContain('смержена');
  });

  it('(в) полоса — от суммы частей: 60+41 при elapsed 100 → ширины в сумме 100 %', () => {
    const { container } = mountHero(mkRun(), mkStats());
    const bar = findNodeByAttr(container, 'data-testid', 'hero-mix-bar');
    expect(bar, 'полоса состава не найдена').toBeTruthy();
    const widths = bar.children.map(c => Number((c.style as Record<string, string>).width?.replace('%', '')));
    const sum = widths.reduce((a, w) => a + w, 0);
    expect(widths.length).toBe(2);
    expect(Math.abs(sum - 100)).toBeLessThan(0.1);
  });

  it('(г) доли — проценты: «60 %» и «41 %», не 0.6', () => {
    const { container } = mountHero(mkRun(), mkStats());
    const rowWork = findNodeByAttr(container, 'data-testid', 'hero-mix-row-work');
    const rowWait = findNodeByAttr(container, 'data-testid', 'hero-mix-row-waiting');
    expect(textOf(rowWork)).toContain('60 %');
    expect(textOf(rowWait)).toContain('41 %');
  });
});

describe('вердикт Глеба ST-F4', () => {
  it('п.1 closed без мержа — «от разреза до закрытия», не «до мержа»', () => {
    const run = mkRun({
      state: 'closed',
      closed: true,
      summary: { mergedAt: null, durationMinutes: 200, tasks: 3, steps: 1, loops: 0 },
    });
    const { container } = mountHero(run, mkStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-merged');
    expect(textOf(hero)).toContain('от разреза до закрытия');
    expect(textOf(hero)).not.toContain('до мержа');
    // Counts бэка никуда не исчезают: они правдивы и без мержа.
    expect(textOf(hero)).toContain('3 задачи');
  });

  it('п.2 few на паузе и в черновике — чип из run.state, не «только начали»', () => {
    for (const [state, label] of [['paused', 'на паузе'], ['draft', 'черновик']] as const) {
      const { container } = mountHero(mkRun({ state }), fewStats());
      const hero = findNodeByAttr(container, 'data-testid', 'hero-few');
      expect(hero, `герой few для ${state} не найден`).toBeTruthy();
      const chip = textOf(findNodeByAttr(hero, 'data-testid', 'hero-chip'));
      expect(chip).toBe(label);
      expect(chip).not.toBe('только начали');
    }
  });

  it('п.3 «ждёт вас» — фон infoBg и рамка info; running — прежний bgCard/borderLight', () => {
    const { container } = mountHero(waitingRun(), mkStats());
    const wait = (findNodeByAttr(container, 'data-testid', 'hero-waiting').style as Record<string, string>);
    expect(wait.background).toBe(C.infoBg);
    expect(wait.border).toBe(`1px solid ${C.info}`);

    const run = mountHero(mkRun(), mkStats()).container;
    const style = findNodeByAttr(run, 'data-testid', 'hero-running').style as Record<string, string>;
    expect(style.background).toBe(C.bgCard);
    expect(style.border).toBe(`1px solid ${C.borderLight}`);
  });
});

describe('состояние «ждёт вас» по решению Андрея', () => {
  it('склонение счётчиков итога: 1 / 2 / 5 / 11 / 21', () => {
    const cases: Array<[number, string]> = [[1, '1 задача · 1 шаг · 1 возврат'], [2, '2 задачи · 2 шага · 2 возврата'],
      [5, '5 задач · 5 шагов · 5 возвратов'], [11, '11 задач · 11 шагов · 11 возвратов'], [21, '21 задача · 21 шаг · 21 возврат']];
    for (const [n, expected] of cases) {
      const run = mkRun({ state: 'merged', summary: { mergedAt: '2026-10-20T16:00:00Z', durationMinutes: 60, tasks: n, steps: n, loops: n } });
      const { container } = mountHero(run, mkStats());
      expect(textOf(findNodeByAttr(container, 'data-testid', 'hero-merged'))).toContain(expected);
    }
  });

  it('ровно одна ghost-кнопка «Открыть задачу»; вопроса и кнопки чата нет', () => {
    const { container } = mountHero(waitingRun(), mkStats());
    const hero = findNodeByAttr(container, 'data-testid', 'hero-waiting');
    const btn = findNodeByAttr(hero, 'data-testid', 'hero-open-task');
    expect(btn, 'кнопка «Открыть задачу» отсутствует').toBeTruthy();
    expect(textOf(btn)).toBe('Открыть задачу');
    // Кнопок «Ответить в чате» в герое нет — только одна кнопка всего.
    expect(textOf(hero)).not.toContain('Ответить');
    // И самого текста вопроса (attention.reason) — он остаётся в строке анализа (п. 4).
    expect(textOf(hero)).not.toContain('Вопрос по шагу');
    const buttons = hero.children.flatMap(c => textNodes(c)).filter(t => t === 'Открыть задачу');
    expect(buttons.length).toBe(1);
  });

  it('клик «Открыть задачу» — openTaskInSection(«t-1») ровно один раз', () => {
    const { container } = mountHero(waitingRun(), mkStats());
    const btn = findNodeByAttr(container, 'data-testid', 'hero-open-task');
    const props = reactProps(btn) as { onClick: () => void };
    act(() => { props.onClick(); });
    expect(mockOpenTask.length).toBe(1);
    expect(mockOpenTask[0].id).toBe('t-1');
  });

  it('без задачи и эпика — кнопки перехода нет', () => {
    const run = mkRun({
      state: 'waiting',
      attention: { kind: 'wait', since: new Date(Date.now() - 5 * 60_000).toISOString(), taskId: null },
    });
    const { container } = mountHero(run, mkStats());
    expect(findNodeByAttr(container, 'data-testid', 'hero-open-task')).toBeNull();
  });
});

describe('шрифт числа героя (сверяется с design.ts)', () => {
  it('десктоп: FONT.serif и FS.display', () => {
    const { container } = mountHero(mkRun(), mkStats());
    const big = findNodeByAttr(container, 'data-testid', 'hero-big');
    const style = big.style as Record<string, string>;
    // React пишет число через px — сравниваем строку «34px» с токеном design.ts.
    expect(style.fontFamily).toBe(FONT.serif);
    expect(style.fontSize).toBe(`${FS.display}px`);
    expect(style.fontWeight).toBe('700'); // макет .a-kpi .n: font:700 …
  });

  it('360 (isMobile): FS.h1', () => {
    const { container } = mountHero(mkRun(), mkStats(), true);
    const big = findNodeByAttr(container, 'data-testid', 'hero-big');
    const style = big.style as Record<string, string>;
    expect(style.fontFamily).toBe(FONT.serif);
    expect(style.fontSize).toBe(`${FS.h1}px`);
  });
});
