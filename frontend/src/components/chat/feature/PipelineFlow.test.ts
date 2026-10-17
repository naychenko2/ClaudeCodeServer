// Конвейер фичи (PipelineFlow) + её чистая логика — строго по макету v2.
//
// Окружение node (без jsdom): фазы/проходы/линии/дуги проверяю через чистые функции
// (stagePhases, stagePasses, stageWidth, laneInfo, laneTrackWidth, arcSpecs, buildArcs,
// arcPath, chevronPath), рендер — renderToStaticMarkup (паттерн FeatureRunView.test.ts).
// useIsMobile() = false → десктоп-раскладка (дуги рисуются, полосы бэк/фронт — строки).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';
import { C } from '../../../lib/design';
import {
  PipelineFlow, stagePhases, stagePasses, stageWidth,
  laneInfo, laneTrackWidth, arcSpecs, buildArcs, arcPath, chevronPath,
} from './PipelineFlow';

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

// === stagePhases: фазы 7 стадий (перенесено из PipelineStages) ===
describe('stagePhases', () => {
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
});

// === stagePasses: число ПРОХОДОВ (не «число задач») ===
describe('stagePasses', () => {
  it('«Производство» = 1 + возвраты (ревью/проверка/финал)', () => {
    const r = run({
      reviews: [
        task({ id: 'r1', status: 'done', verification: { verdict: 'returned' } }),
        task({ id: 'r2', status: 'done', verification: { verdict: 'accepted_with_remarks' } }),
      ],
    });
    // 1 + 2 возврата = 3
    expect(stagePasses(r, 'prod')).toBe(3);
  });

  it('«Производство» без возвратов = 1', () => {
    const r = run({
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    expect(stagePasses(r, 'prod')).toBe(1);
  });

  it('«Ревью»/«Проверка»/«Финал» = число их задач; рез/план/мерж = 1', () => {
    const r = run({
      reviews: [task({ id: 'r1' }), task({ id: 'r2' })],
      qa: [task({ id: 'q1' })],
      final: task({ id: 'f1' }),
    });
    expect(stagePasses(r, 'review')).toBe(2);
    expect(stagePasses(r, 'check')).toBe(1);
    expect(stagePasses(r, 'final')).toBe(1);
    expect(stagePasses(r, 'cut')).toBe(1);
    expect(stagePasses(r, 'plan')).toBe(1);
    expect(stagePasses(r, 'merge')).toBe(1);
  });
});

// === stageWidth: толщина сегмента (формула макета) ===
describe('stageWidth', () => {
  it('pending → 2 (пунктир)', () => {
    expect(stageWidth(3, 'pending')).toBe(2);
  });
  it('passes=3, active → min(2 + (3−1)·1.5, 8) = 5', () => {
    expect(stageWidth(3, 'active')).toBe(5);
  });
  it('passes=1 → min(2 + 0, 8) = 2', () => {
    expect(stageWidth(1, 'done')).toBe(2);
  });
  it('потолок: passes=5 → min(2 + 4·1.5, 8) = 8', () => {
    expect(stageWidth(5, 'done')).toBe(8);
  });
  it('passes>5 капится: passes=9 → min(2 + 4·1.5, 8) = 8', () => {
    expect(stageWidth(9, 'done')).toBe(8);
  });
});

// === laneInfo: линии бэк/фронт ===
describe('laneInfo', () => {
  it('0 шагов → total=0 (строка не рисуется)', () => {
    expect(laneInfo(run())).toEqual({
      back: { label: 'бэк', total: 0, accepted: 0, returns: 0 },
      front: { label: 'фронт', total: 0, accepted: 0, returns: 0 },
    });
  });

  it('3 back-шага (2 done, 1 с возвратом) → total=3, accepted=2, returns=1', () => {
    const r = run({
      steps: [
        task({ id: 's1', lane: 'back', status: 'done' }),
        task({ id: 's2', lane: 'back', status: 'done', verification: { verdict: 'returned' } }),
        task({ id: 's3', lane: 'back', status: 'todo' }),
      ],
    });
    expect(laneInfo(r).back).toEqual({ label: 'бэк', total: 3, accepted: 2, returns: 1 });
    expect(laneInfo(r).front.total).toBe(0);
  });

  it('нормализует русские lane (бэк/фронт) и игнорирует чужие линии', () => {
    const r = run({
      steps: [
        task({ id: 's1', lane: 'бэк', status: 'done' }),
        task({ id: 's2', lane: 'фронт', status: 'done' }),
        task({ id: 's3', lane: 'тест', status: 'done' }),
      ],
    });
    expect(laneInfo(r).back.total).toBe(1);
    expect(laneInfo(r).front.total).toBe(1);
  });
});

// === laneTrackWidth: толщина дорожки = min(2 + r·0.5, 8) ===
describe('laneTrackWidth', () => {
  it('r=0 → 2', () => expect(laneTrackWidth(0)).toBe(2));
  it('r=2 → 3', () => expect(laneTrackWidth(2)).toBe(3));
  it('r=12 → потолок 8', () => expect(laneTrackWidth(12)).toBe(8));
});

// === arcSpecs: стадии-источники возврата ===
describe('arcSpecs', () => {
  it('3 возврата от ревью → { key: review, count: 3, color: warning }', () => {
    const r = run({
      reviews: [
        task({ id: 'r1', status: 'done', verification: { verdict: 'returned' } }),
        task({ id: 'r2', status: 'done', verification: { verdict: 'returned' } }),
        task({ id: 'r3', status: 'done', verification: { verdict: 'returned' } }),
      ],
    });
    const specs = arcSpecs(r);
    expect(specs).toHaveLength(1);
    expect(specs[0].key).toBe('review');
    expect(specs[0].count).toBe(3);
  });

  it('только принятые (без возвратов) → источников нет', () => {
    const r = run({
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    expect(arcSpecs(r)).toEqual([]);
  });

  it('ревью + проверка + финал → 3 источника', () => {
    const r = run({
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'returned' } })],
      qa: [task({ id: 'q1', status: 'done', verification: { verdict: 'returned' } })],
      final: task({ id: 'f1', status: 'done', verification: { verdict: 'returned' } }),
    });
    const specs = arcSpecs(r);
    expect(specs.map(s => s.key)).toEqual(['review', 'check', 'final']);
  });
});

