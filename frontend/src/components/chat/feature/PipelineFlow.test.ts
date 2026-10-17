// Вертикальная диаграмма конвейера (PipelineFlow) + её чистая логика.
//
// Окружение node (без jsdom): фазы/проходы/исход проверяю через чистые функции
// (stagePhases, lanePasses, returnVerdict), рендер — renderToStaticMarkup (паттерн
// FeatureRunView.test.ts). useIsMobile() = false → десктоп-раскладка (дуга возврата
// рисуется, линии бэк/фронт — в ряд).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';
import { PipelineFlow, stagePhases, lanePasses, returnVerdict } from './PipelineFlow';

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

const render = (r: FeatureRun) => renderToStaticMarkup(createElement(PipelineFlow, { run: r }));

describe('stagePhases: фазы 7 стадий (перенесено из PipelineStages)', () => {
  it('все шаги pending → одна активная (производство), остальные приглушены', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'todo' }), task({ id: 's2', status: 'todo' })],
    });
    const phases = stagePhases(r);
    expect(phases).toHaveLength(7);
    expect(phases.filter(p => p === 'active')).toEqual(['active']);
    expect(phases[2]).toBe('active');
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
    expect(phases[0]).toBe('done');
    expect(phases[2]).toBe('done');
    expect(phases[3]).toBe('done');
    expect(phases[4]).toBe('active');
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
    expect(stagePhases(r).slice(0, 6)).toEqual(['done', 'done', 'done', 'done', 'done', 'done']);
  });
});

describe('lanePasses: толщина линии = число проходов', () => {
  it('пустая фича → 0/0', () => {
    expect(lanePasses(run())).toEqual({ back: 0, front: 0 });
  });

  it('3 back-шага → back=3, front=0', () => {
    const r = run({
      steps: [
        task({ id: 's1', lane: 'back', status: 'done' }),
        task({ id: 's2', lane: 'back', status: 'done' }),
        task({ id: 's3', lane: 'back', status: 'done' }),
      ],
    });
    expect(lanePasses(r)).toEqual({ back: 3, front: 0 });
  });

  it('нормализует русские lane (бэк/фронт) и игнорирует чужие линии', () => {
    const r = run({
      steps: [
        task({ id: 's1', lane: 'бэк', status: 'done' }),
        task({ id: 's2', lane: 'фронт', status: 'done' }),
        task({ id: 's3', lane: 'тест', status: 'done' }), // не в бэк/фронт
      ],
    });
    expect(lanePasses(r)).toEqual({ back: 1, front: 1 });
  });
});

describe('returnVerdict: исход петли возврата по run.verdicts', () => {
  const v = (over: Partial<FeatureRun['verdicts']>) =>
    run({ verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0, ...over } });

  it('returned > 0 → «вернул»', () => expect(returnVerdict(v({ returned: 2 }))).toBe('returned'));
  it('acceptedWithRemarks > 0 (без returned) → «принял с правками»', () =>
    expect(returnVerdict(v({ acceptedWithRemarks: 1 }))).toBe('accepted_with_remarks'));
  it('accepted > 0 (без возвратов) → «принял»', () => expect(returnVerdict(v({ accepted: 1 }))).toBe('accepted'));
  it('счётчиков нет → null (петли нет)', () => expect(returnVerdict(run())).toBeNull());
});

describe('PipelineFlow: рендер', () => {
  it('пустая фича: 7 стадий + линии-плейсхолдеры, дуги возврата нет', () => {
    const html = render(run());
    for (const label of ['Рез', 'План', 'Производство', 'Ревью', 'Проверка', 'Финал', 'Мерж']) {
      expect(html).toContain(label);
    }
    // Линии нарисованы (плейсхолдеры, count=0 → «—»)
    expect(html).toContain('бэк —');
    expect(html).toContain('фронт —');
    // Петли возврата нет (счётчики вердиктов пусты)
    expect(html).not.toContain('Возврат:');
  });

  it('3 back-шага + 2 review-возврата: толщина бэка=3, петля возврата видна', () => {
    const r = run({
      steps: [
        task({ id: 's1', lane: 'back', status: 'done' }),
        task({ id: 's2', lane: 'back', status: 'done' }),
        task({ id: 's3', lane: 'back', status: 'done' }),
      ],
      reviews: [
        task({ id: 'r1', status: 'done', verification: { verdict: 'returned' } }),
        task({ id: 'r2', status: 'done', verification: { verdict: 'returned' } }),
      ],
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 2, withdrawn: 0, loops: 2 },
    });
    const html = render(r);
    // Толщина линии бэка = 3 прохода (aria-label несёт count)
    expect(html).toContain('бэк: 3 прохода');
    // Петля возврата видна, исходом «вернул»
    expect(html).toContain('Возврат: вернул');
  });

  it('активная стадия помечена aria-current=step', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    expect(render(r)).toContain('aria-current="step"');
  });
});
