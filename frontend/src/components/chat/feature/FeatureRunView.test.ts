// Карточка фичи (режим «Исполнение фичи») + гейт фич-флага.
// Рендер — renderToStaticMarkup (паттерн соседних тестов, @testing-library нет;
// окружение node — window undefined, useIsMobile() = false, effects не идут).
// Проверяем:
//   1. M1/M3: рендер по БЭКОВОМУ FeatureRun — имя, ветка, прогресс, «финал
//      зелёный», «ждёт вас», «Ревью не было», действия куратора;
//   2. M2: гейт — runs=[] → пилюля в режиме плана, runs=[run] → режим фичи;
//   3. M7: мобила (useIsMobile) не рвёт SSR-рендер.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import { FeatureRunView } from './FeatureRunView';
import { TurnPlanPill } from '../TurnPlanPill';

// Задача-заглушка (подмножество TaskItem, как несёт GET /api/tasks/features).
function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

// Базовый агрегат фичи: эпик + шаг + ревью + финал; готовность/прогресс/вердикты.
function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator-console' }),
    steps: [task({ id: 's1', title: 'Шаг 1', stepRef: 'Ш1', lane: 'фронт', status: 'done' })],
    reviews: [task({ id: 'r1', title: 'Ревью', status: 'done', verification: { verdict: 'accepted' } })],
    qa: [],
    final: task({ id: 'f1', title: 'Финал', status: 'done', verification: { verdict: 'accepted' } }),
    merge: null,
    awaitingHumanSince: null,
    awaitingHumanReason: null,
    verdicts: { accepted: 2, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 1,
    ready: true,
    ...over,
  };
}

const render = (r: FeatureRun) => renderToStaticMarkup(createElement(FeatureRunView, { run: r }));

describe('FeatureRunView (бэковый FeatureRun)', () => {
  it('M1: имя, ветка, прогресс и «финал зелёный»', () => {
    const html = render(run());
    expect(html).toContain('Пульт куратора');
    expect(html).toContain('feat/curator-console');
    expect(html).toContain('100%');
    expect(html).toContain('Финал зелёный');
  });

  it('M3: «Принять» + «Изменить ход» + последствия (closed)', () => {
    const html = render(run());
    expect(html).toContain('Принять');
    expect(html).toContain('Изменить ход');
    expect(html).toContain('closed');
  });

  it('M3: «Принять» неактивно, когда финал не закрыт', () => {
    const html = render(run({
      ready: false,
      final: null,
      progress: 0.5,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    }));
    expect(html).toContain('Принять');
    expect(html).toContain('Доступно, когда финал закрыт');
  });

  it('M3: «Ревью не было» — когда ревью нет', () => {
    const html = render(run({ reviews: [] }));
    expect(html).toContain('Ревью не было');
  });

  it('M3: «Ждёт вас» — когда awaitingHumanSince стоит', () => {
    const html = render(run({
      ready: false,
      awaitingHumanSince: '2026-10-17T10:00:00Z',
      awaitingHumanReason: 'нужно решение о мерже',
    }));
    expect(html).toContain('Ждёт вас');
    expect(html).toContain('нужно решение о мерже');
  });

  it('M7: мобила (useIsMobile) не рвёт SSR-рендер', () => {
    expect(() => render(run())).not.toThrow();
  });
});

describe('Гейт фич-флага (M2)', () => {
  const todo: TodoItem = { content: 'Шаг плана', status: 'pending' };

  it('runs=[] → пилюля в режиме плана (нет «исполнение фичи»)', () => {
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [todo], runs: [] }));
    expect(html).toContain('Показать план хода');
    expect(html).not.toContain('Показать исполнение фичи');
  });

  it('runs=[run] → пилюля в режиме фичи', () => {
    const html = renderToStaticMarkup(createElement(TurnPlanPill, { todos: [], runs: [run()] }));
    expect(html).toContain('Показать исполнение фичи');
  });
});
