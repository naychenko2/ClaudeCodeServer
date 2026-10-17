// Конвейер фичи (PipelineFlow) + её чистая логика — строго по макету v2.
//
// Окружение node (без jsdom): фазы/проходы/линии/дуги проверяю через чистые функции
// (stagePhases, stagePasses, stageWidth, laneInfo, laneTrackWidth, arcSpecs, buildArcs,
// arcPath, chevronPath), рендер — renderToStaticMarkup (паттерн FeatureRunView.test.ts).
// useIsMobile() = false → десктоп-раскладка (дуги рисуются, полосы бэк/фронт — строки).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { act } from 'react';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { FeatureRun, FeatureTask } from '../../../types/featureRun';
import { C, SP, FONT } from '../../../lib/design';
import { findNodeByAttr, installFakeDom, mount, reactProps, uninstallFakeDom } from '../__tests__/fakeDom';
import {
  PipelineFlow, stagePhases, stagePasses, stageWidth, stageTasks,
  laneInfo, laneTrackWidth, arcSpecs, buildArcs, arcPath, chevronPath, ARCS_W,
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

// === stageTasks: задачи стадии для расчёта «идёт сейчас» (inProgress) ===
describe('stageTasks', () => {
  it('prod→steps, review→reviews, check→qa, cut/plan→epic', () => {
    const r = run({
      steps: [task({ id: 's1' })],
      reviews: [task({ id: 'r1' })],
      qa: [task({ id: 'q1' })],
    });
    expect(stageTasks(r, 'cut')).toEqual([r.epic]);
    expect(stageTasks(r, 'plan')).toEqual([r.epic]);
    expect(stageTasks(r, 'prod')).toBe(r.steps);
    expect(stageTasks(r, 'review')).toBe(r.reviews);
    expect(stageTasks(r, 'check')).toBe(r.qa);
  });

  it('final/merge: null → пустой массив, иначе [задача]', () => {
    expect(stageTasks(run(), 'final')).toEqual([]);
    expect(stageTasks(run(), 'merge')).toEqual([]);
    const f = task({ id: 'f1' });
    const m = task({ id: 'm1' });
    const r = run({ final: f, merge: m });
    expect(stageTasks(r, 'final')).toEqual([f]);
    expect(stageTasks(r, 'merge')).toEqual([m]);
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
  it('3 возврата от ревью → { key: review, count: 3, color: textSecondary }', () => {
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
    // Цвет — по стадии-источнику (макет v2): ревью → textSecondary (не warning по исходу).
    expect(specs[0].color).toBe(C.textSecondary);
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

  it('Arcs_РевьюОдинЦвет_ПроверкаДругой_ФиналТретий: 3 источника → 3 разных stroke', () => {
    const r = run({
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'returned' } })],
      qa: [task({ id: 'q1', status: 'done', verification: { verdict: 'returned' } })],
      final: task({ id: 'f1', status: 'done', verification: { verdict: 'returned' } }),
    });
    const specs = arcSpecs(r);
    // Цвет по стадии-источнику (макет v2, drawArcs: COL): review → textSecondary,
    // check → info, final → accent. Все три — РАЗНЫЕ (раньше все были C.warning).
    expect(specs.map(s => s.color)).toEqual([C.textSecondary, C.info, C.accent]);
    expect(new Set(specs.map(s => s.color)).size).toBe(3);
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

  it('активная проверка (qa inProgress) → «стоит сейчас» + ▶', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
      qa: [task({ id: 'q1', status: 'inProgress' })],
    });
    const html = render(r);
    expect(html).toContain('стоит сейчас');
    expect(html).toContain('▶');
  });

  it('активный финал (final inProgress) → «стоит сейчас» + ▶', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
      qa: [task({ id: 'q1', status: 'done', verification: { verdict: 'accepted' } })],
      final: task({ id: 'f1', status: 'inProgress' }),
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

  it('Stages_РельсаНепрерывна_МеждуТочкамиНетПросветов: align-start, top=низ точки, bottom=верх следующей', () => {
    // Окружение node: «замер» — из инлайн-стилей SSR-разметки (getBoundingClientRect
    // в node не laid out). Строка: рамка 1px + паддинг SP.xs(4) → верх точки = 5,
    // центр = 5 + 14/2 = 12 (DOT_CY). Сегмент: top = 5 + 14 = 19 (низ текущей точки),
    // bottom = -(SP.xs + 1) = -5 → низ сегмента = верх следующей точки (5) — без просвета.
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    const html = render(r);

    // 1) Грид строки — теперь на <button> (2.15.1), не на <div>: align-items:start
    //    (точка у верха, центр = 12 в любой строке).
    const rowTag = html.match(/<button[^>]*data-testid="stage-row-prod"[^>]*>/)?.[0] ?? '';
    const rowStyle = rowTag.match(/style="([^"]*)"/)?.[1] ?? '';
    expect(rowStyle).toContain('align-items:start');

    // 2) Сегмент «Производства»: top = низ текущей точки, bottom = до верха следующей.
    const from = html.indexOf('data-stage="prod"');
    const segStyle = html.slice(from).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    const top = segStyle.match(/top:([^;]+)/)?.[1]?.trim();
    const bottom = segStyle.match(/bottom:([^;]+)/)?.[1]?.trim();
    const pad = SP.xs + 1; // рамка 1px + паддинг SP.xs = 5 — смещение верха точки
    expect(top).toBe(`${pad + 14}px`);        // 19 = верх точки (5) + 14 (низ точки)
    expect(bottom).toBe(`-${pad}px`);         // -5 = до верха следующей точки (5)
    // Просвет между низом сегмента и верхом следующей точки: 0 (≤ 1 по допуску).
    // |bottom| = на сколько сегмент уходит НИЖЕ низа строки; pad = верх следующей точки.
    const gap = Math.abs(Math.abs(parseFloat(bottom)) - pad);
    expect(gap).toBeLessThanOrEqual(1);
  });

  it('Geometry_Рельса_ЦентрСовпадаетСКольцом: left сегмента = центр точки (padding-left + 16/2)', () => {
    // Окружение node: «замер» — из инлайн-стилей SSR-разметки (getBoundingClientRect
    // в node не laid out, см. тест выше). Сегмент имеет transform:translateX(-50%),
    // поэтому его CSS `left` — это ВИЗУАЛЬНЫЙ центр (не левый край).
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    const html = render(r);

    // 1) `left` сегмента рельсы «Производства» (первое `left:` в стиле — свойство left,
    //    не border-left: оно стоит раньше в строке).
    const from = html.indexOf('data-stage="prod"');
    const segStyle = html.slice(from).match(/style="([^"]*position:absolute[^"]*)"/)?.[1] ?? '';
    const left = segStyle.match(/left:([^;]+)/)?.[1]?.trim();
    expect(left).toBeTruthy();
    const segmentLeft = parseFloat(left);

    // 2) Центр точки: отступ строки слева (padding-left SP.xs) + половина колонки точки 16px.
    //    `left` считается от padding-box (рамку 1px не прибавляем) → SP.xs + 16/2 = 12.
    const dotCenter = SP.xs + 16 / 2;

    // 3) Центр сегмента совпадает с центром кольца (допуск 1px).
    expect(Math.abs(segmentLeft - dotCenter)).toBeLessThan(1);
  });

  it('Ready_ФиналЗелёный: run.ready=true → «Мерж» done с зелёным акцентом', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
      qa: [task({ id: 'q1', status: 'done', verification: { verdict: 'accepted' } })],
      final: task({ id: 'f1', status: 'done', verification: { verdict: 'accepted' } }),
      ready: true,
    });
    const html = render(r);
    // Строка «Мерж» — зелёная подсветка (C.successBg) и зелёная точка (C.success/C.successText).
    const from = html.indexOf('data-stage="merge"');
    expect(from).toBeGreaterThan(-1);
    const tag = html.match(/<div[^>]*data-stage="merge"[^>]*>/)?.[0] ?? '';
    expect(tag).toContain('var(--c-success-bg)');
    // Точка «Мерж»: зелёное кольцо (C.success) и зелёная галочка (C.successText),
    // а не серые (C.textMuted) как у остальных done-стадий.
    const dot = html.slice(from).match(/<svg[^>]*>[\s\S]*?<\/svg>/)?.[0] ?? '';
    expect(dot).toContain('var(--c-success)');
    expect(dot).toContain('var(--c-success-text)');
  });
});

