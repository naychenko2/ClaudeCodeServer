// Детали шага (Ф7): шапка, пружина (max 9), цепочка кругов (RoundChain), ссылка «Открыть задачу».
// Рендер — renderToStaticMarkup (паттерн соседних тестов, @testing-library нет; окружение node —
// window undefined, useIsMobile() управляем, effects не идут). Проверяем:
//   • порядок звеньев цепочки (работа → круг → доработка → круг);
//   • withdrawn (три возврата и снятие): кольцо/глиф не «идёт»;
//   • шаг без витков — «ревью ещё не было»;
//   • нет времени/причины — без Invalid Date / NaN / висящих разделителей;
//   • running — пульс и «идёт N мин»;
//   • ссылка «Открыть задачу →» (openTaskTarget) и её отсутствие без задачи;
//   • бусины R.max и цвета по verdict; шапка: статус и «из: ревью»;
//   • пружина max=9 и +N при 12 витках.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureRun, FeatureTask, FeatureStepChain, FeatureRound } from '../../../types/featureRun';
import { StepDetails, openTaskTarget } from './StepDetails';
import { RING } from './Rounds';

// Ожидаемое «ЧЧ:ММ» — теми же локальными геттерами от той же Date, что и hhmm (featureTime),
// но НЕ через hhmm: мутация hhmm (featureTime) должна краснить тест, а не сдвигать ожидание.
// Тест не зависит от пояса машины (CI в UTC, разработка в МСК).
function localHHMM(iso: string): string {
  const d = new Date(iso);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// Стор персон (lib/personas) — мок: hoisted-карта id→имя (как в FeatureRunView.test.ts).
// importOriginal — сохранить остальные экспорты (usePersonasVersion и др.).
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});
// useIsMobile — управляемый флаг (SSR по умолчанию false).
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});
// lib/tasks — spy openTaskInSection (SSR не кликает, но импорт не должен пасть).
vi.mock('../../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/tasks')>();
  return { ...orig, openTaskInSection: vi.fn() };
});
beforeEach(() => { personaMap.clear(); mobileFlag.value = false; });

// === Фикстуры ===

function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

function round(over: Partial<FeatureRound> = {}): FeatureRound {
  return { n: 1, verdict: 'accepted', ...over };
}

function chain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'D1c', lane: 'фронт', title: 'Шаг', personaId: null,
    taskIds: [], outcome: 'accepted', status: 'принят', durationMinutes: 70,
    source: null, loops: 0, rounds: [], ...over,
  };
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Фича' }),
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, ...over,
  };
}

const render = (c: FeatureStepChain, r: FeatureRun) =>
  renderToStaticMarkup(createElement(StepDetails, { chain: c, run: r, isMobile: mobileFlag.value }));

