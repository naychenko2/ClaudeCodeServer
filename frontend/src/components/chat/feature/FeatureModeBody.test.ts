// Тело режима «План хода / Исполнение фичи» (FeatureModeBody) — F2 (макет v4):
// чат ШАГА → П2 «Мой шаг» (FeatureStepView); чат БЕЗ шага → П1-сводка (FeatureTruthBody);
// загрузка/ошибка запроса — скелет/«Повторить» (состояния в теле, не в теле-компонентах).
// Рендер — renderToStaticMarkup (окружение node: useIsMobile()=false, effects не идут).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { FeatureRun } from '../../../types/featureRun';
import { FeatureModeBody } from './FeatureModeBody';
import { __test } from '../../../lib/featureRuns';

const { mobileFlag } = vi.hoisted(() => ({ mobileFlag: { value: false } }));
vi.mock('../../../lib/breakpoints', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/breakpoints')>();
  return { ...orig, useIsMobile: () => mobileFlag.value };
});
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>([['p-gleb', 'Глеб']]) }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});

beforeEach(() => { personaMap.clear(); mobileFlag.value = false; __test.reset(); });

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: { id: 'epic-1', title: 'Фича', status: 'todo' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    state: 'running',
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

function renderBody(runs: FeatureRun[], sessionId: string, over: Record<string, unknown> = {}): string {
  __test.setRuns(sessionId, runs);
  return renderToStaticMarkup(
    createElement(FeatureModeBody, { sessionId, todos: [], runs, isMobile: mobileFlag.value, ...over }),
  );
}

describe('FeatureModeBody (F2): выбор тела', () => {
  it('чат шага (задача чата в цепочке) → П2 «Мой шаг»: карточка шага и его цепочка', () => {
    const r = run({
      steps: [{ id: 't-8', title: 'Вёрстка шапки', role: 'step', stepRef: 'Ш8', lane: 'front', status: 'inProgress' }],
      stepChains: [{ stepRef: 'Ш8', title: 'Вёрстка шапки', taskIds: ['t-8'], outcome: 'running', status: 'идёт', durationMinutes: 24, rounds: [], chatId: null, lane: 'front', personaId: 'p-kira' } as never],
    });
    const html = renderBody([r], 's-step', { chatTaskId: 't-8' });
    expect(html).toContain('feature-step-view');
    expect(html).toContain('Этот чат');
    expect(html).toContain('Цепочка шага');
    expect(html).toContain('Ш8 · Вёрстка шапки');
    // аккордеона этапов (старый FeatureRunView) нет
    expect(html).not.toContain('feature-run-stages');
    // подвал «этот чат — роль» — по задаче чата
    expect(html).toContain('Ш8 · фронт');
  });

  it('чат без шага (оркестратор/ревью) → П1-сводка (FeatureTruthBody), не карточка шага', () => {
    const r = run({ stepChains: [{ stepRef: 'Ш8', title: 'Вёрстка', taskIds: ['t-8'], outcome: 'running', status: 'идёт', rounds: [], chatId: null } as never] });
    const html = renderBody([r], 's-orch', { chatTaskId: 't-куратора' });
    expect(html).toContain('feature-truth-body');
    expect(html).not.toContain('feature-step-view');
  });

  it('переключатель «План хода / Исполнение фичи» — в шапке; плана без фич — TodoList', () => {
    const html = renderBody([run()], 's-sw');
    expect(html).toContain('Исполнение фичи');
    expect(html).toContain('План хода');
  });
});

describe('FeatureModeBody (F2): состояния запроса', () => {
  function renderState(sessionId: string, setup: () => void): string {
    __test.setRuns(sessionId, []);
    setup();
    return renderToStaticMarkup(createElement(FeatureModeBody, { sessionId, todos: [], runs: [], isMobile: false }));
  }

  it('загрузка (run нет) — скелет 3 строк', () => {
    const html = renderState('s-load', () => __test.setLoading('s-load', true));
    expect(html).toContain('feature-mode-skeleton');
  });

  it('ошибка — «Не загрузилось · Повторить»', () => {
    const html = renderState('s-err', () => __test.setError('s-err', 'Сервер не ответил'));
    expect(html).toContain('feature-mode-error');
    expect(html).toContain('Повторить');
  });
});