// === Находки Глеба (501163ab, accepted_with_remarks): 3 минорных пробела покрытия ===
// ArcsOverlay (viewBox + font) в SSR не рендерится (нет измеренных координат),
// поэтому проверяю константу/токен напрямую, а не рендер.
describe('Глеб_Покрытие: viewBox-число, FONT.mono, null-guard', () => {
  it('viewBox-число: ARCS_W — число, viewBox-строка svg — только цифры (не "100%")', () => {
    // viewBox = `0 0 ${ARCS_W} ${Math.max(height,1)}`: все 4 части — числа.
    expect(typeof ARCS_W).toBe('number');
    const viewBox = `0 0 ${ARCS_W} ${Math.max(0, 1)}`;
    expect(viewBox.split(' ').every(p => Number.isFinite(Number(p)))).toBe(true);
  });

  it('FONT.mono: токен моноширинного шрифта (не пустой, содержит monospace)', () => {
    // Подписи дуг ×N используют font: `700 10px ${FONT.mono}` — токен, не сырая строка.
    expect(FONT.mono).toBeTruthy();
    expect(FONT.mono).toContain('monospace');
  });

  it('null-guard: null-задача в активной стадии не роняет .some(t => t?.status)', () => {
    // stageTasks(run,'cut') = [epic]; если epic=null → [null]; inProgress = .some(t => t?.status)
    // не падает на null (null-guard `t?.`). Рендер не бросает исключение.
    const r = run({ epic: null });
    expect(() => render(r)).not.toThrow();
  });
});

