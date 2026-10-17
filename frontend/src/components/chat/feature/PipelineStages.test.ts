// Линейка стадий конвейера (PipelineStages) + её чистая логика stagePhases.
//
// Окружение node (без jsdom): фазы проверяю через stagePhases (чистая функция),
// рендер — renderToStaticMarkup (паттерн FeatureRunView.test.ts).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';
import { PipelineStages, stagePhases } from './PipelineStages';

function task(over: Partial<FeatureTask> = {}): FeatureTask {
  return { id: 't', title: 'Задача', status: 'todo', ...over };
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: task({ id: 'epic-1', title: 'Пульт куратора' }),
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    awaitingHumanSince: null,
    awaitingHumanReason: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

const render = (r: FeatureRun) => renderToStaticMarkup(createElement(PipelineStages, { run: r }));

describe('stagePhases: фазы 7 стадий', () => {
  it('все шаги pending → одна активная (производство), остальные приглушены', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'todo' }), task({ id: 's2', status: 'todo' })],
    });
    const phases = stagePhases(r);
    expect(phases).toHaveLength(7);
    // ровно одна активная стадия, она — производство (первая непройденная)
    expect(phases.filter(p => p === 'active')).toEqual(['active']);
    expect(phases[2]).toBe('active');
    // рез и план пройдены (эпик есть, план разложен в шаги), остальное ещё впереди
    expect(phases[0]).toBe('done');
    expect(phases[1]).toBe('done');
    expect(phases.slice(3)).toEqual(['pending', 'pending', 'pending', 'pending']);
  });

  it('эпик + шаг + ревью принято → рез/производство/ревью done, проверка active', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    const phases = stagePhases(r);
    expect(phases[0]).toBe('done');    // рез
    expect(phases[2]).toBe('done');    // производство
    expect(phases[3]).toBe('done');    // ревью
    expect(phases[4]).toBe('active');  // проверка — текущая
    expect(phases[5]).toBe('pending');
    expect(phases[6]).toBe('pending');
  });

  it('run.ready=true → всё до мержа done', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
      qa: [task({ id: 'q1', status: 'done', verification: { verdict: 'accepted' } })],
      final: task({ id: 'f1', status: 'done', verification: { verdict: 'accepted' } }),
      ready: true,
    });
    const phases = stagePhases(r);
    // рез, план, производство, ревью, проверка, финал — все пройдены
    expect(phases.slice(0, 6)).toEqual(['done', 'done', 'done', 'done', 'done', 'done']);
  });
});

describe('PipelineStages: рендер', () => {
  it('рисует 7 ячеек с подписями стадий', () => {
    const html = render(run());
    for (const label of ['Рез', 'План', 'Производство', 'Ревью', 'Проверка', 'Финал', 'Мерж']) {
      expect(html).toContain(label);
    }
  });

  it('активная стадия помечена aria-current=step', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    const html = render(r);
    expect(html).toContain('aria-current="step"');
  });
});