// === buildArcs / arcPath / chevronPath: геометрия дуг ===
describe('buildArcs', () => {
  it('3 возврата от ревью → 3 дуги (cap 4) + подпись ×3', () => {
    const els = buildArcs([{ key: 'review', y: 100, count: 3, color: C.warning }], 50);
    // 3 path-дуги + 1 label = 4 элемента
    expect(els).toHaveLength(4);
    // 3 дуги (path) + 1 подпись
    const paths = els.filter(e => e.d).length;
    const labels = els.filter(e => e.label).length;
    expect(paths).toBe(3);
    expect(labels).toBe(1);
    // Подпись ×3
    expect(els.find(e => e.label)?.label).toBe('×3');
  });

  it('cap: 6 возвратов → 4 дуги (cap) + подпись 6 (n>cap → без ×)', () => {
    const els = buildArcs([{ key: 'review', y: 100, count: 6, color: C.warning }], 50);
    const paths = els.filter(e => e.d).length;
    expect(paths).toBe(4);
    expect(els.find(e => e.label)?.label).toBe('6');
  });

  it('0 возвратов → пусто', () => {
    expect(buildArcs([{ key: 'review', y: 100, count: 0, color: C.warning }], 50)).toEqual([]);
  });
});

describe('arcPath / chevronPath', () => {
  it('lane-сдвиг по x на 7px: lane=0 → x=8, lane=1 → x=15', () => {
    const p0 = arcPath(100, 50, 0, 0);
    const p1 = arcPath(100, 50, 1, 0);
    // x = 8 + lane*7: lane=0 → 8, lane=1 → 15
    expect(p0).toContain('H5');  // x-3 = 8-3 = 5
    expect(p1).toContain('H12'); // x-3 = 15-3 = 12
  });

  it('chevron — стрелка в yProd', () => {
    expect(chevronPath(50)).toBe('M8 47 4 50 8 53');
  });
});

// === Рендер (SSR) ===
describe('PipelineFlow: рендер', () => {
  it('пустая фича: 7 стадий, линий бэк/фронт нет (0 шагов), дуг нет', () => {
    const html = render(run());
    for (const label of ['Рез', 'План', 'Производство', 'Ревью', 'Проверка', 'Финал', 'Мерж']) {
      expect(html).toContain(label);
    }
    // 0 шагов в линиях → строки «бэк · …»/«фронт · …» не выводятся
    expect(html).not.toContain('бэк ·');
    expect(html).not.toContain('фронт ·');
  });

  it('3 back-шага (2 done) → линия «бэк · 2 из 3» + дорожка', () => {
    const r = run({
      steps: [
        task({ id: 's1', lane: 'back', status: 'done' }),
        task({ id: 's2', lane: 'back', status: 'done' }),
        task({ id: 's3', lane: 'back', status: 'todo' }),
      ],
    });
    const html = render(r);
    expect(html).toContain('бэк · 2 из 3');
  });

  it('r ≥ 3 → бейдж ↺r (hot)', () => {
    const r = run({
      steps: [
        task({ id: 's1', lane: 'back', status: 'done', verification: { verdict: 'returned' } }),
        task({ id: 's2', lane: 'back', status: 'done', verification: { verdict: 'returned' } }),
        task({ id: 's3', lane: 'back', status: 'done', verification: { verdict: 'returned' } }),
      ],
    });
    const html = render(r);
    expect(html).toContain('↺3');
  });

  it('активная стадия помечена aria-current=step', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    expect(render(r)).toContain('aria-current="step"');
  });

  it('inProgress → метка «стоит сейчас» + ▶', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'inProgress' })],
    });
    const html = render(r);
    expect(html).toContain('стоит сейчас');
    expect(html).toContain('▶');
  });

  it('awaitingHumanSince → wait-обводка (border C.info) + «ждёт»', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
      awaitingHumanSince: '2026-10-17T10:00:00Z',
      awaitingHumanReason: 'нужно решение',
    });
    const html = render(r);
    // wait-обводка: border 1px solid C.info (var(--c-info))
    expect(html).toContain('var(--c-info)');
  });
});