// === Раскрытие строк стадий (2.15): aria-expanded + список шагов «Производства» ===

// Агрегат с шагами в линиях бэк/фронт (для проверки раскрытия «Производства»).
function runWithSteps(): FeatureRun {
  return run({
    steps: [
      task({ id: 's1', title: 'Бэк-шаг', stepRef: 'Ш1', lane: 'back', status: 'done', personaId: 'denis' }),
      task({
        id: 's2', title: 'Фронт-шаг', stepRef: 'Ш2', lane: 'front', status: 'inProgress',
        personaId: 'kira', awaitingHumanSince: '2026-10-17T10:00:00Z', awaitingHumanReason: 'нужен выбор',
      }),
    ],
  });
}

describe('PipelineFlow: раскрытие строк (2.15)', () => {
  // fake-DOM для интерактивного кейса (клик); SSR-кейсы на renderToStaticMarkup не зависят от него.
  beforeAll(() => installFakeDom());
  afterAll(() => uninstallFakeDom());

  // SSR: «Производство» раскрыто по умолчанию → список шагов в разметке.
  // Мутация «закомментировать <StageDetails/>» обязана покрасить этот кейс (шаги исчезнут).
  it('Prod_СодержитШаги_РаскрытПоУмолчанию: шаги показаны (stepRef, lane, personaId, ожидание)', () => {
    const html = render(runWithSteps());
    // prod раскрыт по умолчанию → StageDetails рендерит список шагов (id stage-details-prod).
    expect(html).toContain('stage-details-prod');
    // Шаги: stepRef, заголовок, исполнитель (personaId).
    expect(html).toContain('Ш1');
    expect(html).toContain('Бэк-шаг');
    expect(html).toContain('Ш2');
    expect(html).toContain('Фронт-шаг');
    expect(html).toContain('denis');
    expect(html).toContain('kira');
    // Ожидание человека: reason + отметка времени «с ЧЧ:ММ» (локальное время, TZ-независимо).
    expect(html).toContain('нужен выбор');
    expect(html).toMatch(/с \d{2}:\d{2}/);
  });

  // SSR: стадия вне расширенного набора по умолчанию (review) → aria-expanded=false, деталей нет.
  it('Review_НетШагов_НетДеталей: aria-expanded=false, деталей нет', () => {
    const r = run({
      steps: [task({ id: 's1', status: 'done' })],
      reviews: [task({ id: 'r1', status: 'done', verification: { verdict: 'accepted' } })],
    });
    const html = render(r);
    // review не в расширенном наборе по умолчанию (только prod) → aria-expanded="false".
    const reviewBtn = html.match(/<button[^>]*data-testid="stage-row-review"[^>]*>/)?.[0] ?? '';
    expect(reviewBtn).toContain('aria-expanded="false"');
    // Деталей review нет: контейнера <div id="stage-details-review"> нет (строка есть
    // только в aria-controls кнопки, не в id деталей).
    expect(html).not.toContain('<div id="stage-details-review"');
  });

  // Интерактив (fake-DOM): клик по строке переключает aria-expanded.
  // Мутация «убрать onClick» (кнопка не переключает) обязана покрасить этот кейс.
  it('StageRows_КликПереключаетРаскрытие: aria-expanded переключается', () => {
    const { root, container } = mount(createElement(PipelineFlow, { run: runWithSteps() }));
    const btn = findNodeByAttr(container, 'data-testid', 'stage-row-prod');
    expect(btn, 'кнопка «Производство» не найдена').not.toBeNull();
    // По умолчанию prod раскрыт.
    expect(btn!.getAttribute('aria-expanded')).toBe('true');
    // Клик сворачивает.
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.getAttribute('aria-expanded')).toBe('false');
    // Клик раскрывает обратно.
    act(() => (reactProps(btn!)!.onClick as () => void)());
    expect(btn!.getAttribute('aria-expanded')).toBe('true');
    root.unmount();
  });
});
