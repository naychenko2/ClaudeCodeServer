// Тело режима «План хода / Исполнение фичи» (общее для меню пилюли и карточки в ленте).
// Рендер — renderToStaticMarkup (окружение node: useIsMobile()=false, effects не идут).
// F-pop-a: счётчик «принято/всего · ↺N» из шапки УБРАН (макет, вопрос 1); тело —
// новая компактная карточка (StatusTag, строка состояния, этапы). Проверяем обе
// поверхности (пилюля и карточка) — шапка одна, счётчика на обеих нет.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureRun } from '../../../types/featureRun';
import { FeatureModeBody } from './FeatureModeBody';
import { __test } from '../../../lib/featureRuns';

// useIsMobile — управляемый флаг (SSR по умолчанию false).
const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});
// Стор персон (lib/personas) — мок: FeatureRunView внутри тела читает getPersonaById.
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});
// api (lib/api) — FeatureRunView вызывает api.tasks.mergeFeature в обработчиках (SSR — не идёт).
vi.mock('../../../lib/api', () => ({
  api: { tasks: { mergeFeature: async (_id: string) => {} } },
}));

beforeEach(() => { personaMap.clear(); mobileFlag.value = false; __test.reset(); });

// Агрегат фичи: state задан (бэк всегда отдаёт), stepsAccepted/stepsTotal + loops.
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: { id: 'epic-1', title: 'Фича', status: 'todo' },
    steps: [],
    reviews: [],
    qa: [],
    final: null as never,
    merge: null,
    state: 'running',
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    now: [],
    attention: null,
    closed: false,
    summary: null,
    ...over,
  } as FeatureRun;
}

// Рендер тела: счётчик шапки — в шапке (controls) на обеих поверхностях.
function renderBody(runs: FeatureRun[], variant: 'pill' | 'card', sessionId: string): string {
  // Стор: срез фич → modeFor(sessionId)='feature'; выбор не задан → runs[0].
  __test.setRuns(sessionId, runs);
  return renderToStaticMarkup(
    createElement(FeatureModeBody, { sessionId, todos: [], runs, isMobile: mobileFlag.value, variant }),
  );
}

describe('FeatureModeBody — F-pop-a (счётчик убран, новая карточка)', () => {
  it('обе поверхности: счётчика «acc/tot · ↺N» в шапке нет', () => {
    const r = run({ stepsAccepted: 2, stepsTotal: 4, verdicts: { accepted: 2, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 1 } });
    const pill = renderBody([r], 'pill', 'sess-pill');
    const card = renderBody([r], 'card', 'sess-card');
    // Счётчик шагов «2/4 · ↺1» из шапки убран (на обеих поверхностях).
    expect(pill).not.toContain('2/4 · ↺1');
    expect(card).not.toContain('2/4 · ↺1');
    // Старый «100%» (доля решённых витков) тоже не в шапке.
    expect(pill).not.toMatch(/>100%</);
    expect(card).not.toMatch(/>100%</);
  });

  it('обе поверхности: тег статуса и переключатель на месте', () => {
    const r = run({ state: 'running' });
    const pill = renderBody([r], 'pill', 'sess-pill2');
    const card = renderBody([r], 'card', 'sess-card2');
    // Тег статуса (StatusTag) — в шапке фичи (data-testid="feature-run-status").
    expect(pill).toContain('feature-run-status');
    expect(card).toContain('feature-run-status');
    // Переключатель «План хода · Исполнение фичи» — в шапке.
    expect(pill).toContain('Исполнение фичи');
    expect(card).toContain('Исполнение фичи');
  });

  it('ноль шагов: счётчика «0/— · ↺N» в шапке нет', () => {
    const r = run({ stepsAccepted: 0, stepsTotal: 0, verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 2 } });
    const pill = renderBody([r], 'pill', 'sess-pill0');
    const card = renderBody([r], 'card', 'sess-card0');
    expect(pill).not.toContain('0/— · ↺2');
    expect(card).not.toContain('0/— · ↺2');
  });
});