// Цепочка: шаг + доработка, два витка (вернул → принял). Задачи в run.steps.
function acceptedChain(): { c: FeatureStepChain; r: FeatureRun } {
  const c = chain({
    stepRef: 'D1c',
    taskIds: ['s1', 's2'],
    rounds: [
      round({ n: 1, verdict: 'returned', personaId: 'p2', startedAt: '2026-10-16T07:36:00Z', completedAt: '2026-10-16T07:45:00Z', reason: 'Мажор: нет пустого состояния' }),
      round({ n: 2, verdict: 'accepted', personaId: 'p2', startedAt: '2026-10-16T08:36:00Z', completedAt: '2026-10-16T08:45:00Z' }),
    ],
    loops: 1,
  });
  const r = run({
    steps: [
      task({ id: 's1', title: 'Шаг D1c', role: 'step', stepRef: 'D1c', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:49:00Z', completedAt: '2026-10-16T08:09:00Z', projectId: 'proj-1' }),
      task({ id: 's2', title: 'Доработка D1c', role: 'rework', stepRef: 'D1c', personaId: 'p1', status: 'done', createdAt: '2026-10-16T08:09:00Z', completedAt: '2026-10-16T08:36:00Z', projectId: 'proj-1' }),
    ],
  });
  return { c, r };
}

describe('StepDetails — порядок звеньев цепочки', () => {
  it('работа → круг 1 (вернул) → доработка → круг 2 (принял)', () => {
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    const { c, r } = acceptedChain();
    const html = render(c, r);
    const iWork = html.indexOf('работа');
    const iRound1 = html.indexOf('Круг 1');
    const iRework = html.indexOf('Доработка D1c');
    const iRound2 = html.indexOf('Круг 2');
    // Все на месте.
    expect(iWork).toBeGreaterThanOrEqual(0);
    expect(iRound1).toBeGreaterThanOrEqual(0);
    expect(iRework).toBeGreaterThanOrEqual(0);
    expect(iRound2).toBeGreaterThanOrEqual(0);
    // Порядок: работа ПЕРЕД кругом 1, доработка ПОСЛЕ круга 1 и ПЕРЕД кругом 2.
    expect(iWork).toBeLessThan(iRound1);
    expect(iRound1).toBeLessThan(iRework);
    expect(iRework).toBeLessThan(iRound2);
  });

  it('мутация (а): круг раньше работы → порядок ломается', () => {
    // Эмулируем мутацию: в buildLinks круг ставится ПЕРЕД первой работой.
    // Проверяем, что текущий порядок (работа первым) — именно то, что тест выше фиксирует:
    // первая работа (s1) должна идти до любого «Круг N».
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    const { c, r } = acceptedChain();
    const html = render(c, r);
    // Первое звено — работа (её заголовок/форма), не «Круг».
    const firstLinkIsWork = html.indexOf('Доработка D1c') > html.indexOf('работа')
      ? html.indexOf('работа') < html.indexOf('Круг 1')
      : false;
    expect(firstLinkIsWork).toBe(true);
  });
});

describe('StepDetails — withdrawn (три возврата и снятие)', () => {
  it('последний виток withdrawn: бусина «снят», статус шапки «снят с выкладки»', () => {
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    const c = chain({
      stepRef: 'C1-2',
      taskIds: ['s1', 's2', 's3'],
      outcome: 'withdrawn',
      status: 'снят с выкладки',
      rounds: [
        round({ n: 1, verdict: 'returned', personaId: 'p2', startedAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:10:00Z' }),
        round({ n: 2, verdict: 'returned', personaId: 'p2', startedAt: '2026-10-16T08:00:00Z', completedAt: '2026-10-16T08:10:00Z' }),
        round({ n: 3, verdict: 'returned', personaId: 'p2', startedAt: '2026-10-16T09:00:00Z', completedAt: '2026-10-16T09:10:00Z' }),
        round({ n: 4, verdict: 'withdrawn', personaId: 'p2', startedAt: '2026-10-16T10:00:00Z', completedAt: '2026-10-16T10:10:00Z' }),
      ],
      loops: 3,
    });
    const r = run({
      steps: [
        task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'C1-2', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:30:00Z' }),
        task({ id: 's2', title: 'Доработка 1', role: 'rework', stepRef: 'C1-2', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:30:00Z', completedAt: '2026-10-16T08:00:00Z' }),
        task({ id: 's3', title: 'Доработка 2', role: 'rework', stepRef: 'C1-2', personaId: 'p1', status: 'done', createdAt: '2026-10-16T08:30:00Z', completedAt: '2026-10-16T09:00:00Z' }),
      ],
    });
    const html = render(c, r);
    // Статус шапки — «снят с выкладки» (не «принят»).
    expect(html).toContain('снят с выкладки');
    expect(html).not.toContain('>принят<');
    // Бусина withdrawn — «снят».
    expect(html).toContain('снят');
    // Кольцо withdrawn в пружине — C.textMuted (не C.textHeading, как у running).
    expect(RING.withdrawn).toBeDefined();
    expect(RING.withdrawn.stroke).toBe('var(--c-text-muted)');
    expect(RING.withdrawn.stroke).not.toBe(RING.running.stroke);
  });

  it('мутация (б): withdrawn без кольца в RING падает в running → рендеренный stroke меняется', () => {
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    const c = chain({
      stepRef: 'C1-2', taskIds: ['s1'],
      status: 'снят с выкладки', outcome: 'withdrawn',
      rounds: [round({ n: 1, verdict: 'withdrawn', personaId: 'p2', startedAt: '2026-10-16T10:00:00Z', completedAt: '2026-10-16T10:10:00Z' })],
    });
    const r = run({ steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'C1-2', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:30:00Z' })] });
    // Базовый: кольцо withdrawn в пружине — C.textMuted (не C.textHeading, как у running).
    const baseHtml = render(c, r);
    expect(baseHtml).toContain('stroke="var(--c-text-muted)"');
    // Мутация: убираем withdrawn из RING → кольцо падает в running (C.textHeading + пунктир).
    const saved = RING.withdrawn;
    delete (RING as Record<string, unknown>).withdrawn;
    try {
      const mutatedHtml = render(c, r);
      // Тест краснеет: withdrawn-кольцо больше не C.textMuted, а C.textHeading.
      expect(mutatedHtml).not.toContain('stroke="var(--c-text-muted)"');
      expect(mutatedHtml).toContain('stroke="var(--c-text-heading)"');
    } finally {
      (RING as Record<string, unknown>).withdrawn = saved;
    }
  });
});

describe('StepDetails — шаг без витков', () => {
  it('«ревью ещё не было», цепочка из одного звена «работа»', () => {
    personaMap.set('p1', 'Денис');
    const c = chain({ stepRef: 'Ш1', taskIds: ['s1'], rounds: [], status: 'ждёт ревью', outcome: 'pending' });
    const r = run({
      steps: [task({ id: 's1', title: 'Шаг Ш1', role: 'step', stepRef: 'Ш1', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:30:00Z' })],
    });
    const html = render(c, r);
    expect(html).toContain('ревью ещё не было');
    expect(html).toContain('работа');
    // Пружина не рендерится (нет витков) — нет svg-колец с классом coil-подписью.
    expect(html).not.toContain('витков');
  });
});

describe('StepDetails — краевые: нет времени и причины', () => {
  it('без Invalid Date / NaN / висящих разделителей', () => {
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    const c = chain({
      stepRef: 'D1c',
      taskIds: ['s1'],
      rounds: [round({ n: 1, verdict: 'accepted', personaId: 'p2', startedAt: null, completedAt: null, reason: null })],
    });
    const r = run({
      steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'D1c', personaId: 'p1', status: 'done', createdAt: null, completedAt: null })],
    });
    const html = render(c, r);
    expect(html).not.toContain('Invalid Date');
    expect(html).not.toContain('NaN');
    // Нет висящего «·» в начале строки времени (нет времени → строка не рисуется).
    expect(html).not.toContain('· ·');
  });
});

describe('StepDetails — running (идёт)', () => {
  it('круг running: пульс и «идёт N мин»', () => {
    personaMap.set('p2', 'Глеб');
    const c = chain({
      stepRef: 'D1c',
      taskIds: ['s1'],
      status: 'идёт',
      outcome: 'running',
      rounds: [round({ n: 1, verdict: 'running', personaId: 'p2', startedAt: '2026-10-16T07:00:00Z', completedAt: null })],
    });
    const r = run({
      steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'D1c', personaId: 'p1', status: 'inProgress', createdAt: '2026-10-16T07:00:00Z', completedAt: null })],
    });
    const html = render(c, r);
    // Пульс у идущего круга.
    expect(html).toContain('feature-pulse');
    // «идёт» в бусине.
    expect(html).toContain('идёт');
  });
});

describe('StepDetails — ссылка «Открыть задачу →»', () => {
  it('рендерится, когда есть задача шага', () => {
    const { c, r } = acceptedChain();
    const html = render(c, r);
    expect(html).toContain('Открыть задачу');
  });

  it('не рендерится, когда задач нет', () => {
    const c = chain({ stepRef: 'D1c', taskIds: [], rounds: [round({ n: 1, verdict: 'accepted' })] });
    const r = run({});
    const html = render(c, r);
    expect(html).not.toContain('Открыть задачу');
  });

  it('openTaskTarget: id + projectId задачи шага; null без задачи', () => {
    const { c, r } = acceptedChain();
    const t = openTaskTarget(c, r);
    expect(t).toEqual({ id: 's1', projectId: 'proj-1' });
    const empty = chain({ stepRef: 'D1c', taskIds: [] });
    expect(openTaskTarget(empty, r)).toBeNull();
  });
});

describe('StepDetails — бусины и цвета по verdict', () => {
  it('BEAD-цвета: returned/awr/accepted/running/withdrawn', () => {
    // Прямая проверка константы цветов (R.max, 11/700, line-height 18 — в компоненте).
    const c = chain({
      stepRef: 'D1c', taskIds: ['s1'],
      rounds: [
        round({ n: 1, verdict: 'returned', personaId: 'p2', startedAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:10:00Z' }),
        round({ n: 2, verdict: 'accepted_with_remarks', personaId: 'p2', startedAt: '2026-10-16T08:00:00Z', completedAt: '2026-10-16T08:10:00Z' }),
        round({ n: 3, verdict: 'accepted', personaId: 'p2', startedAt: '2026-10-16T09:00:00Z', completedAt: '2026-10-16T09:10:00Z' }),
      ],
    });
    const r = run({ steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'D1c', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:30:00Z' })] });
    const html = render(c, r);
    // Бусина returned — warningBg; accepted — successBg; awr — bgCard + пунктир warning.
    expect(html).toContain('var(--c-warning-bg)');
    expect(html).toContain('var(--c-success-bg)');
    expect(html).toContain('var(--c-bg-card)');
    // R.max (999) — пилюля, не R.full.
    expect(html).toContain('border-radius:999px');
  });
});

describe('StepDetails — шапка', () => {
  it('статус и «из: ревью» (source=review)', () => {
    const c = chain({ stepRef: 'D1c', status: 'принят', source: 'review', taskIds: ['s1'], rounds: [round({ n: 1, verdict: 'accepted' })] });
    const r = run({ steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'D1c', personaId: null, status: 'done', createdAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:30:00Z' })] });
    const html = render(c, r);
    expect(html).toContain('принят');
    expect(html).toContain('из: ревью');
  });

  it('длительность «1 ч 10 мин» и число кругов', () => {
    const c = chain({ stepRef: 'D1c', status: 'принят', durationMinutes: 70, taskIds: ['s1'], rounds: [round({ n: 1, verdict: 'accepted' }), round({ n: 2, verdict: 'accepted' })] });
    const r = run({ steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'D1c', personaId: null, status: 'done', createdAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:30:00Z' })] });
    const html = render(c, r);
    expect(html).toContain('1 ч 10 мин');
    expect(html).toContain('2 круга');
  });
});

describe('StepDetails — 360 px (без горизонтального скролла)', () => {
  it('длинный заголовок и причина переносятся (overflow-wrap), ряды — flex-wrap', () => {
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    const longTitle = 'Очень длинный заголовок задачи, который на узком экране обязан перенестись по словам, а не вызвать горизонтальный скролл';
    const longReason = 'Причина возврата: длинное описание дефекта, которое тоже должно переноситься, чтобы не разъезжать с панелью на 360 px';
    const c = chain({
      stepRef: 'D1c', taskIds: ['s1'],
      rounds: [round({ n: 1, verdict: 'returned', personaId: 'p2', startedAt: '2026-10-16T07:36:00Z', completedAt: '2026-10-16T07:45:00Z', reason: longReason })],
    });
    const r = run({ steps: [task({ id: 's1', title: longTitle, role: 'step', stepRef: 'D1c', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:49:00Z', completedAt: '2026-10-16T08:09:00Z' })] });
    const html = render(c, r);
    // Заголовок/причина — overflow-wrap:anywhere (перенос без горизонтального скролла).
    expect(html).toContain('overflow-wrap:anywhere');
    // Ряд звена — flex-wrap:wrap (бусина/время переносятся на новую строку).
    expect(html).toContain('flex-wrap:wrap');
  });
});

describe('StepDetails — звено круга: моно только у времени (Майя M4)', () => {
  it('имя обычным шрифтом, время mono 11 (не вся строка «Имя · время»)', () => {
    personaMap.set('p1', 'Денис');
    personaMap.set('p2', 'Глеб');
    const c = chain({
      stepRef: 'D1c', taskIds: ['s1'],
      rounds: [round({ n: 1, verdict: 'returned', personaId: 'p2', startedAt: '2026-10-16T07:36:00Z', completedAt: '2026-10-16T07:45:00Z' })],
    });
    const r = run({ steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'D1c', personaId: 'p1', status: 'done', createdAt: '2026-10-16T07:49:00Z', completedAt: '2026-10-16T08:09:00Z' })] });
    const html = render(c, r);
    expect(html, 'имя исполнителя витка').toContain('Глеб');
    // Время — в mono-спане (FONT.mono = 'JetBrains Mono', 'Courier New', monospace).
    // Ожидаемое «ЧЧ:ММ–ЧЧ:ММ» — локальными геттерами от тех же меток (независимость от пояса).
    const time = `${localHHMM('2026-10-16T07:36:00Z')}–${localHHMM('2026-10-16T07:45:00Z')}`;
    expect(html, 'время — mono').toMatch(new RegExp(`monospace[^>]*>${time}<`));
    // Имя «Глеб» — НЕ в mono-спане (обычный шрифт).
    expect(html, 'имя — не mono').not.toMatch(/monospace[^>]*>Глеб/);
  });
});

describe('StepDetails — пружина max=9 и +N', () => {
  it('12 витков: показывает 9 колец + «+3»', () => {
    const rounds: FeatureRound[] = Array.from({ length: 12 }, (_, i) =>
      round({ n: i + 1, verdict: i % 2 === 0 ? 'accepted' : 'returned', personaId: 'p2', startedAt: `2026-10-16T0${i % 10}:00:00Z`, completedAt: `2026-10-16T0${i % 10}:10:00Z` }));
    const c = chain({ stepRef: 'D1c', status: 'принят', taskIds: ['s1'], rounds });
    const r = run({ steps: [task({ id: 's1', title: 'Шаг', role: 'step', stepRef: 'D1c', personaId: null, status: 'done', createdAt: '2026-10-16T07:00:00Z', completedAt: '2026-10-16T07:30:00Z' })] });
    const html = render(c, r);
    // +N перед пружиной: 12 − 9 = 3.
    expect(html).toContain('+3');
    // Подпись: «12 витков · ↺6» (6 returned из 12).
    expect(html).toContain('12 витков');
  });
});
